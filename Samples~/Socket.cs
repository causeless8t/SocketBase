using System;
using System.Buffers.Binary;
using System.Net.Sockets;

namespace Causeless3t.Network.Sample
{
    public sealed class Socket : BaseSocket
    {
        private const int LengthFieldSize = sizeof(int);
        private const int CommandFieldSize = sizeof(int);
        private const int HeaderSize = LengthFieldSize + CommandFieldSize;

        private const int DefaultPacketTimeout = 30000;
        private const int InitialReceiveBufferSize = 8192;

        private sealed class MessageBuffer : ISocketBuffer
        {
            public byte[] Buffer { get; }
            public int Length { get; }

            public MessageBuffer(byte[] buffer)
            {
                Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
                Length = buffer.Length;
            }
        }

        private byte[] _receiveBuffer = new byte[InitialReceiveBufferSize];
        private int _receivedLength;

        /// <summary>
        /// 하나의 완전한 패킷이 파싱되었을 때 호출됩니다.
        ///
        /// payload는 내부 Receive Buffer를 참조하므로
        /// 이벤트 호출 이후 보관하려면 복사해야 합니다.
        /// </summary>
        public event Action<int, ArraySegment<byte>> OnPacketReceived;

        public Socket(int sendBuffer = -1, int receiveBuffer = -1) : base(sendBuffer, receiveBuffer)
        {
        }

        #region Public

        public override void Connect(string address, int port, ProtocolType type = ProtocolType.Tcp)
        {
            base.Connect(address, port, type);

            if (InternalSocket != null)
            {
                InternalSocket.ReceiveTimeout = DefaultPacketTimeout;
                InternalSocket.SendTimeout = DefaultPacketTimeout;
            }
        }

        public void SetTimeout(int milliseconds)
        {
            if (milliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(milliseconds));

            var socket = InternalSocket;
            if (socket == null)
                return;

            socket.ReceiveTimeout = milliseconds;
            socket.SendTimeout = milliseconds;
        }

        /// <summary>
        /// [Length][Command][Payload] 형식으로 패킷을 전송합니다.
        ///
        /// Length에는 Command + Payload의 크기가 기록됩니다.
        /// 모든 정수는 Network Byte Order(Big Endian)를 사용합니다.
        /// </summary>
        public bool SendMessage(int command, byte[] payload)
        {
            payload ??= Array.Empty<byte>();

            var bodyLength = CommandFieldSize + payload.Length;
            var packetLength = LengthFieldSize + bodyLength;

            var packet = new byte[packetLength];

            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, LengthFieldSize), bodyLength);

            BinaryPrimitives.WriteInt32BigEndian(
                packet.AsSpan(LengthFieldSize, CommandFieldSize),
                command);

            if (payload.Length > 0)
            {
                Buffer.BlockCopy(payload, 0, packet, HeaderSize, payload.Length);
            }

            return Send(new MessageBuffer(packet));
        }

        public override void Stop()
        {
            base.Stop();

            _receivedLength = 0;
        }

        #endregion

        #region Receive

        protected override void OnReceive(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0)
                return;

            EnsureReceiveBufferCapacity(_receivedLength + count);

            Buffer.BlockCopy(buffer, offset, _receiveBuffer, _receivedLength, count);

            _receivedLength += count;

            ParsePackets();
        }

        private void ParsePackets()
        {
            var readOffset = 0;

            while (true)
            {
                var available = _receivedLength - readOffset;

                if (available < HeaderSize)
                    break;

                var bodyLength = BinaryPrimitives.ReadInt32BigEndian(
                    _receiveBuffer.AsSpan(
                        readOffset,
                        LengthFieldSize));

                if (bodyLength < CommandFieldSize)
                {
                    throw new InvalidOperationException(
                        $"Invalid packet length: {bodyLength}");
                }

                var packetLength = LengthFieldSize + bodyLength;

                if (available < packetLength)
                    break;

                var commandOffset = readOffset + LengthFieldSize;

                var command = BinaryPrimitives.ReadInt32BigEndian(
                    _receiveBuffer.AsSpan(
                        commandOffset,
                        CommandFieldSize));

                var payloadOffset = commandOffset + CommandFieldSize;
                var payloadLength = bodyLength - CommandFieldSize;

                var payload = new ArraySegment<byte>(_receiveBuffer, payloadOffset, payloadLength);

                OnPacketReceived?.Invoke(command, payload);

                readOffset += packetLength;
            }

            if (readOffset == 0)
                return;

            var remaining = _receivedLength - readOffset;

            if (remaining > 0)
            {
                Buffer.BlockCopy(_receiveBuffer, readOffset, _receiveBuffer, 0, remaining);
            }

            _receivedLength = remaining;
        }

        private void EnsureReceiveBufferCapacity(int requiredSize)
        {
            if (_receiveBuffer.Length >= requiredSize)
                return;

            var newSize = _receiveBuffer.Length;

            while (newSize < requiredSize)
                newSize *= 2;

            Array.Resize(ref _receiveBuffer, newSize);
        }

        #endregion
    }
}