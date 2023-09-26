using System;
using System.Net.Sockets;

namespace Causeless3t.Network.Sample
{
    public sealed class Socket : BaseSocket
    {
	    private struct MessageBuffer : IBuffer
	    {
		    public byte[] Array { get; }
		    public int Count { get; }
		    public int Offset { get; set; }
		    public MessageBuffer(int count)
		    {
			    Array = new byte[count];
			    Count = count;
			    Offset = 0;
		    }
	    }
	    
        private struct ReceiveContext
		{
			public int Command;
			public ArraySegment<byte> Message;
		}

		private static ReceiveContext _convertedContext;

		/// <summary>
		/// 파싱된 메시지를 넘겨주는 이벤트.
		/// </summary>
		public event Action<int, ArraySegment<byte>> OnPacketReceived;
		
		private static readonly int PacketTimeout = 30000;
		
		/// <summary>
		/// 지정된 버퍼 사이즈로 소켓을 생성합니다. 기본사이즈는 보내기 8192, 받기 65535입니다.
		/// </summary>
		/// <param name="sendsize">보내기 최대 버퍼 사이즈</param>
		/// <param name="receivesize">받기 최대 버퍼 사이즈</param>
		public Socket(int sendsize = -1, int receivesize = -1) : base(sendsize, receivesize) { }
		
		public override bool IsConnected => InternalSocket?.Connected ?? false;

		#region Public Functions
		/// <summary>
		/// 지정된 주소, 포트로 연결을 수립합니다. 기본 타임아웃은 10초로 설정되어 있습니다.
		/// </summary>
		/// <param name="address">네트워크 주소</param>
		/// <param name="port">포트</param>
		/// <param name="type">프로토콜 방식. 기본은 TCP</param>
		public override void Connect(string address, int port, ProtocolType type = ProtocolType.Tcp)
		{
			OnReceivedBytes += ReceiveProcess;
			
			base.Connect(address, port, type);
			InternalSocket!.ReceiveTimeout   = InternalSocket.SendTimeout = PacketTimeout; // default : 0(unlimited)
			InternalSocket.SendBufferSize    = SendBufferSize;                             // default : 8192
			InternalSocket.ReceiveBufferSize = ReceiveBufferSize;                          // default : 65535
		}

		/// <summary>
		/// 내부 소켓의 타임아웃 시간을 정합니다.
		/// </summary>
		/// <param name="time">타임아웃 시간(ms)</param>
		public void SetTimeout(int time)
		{
			if (InternalSocket == null) return;
			InternalSocket.ReceiveTimeout = InternalSocket.SendTimeout = time;
		}

		/// <summary>
		/// 연결을 중단합니다.
		/// </summary>
		public override void Stop()
		{
			OnReceivedBytes -= ReceiveProcess;
			base.Stop();
		}
		
		/// <summary>
		/// 메시지를 보냅니다.
		/// </summary>
		/// <param name="command">커맨드</param>
		/// <param name="buffer">페이로드</param>
		/// <returns></returns>
		public bool SendMessage(int command, byte[] buffer)
		{
			MessageBuffer dst = new MessageBuffer(buffer.Length + 4);
			Buffer.BlockCopy(BitConverter.GetBytes(command), 0, dst.Array, dst.Offset, 4);
			if (BitConverter.IsLittleEndian)
				Array.Reverse((Array) dst.Array, dst.Offset, 4);
			dst.Offset += 4;
			Buffer.BlockCopy(buffer, 0, dst.Array, dst.Offset, buffer.Length);
			return Send(dst);
		}
		#endregion Public Functions
		
		private void ReceiveProcess(byte[] context)
		{
			if (context.Length == 0) return;
			
			int srcOffset = 0;
			if (BitConverter.IsLittleEndian)
				Array.Reverse((Array) context, srcOffset, 4);
			var command = BitConverter.ToInt32(context, srcOffset);
			srcOffset += 4;
			
			int count = context.Length - srcOffset;
			var payload = new ArraySegment<byte>(context, srcOffset, count);
			
			_convertedContext.Command   = command;
			_convertedContext.Message   = payload;
			
			OnPacketReceived?.Invoke(_convertedContext.Command, _convertedContext.Message);
		}

		protected override bool Send(IBuffer context)
		{
			var result = CanSend(context);
			if (!result) return false;
			SendMessages!.Enqueue(context);
			return true;
		}
    }
}

