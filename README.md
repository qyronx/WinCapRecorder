# WinCapRecorder

특정 창만 골라서 **화면 + 그 창의 소리만** 녹화하는 Windows용 프로그램입니다.

다른 프로그램 화면·소리가 섞이지 않으며, 전역 단축키로 시작/정지/일시정지/소리 토글이 가능합니다.

---

## 기능

| 기능 | 설명 |
|------|------|
| 창 단위 화면 캡처 | Windows Graphics Capture (WGC)로 선택한 창만 녹화 |
| 프로세스 단위 소리 캡처 | WASAPI Process Loopback — 해당 프로세스(및 자식) 소리만 녹음 |
| 녹화 중 소리 ON/OFF | 체크박스·단축키로 즉시 반영 (큐에 쌓인 오디오도 바로 버림) |
| 시작 / 정지 / 일시정지 / 재개 | UI 버튼 + 전역 단축키 |
| 전역 단축키 | 다른 창에 포커스가 있어도 동작 (`Ctrl+Shift+F9`~`F12` 기본값) |
| 고화질 MP4 | H.264 + AAC, 기본 약 **20 Mbps** / **30 FPS** |
| 창 리사이즈 대응 | 녹화 중 창 크기 변경 시 해상도 자동 맞춤 |
| 트레이 상주 | 창을 닫아도 트레이로 내려가 녹화 유지 |
| 단일 exe 배포 | self-contained — 사용자 PC에 .NET 설치 불필요 |

---

## 요구 사항

### 실행

- **Windows 10 버전 2004 (빌드 19041) 이상** 또는 **Windows 11**
  - 창별 캡처(WGC)와 프로세스 루프백 오디오가 이 버전부터 지원됩니다.
- x64

### 빌드

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (x64)
- Windows PC (WPF / Windows SDK API 사용)
- 최초 빌드 시 NuGet 패키지 다운로드를 위한 인터넷 연결

---

## 빌드 방법

1. 이 폴더 전체를 Windows PC로 복사합니다.
2. .NET 9 SDK를 설치합니다.
3. `build.bat`을 실행합니다.  
   (`bin` / `obj` 정리 → `dotnet restore` → `dotnet publish` self-contained)
4. 결과물:

```text
WinCapRecorder\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\WinCapRecorder.exe
```

이 exe 하나만 다른 PC에 복사해도 실행됩니다 (런타임 포함).

---

## 사용 방법

1. `WinCapRecorder.exe` 실행
2. **녹화할 창 선택**에서 대상 창 선택  
   - 닫혔거나 최소화된 창은 목록에 없습니다 → **새로고침**
   - 목록이 길면 드롭다운을 스크롤할 수 있습니다
3. 필요 시 **이 창의 소리도 녹화** 체크
4. (선택) 하단에서 전역 단축키 변경  
   - 칸 **클릭** → 파란 입력 대기 → 키 조합 입력  
   - **Esc** = 해당 단축키 해제
5. **녹화 시작** 또는 단축키로 시작
6. 저장 위치 기본값: `내 동영상\WinCapRecorder`  
   - **폴더 열기** / **폴더 변경** 버튼으로 확인·변경 가능

창을 닫으면 트레이로 이동합니다. 완전히 종료하려면 트레이 아이콘 우클릭 → 종료.

---

## 기본 전역 단축키

| 동작 | 기본 키 |
|------|---------|
| 녹화 시작 | `Ctrl+Shift+F9` |
| 정지 | `Ctrl+Shift+F10` |
| 일시정지 / 재개 | `Ctrl+Shift+F11` |
| 소리 녹화 토글 | `Ctrl+Shift+F12` |

설정은 `%AppData%\WinCapRecorder\settings.json` 에 저장됩니다.  
단축키가 다른 프로그램과 겹치면 상태바에 등록 실패 메시지가 뜹니다. 다른 조합으로 바꿔 주세요.

> `Ctrl+Alt+F*` 조합은 그래픽 드라이버 오버레이에 자주 가로채이므로, 기본값은 `Ctrl+Shift` 입니다.

---

## 기술 구성

```text
WinCapRecorder/
  App.xaml(.cs)           앱 진입점
  MainWindow.xaml(.cs)    UI, 단축키·트레이 연동
  RecordingController.cs  녹화 전체 흐름 (캡처 → 큐 → 인코더)
  Settings.cs             단축키·저장 경로 등
  Capture/                Windows Graphics Capture + D3D11 리드백
  Audio/                  프로세스 루프백 (NAudio WASAPI)
  Encode/                 Media Foundation Sink Writer (H.264 NV12 + AAC)
  Native/                 창 열거, RegisterHotKey, 권한 검사
  Controls/               HotkeyBox (단축키 입력 UI)
```

| 구분 | 사용 기술 |
|------|-----------|
| UI | WPF (`net9.0-windows10.0.19041.0`) |
| 화면 캡처 | Windows.Graphics.Capture, Vortice.Direct3D11 |
| 소리 캡처 | NAudio Process Loopback (48 kHz, 필요 시 float→PCM16) |
| 인코딩 | Media Foundation (소프트웨어 H.264 우선, 입력 NV12 / AAC) |
| 전역 단축키 | `RegisterHotKey` + `HwndSource` 훅 |

### 녹화 파라미터 (코드 상수)

- `RecordingController.TargetFps` = **30**
- `RecordingController.VideoBitrateBps` = **20_000_000** (20 Mbps)

용량을 줄이려면 `VideoBitrateBps` 값을 낮추면 됩니다.

---

## 흰 화면 / 무음이 나올 때

코드 버그가 아니라 **Windows 보안·정책**인 경우가 많습니다.

1. **권한 불일치**  
   대상 창이 **관리자 권한**으로 실행 중이면, 일반 권한인 WinCapRecorder에는 WGC가 빈 프레임·루프백이 무음을 반환합니다.  
   → `WinCapRecorder.exe`를 **관리자 권한으로 실행**해 보세요.  
   (녹화 시작 시 상태바에 경고가 뜰 수 있습니다.)

2. **DRM / 보호된 콘텐츠**  
   일부 스트리밍·하드웨어 오버레이 플레이어는 OS가 캡처를 의도적으로 막습니다. 우회할 수 없습니다.

3. **진단**  
   exe와 같은 폴더의 `crash.log` 를 확인하세요.  
   `CAPTURE_BLANK_WARNING`, `AUDIO_SILENT_WARNING`, `PRIVILEGE_MISMATCH`, `HOTKEY_REGISTER_FAIL` 등이 기록됩니다.

---

## 알아두면 좋은 점

- 소리 캡처는 **선택한 창의 프로세스 트리**만 포함합니다. 브라우저를 고르면 그 브라우저 소리는 들어가고, 다른 앱·시스템 알림은 들어가지 않습니다.
- 녹화 중 소리 토글은 인코더 큐의 오디오를 즉시 비우므로, 토글 반영 지연이 거의 없습니다.
- Windows N/KN 에디션은 Media Feature Pack이 없으면 AAC 인코딩이 실패할 수 있습니다.

---

## 빌드 오류가 날 경우

- **`Windows.Graphics.Capture` 등 WinRT 타입을 찾을 수 없음**  
  → `TargetFramework`가 `net9.0-windows10.0.19041.0` 인지 확인하세요.

- **`Color` / `KeyEventArgs` 모호한 참조**  
  → 프로젝트에 WPF + Windows Forms가 함께 있어 `System.Windows.*` 와 `System.Drawing` / `System.Windows.Forms` 가 겹칩니다.  
  → `System.Windows.Media.Color`, `System.Windows.Input.KeyEventArgs` 처럼 전체 이름으로 지정하세요.

- **Vortice / SharpGen 관련 오류**  
  → `WinCapRecorder.csproj`의 `Vortice.Direct3D11` 버전을 맞추거나, 오류 메시지의 API 이름에 맞게 호출부를 조정하세요.

- **AAC / Media Foundation 실패**  
  → Media Feature Pack 설치 여부를 확인하세요.

---

## 라이선스 / 의존성

이 프로젝트는 다음 라이브러리를 사용합니다.

- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D11 / DXGI)
- [NAudio](https://github.com/naudio/NAudio)
- Microsoft.Windows.CsWinRT / Windows SDK 프로젝션
