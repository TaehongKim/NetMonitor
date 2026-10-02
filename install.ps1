<#
    NetMonitor 설치 스크립트 (릴리스 zip을 푼 폴더에서 실행)
      powershell -ExecutionPolicy Bypass -File .\install.ps1
      powershell -ExecutionPolicy Bypass -File .\install.ps1 -Autostart      # 윈도우 시작 시 자동 실행까지 등록

    - 설치 위치 기본값: %LOCALAPPDATA%\NetMonitor (관리자 권한 불필요, 설정 파일을 앱이 직접 쓸 수 있음)
    - 시작 메뉴 바로가기를 만들고, 내려받은 파일의 '인터넷에서 받음' 차단 표시를 풀어줍니다.
    - 기존 설정 파일(WifiRoute.config.json, NetMonitor.settings.json 등)은 덮어쓰지 않습니다.
#>
param(
    [string]$Dir = (Join-Path $env:LOCALAPPDATA 'NetMonitor'),
    [switch]$Autostart,
    [switch]$NoShortcut,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot

if ([Environment]::OSVersion.Version.Major -lt 10) { throw 'Windows 10 이상이 필요합니다.' }
foreach ($f in 'NetMonitor.exe', 'WifiRoute.ps1') {
    if (-not (Test-Path (Join-Path $src $f))) { throw "$f 파일이 이 폴더에 없습니다. 릴리스 zip을 완전히 푼 폴더에서 실행하세요." }
}

$exe = Join-Path $Dir 'NetMonitor.exe'

# 실행 중이면 종료 (파일 교체를 위해). 설치 폴더의 NetMonitor만 대상.
Get-Process NetMonitor -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 300

New-Item -ItemType Directory -Force $Dir | Out-Null
if ((Resolve-Path $src).Path -ne (Resolve-Path $Dir).Path) {
    foreach ($f in 'NetMonitor.exe', 'WifiRoute.ps1', 'WifiRoute.config.example.json', 'README.md', 'uninstall.ps1') {
        $p = Join-Path $src $f
        if (Test-Path $p) { Copy-Item $p $Dir -Force }
    }
    $docs = Join-Path $src 'docs'
    if (Test-Path $docs) { Copy-Item $docs $Dir -Recurse -Force }
}

# 내려받은 zip에서 풀면 파일에 차단 표시(Mark of the Web)가 붙어 SmartScreen 경고가 뜰 수 있음
Get-ChildItem $Dir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

if (-not $NoShortcut) {
    $lnkPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'NetMonitor.lnk'
    $l = (New-Object -ComObject WScript.Shell).CreateShortcut($lnkPath)
    $l.TargetPath = $exe; $l.WorkingDirectory = $Dir; $l.Description = 'NetMonitor'; $l.Save()
    Write-Host "시작 메뉴 바로가기: $lnkPath"
}

if ($Autostart) {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Set-ItemProperty -Path $key -Name 'NetMonitor' -Value "`"$exe`" --tray"
    Write-Host '윈도우 시작 시 자동 실행: 등록됨 (트레이로 시작)'
}

Write-Host "`n설치 완료: $Dir" -ForegroundColor Green

# 이 도구는 어댑터가 2개 이상일 때 의미가 있음. 지금 연결된 어댑터 수를 알려준다.
$up = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up')
if ($up.Count -lt 2) {
    Write-Host "참고: 지금 연결된 물리 어댑터가 $($up.Count)개입니다. 목적지별 라우팅은 어댑터가 2개 이상 연결돼 있어야 효과가 있습니다." -ForegroundColor Yellow
}

if (-not $NoStart) {
    Start-Process $exe
    Write-Host '실행했습니다. 처음에는 설정 탭에서 무료로 쓰는 어댑터를 지정하세요.'
}
