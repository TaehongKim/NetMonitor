<#
    NetMonitor 빌드: Windows에 기본 포함된 .NET Framework의 csc.exe로 컴파일합니다 (SDK 설치 불필요).
    .\build.ps1                       -> bin\NetMonitor.exe (+ WifiRoute.ps1 사본)
    .\build.ps1 -Deploy C:\Tools\NM   -> 추가로 해당 폴더에 복사 (실행 중이면 먼저 종료)
#>
param([string]$Deploy)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe를 찾을 수 없습니다: $csc" }

$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$exe = Join-Path $bin 'NetMonitor.exe'

& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$exe" `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    (Join-Path $root 'Core.cs') (Join-Path $root 'MainForm.cs') (Join-Path $root 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw "빌드 실패 (csc 종료 코드 $LASTEXITCODE)" }

Copy-Item (Join-Path $root 'WifiRoute.ps1') $bin -Force
Write-Host "빌드 완료: $exe" -ForegroundColor Green

if ($Deploy) {
    New-Item -ItemType Directory -Force $Deploy | Out-Null
    $target = Join-Path $Deploy 'NetMonitor.exe'
    Get-Process NetMonitor -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $target } | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    Copy-Item $exe $Deploy -Force
    if (-not (Test-Path (Join-Path $Deploy 'WifiRoute.ps1'))) { Copy-Item (Join-Path $bin 'WifiRoute.ps1') $Deploy }
    Write-Host "배포 완료: $target" -ForegroundColor Green
}
