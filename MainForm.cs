using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace NetMonitor
{
    // ── 송수신 그래프 ───────────────────────────────────────────
    public class GraphPanel : Panel
    {
        readonly ThroughputMonitor mon;

        public GraphPanel(ThroughputMonitor monitor)
        {
            mon = monitor;
            DoubleBuffered = true;
            BackColor = Color.White;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int padL = 80, padR = 12, padT = 10, padB = 12;
            int w = ClientSize.Width - padL - padR, h = ClientSize.Height - padT - padB;
            if (w < 20 || h < 20) return;

            double max = 10 * 1024;
            foreach (Series s in mon.Items.Values)
            {
                foreach (double v in s.Rx) if (v > max) max = v;
                foreach (double v in s.Tx) if (v > max) max = v;
            }
            max *= 1.1;

            using (Pen grid = new Pen(Color.Gainsboro))
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = padT + h * i / 4f;
                    g.DrawLine(grid, padL, y, padL + w, y);
                    g.DrawString(Store.FormatRate(max * (4 - i) / 4), Font, Brushes.DimGray, 2, y - 7);
                }
            }
            float step = w / (float)(ThroughputMonitor.MaxPoints - 1);
            foreach (Series s in mon.Items.Values)
            {
                int n = s.Rx.Count;
                if (n < 2) continue;
                for (int k = 0; k < 2; k++)
                {
                    List<double> list = k == 0 ? s.Rx : s.Tx;
                    PointF[] pts = new PointF[n];
                    for (int i = 0; i < n; i++)
                        pts[i] = new PointF(padL + (ThroughputMonitor.MaxPoints - n + i) * step,
                                            (float)(padT + h - list[i] / max * h));
                    using (Pen pen = new Pen(s.Color, 2))
                    {
                        if (k == 1) pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                        g.DrawLines(pen, pts);
                    }
                }
            }
        }
    }

    // ── 규칙 추가 / 기본 인터페이스 선택 다이얼로그 ─────────────────
    public class RuleDialog : Form
    {
        readonly TextBox tbTarget = new TextBox();
        readonly CheckedListBox clb = new CheckedListBox();
        readonly NumericUpDown num = new NumericUpDown();
        readonly bool noTargetMode;

        public string Target { get { return tbTarget.Text.Trim(); } }
        public int Metric { get { return (int)num.Value; } }
        public List<string> Interfaces = new List<string>();

        public RuleDialog(string title, bool single, bool noTarget)
        {
            noTargetMode = noTarget;
            Text = title; StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false; Font = SystemFonts.MessageBoxFont;
            int y = 12;
            if (!noTarget)
            {
                Label l = new Label { Text = "목적지 (도메인 또는 IP)", Left = 12, Top = 12, AutoSize = true };
                tbTarget.SetBounds(12, 32, 380, 24);
                Controls.Add(l); Controls.Add(tbTarget); y = 66;
            }
            Controls.Add(new Label
            {
                Text = single ? "인터페이스 선택" : "인터페이스 체크 (위쪽이 우선순위 높음, 비활성이어도 예비로 가능)",
                Left = 12, Top = y, AutoSize = true
            });
            clb.SetBounds(12, y + 22, 340, 150); clb.CheckOnClick = true;
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                clb.Items.Add(n.Name + "  [" + n.OperationalStatus + "]");
            }
            Controls.Add(clb);
            if (single) clb.ItemCheck += OnSingleCheck;
            else
            {
                Button up = new Button { Text = "▲", Left = 358, Top = y + 22, Width = 34, Height = 28 };
                Button dn = new Button { Text = "▼", Left = 358, Top = y + 54, Width = 34, Height = 28 };
                up.Click += delegate { MoveItem(-1); }; dn.Click += delegate { MoveItem(1); };
                Controls.Add(up); Controls.Add(dn);
            }
            if (!noTarget)
            {
                Controls.Add(new Label { Text = "메트릭", Left = 12, Top = y + 185, AutoSize = true });
                num.Minimum = 1; num.Maximum = 9999; num.Value = 1; num.SetBounds(70, y + 182, 70, 24);
                Controls.Add(num);
            }
            Button ok = new Button { Text = "확인", Left = 222, Top = y + 220, DialogResult = DialogResult.OK };
            Button cancel = new Button { Text = "취소", Left = 312, Top = y + 220, DialogResult = DialogResult.Cancel };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            ClientSize = new Size(410, y + 260);
        }

        void OnSingleCheck(object s, ItemCheckEventArgs e)
        {
            if (e.NewValue != CheckState.Checked) return;
            for (int i = 0; i < clb.Items.Count; i++) if (i != e.Index) clb.SetItemChecked(i, false);
        }

        void MoveItem(int dir)
        {
            int i = clb.SelectedIndex, j = i + dir;
            if (i < 0 || j < 0 || j >= clb.Items.Count) return;
            object ti = clb.Items[i], tj = clb.Items[j];
            bool ci = clb.GetItemChecked(i), cj = clb.GetItemChecked(j);
            clb.Items[i] = tj; clb.SetItemChecked(i, cj);
            clb.Items[j] = ti; clb.SetItemChecked(j, ci);
            clb.SelectedIndex = j;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                Interfaces.Clear();
                foreach (int i in clb.CheckedIndices)
                {
                    string s = clb.Items[i].ToString();
                    Interfaces.Add(s.Substring(0, s.LastIndexOf("  [", StringComparison.Ordinal)));
                }
                if (Interfaces.Count == 0 || (!noTargetMode && Target.Length == 0))
                {
                    MessageBox.Show(this, "목적지와 인터페이스를 입력/선택하세요.");
                    e.Cancel = true; return;
                }
            }
            base.OnFormClosing(e);
        }
    }

    // ── 메인 창 ─────────────────────────────────────────────────
    public class MainForm : Form
    {
        readonly ThroughputMonitor mon;
        readonly Settings settings;
        readonly bool demo;      // --demo: 스크린샷용 가짜 연결/규칙 표시, 개인 정보(MAC) 가림, 설정 저장 안 함
        readonly TabControl tabs = new TabControl();
        readonly TabPage tabConn = new TabPage("연결"), tabGraph = new TabPage("송수신 그래프"),
            tabRules = new TabPage("라우팅 규칙"), tabSettings = new TabPage("설정");

        // 연결
        readonly DataGridView gridConn = NewGrid();
        readonly ComboBox cmbAdapter = new ComboBox();
        readonly CheckBox chkLoop = new CheckBox(), chkListen = new CheckBox();
        readonly Label lblCount = new Label();
        readonly System.Windows.Forms.Timer connTimer = new System.Windows.Forms.Timer();
        Dictionary<string, string> ruleByIp = new Dictionary<string, string>();

        // 그래프
        readonly GraphPanel graph;
        readonly ListView legend = new ListView();

        // 규칙
        readonly DataGridView gridRules = NewGrid();
        readonly Label lblInfo = new Label();
        readonly List<Button> ruleButtons = new List<Button>();

        public bool AllowExit;
        readonly Label statusLabel = new Label();

        public void SetStatus(string text, Color color)
        {
            statusLabel.Text = text; statusLabel.BackColor = color;
        }

        public MainForm(ThroughputMonitor monitor, Settings cfg, bool demoMode)
        {
            mon = monitor; settings = cfg; demo = demoMode;
            graph = new GraphPanel(mon);
            Text = "NetMonitor v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            Size = new Size(960, 600); StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;

            BuildConnTab(); BuildGraphTab(); BuildRulesTab(); BuildSettingsTab();
            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.AddRange(new TabPage[] { tabConn, tabGraph, tabRules, tabSettings });
            Controls.Add(tabs);

            // 하단 상태 표시줄: 트레이 아이콘과 같은 신호등 상태를 보여줌
            statusLabel.Dock = DockStyle.Bottom; statusLabel.Height = 26;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft; statusLabel.Padding = new Padding(8, 0, 0, 0);
            statusLabel.ForeColor = Color.White; statusLabel.BackColor = Color.SeaGreen; statusLabel.Text = "상태 확인 중...";
            Controls.Add(statusLabel);

            connTimer.Interval = 3000;
            connTimer.Tick += delegate { if (Visible && tabs.SelectedTab == tabConn) RefreshConnections(); };
            connTimer.Start();
            tabs.SelectedIndexChanged += delegate
            {
                if (tabs.SelectedTab == tabConn) RefreshConnections();
                else if (tabs.SelectedTab == tabRules) RefreshRules();
                else if (tabs.SelectedTab == tabSettings) RefreshSettingsList();
            };
            VisibleChanged += delegate { if (Visible) { RefreshConnections(); RefreshThroughput(); } };
        }

        static DataGridView NewGrid()
        {
            DataGridView g = new DataGridView();
            g.Dock = DockStyle.Fill; g.ReadOnly = true; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false;
            g.RowHeadersVisible = false; g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = false;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; g.BackgroundColor = SystemColors.Window;
            g.BorderStyle = BorderStyle.None; g.AllowUserToResizeRows = false;
            return g;
        }

        // 창을 닫으면 종료하지 않고 트레이로 숨김
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }

        // ── 연결 탭 ──
        void BuildConnTab()
        {
            FlowLayoutPanel top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4, 4, 0, 0) };
            top.Controls.Add(new Label { Text = "어댑터:", AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            cmbAdapter.DropDownStyle = ComboBoxStyle.DropDownList; cmbAdapter.Width = 140;
            cmbAdapter.Items.Add("(전체)"); cmbAdapter.SelectedIndex = 0;
            cmbAdapter.SelectedIndexChanged += delegate { RefreshConnections(); };
            chkLoop.Text = "로컬(127.x) 연결 포함"; chkLoop.AutoSize = true; chkLoop.Margin = new Padding(12, 3, 0, 0);
            chkListen.Text = "수신 대기 포함"; chkListen.AutoSize = true; chkListen.Margin = new Padding(12, 3, 0, 0);
            chkLoop.CheckedChanged += delegate { RefreshConnections(); };
            chkListen.CheckedChanged += delegate { RefreshConnections(); };
            lblCount.AutoSize = true; lblCount.Margin = new Padding(16, 5, 0, 0);
            top.Controls.AddRange(new Control[] { cmbAdapter, chkLoop, chkListen, lblCount });

            foreach (string c in new string[] { "프로세스", "PID", "로컬 주소", "원격 주소", "상태", "어댑터", "적용 규칙" })
                gridConn.Columns.Add(c, c);
            gridConn.Columns[1].FillWeight = 40; gridConn.Columns[4].FillWeight = 60;
            tabConn.Controls.Add(gridConn); tabConn.Controls.Add(top);
        }

        public void RefreshConnections()
        {
            if (demo) { ShowDemoConnections(); return; }
            Dictionary<string, string> ipMap = new Dictionary<string, string>();
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
                foreach (UnicastIPAddressInformation u in n.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork) ipMap[u.Address.ToString()] = n.Name;

            Dictionary<int, string> procs = new Dictionary<int, string>();
            foreach (Process p in Process.GetProcesses()) { procs[p.Id] = p.ProcessName; p.Dispose(); }

            bool loop = chkLoop.Checked, listen = chkListen.Checked;
            string filter = cmbAdapter.SelectedItem as string;
            List<object[]> rows = new List<object[]>();
            foreach (TcpRow r in Native.GetTcp())
            {
                bool est = r.State == 5, lis = r.State == 2;
                if (!(est || (listen && lis))) continue;
                string local = r.Local.ToString(), remote = r.Remote.ToString();
                if (!loop && (local.StartsWith("127.") || remote.StartsWith("127."))) continue;
                string ad;
                if (!ipMap.TryGetValue(local, out ad)) ad = "(전체/미지정)";
                if (ad[0] != '(' && !cmbAdapter.Items.Contains(ad)) cmbAdapter.Items.Add(ad);
                if (filter != null && filter != "(전체)" && ad != filter) continue;
                string pn, rule;
                procs.TryGetValue(r.Pid, out pn); ruleByIp.TryGetValue(remote, out rule);
                rows.Add(new object[] { pn, r.Pid, local + ":" + r.LocalPort, remote + ":" + r.RemotePort, r.StateName, ad, rule });
            }
            rows.Sort(delegate (object[] a, object[] b)
            {
                int c = string.Compare((string)a[5], (string)b[5], StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare((string)a[0], (string)b[0], StringComparison.OrdinalIgnoreCase);
            });

            int first = gridConn.FirstDisplayedScrollingRowIndex;
            gridConn.SuspendLayout(); gridConn.Rows.Clear();
            foreach (object[] row in rows) gridConn.Rows.Add(row);
            if (first > 0 && first < gridConn.Rows.Count) gridConn.FirstDisplayedScrollingRowIndex = first;
            gridConn.ResumeLayout();
            lblCount.Text = "연결 " + rows.Count + "개";
        }

        // ── 그래프 탭 ──
        void BuildGraphTab()
        {
            legend.Dock = DockStyle.Bottom; legend.Height = 120; legend.View = View.Details; legend.FullRowSelect = true;
            legend.Columns.Add("어댑터", 160); legend.Columns.Add("↓ 수신", 110); legend.Columns.Add("↑ 송신", 110);
            legend.Columns.Add("누적 수신", 110); legend.Columns.Add("누적 송신", 110); legend.Columns.Add("설명", 300);
            Label note = new Label { Dock = DockStyle.Top, Height = 22, Text = "  실선 = 수신(↓), 점선 = 송신(↑) · 최근 60초" };
            graph.Dock = DockStyle.Fill;
            tabGraph.Controls.Add(graph); tabGraph.Controls.Add(note); tabGraph.Controls.Add(legend);
        }

        // TrayApp의 1초 타이머가 샘플링 후 호출
        public void RefreshThroughput()
        {
            if (!Visible) return;
            legend.BeginUpdate(); legend.Items.Clear();
            List<string> names = new List<string>(mon.Items.Keys); names.Sort();
            foreach (string name in names)
            {
                Series s = mon.Items[name];
                if (!s.Active || s.Rx.Count == 0) continue;
                ListViewItem it = new ListViewItem(name);
                it.ForeColor = s.Color;
                it.SubItems.Add(Store.FormatRate(s.Rx[s.Rx.Count - 1]));
                it.SubItems.Add(Store.FormatRate(s.Tx[s.Tx.Count - 1]));
                it.SubItems.Add((s.TotalRx / 1048576.0).ToString("N1") + " MB");
                it.SubItems.Add((s.TotalTx / 1048576.0).ToString("N1") + " MB");
                it.SubItems.Add(s.Description);
                legend.Items.Add(it);
            }
            legend.EndUpdate();
            if (tabs.SelectedTab == tabGraph) graph.Invalidate();
        }

        // ── 설정 탭: 무료/유료 어댑터 ──
        readonly ListView lvAdapters = new ListView();
        readonly NumericUpDown numThreshold = new NumericUpDown();
        readonly Label lblSaved = new Label();
        bool loadingList;

        void BuildSettingsTab()
        {
            Label help = new Label
            {
                Dock = DockStyle.Top, Height = 48, Padding = new Padding(6, 6, 6, 0),
                Text = "체크한 어댑터 = 무료, 체크하지 않은 어댑터 = 유료입니다. 어댑터 이름(Wi-Fi 2, 3, 4...)은 바뀔 수 있어서 " +
                       "MAC 주소로 같은 장치를 찾습니다. 유료 어댑터로 임계 속도 이상 통신하면 트레이 아이콘이 빨갛게 바뀝니다."
            };
            lvAdapters.Dock = DockStyle.Fill; lvAdapters.View = View.Details;
            lvAdapters.CheckBoxes = true; lvAdapters.FullRowSelect = true;
            lvAdapters.Columns.Add("이름", 130); lvAdapters.Columns.Add("장치 설명", 300); lvAdapters.Columns.Add("MAC", 150);
            lvAdapters.Columns.Add("상태", 100); lvAdapters.Columns.Add("구분", 60);
            lvAdapters.ItemChecked += delegate (object s, ItemCheckedEventArgs e)
            {
                if (!loadingList) e.Item.SubItems[4].Text = e.Item.Checked ? "무료" : "유료";
            };

            FlowLayoutPanel bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6, 6, 0, 0) };
            bottom.Controls.Add(new Label { Text = "유료 판정 임계 속도 (KB/s):", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            numThreshold.DecimalPlaces = 1; numThreshold.Minimum = 0; numThreshold.Maximum = 1000000;
            numThreshold.Increment = 1; numThreshold.Width = 80;
            bottom.Controls.Add(numThreshold);
            Button save = new Button { Text = "저장", Width = 80, Height = 26 };
            Button reload = new Button { Text = "되돌리기", Width = 80, Height = 26 };
            save.Click += delegate { SaveSettings(); };
            reload.Click += delegate { RefreshSettingsList(); lblSaved.Text = ""; };
            lblSaved.AutoSize = true; lblSaved.Margin = new Padding(8, 6, 0, 0);
            bottom.Controls.AddRange(new Control[] { save, reload, lblSaved });

            tabSettings.Controls.Add(lvAdapters); tabSettings.Controls.Add(help); tabSettings.Controls.Add(bottom);
        }

        string MacDisplay(string mac)
        {
            // 데모(스크린샷) 모드에서는 개인 식별 정보인 MAC 앞부분을 가림
            if (demo && mac != null && mac.Length >= 17) return "**-**-**-**-**-" + mac.Substring(15);
            return mac;
        }

        void AddAdapterRow(string name, string desc, string mac, string status, bool free)
        {
            ListViewItem it = new ListViewItem(name);
            it.SubItems.Add(desc); it.SubItems.Add(MacDisplay(mac)); it.SubItems.Add(status);
            it.SubItems.Add(free ? "무료" : "유료");
            it.Tag = new string[] { name, mac, desc };
            if (status != "연결됨") it.ForeColor = SystemColors.GrayText;
            it.Checked = free;
            lvAdapters.Items.Add(it);
        }

        public void SelectSettings()
        {
            tabs.SelectedTab = tabSettings;
            RefreshSettingsList();
        }

        public void RefreshSettingsList()
        {
            loadingList = true; lvAdapters.BeginUpdate(); lvAdapters.Items.Clear();
            List<FreeEntry> unmatched = new List<FreeEntry>(settings.FreeAdapters);
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                string mac = Store.MacOf(n), name = n.Name, desc = n.Description;
                unmatched.RemoveAll(delegate (FreeEntry e) { return e.Matches(name, mac, desc); });
                AddAdapterRow(name, desc, mac, n.OperationalStatus == OperationalStatus.Up ? "연결됨" : "끊김",
                    settings.IsFree(name, mac, desc));
            }
            // 지금은 없지만 무료로 저장해 둔 어댑터(USB 동글을 뽑아둔 경우 등)도 목록에 유지
            foreach (FreeEntry e in unmatched) AddAdapterRow(e.Name, e.Description, e.Mac, "없음(저장됨)", true);
            numThreshold.Value = (decimal)Math.Min(settings.PaidThresholdKBps, 1000000);
            lvAdapters.EndUpdate(); loadingList = false;
        }

        void SaveSettings()
        {
            List<FreeEntry> list = new List<FreeEntry>();
            foreach (ListViewItem it in lvAdapters.Items)
            {
                if (!it.Checked) continue;
                string[] t = (string[])it.Tag;
                FreeEntry e = new FreeEntry(); e.Name = t[0]; e.Mac = t[1]; e.Description = t[2];
                list.Add(e);
            }
            settings.FreeAdapters = list;                         // TrayApp과 같은 객체라 바로 적용됨
            settings.PaidThresholdKBps = (double)numThreshold.Value;
            if (!demo) settings.Save();
            lblSaved.Text = "저장됨 (" + DateTime.Now.ToString("HH:mm:ss") + ") - 바로 적용됩니다";
        }

        // ── 데모 데이터 (--demo, README 스크린샷용. 문서용 예약 IP 사용) ──
        void ShowDemoConnections()
        {
            object[][] rows = {
                new object[] { "chrome", 4120, "172.16.12.20:51324", "198.51.100.10:443", "Established", "Wi-Fi 4", null },
                new object[] { "chrome", 4120, "172.16.12.20:51330", "198.51.100.24:443", "Established", "Wi-Fi 4", null },
                new object[] { "Teams", 8844, "172.16.12.20:51402", "203.0.113.5:443", "Established", "Wi-Fi 4", null },
                new object[] { "OneDrive", 6012, "172.16.12.20:51477", "198.51.100.88:443", "Established", "Wi-Fi 4", null },
                new object[] { "code", 7320, "172.16.12.20:51520", "203.0.113.41:443", "Established", "Wi-Fi 4", null },
                new object[] { "ssh", 9210, "192.168.0.20:52011", "203.0.113.10:22", "Established", "Wi-Fi", "files.example.com" },
                new object[] { "rsync", 9288, "192.168.0.20:52140", "203.0.113.10:873", "Established", "Wi-Fi", "files.example.com" },
            };
            gridConn.Rows.Clear();
            foreach (object[] r in rows) gridConn.Rows.Add(r);
            lblCount.Text = "연결 " + rows.Length + "개";
        }

        void ShowDemoRules()
        {
            gridRules.Rows.Clear();
            object[][] rows = {
                new object[] { "files.example.com", "203.0.113.10", "Wi-Fi > 이더넷", 1, "Wi-Fi (정상)" },
                new object[] { "api.example.org", "203.0.113.25", "Wi-Fi > 이더넷", 1, "Wi-Fi (정상)" },
                new object[] { "cdn.example.net", "198.51.100.77", "Wi-Fi", 1, "Wi-Fi 4 (불일치)" },
            };
            foreach (object[] r in rows)
            {
                int i = gridRules.Rows.Add(r);
                bool ok = ((string)r[4]).EndsWith("(정상)");
                gridRules.Rows[i].Cells[4].Style.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
            }
            lblInfo.Text = "기본 인터페이스 고정: Wi-Fi 4\r\n자동 동기화: 설치됨 · 마지막 동기화 방금 전";
        }

        // ── 라우팅 규칙 탭 ──
        void BuildRulesTab()
        {
            FlowLayoutPanel top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(4, 4, 0, 0) };
            AddBtn(top, "규칙 추가", 90, OnAdd);
            AddBtn(top, "선택 삭제", 90, OnDelete);
            AddBtn(top, "지금 동기화", 100, delegate { RunTool("-Sync"); });
            AddBtn(top, "새로고침", 80, delegate { RefreshRules(); });
            AddBtn(top, "기본 인터페이스 고정", 150, OnSetDefault);
            AddBtn(top, "고정 해제", 80, delegate { RunTool("-ClearDefault"); });
            AddBtn(top, "자동 동기화 설치", 120, delegate { RunTool("-InstallTask"); });
            AddBtn(top, "제거", 60, delegate { RunTool("-UninstallTask"); });

            foreach (string c in new string[] { "목적지", "IP", "인터페이스(우선순위)", "메트릭", "실제 경로" }) gridRules.Columns.Add(c, c);
            gridRules.Columns[3].FillWeight = 40;
            lblInfo.Dock = DockStyle.Bottom; lblInfo.Height = 48; lblInfo.Padding = new Padding(6, 6, 0, 0);
            tabRules.Controls.Add(gridRules); tabRules.Controls.Add(top); tabRules.Controls.Add(lblInfo);
        }

        void AddBtn(FlowLayoutPanel p, string text, int w, EventHandler h)
        {
            Button b = new Button { Text = text, Width = w, Height = 26 };
            b.Click += h; ruleButtons.Add(b); p.Controls.Add(b);
        }

        void SetBusy(bool busy)
        {
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            foreach (Button b in ruleButtons) b.Enabled = !busy;
        }

        // DNS/라우트 조회는 느릴 수 있어 백그라운드에서 수행. 시작 시에도 한 번 불러 '적용 규칙' 열을 채움.
        public void RefreshRules()
        {
            if (demo) { ShowDemoRules(); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Rule> rules = Store.LoadRulesWithStatus();
                string def = Store.DefaultInterface();
                string task = Store.AutoSyncStatus();
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        gridRules.Rows.Clear();
                        Dictionary<string, string> map = new Dictionary<string, string>();
                        foreach (Rule r in rules)
                        {
                            int i = gridRules.Rows.Add(r.Target, r.Ip, string.Join(" > ", r.Aliases.ToArray()), r.Metric, r.Actual);
                            gridRules.Rows[i].Cells[4].Style.ForeColor = r.Ok ? Color.ForestGreen : Color.Firebrick;
                            if (r.Ip != null) map[r.Ip] = r.Target;
                        }
                        ruleByIp = map;
                        lblInfo.Text = (def != null ? "기본 인터페이스 고정: " + def : "기본 인터페이스 고정: 없음 (Windows 자동)")
                            + "\r\n자동 동기화: " + task;
                    });
                }
                catch (InvalidOperationException) { }   // 창 핸들이 아직 없거나 이미 닫힌 경우
            });
        }

        // WifiRoute.ps1 실행(UAC 대기 포함)은 백그라운드에서
        void RunTool(string args)
        {
            SetBusy(true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Store.RunRouteTool(args); } catch { }
                try { BeginInvoke((MethodInvoker)delegate { SetBusy(false); RefreshRules(); }); }
                catch (InvalidOperationException) { }
            });
        }

        void OnAdd(object s, EventArgs e)
        {
            using (RuleDialog d = new RuleDialog("규칙 추가 (목적지 → 인터페이스)", false, false))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                RunTool("-AddTarget \"" + d.Target + "\" -Interface \"" + string.Join(",", d.Interfaces.ToArray()) +
                        "\" -Metric " + d.Metric);
            }
        }

        void OnDelete(object s, EventArgs e)
        {
            if (gridRules.SelectedRows.Count == 0) return;
            string t = Convert.ToString(gridRules.SelectedRows[0].Cells[0].Value);
            if (MessageBox.Show(this, "'" + t + "' 규칙을 삭제할까요?", "삭제 확인", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes) return;
            RunTool("-RemoveTarget \"" + t + "\"");
        }

        void OnSetDefault(object s, EventArgs e)
        {
            using (RuleDialog d = new RuleDialog("기본 인터페이스 고정", true, true))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                RunTool("-SetDefault \"" + d.Interfaces[0] + "\"");
            }
        }
    }
}

