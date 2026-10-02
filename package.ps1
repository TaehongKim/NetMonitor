<#
    릴리스 zip 만들기: build.ps1로 빌드한 뒤 dist\NetMonitor-v<버전>.zip 과 SHA256SUMS.txt 를 만듭니다.
    버전은 NetMonitor.exe의 파일 버전(Program.cs의 AssemblyFileVersion)에서 읽습니다.
      .\package.ps1
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

& (Join-Path $root 'build.ps1')

$exe = Join-Path $root 'bin\NetMonitor.exe'
$ver = ([Version](Get-Item $exe).VersionInfo.FileVersion).ToString(3)
$name = "NetMonitor-v$ver"
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zip -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item $exe $stage
foreach ($f in 'WifiRoute.ps1', 'WifiRoute.config.example.json', 'install.ps1', 'uninstall.ps1', 'README.md') {
    Copy-Item (Join-Path $root $f) $stage
}
Copy-Item (Join-Path $root 'docs') $stage -Recurse

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
"$hash  $name.zip" | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII

Write-Host "릴리스 파일: $zip ($([int]((Get-Item $zip).Length / 1KB)) KB)" -ForegroundColor Green
Write-Host "SHA256: $hash"
