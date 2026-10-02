<#
    NetMonitor - 경량 네트워크 모니터 (WinForms, 단일 .ps1)
    탭 1) 연결     : 활성 TCP 연결이 어떤 어댑터로 나가는지 (프로세스/원격 주소/어댑터/적용 규칙)
    탭 2) 그래프   : 어댑터별 송수신량 실시간 그래프 (최근 60초)
    탭 3) 라우팅   : WifiRoute 규칙 조회/추가/삭제/동기화, 기본 인터페이스 고정
    조회는 일반 권한, 규칙 변경은 WifiRoute.ps1이 UAC로 승격해서 처리합니다.
    트레이 상주: 창을 닫으면 트레이로 숨고, 트레이 메뉴의 '종료'로만 끝납니다.
    -Tray : 창 없이 트레이로 바로 시작 (시작프로그램용). 트레이 메뉴에서 시작프로그램 등록/해제 가능.
    실행: powershell -STA -File NetMonitor.ps1 [-Tray]
#>

param([switch]$Tray)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# 중복 실행 방지 (이미 떠 있으면 조용히 종료)
$script:Mutex = New-Object System.Threading.Mutex($false, 'Local\NetMonitor.Tim')
if (-not $script:Mutex.WaitOne(0)) { exit }

$script:ScriptDir   = Split-Path -Parent $PSCommandPath
$script:RouteScript = Join-Path $script:ScriptDir 'WifiRoute.ps1'
$script:ConfigPath  = Join-Path $script:ScriptDir 'WifiRoute.config.json'
$script:DefaultPath = Join-Path $script:ScriptDir 'WifiRoute.default.json'
$script:MaxPoints   = 60
$script:RuleIps     = @{}   # target -> ip (규칙 새로고침 때만 DNS 조회)

# ── 공용 ────────────────────────────────────────────────────
function Get-Rules {
    if (-not (Test-Path $script:ConfigPath)) { return @() }
    $raw = Get-Content $script:ConfigPath -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
    return @($raw | ConvertFrom-Json)
}

function Get-DefaultPref {
    if (-not (Test-Path $script:DefaultPath)) { return $null }
    $raw = Get-Content $script:DefaultPath -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    return $raw | ConvertFrom-Json
}

function Resolve-RuleIp {
    param([string]$Target)
    if ($Target -as [ipaddress]) { return $Target }
    try {
        return ([System.Net.Dns]::GetHostAddresses($Target) |
            Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1).IPAddressToString
    }
    catch { return $null }
}

function Invoke-RouteTool {
    param([string]$Arguments)
    $form.Cursor = 'WaitCursor'
    try {
        Start-Process powershell -Wait -WindowStyle Hidden `
            -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$script:RouteScript`" $Arguments"
    }
    finally { $form.Cursor = 'Default' }
}

function Format-Rate {
    param([double]$Bps)
    if ($Bps -ge 1MB) { return ('{0:N1} MB/s' -f ($Bps / 1MB)) }
    if ($Bps -ge 1KB) { return ('{0:N1} KB/s' -f ($Bps / 1KB)) }
    return ('{0:N0} B/s' -f $Bps)
}

$script:Palette = @(
    [System.Drawing.Color]::FromArgb(31, 119, 180),  [System.Drawing.Color]::FromArgb(214, 39, 40),
    [System.Drawing.Color]::FromArgb(44, 160, 44),   [System.Drawing.Color]::FromArgb(255, 127, 14),
    [System.Drawing.Color]::FromArgb(148, 103, 189), [System.Drawing.Color]::FromArgb(23, 190, 207)
)

function New-Grid {
    $g = New-Object System.Windows.Forms.DataGridView
    $g.Dock = 'Fill'; $g.ReadOnly = $true; $g.AllowUserToAddRows = $false; $g.AllowUserToDeleteRows = $false
    $g.RowHeadersVisible = $false; $g.SelectionMode = 'FullRowSelect'; $g.MultiSelect = $false
    $g.AutoSizeColumnsMode = 'Fill'; $g.BackgroundColor = [System.Drawing.SystemColors]::Window
    $g.BorderStyle = 'None'; $g.AllowUserToResizeRows = $false
    return $g
}

function New-Button {
    param($Text, $Width = 100)
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $Text; $b.Width = $Width; $b.Height = 26
    return $b
}

# ── 폼 ──────────────────────────────────────────────────────
$form = New-Object System.Windows.Forms.Form
$form.Text = 'NetMonitor'; $form.Size = New-Object System.Drawing.Size(960, 600)
$form.StartPosition = 'CenterScreen'; $form.Font = New-Object System.Drawing.Font('Malgun Gothic', 9)

$tabs = New-Object System.Windows.Forms.TabControl
$tabs.Dock = 'Fill'
$tabConn  = New-Object System.Windows.Forms.TabPage '연결'
$tabGraph = New-Object System.Windows.Forms.TabPage '송수신 그래프'
$tabRules = New-Object System.Windows.Forms.TabPage '라우팅 규칙'
$tabs.TabPages.AddRange(@($tabConn, $tabGraph, $tabRules))
$form.Controls.Add($tabs)

# ── 탭 1: 연결 ──────────────────────────────────────────────
$connTop = New-Object System.Windows.Forms.FlowLayoutPanel
$connTop.Dock = 'Top'; $connTop.Height = 34; $connTop.Padding = New-Object System.Windows.Forms.Padding(4, 4, 0, 0)
$lblAd = New-Object System.Windows.Forms.Label; $lblAd.Text = '어댑터:'; $lblAd.AutoSize = $true
$lblAd.Margin = New-Object System.Windows.Forms.Padding(0, 5, 4, 0)
$cmbAdapter = New-Object System.Windows.Forms.ComboBox
$cmbAdapter.DropDownStyle = 'DropDownList'; $cmbAdapter.Width = 140
[void]$cmbAdapter.Items.Add('(전체)'); $cmbAdapter.SelectedIndex = 0
$chkLoopback = New-Object System.Windows.Forms.CheckBox; $chkLoopback.Text = '로컬(127.x) 연결 포함'; $chkLoopback.AutoSize = $true
$chkLoopback.Margin = New-Object System.Windows.Forms.Padding(12, 3, 0, 0)
$chkListen = New-Object System.Windows.Forms.CheckBox; $chkListen.Text = '수신 대기 포함'; $chkListen.AutoSize = $true
$chkListen.Margin = New-Object System.Windows.Forms.Padding(12, 3, 0, 0)
$lblCount = New-Object System.Windows.Forms.Label; $lblCount.AutoSize = $true
$lblCount.Margin = New-Object System.Windows.Forms.Padding(16, 5, 0, 0)
$connTop.Controls.AddRange(@($lblAd, $cmbAdapter, $chkLoopback, $chkListen, $lblCount))

$gridConn = New-Grid
foreach ($c in '프로세스', 'PID', '로컬 주소', '원격 주소', '상태', '어댑터', '적용 규칙') { [void]$gridConn.Columns.Add($c, $c) }
$gridConn.Columns['PID'].FillWeight = 40; $gridConn.Columns['상태'].FillWeight = 60
$tabConn.Controls.Add($gridConn); $tabConn.Controls.Add($connTop)

function Update-Connections {
    $ipMap = @{}
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object { $ipMap[$_.IPAddress] = $_.InterfaceAlias }
    $procMap = @{}
    Get-Process -ErrorAction SilentlyContinue | ForEach-Object { $procMap[$_.Id] = $_.ProcessName }
    $ruleByIp = @{}
    foreach ($k in $script:RuleIps.Keys) { if ($script:RuleIps[$k]) { $ruleByIp[$script:RuleIps[$k]] = $k } }

    $states = if ($chkListen.Checked) { @('Established', 'Listen') } else { @('Established') }
    $conns = @(Get-NetTCPConnection -State $states -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalAddress -notmatch ':' })      # IPv4만
    if (-not $chkLoopback.Checked) {
        $conns = @($conns | Where-Object { $_.RemoteAddress -notlike '127.*' -and $_.LocalAddress -notlike '127.*' })
    }

    $adapters = @($conns | ForEach-Object { $ipMap[$_.LocalAddress] } | Where-Object { $_ } | Sort-Object -Unique)
    foreach ($a in $adapters) { if (-not $cmbAdapter.Items.Contains($a)) { [void]$cmbAdapter.Items.Add($a) } }
    $filter = [string]$cmbAdapter.SelectedItem

    $rows = foreach ($c in $conns) {
        $ad = $ipMap[$c.LocalAddress]; if (-not $ad) { $ad = '(전체/미지정)' }
        if ($filter -and $filter -ne '(전체)' -and $ad -ne $filter) { continue }
        [PSCustomObject]@{
            Proc = $procMap[[int]$c.OwningProcess]; ProcId = $c.OwningProcess
            Local = "$($c.LocalAddress):$($c.LocalPort)"; Remote = "$($c.RemoteAddress):$($c.RemotePort)"
            State = [string]$c.State; Adapter = $ad; Rule = $ruleByIp[$c.RemoteAddress]
        }
    }
    $rows = @($rows | Sort-Object Adapter, Proc)

    $first = if ($gridConn.FirstDisplayedScrollingRowIndex -ge 0) { $gridConn.FirstDisplayedScrollingRowIndex } else { 0 }
    $gridConn.SuspendLayout(); $gridConn.Rows.Clear()
    foreach ($r in $rows) { [void]$gridConn.Rows.Add($r.Proc, $r.ProcId, $r.Local, $r.Remote, $r.State, $r.Adapter, $r.Rule) }
    if ($first -gt 0 -and $first -lt $gridConn.Rows.Count) { $gridConn.FirstDisplayedScrollingRowIndex = $first }
    $gridConn.ResumeLayout()
    $lblCount.Text = "연결 $($rows.Count)개"
}

# ── 탭 2: 송수신 그래프 ─────────────────────────────────────
$script:Hist = @{}                       # 어댑터명 -> @{Rx; Tx; LastRx; LastTx; Color; Total}
$script:Clock = [System.Diagnostics.Stopwatch]::StartNew()
$script:LastTick = 0.0

$graph = New-Object System.Windows.Forms.Panel
$graph.Dock = 'Fill'; $graph.BackColor = [System.Drawing.Color]::White
[System.Windows.Forms.Panel].GetProperty('DoubleBuffered', [Reflection.BindingFlags]'NonPublic,Instance').SetValue($graph, $true)

$legend = New-Object System.Windows.Forms.ListView
$legend.Dock = 'Bottom'; $legend.Height = 120; $legend.View = 'Details'; $legend.FullRowSelect = $true
[void]$legend.Columns.Add('어댑터', 160); [void]$legend.Columns.Add('↓ 수신', 110); [void]$legend.Columns.Add('↑ 송신', 110)
[void]$legend.Columns.Add('누적 수신', 110); [void]$legend.Columns.Add('누적 송신', 110); [void]$legend.Columns.Add('설명', 300)
$lblNote = New-Object System.Windows.Forms.Label
$lblNote.Dock = 'Top'; $lblNote.Height = 22; $lblNote.Text = '  실선 = 수신(↓), 점선 = 송신(↑) · 최근 60초'
$tabGraph.Controls.Add($graph); $tabGraph.Controls.Add($lblNote); $tabGraph.Controls.Add($legend)

function Update-Throughput {
    $now = $script:Clock.Elapsed.TotalSeconds
    $dt = $now - $script:LastTick; if ($dt -le 0) { $dt = 1 }
    $script:LastTick = $now
    $seen = @{}
    $descs = @{}
    foreach ($nic in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.OperationalStatus -ne 'Up') { continue }
        if ($nic.NetworkInterfaceType -in 'Loopback', 'Tunnel') { continue }
        $st = $nic.GetIPv4Statistics()
        $seen[$nic.Name] = $true; $descs[$nic.Name] = $nic.Description
        if (-not $script:Hist.ContainsKey($nic.Name)) {
            $script:Hist[$nic.Name] = @{
                Rx = New-Object 'System.Collections.Generic.List[double]'; Tx = New-Object 'System.Collections.Generic.List[double]'
                LastRx = $st.BytesReceived; LastTx = $st.BytesSent
                Color = $script:Palette[$script:Hist.Count % $script:Palette.Count]; Total = @(0, 0)
            }
        }
        $h = $script:Hist[$nic.Name]
        $rx = [math]::Max(0, ($st.BytesReceived - $h.LastRx) / $dt)
        $tx = [math]::Max(0, ($st.BytesSent - $h.LastTx) / $dt)
        $h.LastRx = $st.BytesReceived; $h.LastTx = $st.BytesSent
        $h.Rx.Add($rx); $h.Tx.Add($tx)
        $h.Total = @($st.BytesReceived, $st.BytesSent)
    }
    foreach ($name in @($script:Hist.Keys)) {
        $h = $script:Hist[$name]
        if (-not $seen.ContainsKey($name)) { $h.Rx.Add(0); $h.Tx.Add(0); $h.Total = @(0, 0) }
        while ($h.Rx.Count -gt $script:MaxPoints) { $h.Rx.RemoveAt(0); $h.Tx.RemoveAt(0) }
    }

    $legend.BeginUpdate(); $legend.Items.Clear()
    foreach ($name in ($script:Hist.Keys | Sort-Object)) {
        if (-not $seen.ContainsKey($name)) { continue }
        $h = $script:Hist[$name]
        $it = New-Object System.Windows.Forms.ListViewItem $name
        $it.ForeColor = $h.Color
        [void]$it.SubItems.Add((Format-Rate $h.Rx[$h.Rx.Count - 1]))
        [void]$it.SubItems.Add((Format-Rate $h.Tx[$h.Tx.Count - 1]))
        [void]$it.SubItems.Add(('{0:N1} MB' -f ($h.Total[0] / 1MB)))
        [void]$it.SubItems.Add(('{0:N1} MB' -f ($h.Total[1] / 1MB)))
        [void]$it.SubItems.Add($descs[$name])
        [void]$legend.Items.Add($it)
    }
    $legend.EndUpdate()
    if ($form.Visible -and $tabs.SelectedTab -eq $tabGraph) { $graph.Invalidate() }

    $sumRx = 0.0; $sumTx = 0.0
    foreach ($name in $seen.Keys) {
        $h = $script:Hist[$name]
        $sumRx += $h.Rx[$h.Rx.Count - 1]; $sumTx += $h.Tx[$h.Tx.Count - 1]
    }
    $tip = "NetMonitor`n↓ $(Format-Rate $sumRx)  ↑ $(Format-Rate $sumTx)"
    $notify.Text = $tip.Substring(0, [math]::Min(63, $tip.Length))
}

$graph.Add_Paint({
    param($s, $e)
    $g = $e.Graphics; $g.SmoothingMode = 'AntiAlias'
    $padL = 80; $padR = 12; $padT = 10; $padB = 12
    $w = $s.ClientSize.Width - $padL - $padR; $hgt = $s.ClientSize.Height - $padT - $padB
    if ($w -lt 20 -or $hgt -lt 20) { return }

    $max = 10KB
    foreach ($h in $script:Hist.Values) {
        foreach ($v in $h.Rx) { if ($v -gt $max) { $max = $v } }
        foreach ($v in $h.Tx) { if ($v -gt $max) { $max = $v } }
    }
    $max = $max * 1.1

    $gridPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::Gainsboro)
    for ($i = 0; $i -le 4; $i++) {
        $y = $padT + $hgt * $i / 4
        $g.DrawLine($gridPen, [single]$padL, [single]$y, [single]($padL + $w), [single]$y)
        $g.DrawString((Format-Rate ($max * (4 - $i) / 4)), $s.Font, [System.Drawing.Brushes]::DimGray, [single]2, [single]($y - 7))
    }
    $gridPen.Dispose()

    $step = $w / ($script:MaxPoints - 1)
    foreach ($name in $script:Hist.Keys) {
        $h = $script:Hist[$name]
        $n = $h.Rx.Count; if ($n -lt 2) { continue }
        foreach ($kind in 'Rx', 'Tx') {
            $list = $h[$kind]
            $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
            for ($i = 0; $i -lt $n; $i++) {
                $x = $padL + ($script:MaxPoints - $n + $i) * $step
                $y = $padT + $hgt - ($list[$i] / $max) * $hgt
                $pts.Add((New-Object System.Drawing.PointF ([single]$x), ([single]$y)))
            }
            $pen = New-Object System.Drawing.Pen ($h.Color, 2)
            if ($kind -eq 'Tx') { $pen.DashStyle = 'Dash' }
            $g.DrawLines($pen, $pts.ToArray()); $pen.Dispose()
        }
    }
})

# ── 탭 3: 라우팅 규칙 ───────────────────────────────────────
$ruleTop = New-Object System.Windows.Forms.FlowLayoutPanel
$ruleTop.Dock = 'Top'; $ruleTop.Height = 38; $ruleTop.Padding = New-Object System.Windows.Forms.Padding(4, 4, 0, 0)
$btnAdd = New-Button '규칙 추가'; $btnDel = New-Button '선택 삭제'; $btnSync = New-Button '지금 동기화'
$btnRefresh = New-Button '새로고침'; $btnDef = New-Button '기본 인터페이스 고정' 150; $btnDefClr = New-Button '고정 해제'
$ruleTop.Controls.AddRange(@($btnAdd, $btnDel, $btnSync, $btnRefresh, $btnDef, $btnDefClr))

$gridRules = New-Grid
foreach ($c in '목적지', 'IP', '인터페이스(우선순위)', '메트릭', '실제 경로') { [void]$gridRules.Columns.Add($c, $c) }
$gridRules.Columns['메트릭'].FillWeight = 40

$lblDefault = New-Object System.Windows.Forms.Label
$lblDefault.Dock = 'Bottom'; $lblDefault.Height = 48; $lblDefault.Padding = New-Object System.Windows.Forms.Padding(6, 6, 0, 0)
$tabRules.Controls.Add($gridRules); $tabRules.Controls.Add($ruleTop); $tabRules.Controls.Add($lblDefault)

function Update-Rules {
    $form.Cursor = 'WaitCursor'
    try {
        $gridRules.Rows.Clear()
        $script:RuleIps = @{}
        foreach ($r in (Get-Rules)) {
            $ip = Resolve-RuleIp $r.Target
            $script:RuleIps[$r.Target] = $ip
            $aliases = @($r.InterfaceAlias)
            if ($ip) {
                $rt = Get-NetRoute -DestinationPrefix "$ip/32" -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($rt) {
                    $an = (Get-NetAdapter -InterfaceIndex $rt.InterfaceIndex -ErrorAction SilentlyContinue).Name
                    $actual = if ($aliases -contains $an) { "$an (정상)" } else { "$an (불일치)" }
                }
                else { $actual = '라우트 없음' }
            }
            else { $actual = 'DNS 실패' }
            $i = $gridRules.Rows.Add($r.Target, $ip, ($aliases -join ' > '), $r.Metric, $actual)
            $gridRules.Rows[$i].Cells['실제 경로'].Style.ForeColor =
                if ($actual -like '*(정상)') { [System.Drawing.Color]::ForestGreen } else { [System.Drawing.Color]::Firebrick }
        }
        $pref = Get-DefaultPref
        $task = Get-ScheduledTask -TaskName 'WifiRouteSync' -ErrorAction SilentlyContinue
        $defText = if ($pref) { "기본 인터페이스 고정: $($pref.InterfaceAlias)" } else { '기본 인터페이스 고정: 없음 (Windows 자동)' }
        $taskText = if ($task) { "자동 동기화: 설치됨 ($($task.State))" } else { '자동 동기화: 설치 안 됨' }
        $lblDefault.Text = "$defText`r`n$taskText"
    }
    finally { $form.Cursor = 'Default' }
}

# 인터페이스 선택/순서 지정 다이얼로그. -Single이면 하나만, -NoTarget이면 목적지/메트릭 입력 없음.
function Show-RuleDialog {
    param([switch]$Single, [string]$Title, [switch]$NoTarget)
    $d = New-Object System.Windows.Forms.Form
    $d.Text = $Title; $d.StartPosition = 'CenterParent'
    $d.FormBorderStyle = 'FixedDialog'; $d.MaximizeBox = $false; $d.MinimizeBox = $false; $d.Font = $form.Font
    $y = 12
    $tb = New-Object System.Windows.Forms.TextBox
    if (-not $NoTarget) {
        $l = New-Object System.Windows.Forms.Label; $l.Text = '목적지 (도메인 또는 IP)'
        $l.Location = New-Object System.Drawing.Point(12, 12); $l.AutoSize = $true
        $tb.Location = New-Object System.Drawing.Point(12, 32); $tb.Width = 380
        $d.Controls.AddRange(@($l, $tb)); $y = 66
    }
    $l2 = New-Object System.Windows.Forms.Label
    $l2.Text = if ($Single) { '인터페이스 선택' } else { '인터페이스 체크 (위쪽이 우선순위 높음, 비활성이어도 예비로 가능)' }
    $l2.Location = New-Object System.Drawing.Point(12, $y); $l2.AutoSize = $true
    $clb = New-Object System.Windows.Forms.CheckedListBox
    $clb.Location = New-Object System.Drawing.Point(12, ($y + 22)); $clb.Size = New-Object System.Drawing.Size(340, 150); $clb.CheckOnClick = $true
    foreach ($a in (Get-NetAdapter | Sort-Object ifIndex)) { [void]$clb.Items.Add("$($a.Name)  [$($a.Status)]") }
    $up = New-Object System.Windows.Forms.Button; $up.Text = '▲'
    $up.Location = New-Object System.Drawing.Point(358, ($y + 22)); $up.Size = New-Object System.Drawing.Size(34, 28)
    $dn = New-Object System.Windows.Forms.Button; $dn.Text = '▼'
    $dn.Location = New-Object System.Drawing.Point(358, ($y + 54)); $dn.Size = New-Object System.Drawing.Size(34, 28)
    $move = {
        param($dir)
        $i = $clb.SelectedIndex; $j = $i + $dir
        if ($i -lt 0 -or $j -lt 0 -or $j -ge $clb.Items.Count) { return }
        $ti = $clb.Items[$i]; $tc = $clb.GetItemChecked($i); $tj = $clb.Items[$j]; $cj = $clb.GetItemChecked($j)
        $clb.Items[$i] = $tj; $clb.SetItemChecked($i, $cj)
        $clb.Items[$j] = $ti; $clb.SetItemChecked($j, $tc)
        $clb.SelectedIndex = $j
    }.GetNewClosure()
    $up.Add_Click({ & $move -1 }.GetNewClosure())
    $dn.Add_Click({ & $move 1 }.GetNewClosure())
    if ($Single) {
        $up.Visible = $false; $dn.Visible = $false
        $clb.Add_ItemCheck({
            param($s, $e)
            if ($e.NewValue -eq 'Checked') {
                for ($k = 0; $k -lt $s.Items.Count; $k++) { if ($k -ne $e.Index) { $s.SetItemChecked($k, $false) } }
            }
        })
    }
    $lm = New-Object System.Windows.Forms.Label; $lm.Text = '메트릭'
    $lm.Location = New-Object System.Drawing.Point(12, ($y + 185)); $lm.AutoSize = $true
    $num = New-Object System.Windows.Forms.NumericUpDown
    $num.Minimum = 1; $num.Maximum = 9999; $num.Value = 1; $num.Width = 70
    $num.Location = New-Object System.Drawing.Point(70, ($y + 182))
    if ($NoTarget) { $num.Visible = $false; $lm.Visible = $false }
    $ok = New-Object System.Windows.Forms.Button; $ok.Text = '확인'; $ok.DialogResult = 'OK'
    $ok.Location = New-Object System.Drawing.Point(222, ($y + 220))
    $cn = New-Object System.Windows.Forms.Button; $cn.Text = '취소'; $cn.DialogResult = 'Cancel'
    $cn.Location = New-Object System.Drawing.Point(312, ($y + 220))
    $d.AcceptButton = $ok; $d.CancelButton = $cn
    $d.ClientSize = New-Object System.Drawing.Size(410, ($y + 260))
    $d.Controls.AddRange(@($l2, $clb, $up, $dn, $lm, $num, $ok, $cn))
    if ($d.ShowDialog($form) -ne 'OK') { return $null }
    $names = @(foreach ($i in $clb.CheckedIndices) { ($clb.Items[$i] -replace '\s+\[[^\]]*\]$', '') })
    if ($names.Count -eq 0 -or (-not $NoTarget -and [string]::IsNullOrWhiteSpace($tb.Text))) {
        [void][System.Windows.Forms.MessageBox]::Show('목적지와 인터페이스를 입력/선택하세요.')
        return $null
    }
    return [PSCustomObject]@{ Target = $tb.Text.Trim(); Interfaces = $names; Metric = [int]$num.Value }
}

$btnAdd.Add_Click({
    $r = Show-RuleDialog -Title '규칙 추가 (목적지 → 인터페이스)'
    if (-not $r) { return }
    Invoke-RouteTool "-AddTarget `"$($r.Target)`" -Interface `"$($r.Interfaces -join ',')`" -Metric $($r.Metric)"
    Update-Rules
})
$btnDel.Add_Click({
    if ($gridRules.SelectedRows.Count -eq 0) { return }
    $t = [string]$gridRules.SelectedRows[0].Cells['목적지'].Value
    if ([System.Windows.Forms.MessageBox]::Show("'$t' 규칙을 삭제할까요?", '삭제 확인', 'YesNo', 'Question') -ne 'Yes') { return }
    Invoke-RouteTool "-RemoveTarget `"$t`""
    Update-Rules
})
$btnSync.Add_Click({ Invoke-RouteTool '-Sync'; Update-Rules })
$btnRefresh.Add_Click({ Update-Rules })
$btnDef.Add_Click({
    $r = Show-RuleDialog -Single -NoTarget -Title '기본 인터페이스 고정'
    if (-not $r) { return }
    Invoke-RouteTool "-SetDefault `"$($r.Interfaces[0])`""
    Update-Rules
})
$btnDefClr.Add_Click({ Invoke-RouteTool '-ClearDefault'; Update-Rules })

# ── 타이머/이벤트 ───────────────────────────────────────────
$tmFast = New-Object System.Windows.Forms.Timer; $tmFast.Interval = 1000
$tmFast.Add_Tick({ try { Update-Throughput } catch {} })
$tmConn = New-Object System.Windows.Forms.Timer; $tmConn.Interval = 3000
$tmConn.Add_Tick({ if ($form.Visible -and $tabs.SelectedTab -eq $tabConn) { try { Update-Connections } catch {} } })
$tabs.Add_SelectedIndexChanged({
    if ($tabs.SelectedTab -eq $tabConn) { Update-Connections }
    elseif ($tabs.SelectedTab -eq $tabRules) { Update-Rules }
})
$cmbAdapter.Add_SelectedIndexChanged({ Update-Connections })
$chkLoopback.Add_CheckedChanged({ Update-Connections })
$chkListen.Add_CheckedChanged({ Update-Connections })

# ── 트레이 ──────────────────────────────────────────────────
$script:StartupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'NetMonitor.lnk'
$script:Exiting = $false
$form.Icon = [System.Drawing.SystemIcons]::Application

function Show-MainWindow {
    $form.Show(); $form.WindowState = 'Normal'; $form.Activate()
    Update-Connections
}

function Set-StartupEnabled {
    param([bool]$On)
    if ($On) {
        $ws = New-Object -ComObject WScript.Shell
        $l = $ws.CreateShortcut($script:StartupLnk)
        $l.TargetPath = 'powershell.exe'
        $l.Arguments = "-STA -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Tray"
        $l.WorkingDirectory = $script:ScriptDir; $l.WindowStyle = 7; $l.Save()
    }
    elseif (Test-Path $script:StartupLnk) { Remove-Item $script:StartupLnk -Force }
}

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miOpen = $menu.Items.Add('열기')
$miStartup = New-Object System.Windows.Forms.ToolStripMenuItem '윈도우 시작 시 자동 실행'
$miStartup.CheckOnClick = $true
[void]$menu.Items.Add($miStartup)
[void]$menu.Items.Add('-')
$miExit = $menu.Items.Add('종료')
$menu.Add_Opening({ $miStartup.Checked = Test-Path $script:StartupLnk })
$miOpen.Add_Click({ Show-MainWindow })
$miStartup.Add_Click({ Set-StartupEnabled $miStartup.Checked })
$miExit.Add_Click({ $script:Exiting = $true; $notify.Visible = $false; $ctx.ExitThread() })

$notify = New-Object System.Windows.Forms.NotifyIcon
$notify.Icon = [System.Drawing.SystemIcons]::Application
$notify.Text = 'NetMonitor'; $notify.ContextMenuStrip = $menu; $notify.Visible = $true
$notify.Add_DoubleClick({ Show-MainWindow })

# 닫기(X) = 트레이로 숨김. 트레이 '종료'나 시스템 종료일 때만 실제 종료.
$form.Add_FormClosing({
    param($s, $e)
    if (-not $script:Exiting -and $e.CloseReason -eq 'UserClosing') { $e.Cancel = $true; $form.Hide() }
})

$form.Add_Shown({
    foreach ($r in (Get-Rules)) { $script:RuleIps[$r.Target] = Resolve-RuleIp $r.Target }   # '적용 규칙' 열용
    Update-Connections
})

$ctx = New-Object System.Windows.Forms.ApplicationContext
Update-Throughput
foreach ($r in (Get-Rules)) { $script:RuleIps[$r.Target] = Resolve-RuleIp $r.Target }
$tmFast.Start(); $tmConn.Start()
if (-not $Tray) { Show-MainWindow }
[System.Windows.Forms.Application]::Run($ctx)

$tmFast.Stop(); $tmConn.Stop(); $notify.Dispose(); $script:Mutex.ReleaseMutex()
