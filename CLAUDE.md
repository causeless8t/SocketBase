# CLAUDE.md

이 문서는 SocketBase 저장소에서 작업하는 AI 코딩 도구를 위한 프로젝트 지침이다. 변경 전 실제 코드와 `package.json`을 먼저 읽고, 문서나 과거 설계안보다 현재 구현을 우선한다.

## 프로젝트 개요

SocketBase는 Unity 프로젝트에서 애플리케이션별 프로토콜을 구현하기 위한 경량 소켓 기반 패키지다.

이 프로젝트의 핵심 책임은 다음과 같다.

- 소켓 생성과 연결 수명 주기 관리
- 백그라운드 송신·수신 작업
- 스레드 안전 전송 큐
- 부분 전송 처리
- 원시 수신 바이트 전달
- 정상적인 소켓 및 작업 스레드 종료

패킷 프레이밍, 직렬화, 명령 라우팅, 재연결 정책, 메인 스레드 디스패치는 사용하는 프로젝트 또는 상속 클래스의 책임이다. SocketBase를 범용 네트워크 프레임워크로 확장하지 않는다.

## 기술 기준

- 최소 Unity 버전: 2022.3
- 런타임 언어: C#
- 네임스페이스: `Causeless3t.Network`
- Runtime 어셈블리: `Causeless3t.SocketBase`
- UnityEngine 의존성 없음
- 외부 패키지 의존성 없음
- 패키지 버전은 Semantic Versioning을 따른다.

## 현재 구조

```text
SocketBase/
├── Runtime/
│   ├── BaseSocket.cs
│   ├── BaseSocket.cs.meta
│   ├── Socket.asmdef
│   └── Socket.asmdef.meta
├── Samples~/
│   └── Socket.cs
├── CHANGELOG.md
├── LICENSE
├── README.md
└── package.json
```

### 알려진 구조 불일치

`package.json`은 샘플 경로를 `Samples~/SimpleTcp`로 선언하지만 현재 파일은 `Samples~/Socket.cs`에 있다. 샘플 구조를 수정할 때는 다음 중 하나로 반드시 통일한다.

- 권장: 파일을 `Samples~/SimpleTcp/Socket.cs`로 이동하고 해당 폴더에 샘플 설명을 추가한다.
- 대안: `package.json`의 samples path를 실제 경로에 맞게 수정한다.

샘플 import가 검증되기 전에는 README에서 Package Manager를 통한 샘플 import가 정상 동작한다고 단정하지 않는다.

## 핵심 타입

### `ISocketBuffer`

전송할 배열과 유효 길이를 제공한다.

```csharp
public interface ISocketBuffer
{
    byte[] Buffer { get; }
    int Length { get; }
}
```

계약:

- `Buffer`는 null이면 안 된다.
- `Length`는 1 이상, `Buffer.Length` 이하여야 한다.
- 현재 전송은 배열의 인덱스 0부터 `Length`만큼 수행한다.
- 전송 오프셋이 필요해지면 인터페이스와 전송 구현을 함께 변경하고 호환성 영향을 기록한다.

### `BaseSocket`

연결, 전송 큐, 송수신 작업 스레드와 종료를 담당하는 추상 기반 클래스다.

상속 클래스는 반드시 다음 메서드를 구현한다.

```csharp
protected abstract void OnReceive(byte[] buffer, int offset, int count);
```

`OnReceive`의 버퍼는 `ArrayPool<byte>`에서 임대된다. 메서드가 반환된 뒤 참조를 저장하거나 다른 스레드로 넘기지 않는다. 데이터 보관이 필요하면 `offset`과 `count` 범위만 복사한다.

## 변경 시 지켜야 할 계약

### 스레드 안전성

- 메인 스레드와 송신 스레드가 함께 접근하는 전송 큐는 thread-safe 컬렉션을 유지한다.
- 공유 상태를 추가하면 접근 스레드와 동기화 방식을 명시한다.
- `Thread.Abort`를 사용하지 않는다.
- `Stop()`은 현재 스레드 자신을 `Join`하지 않아야 한다.
- 종료 중 발생한 정상적인 `ObjectDisposedException`은 오류로 보고하지 않는다.
- 연결 해제 이벤트는 한 연결 주기당 한 번만 발생해야 한다.

### 송신

- `Socket.Send`가 요청 길이보다 적은 바이트를 반환할 수 있다는 전제를 유지한다.
- 전체 `ISocketBuffer.Length`가 전송될 때까지 반복하거나 연결 오류로 종료한다.
- 유효하지 않은 버퍼는 큐에 추가하지 않는다.
- 큐에 넣은 뒤 호출자가 버퍼 내용을 변경하지 않는다는 현재 계약을 문서화한다. 소유권 모델을 바꾸면 API와 샘플도 함께 수정한다.

### 수신

- `Socket.Receive`가 반환한 실제 길이만 `OnReceive`에 전달한다.
- TCP의 `Receive == 0`은 원격지의 정상적인 연결 종료로 처리한다.
- TCP의 수신 단위와 애플리케이션 패킷 단위를 동일시하지 않는다.
- Runtime은 특정 패킷 헤더나 직렬화 방식을 강제하지 않는다.
- `ArrayPool` 버퍼는 `finally`에서 반드시 반환한다.

### 이벤트

다음 이벤트와 `OnReceive`는 Unity 메인 스레드가 아닌 작업 스레드에서 호출될 수 있다.

- `OnConnected`
- `OnDisconnected`
- `OnSocketException`
- `OnReceive`

Runtime에서 Unity API를 호출하거나 Unity 전용 디스패처를 직접 추가하지 않는다. 메인 스레드 전달은 소비자 프로젝트가 선택하도록 유지한다.

### 연결과 종료

- 새 연결을 시작하기 전에 이전 연결과 작업 스레드를 정리한다.
- 소켓 종료와 Dispose는 여러 번 호출되어도 안전해야 한다.
- `OnDisconnected` 중복 호출을 방지한다.
- 연결 실패 시 생성된 소켓을 남기지 않는다.
- 동기화나 종료 문제를 숨기기 위해 무기한 대기 또는 임의의 긴 Sleep을 추가하지 않는다.

## TCP와 UDP 범위

현재 구현은 TCP 동작을 중심으로 설계되어 있다. `Connect`는 `ProtocolType.Tcp`와 `ProtocolType.Udp`를 허용하지만 다음을 주의한다.

- TCP 전용 의미인 스트림 종료와 패킷 프레이밍을 UDP에 그대로 적용하지 않는다.
- `Samples~/Socket.cs`는 TCP 길이 프리픽스 프로토콜 예제다.
- UDP 동작을 변경하거나 공식 지원으로 명시하기 전에는 별도 테스트와 API 검토가 필요하다.
- TCP 전용 패키지로 범위를 좁히는 변경은 공개 API 변경이므로 버전과 CHANGELOG에 반영한다.

## Simple TCP 샘플 규칙

샘플은 다음 프레임을 사용한다.

```text
[Length: 4 bytes][Command: 4 bytes][Payload: N bytes]
```

- 정수는 Big Endian이다.
- Length는 Command와 Payload의 합이다.
- 분할 수신과 여러 패킷의 동시 수신을 모두 처리해야 한다.
- 잘못된 길이를 거부해야 한다.
- 패킷 크기 상한을 추가할 경우 송신과 수신 양쪽에 동일한 기준을 적용한다.
- 이벤트로 전달하는 `ArraySegment<byte>`가 내부 누적 버퍼를 참조한다면 수명 제한을 XML 문서와 README에 유지한다.

## 코드 스타일

- 기존 파일의 C# 스타일과 네임스페이스 구조를 따른다.
- public/protected API에는 동작, 스레드, 소유권 계약이 드러나는 XML 문서를 작성한다.
- 의미 없는 래퍼, 미래 사용을 가정한 계층, 사용되지 않는 인터페이스를 추가하지 않는다.
- 현재 규모에서는 Core, Transport, Protocol 등으로 폴더를 과도하게 분리하지 않는다.
- UnityEngine 참조를 추가하지 않는다.
- 외부 비동기 라이브러리나 로깅 라이브러리를 추가하지 않는다.
- 예외를 무조건 삼키지 않는다. 종료 과정에서 예상 가능한 예외만 제한적으로 처리한다.
- 코드와 주석이 충돌하면 코드를 고치거나 주석을 함께 갱신한다.

## 패키지 관리

- `package.json`의 `name`은 `com.causeless3t.socketbase`를 유지한다.
- 최소 Unity 버전을 변경하면 실제 사용 API와 함께 검토한다.
- Runtime asmdef의 `noEngineReferences`는 `true`를 유지한다.
- 공개 API가 호환되지 않게 바뀌면 major version 증가를 검토한다.
- 사용자에게 보이는 변경은 `CHANGELOG.md`에 기록한다.
- Git URL 루트에서 UPM 패키지로 설치 가능한 구조를 유지한다.
- Unity가 추적하는 파일이나 폴더를 이동·추가할 때 `.meta` 파일을 함께 관리한다.
- `.DS_Store` 등 운영체제 생성 파일을 커밋하지 않는다.

## 변경 절차

1. `Runtime/BaseSocket.cs`, 샘플, `package.json`, README를 읽어 현재 계약을 확인한다.
2. 변경이 Runtime 책임인지 애플리케이션 프로토콜 책임인지 구분한다.
3. public/protected API 호환성과 스레드·버퍼 소유권 영향을 검토한다.
4. 가장 작은 범위로 구현한다.
5. 관련 샘플과 문서를 함께 갱신한다.
6. 사용자에게 보이는 변경은 CHANGELOG에 기록한다.
7. Unity 2022.3 기준 컴파일과 UPM 설치를 검증한다.

## 검증 체크리스트

코드 변경 후 가능한 범위에서 다음을 확인한다.

- 호스트 이름과 IPv4 주소 연결
- 연결 성공 이벤트
- 연결 실패 시 예외 이벤트와 자원 정리
- 작은 메시지 전송
- 송신 버퍼보다 큰 메시지의 부분 전송
- 여러 스레드에서 동시에 Send 호출
- 분할되어 도착한 TCP 패킷 재조립
- 한 번에 도착한 여러 TCP 패킷 분리
- 원격 정상 종료 시 연결 해제 이벤트 1회
- 로컬 `Stop()` 반복 호출
- 이벤트 콜백 내부에서 `Stop()` 호출
- 재연결 시 이전 스레드와 큐 정리
- 수신 버퍼의 유효 범위와 수명 준수
- Runtime asmdef의 UnityEngine 비의존성
- Git URL을 이용한 UPM 설치
- `package.json`의 샘플 경로와 실제 디렉터리 일치

자동화된 테스트가 아직 없다면 검증하지 않은 동작을 검증 완료라고 표현하지 않는다.

## 금지 사항

- TCP 수신 한 번을 패킷 하나로 가정하지 않는다.
- `Queue<T>`로 전송 큐를 되돌리지 않는다.
- `Thread.Abort`를 사용하지 않는다.
- 수신 배열 전체를 실제 데이터로 간주하지 않는다.
- 풀링된 수신 버퍼를 `OnReceive` 이후 보관하지 않는다.
- Runtime에 UnityEngine, UniTask 또는 특정 직렬화 패키지 의존성을 추가하지 않는다.
- 요청 없이 프로토콜, 재연결, 암호화 등 상위 기능을 Runtime에 포함하지 않는다.
- 실제 테스트 없이 멀티플랫폼 또는 UDP 완전 지원을 주장하지 않는다.
