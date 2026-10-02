<#
    무선랜 정책 라우팅 관리 도구
    - 특정 목적지(도메인/IP)를 지정한 네트워크 인터페이스(장치)로만 보내도록
      영구(재부팅 후에도 유지) 라우트를 추가/삭제/조회합니다.
    - 게이트웨이 IP를 고정 저장하지 않고 "이 목적지는 이 인터페이스로" 규칙만
      저장해두고, 실행할 때마다 해당 인터페이스의 "현재" 게이트웨이를 조회해서
      라우트를 다시 씁니다. 그래서 같은 무선랜 장치가 다른 네트워크(SSID)에
      붙어 게이트웨이가 바뀌어도 자동으로 따라갑니다.
    - -Sync 로 실행하면 메뉴 없이 규칙을 조용히 동기화합니다 (작업 스케줄러용).
    - -AddTarget/-Interface/-Metric, -RemoveTarget, -InstallTask, -UninstallTask,
      -Status, -SetDefault, -ClearDefault 파라미터로 메뉴 없이 스크립트로도 조작할 수 있습니다.
    - -SetDefault "인터페이스이름" : 그 인터페이스를 시스템 "기본 경로"로 강제 고정
      (인터페이스 메트릭을 낮춰서 동기화 때마다 재적용). 목적지별 규칙과 반대로,
      "이건 기본으로, 특정 목적지만 다른 인터페이스로" 구성을 만들 때 사용.
#>

param(
    [switch]$Sync,
    [switch]$Status,
    [switch]$InstallTask,
    [switch]$UninstallTask,
    [string]$AddTarget,
    [string]$Interface,
    [int]$Metric = 1,
    [string]$RemoveTarget,
    [string]$SetDefault,
    [switch]$ClearDefault
)

$ScriptDir   = Split-Path -Parent $PSCommandPath
$ConfigPath  = Join-Path $ScriptDir 'WifiRoute.config.json'
$DefaultPath = Join-Path $ScriptDir 'WifiRoute.default.json'
$LogPath     = Join-Path $ScriptDir 'WifiRoute.log'
$TaskName    = 'WifiRouteSync'

# ── 관리자 권한 확인 및 자동 재실행 ─────────────────────────────
function Test-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-ReinvokeArgs {
    $parts = @()
    if ($Sync) { $parts += '-Sync' }
    if ($Status) { $parts += '-Status' }
    if ($InstallTask) { $parts += '-InstallTask' }
    if ($UninstallTask) { $parts += '-UninstallTask' }
    if ($AddTarget) { $parts += "-AddTarget `"$AddTarget`"" }
    if ($Interface) { $parts += "-Interface `"$Interface`"" }
    if ($PSBoundParameters.ContainsKey('Metric')) { $parts += "-Metric $Metric" }
    if ($RemoveTarget) { $parts += "-RemoveTarget `"$RemoveTarget`"" }
    if ($SetDefault) { $parts += "-SetDefault `"$SetDefault`"" }
    if ($ClearDefault) { $parts += '-ClearDefault' }
    return ($parts -join ' ')
}

if (-not (Test-Admin)) {
    $reArgs = Get-ReinvokeArgs
    if ($reArgs) {
        Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" $reArgs"
    }
    else {
        Write-Host "관리자 권한이 필요합니다. 관리자 권한으로 다시 실행합니다..." -ForegroundColor Yellow
        Start-Process powershell -Verb RunAs -ArgumentList "-NoExit -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    }
    exit
}

# ── 로그 ────────────────────────────────────────────────────
function Write-Log {
    param([string]$Message)
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -Path $LogPath -Value $line -Encoding UTF8
    Write-Host $Message
}

# ── 설정 파일 (목적지 -> 인터페이스 규칙) ───────────────────────
function Get-Config {
    if (Test-Path $ConfigPath) {
        $raw = Get-Content $ConfigPath -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
        $parsed = $raw | ConvertFrom-Json
        return @($parsed)
    }
    return @()
}

function Save-Config {
    param($ConfigList)
    ConvertTo-Json -InputObject @($ConfigList) -Depth 5 -AsArray | Set-Content -Path $ConfigPath -Encoding UTF8
}

# ── 기본(디폴트) 인터페이스 고정 ─────────────────────────────
# 목적지별 규칙과는 별개로, "이 인터페이스가 항상 시스템 기본 경로가 되게"
# 강제하는 설정. Windows는 재연결/재부팅 시 인터페이스 메트릭을 자동으로
# 다시 매길 수 있어서, 동기화 때마다 원하는 인터페이스의 메트릭을 다른
# 활성 인터페이스보다 낮게(=우선순위 높게) 재설정한다.
function Get-DefaultPreference {
    if (Test-Path $DefaultPath) {
        $raw = Get-Content $DefaultPath -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return $raw | ConvertFrom-Json
    }
    return $null
}

function Save-DefaultPreference {
    param($Pref)
    if ($null -eq $Pref) {
        if (Test-Path $DefaultPath) { Remove-Item $DefaultPath -Force }
        return
    }
    $Pref | ConvertTo-Json -Depth 3 | Set-Content -Path $DefaultPath -Encoding UTF8
}

# 어댑터 이름(Wi-Fi 4, Wi-Fi 8 등)은 재연결 시 바뀔 수 있으므로,
# default.json에 InterfaceDescription이 있으면 장치 설명으로 현재 이름을 찾는다.
function Resolve-DefaultAlias {
    param($Pref)
    if ($Pref.InterfaceDescription) {
        $a = Get-NetAdapter -ErrorAction SilentlyContinue |
            Where-Object { $_.InterfaceDescription -like "*$($Pref.InterfaceDescription)*" } |
            Sort-Object { $_.Status -ne 'Up' } | Select-Object -First 1
        if ($a) { return $a.Name }
    }
    return $Pref.InterfaceAlias
}

function Set-DefaultInterfacePreference {
    $pref = Get-DefaultPreference
    if (-not $pref) { return }
    $pref.InterfaceAlias = Resolve-DefaultAlias $pref

    $active = @(Get-ActiveInterfaces)
    $preferred = $active | Where-Object { $_.InterfaceAlias -eq $pref.InterfaceAlias } | Select-Object -First 1
    if (-not $preferred) {
        Write-Log "건너뜀(기본 인터페이스): '$($pref.InterfaceAlias)'가 현재 비활성 상태"
        return
    }

    $desiredLow  = if ($pref.DefaultMetric) { $pref.DefaultMetric } else { 10 }
    $desiredHigh = if ($pref.OtherMetric) { $pref.OtherMetric } else { 50 }
    $changed = $false

    foreach ($ifc in $active) {
        $target = if ($ifc.InterfaceAlias -eq $pref.InterfaceAlias) { $desiredLow } else { $desiredHigh }
        $current = (Get-NetIPInterface -InterfaceIndex $ifc.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).InterfaceMetric
        if ($current -ne $target) {
            Set-NetIPInterface -InterfaceIndex $ifc.InterfaceIndex -InterfaceMetric $target -ErrorAction SilentlyContinue
            $changed = $true
        }
    }

    if ($changed) {
        Write-Log "기본 인터페이스 갱신: '$($pref.InterfaceAlias)' 우선(메트릭 $desiredLow), 나머지 메트릭 $desiredHigh"
    }
}

# ── 공용 함수 ────────────────────────────────────────────────
function Get-ActiveInterfaces {
    Get-NetIPConfiguration | Where-Object {
        $_.NetAdapter.Status -eq 'Up' -and $_.IPv4DefaultGateway
    } | ForEach-Object {
        [PSCustomObject]@{
            InterfaceAlias = $_.InterfaceAlias
            InterfaceIndex = $_.InterfaceIndex
            IP             = $_.IPv4Address.IPAddress
            Gateway        = $_.IPv4DefaultGateway.NextHop
        }
    }
}

function Resolve-Target {
    param([string]$Target)
    if ($Target -as [ipaddress]) { return $Target }
    try {
        $resolved = Resolve-DnsName -Name $Target -Type A -ErrorAction Stop |
            Where-Object { $_.Type -eq 'A' } | Select-Object -First 1
        return $resolved.IPAddress
    }
    catch {
        return $null
    }
}

function Remove-ExistingRouteQuiet {
    param([string]$Prefix)
    # 활성 스토어 + 영구 스토어 양쪽에 남아있을 수 있는 동일 목적지 라우트를
    # 모두 찾아 정리한다 (다른 인터페이스로 저장된 낡은 영구 라우트가 있으면
    # 새 라우트 추가 시 충돌해서 조용히 실패하는 원인이 되므로 반드시 필요).
    $all = @()
    $all += Get-NetRoute -DestinationPrefix $Prefix -ErrorAction SilentlyContinue
    $all += Get-NetRoute -PolicyStore PersistentStore -DestinationPrefix $Prefix -ErrorAction SilentlyContinue
    $all | Select-Object -Unique -Property InterfaceIndex | ForEach-Object {
        $alias = (Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue).Name
        if ($alias) {
            netsh interface ipv4 delete route $Prefix $alias 2>$null | Out-Null
        }
    }
}

# 규칙 하나(목적지 -> 인터페이스)를 실제 라우트에 반영. 인터페이스의 "현재"
# 게이트웨이를 조회해서 쓰므로, 같은 인터페이스가 다른 네트워크에 붙어도 따라간다.
# netsh 실행 후 반드시 영구 스토어를 재조회해서 실제로 반영됐는지 확인한다
# (netsh는 실패해도 비영시적으로 종료 코드만으로는 알기 어려워서, 결과를 직접 검증).
function Set-RouteForEntry {
    param($Entry)

    $ip = Resolve-Target -Target $Entry.Target
    if (-not $ip) {
        Write-Log "실패: '$($Entry.Target)' DNS 확인 불가 (건너뜀)"
        return
    }

    # InterfaceAlias는 문자열 하나 또는 여러 개(배열)일 수 있다. 배열이면 앞에서부터
    # 순서대로 확인해 "현재 활성 상태인 첫 번째" 인터페이스를 사용한다 (우선순위 목록).
    $candidates = @($Entry.InterfaceAlias)
    $active = Get-ActiveInterfaces
    $ifc = $null
    foreach ($alias in $candidates) {
        $ifc = $active | Where-Object { $_.InterfaceAlias -eq $alias } | Select-Object -First 1
        if ($ifc) { break }
    }
    if (-not $ifc) {
        Write-Log "건너뜀: 인터페이스 '$($candidates -join ' / ')' 중 활성 상태인 것이 없음 ($($Entry.Target))"
        return
    }

    $prefix = "$ip/32"
    $existing = Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existing -and $existing.InterfaceIndex -eq $ifc.InterfaceIndex -and $existing.NextHop -eq $ifc.Gateway) {
        Write-Log "유지: $($Entry.Target) ($ip) -> $($ifc.InterfaceAlias) via $($ifc.Gateway) (변경 없음)"
        return
    }

    Remove-ExistingRouteQuiet -Prefix $prefix
    $out = netsh interface ipv4 add route $prefix $ifc.InterfaceAlias $ifc.Gateway "metric=$($Entry.Metric)" store=persistent 2>&1

    $verify = Get-NetRoute -PolicyStore PersistentStore -DestinationPrefix $prefix -ErrorAction SilentlyContinue |
        Where-Object { $_.InterfaceIndex -eq $ifc.InterfaceIndex -and $_.NextHop -eq $ifc.Gateway }
    if ($verify) {
        Write-Log "갱신: $($Entry.Target) ($ip) -> $($ifc.InterfaceAlias) via $($ifc.Gateway)"
    }
    else {
        Write-Log "실패: $($Entry.Target) ($ip) -> $($ifc.InterfaceAlias) 라우트 추가 실패 (netsh 출력: $out)"
    }
}

function Sync-AllRoutes {
    Set-DefaultInterfacePreference

    $cfg = @(Get-Config)
    if ($cfg.Count -eq 0) {
        Write-Log "동기화할 규칙이 없습니다."
        return
    }
    foreach ($entry in $cfg) {
        Set-RouteForEntry -Entry $entry
    }
}

# ── 작업 스케줄러 (자동 동기화) ──────────────────────────────
function Install-AutoSync {
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Sync"

    $triggerRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date) `
        -RepetitionInterval (New-TimeSpan -Minutes 2) -RepetitionDuration (New-TimeSpan -Days 3650)
    $triggerLogon  = New-ScheduledTaskTrigger -AtLogOn
    $triggerBoot   = New-ScheduledTaskTrigger -AtStartup

    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -MultipleInstances IgnoreNew

    try {
        Register-ScheduledTask -TaskName $TaskName -Action $action `
            -Trigger @($triggerRepeat, $triggerLogon, $triggerBoot) `
            -Principal $principal -Settings $settings `
            -Description '무선랜 정책 라우팅 게이트웨이 자동 동기화 (WifiRoute.ps1 -Sync)' -Force -ErrorAction Stop | Out-Null
    }
    catch {
        Write-Host "자동 동기화 작업 등록 실패: $($_.Exception.Message)" -ForegroundColor Red
        return
    }

    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Write-Host "자동 동기화 작업 등록 완료: '$TaskName' (2분마다 + 로그온/부팅 시 실행)" -ForegroundColor Green
    }
    else {
        Write-Host "자동 동기화 작업 등록 실패: 등록 후 확인되지 않음" -ForegroundColor Red
    }
}

function Uninstall-AutoSync {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    if (-not (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
        Write-Host "자동 동기화 작업 제거 완료: '$TaskName'" -ForegroundColor Green
    }
    else {
        Write-Host "자동 동기화 작업 제거 실패" -ForegroundColor Red
    }
}

# ── 메뉴 기능 ────────────────────────────────────────────────
function Show-Status {
    Write-Host "`n===== 네트워크 인터페이스 현황 =====" -ForegroundColor Cyan
    Get-ActiveInterfaces | Format-Table InterfaceAlias, InterfaceIndex, IP, Gateway -AutoSize

    Write-Host "===== 등록된 목적지 규칙 =====" -ForegroundColor Cyan
    $cfg = @(Get-Config)
    if ($cfg.Count -eq 0) {
        Write-Host "(없음)`n"
    }
    else {
        foreach ($e in $cfg) {
            $ip = Resolve-Target -Target $e.Target
            $candidates = @($e.InterfaceAlias)
            $line = "  $($e.Target)"
            if ($ip) { $line += " ($ip)" }
            $line += " -> $($candidates -join ' / ') (metric $($e.Metric))"

            if ($ip) {
                $r = Get-NetRoute -DestinationPrefix "$ip/32" -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($r) {
                    $alias = (Get-NetAdapter -InterfaceIndex $r.InterfaceIndex -ErrorAction SilentlyContinue).Name
                    if ($candidates -contains $alias) {
                        $line += "  [정상, 현재 '$alias' 경유, 게이트웨이 $($r.NextHop)]"
                        Write-Host $line -ForegroundColor Green
                    }
                    else {
                        $line += "  [불일치! 실제 인터페이스: $alias]"
                        Write-Host $line -ForegroundColor Red
                    }
                }
                else {
                    $line += "  [라우트 없음]"
                    Write-Host $line -ForegroundColor Red
                }
            }
            else {
                Write-Host "$line  [DNS 확인 실패]" -ForegroundColor Red
            }
        }
    }

    Write-Host "`n===== 기본(디폴트) 인터페이스 고정 =====" -ForegroundColor Cyan
    $pref = Get-DefaultPreference
    if ($pref) {
        $pref.InterfaceAlias = Resolve-DefaultAlias $pref
        # 실제 우선순위는 라우트별 RouteMetric과 인터페이스의 InterfaceMetric의 합으로
        # 정해지므로, RouteMetric만으로 비교하면 안 된다 (합산값이 가장 작은 것이 우선).
        $actualDefault = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | ForEach-Object {
            $ifMetric = (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).InterfaceMetric
            [PSCustomObject]@{ Route = $_; Effective = $_.RouteMetric + $ifMetric }
        } | Sort-Object Effective | Select-Object -First 1 -ExpandProperty Route
        $actualAlias = if ($actualDefault) { (Get-NetAdapter -InterfaceIndex $actualDefault.InterfaceIndex -ErrorAction SilentlyContinue).Name } else { $null }
        if ($actualAlias -eq $pref.InterfaceAlias) {
            Write-Host "  설정값: '$($pref.InterfaceAlias)' 우선  [정상, 실제 기본 경로도 '$actualAlias']" -ForegroundColor Green
        }
        else {
            Write-Host "  설정값: '$($pref.InterfaceAlias)' 우선  [불일치! 실제 기본 경로: $actualAlias]" -ForegroundColor Red
        }
    }
    else {
        Write-Host "  (설정 안 됨 - Windows 자동 메트릭 사용 중)"
    }

    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Write-Host "`n자동 동기화 작업: $(if ($task) { "설치됨 (상태: $($task.State))" } else { '설치 안 됨' })"

    if (Test-Path $LogPath) {
        Write-Host "`n===== 최근 동기화 로그 (최대 10줄) =====" -ForegroundColor Cyan
        Get-Content $LogPath -Tail 10
    }
}

function Add-RouteEntry {
    param([string]$TargetIn, [string]$InterfaceIn, [int]$MetricIn = 1)

    if (-not $TargetIn) {
        $TargetIn = Read-Host "`n라우팅할 목적지 (도메인 또는 IP)를 입력하세요"
    }
    if ([string]::IsNullOrWhiteSpace($TargetIn)) { Write-Host "입력이 없어 취소합니다." -ForegroundColor Red; return }

    $ip = Resolve-Target -Target $TargetIn
    if (-not $ip) { Write-Host "DNS 확인 실패: $TargetIn" -ForegroundColor Red; return }
    Write-Host "목적지 IP: $ip"

    # 여러 인터페이스 중 "먼저 연결되는 것" 우선순위 목록으로 등록할 수 있도록,
    # 지금 당장 활성 상태가 아닌 인터페이스도 고를 수 있게 전체 어댑터를 보여준다.
    $allAdapters = @(Get-NetAdapter | Sort-Object ifIndex)
    if ($allAdapters.Count -eq 0) { Write-Host "사용 가능한 인터페이스가 없습니다." -ForegroundColor Red; return }

    $chosenAliases = @()
    if ($InterfaceIn) {
        $chosenAliases = @($InterfaceIn -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        foreach ($a in $chosenAliases) {
            if (-not (Get-NetAdapter -Name $a -ErrorAction SilentlyContinue)) {
                Write-Host "인터페이스 '$a'를 찾을 수 없습니다." -ForegroundColor Red; return
            }
        }
    }
    else {
        Write-Host "`n사용 가능한 인터페이스 (지금 비활성 상태라도 나중에 연결될 예비용으로 선택 가능):"
        for ($i = 0; $i -lt $allAdapters.Count; $i++) {
            Write-Host "  [$i] $($allAdapters[$i].Name)  ($($allAdapters[$i].InterfaceDescription), 상태: $($allAdapters[$i].Status))"
        }
        $sel = Read-Host "`n사용할 인터페이스 번호 선택 (여러 개는 쉼표로 구분, 예: 0,2 - 앞쪽이 우선순위 높음)"
        $indices = @($sel -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        foreach ($idx in $indices) {
            if ($idx -notmatch '^\d+$' -or [int]$idx -ge $allAdapters.Count) {
                Write-Host "잘못된 선택입니다: $idx" -ForegroundColor Red; return
            }
            $chosenAliases += $allAdapters[[int]$idx].Name
        }
    }

    if ($chosenAliases.Count -eq 0) { Write-Host "인터페이스 선택이 없어 취소합니다." -ForegroundColor Red; return }

    if (-not $PSBoundParameters.ContainsKey('MetricIn') -and -not $Metric) {
        $metricInput = Read-Host "라우트 메트릭 (기본값 1, 그냥 Enter 가능)"
        $MetricIn = if ($metricInput -match '^\d+$') { [int]$metricInput } else { 1 }
    }

    $entry = [PSCustomObject]@{
        Target         = $TargetIn
        InterfaceAlias = if ($chosenAliases.Count -eq 1) { $chosenAliases[0] } else { $chosenAliases }
        Metric         = $MetricIn
    }
    Set-RouteForEntry -Entry $entry

    $cfg = @(Get-Config | Where-Object { $_.Target -ne $TargetIn })
    $cfg += $entry
    Save-Config -ConfigList $cfg

    Write-Host "규칙 저장 완료: $TargetIn -> $($chosenAliases -join ' / ') (앞으로 네트워크가 바뀌거나, 등록한 인터페이스 중 하나만 연결돼도 자동 동기화 시 그 인터페이스로 게이트웨이를 다시 찾습니다)" -ForegroundColor Green
}

function Remove-RouteEntry {
    param([string]$TargetIn)

    $cfg = @(Get-Config)
    if ($cfg.Count -eq 0) { Write-Host "`n삭제할 규칙이 없습니다." -ForegroundColor Yellow; return }

    $target = $null
    if ($TargetIn) {
        $target = $cfg | Where-Object { $_.Target -eq $TargetIn } | Select-Object -First 1
        if (-not $target) { Write-Host "규칙 '$TargetIn'을 찾을 수 없습니다." -ForegroundColor Red; return }
    }
    else {
        Write-Host "`n등록된 규칙:"
        for ($i = 0; $i -lt $cfg.Count; $i++) {
            Write-Host "  [$i] $($cfg[$i].Target) -> $($cfg[$i].InterfaceAlias) (metric $($cfg[$i].Metric))"
        }
        $sel = Read-Host "`n삭제할 번호 선택"
        if ($sel -notmatch '^\d+$' -or [int]$sel -ge $cfg.Count) {
            Write-Host "잘못된 선택입니다." -ForegroundColor Red; return
        }
        $target = $cfg[[int]$sel]
    }

    $ip = Resolve-Target -Target $target.Target
    if ($ip) { Remove-ExistingRouteQuiet -Prefix "$ip/32" }

    $newCfg = @($cfg | Where-Object { $_.Target -ne $target.Target })
    Save-Config -ConfigList $newCfg

    Write-Host "삭제 완료: $($target.Target)" -ForegroundColor Green
}

# ── 비대화형 실행 (스크립트/작업 스케줄러용) ────────────────────
if ($Sync) {
    Write-Log "===== 동기화 시작 ====="
    Sync-AllRoutes
    Write-Log "===== 동기화 종료 ====="
    exit
}
if ($Status) { Show-Status; exit }
if ($InstallTask) { Install-AutoSync; exit }
if ($UninstallTask) { Uninstall-AutoSync; exit }
if ($AddTarget) { Add-RouteEntry -TargetIn $AddTarget -InterfaceIn $Interface -MetricIn $Metric; exit }
if ($RemoveTarget) { Remove-RouteEntry -TargetIn $RemoveTarget; exit }
if ($SetDefault) {
    Save-DefaultPreference -Pref ([PSCustomObject]@{ InterfaceAlias = $SetDefault; DefaultMetric = 10; OtherMetric = 50 })
    Set-DefaultInterfacePreference
    Write-Host "기본 인터페이스 설정 완료: '$SetDefault' (동기화 때마다 최우선 유지)" -ForegroundColor Green
    exit
}
if ($ClearDefault) {
    Save-DefaultPreference -Pref $null
    Write-Host "기본 인터페이스 고정 해제 완료 (Windows 자동 메트릭으로 되돌아감)" -ForegroundColor Green
    exit
}

# ── 메인 메뉴 루프 (대화형) ───────────────────────────────────
do {
    Write-Host "`n================================" -ForegroundColor Cyan
    Write-Host "   무선랜 정책 라우팅 관리 도구" -ForegroundColor Cyan
    Write-Host "================================"
    Write-Host "1. 현황 보기"
    Write-Host "2. 라우트 추가 (목적지 -> 인터페이스)"
    Write-Host "3. 라우트 삭제"
    Write-Host "4. 자동 동기화 설치 (네트워크 바뀌어도 게이트웨이 자동 갱신)"
    Write-Host "5. 자동 동기화 제거"
    Write-Host "6. 지금 즉시 동기화 실행"
    Write-Host "7. 기본(디폴트) 인터페이스 고정 설정"
    Write-Host "8. 기본 인터페이스 고정 해제"
    Write-Host "9. 종료"
    $choice = Read-Host "`n선택"

    switch ($choice) {
        '1' { Show-Status }
        '2' { Add-RouteEntry }
        '3' { Remove-RouteEntry }
        '4' { Install-AutoSync }
        '5' { Uninstall-AutoSync }
        '6' { Sync-AllRoutes }
        '7' {
            $allAdapters = @(Get-NetAdapter | Sort-Object ifIndex)
            Write-Host "`n사용 가능한 인터페이스:"
            for ($i = 0; $i -lt $allAdapters.Count; $i++) {
                Write-Host "  [$i] $($allAdapters[$i].Name)  (상태: $($allAdapters[$i].Status))"
            }
            $sel = Read-Host "`n기본으로 고정할 인터페이스 번호"
            if ($sel -match '^\d+$' -and [int]$sel -lt $allAdapters.Count) {
                Save-DefaultPreference -Pref ([PSCustomObject]@{ InterfaceAlias = $allAdapters[[int]$sel].Name; DefaultMetric = 10; OtherMetric = 50 })
                Set-DefaultInterfacePreference
                Write-Host "기본 인터페이스 설정 완료: '$($allAdapters[[int]$sel].Name)'" -ForegroundColor Green
            }
            else {
                Write-Host "잘못된 선택입니다." -ForegroundColor Red
            }
        }
        '8' {
            Save-DefaultPreference -Pref $null
            Write-Host "기본 인터페이스 고정 해제 완료" -ForegroundColor Green
        }
        '9' { Write-Host "종료합니다." }
        default { Write-Host "잘못된 입력입니다." -ForegroundColor Red }
    }
} while ($choice -ne '9')
