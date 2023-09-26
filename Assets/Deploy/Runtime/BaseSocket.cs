using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace Causeless3t.Network
{
	public interface IBuffer
	{
		public byte[] Array { get; }
		public int Count { get; }
		public int Offset { get; set; }
	}
	
    public abstract class BaseSocket
    {
	    private static readonly int DefaultSendBufferSize    = 8192;  // 소켓 내부 버퍼사이즈(보내기)
	    private static readonly int DefaultReceiveBufferSize = 65535; // 소켓 내부 버퍼사이즈(받기)
	    private static readonly int ReadBufferSizeAtOnce     = 8192;  // 한번에 읽을 수 있는 버퍼사이즈

	    #region Events
	    /// <summary>
	    /// 연결이 수립되면 호출되는 이벤트
	    /// </summary>
	    public event Action OnConnected;
	    /// <summary>
	    /// byte[] 메시지를 수신할때마다 호출되는 이벤트
	    /// </summary>
	    protected event Action<byte[]> OnReceivedBytes;
	    /// <summary>
	    /// 소켓관련 Exception이 발생하면 호출되는 이벤트
	    /// </summary>
	    public event Action<SocketException> OnSocketException;
	    /// <summary>
	    /// 소켓이 Disconnect 될때 발생되는 이벤트
	    /// </summary>
	    public event Action OnDisconnected;
	    #endregion Events

	    #region Properties
	    /// <summary>
	    /// 내부 소켓 연결 상태
	    /// </summary>
	    public abstract bool IsConnected { get; }
	    #endregion Properties

	    #region Variables
	    protected Socket InternalSocket;
	    private Thread _listenerThread;
	    private Thread _senderThread;
	    protected readonly Queue<IBuffer> SendMessages = new();
	    
	    protected int SendBufferSize;
	    protected int ReceiveBufferSize;
	    protected int ListenerThreadSleepTime = 1;
	    protected int SenderThreadSleepTime   = 10;
	    #endregion Variables

	    private static IPAddress GetIP4Address(string address)
		{
			var addresses = Dns.GetHostAddresses(address);
			IPAddress selected = null;
			for (int index = 0; index < addresses.Length; ++index)
			{
				IPAddress ipAddress = addresses[index];
				if (ipAddress is not { AddressFamily: AddressFamily.InterNetwork }) continue;

				if (selected == null)
					selected = ipAddress;
				else if (ipAddress.ToString().Equals(address))
					selected = ipAddress;
			}
			return selected;
		}

		/// <summary>
		/// 지정된 버퍼 사이즈로 소켓을 생성합니다. 기본사이즈는 보내기 8192, 받기 65535입니다.
		/// </summary>
		/// <param name="sendBuffer">최대 보내기 버퍼 사이즈</param>
		/// <param name="receiveBuffer">최대 받기 버퍼 사이즈</param>
		protected BaseSocket(int sendBuffer = -1, int receiveBuffer = -1)
		{
			if (sendBuffer == -1)
				SendBufferSize = DefaultSendBufferSize;
			if (receiveBuffer == -1)
				ReceiveBufferSize = DefaultReceiveBufferSize;
		}
		
		#region Public Functions
		/// <summary>
		/// 주어진 주소와 포트로 연결을 시도합니다.
		/// </summary>
		/// <param name="address">네트워크 주소</param>
		/// <param name="port">포트</param>
		/// <param name="type">프로토콜 방식. 기본은 TCP</param>
		public virtual void Connect(string address, int port, ProtocolType type = ProtocolType.Tcp)
		{
			IPAddress  ipAddress = GetIP4Address(address);
			IPEndPoint endPoint  = new IPEndPoint(ipAddress, port);
			if (type != ProtocolType.Tcp && type != ProtocolType.Udp)
			{
				Debug.LogError($"{type.ToString()} is unsupported Type! Cannot connected remote server.");
				return;
			}

			if (type == ProtocolType.Tcp)
				InternalSocket = new Socket(ipAddress.AddressFamily, SocketType.Stream, type);
			else
				InternalSocket = new Socket(ipAddress.AddressFamily, SocketType.Dgram, type);

			try
			{
				InternalSocket.BeginConnect(endPoint, OnConnect, null);
			}
			catch (Exception e)
			{
				Debug.LogError($"Error while accepting connection: {e.Message}\n{e.StackTrace}");
			}
		}
		
		/// <summary>
		/// 연결을 중단합니다.
		/// </summary>
		public virtual void Stop()
		{
			SendMessages!.Clear();
			if (_senderThread is { IsAlive: true })
			{
				_senderThread.Abort();
				_senderThread = null;
			}
			if (_listenerThread is { IsAlive: true })
			{
				_listenerThread.Abort();
				_listenerThread = null;
			}

			try
			{
				if (InternalSocket?.Connected ?? false)
					InternalSocket?.Disconnect(false);
				InternalSocket?.Close();
				InternalSocket?.Dispose();
			}
			catch (ObjectDisposedException e)
			{
				Debug.Log($"{e.Message}\n{e.StackTrace}");
			}
		}
		#endregion Public Functions

		private void OnConnect(IAsyncResult ar)
		{
			if (InternalSocket.Connected == false || ar == null)
				return;

			InternalSocket.EndConnect(ar);

			_senderThread = new Thread(SenderWork);
			_senderThread.Start();
			_listenerThread = new Thread(ListenerWork);
			_listenerThread.Start();

			OnConnected?.Invoke();
		}

		#region Threads
		private void SenderWork()
		{
			while (!IsConnected)
				Thread.Yield();
			while (IsConnected)
			{
				try
				{
					if (InternalSocket == null) break;
					while (SendMessages.TryDequeue(out var context))
					{
						if (context == null) continue;
						InternalSocket.Send(context.Array!, SocketFlags.None);
					}
					Thread.Sleep(SenderThreadSleepTime);
				}
				catch (SocketException sock_exc)
				{
					Debug.LogError($"{sock_exc.Message}\n{sock_exc.StackTrace}");
					OnSocketException?.Invoke(sock_exc);
				}
				catch (ThreadAbortException thread_exc)
				{
					Debug.Log($"{thread_exc.Message}\n{thread_exc.StackTrace}");
				}
				catch (Exception exc)
				{
					Debug.LogError($"{exc.Message}\n{exc.StackTrace}");
				}
			}
		}
		
		private void ListenerWork()
		{
			while (!IsConnected)
				Thread.Yield();
			while (IsConnected)
			{
				try
				{
					if (InternalSocket == null) break;
					var readData = ArrayPool<byte>.Shared.Rent(ReadBufferSizeAtOnce);
					var nRecv = InternalSocket.Receive(readData, 0, readData.Length, SocketFlags.None);
					if (nRecv == 0)
					{
						ArrayPool<byte>.Shared.Return(readData);
						Thread.Sleep(ListenerThreadSleepTime);
						continue;
					}
					
					OnReceivedBytes?.Invoke(readData);
					ArrayPool<byte>.Shared.Return(readData);
					Thread.Sleep(ListenerThreadSleepTime);
				}
				catch (SocketException sock_exc)
				{
					Debug.LogError($"{sock_exc.Message}\n{sock_exc.StackTrace}");
					OnSocketException?.Invoke(sock_exc);
				}
				catch (ThreadAbortException thread_exc)
				{
					Debug.Log($"{thread_exc.Message}\n{thread_exc.StackTrace}");
				}
				catch (Exception exc)
				{
					Debug.LogError($"{exc.Message}\n{exc.StackTrace}");
				}
			}
			OnDisconnected?.Invoke();
		}
		#endregion Threads
		
		protected bool CanSend(IBuffer context)
		{
			if (context is not { Count: > 0 }) return false;
			if (IsConnected == false)
			{
				Debug.LogError("Cannot send message. It was disconnected.");
				return false;
			}
			return true;
		}

		protected abstract bool Send(IBuffer context);
    }
}
