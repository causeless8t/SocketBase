using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Causeless3t.Network
{
    public interface ISocketBuffer
    {
        byte[] Buffer { get; }
        int Length { get; }
    }

    public abstract class BaseSocket : IDisposable
    {
        private const int DefaultSendBufferSize = 8192;
        private const int DefaultReceiveBufferSize = 65535;
        private const int ReadBufferSizeAtOnce = 8192;

        #region Events

        /// <summary>
        /// 연결이 수립되면 호출됩니다.
        /// Worker Thread에서 호출될 수 있습니다.
        /// </summary>
        public event Action OnConnected;

        /// <summary>
        /// SocketException이 발생하면 호출됩니다.
        /// Worker Thread에서 호출될 수 있습니다.
        /// </summary>
        public event Action<SocketException> OnSocketException;

        /// <summary>
        /// 연결이 종료되면 호출됩니다.
        /// Worker Thread에서 호출될 수 있습니다.
        /// </summary>
        public event Action OnDisconnected;

        #endregion

        #region Properties

        public bool IsConnected => InternalSocket?.Connected ?? false;

        #endregion

        #region Fields

        protected Socket InternalSocket;

        protected readonly ConcurrentQueue<ISocketBuffer> SendMessages = new();

        protected readonly int SendBufferSize;
        protected readonly int ReceiveBufferSize;

        private Thread _listenerThread;
        private Thread _senderThread;

        private volatile bool _running;
        private int _disconnectedRaised;

        #endregion

        protected BaseSocket(int sendBuffer = -1, int receiveBuffer = -1)
        {
            SendBufferSize = sendBuffer > 0 ? sendBuffer : DefaultSendBufferSize;

            ReceiveBufferSize = receiveBuffer > 0 ? receiveBuffer : DefaultReceiveBufferSize;
        }

        #region Public

        /// <summary>
        /// 지정한 주소와 포트로 연결합니다.
        /// </summary>
        public virtual void Connect(string address, int port, ProtocolType type = ProtocolType.Tcp)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("Address cannot be null or empty.", nameof(address));

            if (port is <= 0 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));

            if (type != ProtocolType.Tcp && type != ProtocolType.Udp)
                throw new NotSupportedException($"{type} is not supported.");

            Stop();

            var ipAddress = GetIPv4Address(address);
            if (ipAddress == null)
                throw new SocketException((int)SocketError.HostNotFound);

            var endPoint = new IPEndPoint(ipAddress, port);

            InternalSocket = type == ProtocolType.Tcp
                ? new Socket(ipAddress.AddressFamily, SocketType.Stream, type)
                : new Socket(ipAddress.AddressFamily, SocketType.Dgram, type);

            InternalSocket.SendBufferSize = SendBufferSize;
            InternalSocket.ReceiveBufferSize = ReceiveBufferSize;

            try
            {
                InternalSocket.BeginConnect(endPoint, OnConnect, null);
            }
            catch
            {
                DisposeSocket();
                throw;
            }
        }

        /// <summary>
        /// 연결을 종료하고 Worker Thread를 정리합니다.
        /// </summary>
        public virtual void Stop()
        {
            var wasRunning = _running || InternalSocket != null;

            _running = false;

            while (SendMessages.TryDequeue(out _))
            {
            }

            ShutdownSocket();

            JoinThread(_senderThread);
            JoinThread(_listenerThread);

            _senderThread = null;
            _listenerThread = null;

            DisposeSocket();

            if (wasRunning)
                RaiseDisconnected();
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Protected

        /// <summary>
        /// 전송할 버퍼를 Send Queue에 등록합니다.
        /// </summary>
        protected bool Send(ISocketBuffer context)
        {
            if (!CanSend(context))
                return false;

            SendMessages.Enqueue(context);
            return true;
        }

        /// <summary>
        /// Socket에서 수신한 데이터입니다.
        ///
        /// buffer는 ArrayPool에서 임대한 배열이므로 이 메서드가 반환된 이후
        /// 참조를 보관해서는 안 됩니다.
        /// 필요한 경우 데이터를 복사해서 사용해야 합니다.
        /// </summary>
        protected abstract void OnReceive(byte[] buffer, int offset, int count);

        protected bool CanSend(ISocketBuffer context)
        {
            if (context == null)
                return false;

            if (context.Buffer == null ||
                context.Length <= 0 ||
                context.Length > context.Buffer.Length)
            {
                return false;
            }

            return _running && IsConnected;
        }

        #endregion

        #region Connection

        private void OnConnect(IAsyncResult asyncResult)
        {
            try
            {
                var socket = InternalSocket;
                if (socket == null)
                    return;

                socket.EndConnect(asyncResult);

                if (!socket.Connected)
                    return;

                _disconnectedRaised = 0;
                _running = true;

                _senderThread = new Thread(SenderWork)
                {
                    IsBackground = true,
                    Name = $"{GetType().Name}.Sender"
                };

                _listenerThread = new Thread(ListenerWork)
                {
                    IsBackground = true,
                    Name = $"{GetType().Name}.Listener"
                };

                _senderThread.Start();
                _listenerThread.Start();

                OnConnected?.Invoke();
            }
            catch (SocketException exception)
            {
                OnSocketException?.Invoke(exception);
                Stop();
            }
            catch
            {
                Stop();
                throw;
            }
        }

        #endregion

        #region Workers

        private void SenderWork()
        {
            try
            {
                while (_running)
                {
                    if (!SendMessages.TryDequeue(out var context))
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    SendAll(context);
                }
            }
            catch (SocketException exception)
            {
                if (_running)
                    OnSocketException?.Invoke(exception);
            }
            finally
            {
                HandleWorkerStopped();
            }
        }

        private void ListenerWork()
        {
            var readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSizeAtOnce);

            try
            {
                while (_running)
                {
                    var socket = InternalSocket;
                    if (socket == null)
                        break;

                    var received = socket.Receive(readBuffer, 0, ReadBufferSizeAtOnce, SocketFlags.None);

                    // TCP에서 Receive() == 0은 원격지의 정상적인 연결 종료를 의미한다.
                    if (received == 0)
                        break;

                    OnReceive(readBuffer, 0, received);
                }
            }
            catch (SocketException exception)
            {
                if (_running)
                    OnSocketException?.Invoke(exception);
            }
            catch (ObjectDisposedException)
            {
                // Stop()에 의해 Socket이 종료된 경우.
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuffer);

                HandleWorkerStopped();
            }
        }

        private void SendAll(ISocketBuffer context)
        {
            var socket = InternalSocket;
            if (socket == null)
                return;

            var offset = 0;
            var remaining = context.Length;

            while (_running && remaining > 0)
            {
                var sent = socket.Send(context.Buffer, offset, remaining, SocketFlags.None);

                if (sent <= 0)
                    throw new SocketException((int)SocketError.ConnectionReset);

                offset += sent;
                remaining -= sent;
            }
        }

        private void HandleWorkerStopped()
        {
            if (!_running)
                return;

            _running = false;

            ShutdownSocket();
            RaiseDisconnected();
        }

        #endregion

        #region Helpers

        private static IPAddress GetIPv4Address(string address)
        {
            if (IPAddress.TryParse(address, out var parsedAddress) &&
                parsedAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                return parsedAddress;
            }

            var addresses = Dns.GetHostAddresses(address);

            foreach (var ipAddress in addresses)
            {
                if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
                    return ipAddress;
            }

            return null;
        }

        private void ShutdownSocket()
        {
            var socket = InternalSocket;
            if (socket == null)
                return;

            try
            {
                if (socket.Connected)
                    socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // 이미 연결이 끊어진 경우 무시합니다.
            }
            catch (ObjectDisposedException)
            {
                // 이미 정리된 Socket입니다.
            }
        }

        private void DisposeSocket()
        {
            var socket = InternalSocket;
            InternalSocket = null;

            if (socket == null)
                return;

            try
            {
                socket.Close();
            }
            catch (ObjectDisposedException)
            {
            }

            socket.Dispose();
        }

        private static void JoinThread(Thread thread)
        {
            if (thread == null ||
                !thread.IsAlive ||
                thread == Thread.CurrentThread)
            {
                return;
            }

            thread.Join();
        }

        private void RaiseDisconnected()
        {
            if (Interlocked.Exchange(ref _disconnectedRaised, 1) != 0)
                return;

            OnDisconnected?.Invoke();
        }

        #endregion
    }
}