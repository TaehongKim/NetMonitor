<#
    NetMonitor 제거 스크립트
      powershell -ExecutionPolicy Bypass -File .\uninstall.ps1                 # 앱만 제거 (설정/규칙 파일은 남김)
      powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RemoveRoutes   # 등록해 둔 라우팅 규칙(영구 라우트)과 기본 어댑터 고정도 해제 (UAC 필요)
      powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Purge          # 설정/규칙/로그 파일까지 모두 삭제

    주의: 앱이 만든 영구 라우트는 앱을 지워도 Windows에 남아 있습니다. 라우팅을 원래대로 돌리려면 -RemoveRoutes를 쓰거나,
    제거하기 전에 앱의 '라우팅 규칙' 탭에서 규칙을 삭제하세요.
#>
param(
    [string]$Dir = $PSScriptRoot,
    [switch]$RemoveRoutes,
    [switch]$SkipTask,
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $Dir 'NetMonitor.exe'
$route = Join-Path $Dir 'WifiRoute.ps1'
$config = Join-Path $Dir 'WifiRoute.config.json'

Get-Process NetMonitor -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 300

# 1) 라우팅 규칙 해제: 관리자 권한으로 한 번에 처리 (UAC 한 번)
if ($RemoveRoutes -and (Test-Path $route)) {
    $targets = @()
    if (Test-Path $config) {
        $raw = Get-Content $config -Raw -Encoding UTF8
        if (-not [string]::IsNullOrWhiteSpace($raw)) { $targets = @(($raw | ConvertFrom-Json) | ForEach-Object { $_.Target }) }
    }
    $cmds = @()
    foreach ($t in $targets) { $cmds += "& '$route' -RemoveTarget '$($t -replace "'", "''")'" }
    $cmds += "& '$route' -ClearDefault"
    $tmp = Join-Path $env:TEMP 'netmonitor_remove_routes.ps1'
    Set-Content -Path $tmp -Value ($cmds -join "`r`n") -Encoding UTF8
    Write-Host "라우팅 규칙 $($targets.Count)개와 기본 어댑터 고정을 해제합니다 (관리자 권한 확인 창이 뜹니다)..."
    Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$tmp`""
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}

# 2) 자동 동기화 작업 제거. 작업은 SYSTEM 소유라 일반 권한으론 조회가 안 되므로,
#    한 번이라도 동기화가 돌았다는 흔적(로그)이 있을 때만 제거를 시도한다.
if (-not $SkipTask -and (Test-Path $route) -and (Test-Path (Join-Path $Dir 'WifiRoute.log'))) {
    Write-Host '자동 동기화 작업을 제거합니다 (관리자 권한 확인 창이 뜹니다)...'
    Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$route`" -UninstallTask"
}

# 3) 자동 시작 / 시작 메뉴 바로가기
# 자동 시작 값은 이 설치본의 exe를 가리킬 때만 지운다 (다른 위치의 복사본 항목은 건드리지 않음)
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runVal = (Get-ItemProperty $runKey -Name 'NetMonitor' -ErrorAction SilentlyContinue).NetMonitor
if ($runVal -and $runVal.IndexOf($exe, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    Remove-ItemProperty -Path $runKey -Name 'NetMonitor' -ErrorAction SilentlyContinue
}
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'NetMonitor.lnk') -Force -ErrorAction SilentlyContinue

# 4) 파일 삭제
$appFiles = 'NetMonitor.exe', 'WifiRoute.ps1', 'WifiRoute.config.example.json', 'README.md'
foreach ($f in $appFiles) { Remove-Item (Join-Path $Dir $f) -Force -ErrorAction SilentlyContinue }
Remove-Item (Join-Path $Dir 'docs') -Recurse -Force -ErrorAction SilentlyContinue
if ($Purge) {
    foreach ($f in 'WifiRoute.config.json', 'WifiRoute.default.json', 'NetMonitor.settings.json', 'WifiRoute.log') {
        Remove-Item (Join-Path $Dir $f) -Force -ErrorAction SilentlyContinue
    }
}
Remove-Item $PSCommandPath -Force -ErrorAction SilentlyContinue
if (-not (Get-ChildItem $Dir -Force -ErrorAction SilentlyContinue)) { Remove-Item $Dir -Force -ErrorAction SilentlyContinue }

Write-Host '제거 완료.' -ForegroundColor Green
if (-not $Purge -and (Test-Path $Dir)) { Write-Host "설정/규칙 파일은 남겨 두었습니다: $Dir  (모두 지우려면 -Purge)" }
if (-not $RemoveRoutes) { Write-Host '등록해 둔 라우팅 규칙(영구 라우트)은 그대로 남아 있습니다. 해제하려면 -RemoveRoutes로 다시 실행하거나 앱에서 먼저 규칙을 삭제하세요.' -ForegroundColor Yellow }
