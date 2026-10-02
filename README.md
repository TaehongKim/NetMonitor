# NetMonitor

어떤 네트워크 어댑터(Wi-Fi, 이더넷 등)로 통신하는지 보여주는 경량 Windows GUI 도구입니다.
PowerShell + WinForms 단일 스크립트라 설치나 빌드가 필요 없습니다.

## 기능

- **연결 탭**: 활성 TCP 연결마다 프로세스, PID, 로컬/원격 주소, 상태, 사용 어댑터, 적용 규칙을 3초마다 갱신해 보여줍니다. 어댑터별 필터, 로컬(127.x) 연결 및 수신 대기 포함 옵션이 있습니다.
- **송수신 그래프 탭**: 어댑터별 수신(실선)/송신(점선) 속도를 최근 60초 동안 1초 간격으로 그립니다. 하단 표에 현재 속도와 누적량이 나옵니다.
- **라우팅 규칙 탭**: `WifiRoute` 규칙(목적지 → 인터페이스 우선순위)을 조회, 추가, 삭제, 동기화하고 기본 인터페이스 고정을 설정/해제합니다. 각 규칙의 실제 경로가 정상인지 표시합니다.
- **트레이 상주**: 창을 닫으면 트레이로 숨고, 아이콘 툴팁에 전체 ↓/↑ 속도가 표시됩니다. 트레이 메뉴의 `종료`로만 끝납니다. 중복 실행은 막습니다.
- **자동 시작**: 트레이 메뉴의 `윈도우 시작 시 자동 실행`을 체크하면 시작프로그램 폴더에 `-Tray` 바로가기가 만들어집니다.

## 요구 사항

- Windows 10/11, Windows PowerShell 5.1 (기본 포함)
- 조회는 일반 권한으로 동작합니다. 규칙 변경은 `WifiRoute.ps1`이 UAC로 관리자 권한을 요청합니다.

## 실행

```powershell
powershell -STA -ExecutionPolicy Bypass -File NetMonitor.ps1          # 창으로 시작
powershell -STA -ExecutionPolicy Bypass -File NetMonitor.ps1 -Tray    # 트레이로 바로 시작
```

바로가기를 만들 때는 대상에 `powershell.exe`, 인수에 `-STA -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "경로\NetMonitor.ps1"`을 넣으면 콘솔 창 없이 뜹니다.

## 구성 파일

| 파일 | 설명 |
|---|---|
| `NetMonitor.ps1` | GUI 본체 |
| `WifiRoute.ps1` | 목적지별 라우팅 규칙을 실제 라우트에 반영하는 도구 (NetMonitor가 호출) |
| `WifiRoute.config.example.json` | 규칙 파일 예시. `WifiRoute.config.json`으로 복사해서 사용 |
| `WifiRoute.config.json` | 내 규칙 (git 제외) |
| `WifiRoute.default.json` | 기본 인터페이스 고정 설정 (git 제외, `WifiRoute.ps1 -SetDefault`로 생성) |
| `WifiRoute.log` | 동기화 로그 (git 제외) |

규칙 파일 형식:

```json
[
  { "Target": "example.com", "InterfaceAlias": ["Wi-Fi", "Wi-Fi 2"], "Metric": 1 }
]
```

`InterfaceAlias`는 문자열 하나 또는 우선순위 배열이고, 배열이면 앞에서부터 현재 연결된 첫 인터페이스를 사용합니다. 게이트웨이 IP는 저장하지 않고 동기화 때마다 그 인터페이스의 현재 게이트웨이를 조회합니다.

## WifiRoute.ps1 단독 사용

```powershell
.\WifiRoute.ps1 -Status                                    # 현황
.\WifiRoute.ps1 -AddTarget example.com -Interface "Wi-Fi,Wi-Fi 2" -Metric 1
.\WifiRoute.ps1 -RemoveTarget example.com
.\WifiRoute.ps1 -SetDefault "Wi-Fi"                        # 기본 인터페이스 고정
.\WifiRoute.ps1 -ClearDefault
.\WifiRoute.ps1 -Sync                                      # 규칙 즉시 동기화
.\WifiRoute.ps1 -InstallTask                               # 2분마다 + 로그온/부팅 시 자동 동기화
.\WifiRoute.ps1 -UninstallTask
```

Wi-Fi가 다른 SSID로 바뀌어도 게이트웨이가 따라가게 하려면 `-InstallTask`로 자동 동기화를 설치하세요.

## 한계

- 연결 탭은 IPv4 TCP만 표시합니다 (UDP 제외).
- PowerShell 기반이라 상주 시 메모리를 약 50~80MB 사용합니다.
- 기본 앱 아이콘을 사용합니다.
