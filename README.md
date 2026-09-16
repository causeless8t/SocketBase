# SocketBase

Unity 프로젝트에서 애플리케이션별 네트워크 프로토콜을 구현할 수 있도록 만든 경량 소켓 기반 패키지입니다.

연결 수명 주기, 백그라운드 송수신, 스레드 안전 전송 큐와 부분 전송 처리는 `BaseSocket`이 담당합니다. 패킷 형식과 수신 데이터 파싱은 이를 상속하는 클래스에서 정의합니다.

## 주요 기능

- 호스트 이름 또는 IPv4 주소를 이용한 비동기 연결
- 송신·수신 전용 백그라운드 스레드
- `ConcurrentQueue` 기반의 스레드 안전 전송 큐
- 한 번의 `Socket.Send`로 모든 데이터가 전송되지 않는 경우를 고려한 부분 전송 처리
- 실제 수신 길이를 포함한 원시 바이트 전달
- `ArrayPool<byte>`를 이용한 수신 버퍼 재사용
- 연결, 연결 해제, `SocketException` 이벤트
- `IDisposable`과 명시적인 `Stop()` 지원
- UnityEngine에 의존하지 않는 순수 C# Runtime 어셈블리

## 요구 사항

- Unity 2022.3 이상
- API Compatibility Level: .NET Standard 2.1 또는 호환 프로파일

## 설치

Unity Package Manager에서 **Add package from git URL...**을 선택한 뒤 다음 URL을 입력합니다.

```text
https://github.com/causeless8t/SocketBase.git
```

특정 버전을 고정하려면 Git 태그를 URL 뒤에 추가할 수 있습니다.

```text
https://github.com/causeless8t/SocketBase.git#1.1.0
```

또는 프로젝트의 `Packages/manifest.json`에 직접 추가합니다.

```json
{
  "dependencies": {
    "com.causeless3t.socketbase": "https://github.com/causeless8t/SocketBase.git#1.1.0"
  }
}
```

## 기본 사용법

`BaseSocket`은 패킷 규격을 강제하지 않습니다. 애플리케이션에 맞는 전송 버퍼와 수신 파서를 구현해 사용합니다.

```csharp
using System;
using Causeless3t.Network;

public sealed class GameSocket : BaseSocket
{
    private sealed class SocketBuffer : ISocketBuffer
    {
        public byte[] Buffer { get; }
        public int Length => Buffer.Length;

        public SocketBuffer(byte[] buffer)
        {
            Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        }
    }

    public event Action<byte[]> BytesReceived;

    public bool SendBytes(byte[] bytes)
    {
        return Send(new SocketBuffer(bytes));
    }

    protected override void OnReceive(byte[] buffer, int offset, int count)
    {
        // buffer는 메서드 반환 후 ArrayPool로 돌아가므로,
        // 외부에 전달하거나 보관하려면 유효 범위만 복사해야 합니다.
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        BytesReceived?.Invoke(copy);
    }
}
```

연결 이벤트를 등록하고 서버에 연결합니다.

```csharp
var socket = new GameSocket();

socket.OnConnected += () =>
{
    // 이 콜백은 Unity 메인 스레드가 아닐 수 있습니다.
};

socket.OnSocketException += exception =>
{
    // 로그 출력이나 재연결 정책은 사용하는 프로젝트에서 결정합니다.
};

socket.OnDisconnected += () =>
{
    // 이 콜백은 Unity 메인 스레드가 아닐 수 있습니다.
};

socket.Connect("127.0.0.1", 9000);
```

사용이 끝나면 연결과 작업 스레드를 정리합니다.

```csharp
socket.Stop();
// 또는
socket.Dispose();
```

## Simple TCP 샘플

`Samples~/Socket.cs`에는 TCP 스트림에서 패킷 경계를 복원하는 예제가 포함되어 있습니다.

샘플 프로토콜은 다음 형식을 사용합니다.

| 필드 | 크기 | 설명 |
| --- | ---: | --- |
| Length | 4 bytes | Command와 Payload를 합한 바이트 수 |
| Command | 4 bytes | 메시지 식별자 |
| Payload | N bytes | 메시지 데이터 |

정수는 Network Byte Order(Big Endian)로 기록합니다.

```text
[Length: 4][Command: 4][Payload: N]
```

TCP의 한 번의 `Receive`는 하나의 패킷과 일치하지 않을 수 있습니다. 샘플은 분할 수신과 여러 패킷의 동시 수신을 모두 처리하도록 누적 버퍼에서 완성된 패킷만 파싱합니다.

> 현재 저장소의 실제 샘플 위치는 `Samples~/Socket.cs`입니다. `package.json`에 선언된 `Samples~/SimpleTcp` 경로와 일치하도록 샘플 디렉터리를 정리하기 전까지는 Package Manager의 Samples Import 대신 소스 파일을 참고하세요.

## 스레드와 버퍼 계약

### 콜백 스레드

`OnConnected`, `OnDisconnected`, `OnSocketException`, `OnReceive`는 작업 스레드에서 호출될 수 있습니다. 콜백에서 Unity API를 직접 호출하지 말고, 필요한 작업을 메인 스레드 디스패처나 프로젝트의 메시지 큐로 전달해야 합니다.

### 수신 버퍼 수명

`OnReceive(byte[] buffer, int offset, int count)`의 `buffer`는 `ArrayPool<byte>`에서 임대한 배열입니다.

- 유효 데이터는 `offset`부터 `count` 바이트입니다.
- `OnReceive`가 반환된 후 버퍼 참조를 보관하면 안 됩니다.
- 비동기 처리나 외부 저장이 필요하면 유효 범위만 복사합니다.

### TCP 패킷 경계

`BaseSocket`은 수신한 바이트를 그대로 전달하며 메시지 경계를 해석하지 않습니다. TCP를 사용할 때는 길이 프리픽스, 구분자 또는 고정 길이 등 명시적인 프레이밍 규칙을 상속 클래스에서 구현해야 합니다.

## 설계 범위

SocketBase는 다음 기능을 애플리케이션에 맡깁니다.

- 직렬화 및 역직렬화
- 패킷 프레이밍과 명령 라우팅
- 재연결, 재시도 및 백오프 정책
- 인증과 암호화
- Unity 메인 스레드 디스패치
- 요청·응답 매칭
- 하트비트와 연결 상태 판정

현재 API는 TCP 사용을 중심으로 설계되고 검증되었습니다. `Connect`가 `ProtocolType.Udp`도 허용하지만 Simple TCP 샘플의 프레이밍과 동작을 UDP에 그대로 적용해서는 안 됩니다.

## 프로젝트 구조

```text
SocketBase/
├── Runtime/
│   ├── BaseSocket.cs
│   └── Socket.asmdef
├── Samples~/
│   └── Socket.cs
├── CHANGELOG.md
├── LICENSE
├── README.md
└── package.json
```

## 라이선스

이 프로젝트는 [MIT License](LICENSE)를 따릅니다.
