using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetMonitor
{
    // 트레이 상주 앱: 창은 필요할 때만 보이고, 속도 샘플링과 툴팁 갱신은 항상 동작
    public class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "NetMonitor";

        readonly ThroughputMonitor monitor = new ThroughputMonitor();
        readonly MainForm form;
        readonly NotifyIcon notify = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly ToolStripMenuItem miStartup = new ToolStripMenuItem("윈도우 시작 시 자동 실행");
        // 신호등: 정상(초록) / 라우팅 이상(노랑) / 유료망 사용 중(빨강). 숫자가 클수록 심각.
        enum State { Ok = 0, RuleBad = 1, Paid = 2 }
        readonly Settings settings = Settings.Load();
        readonly Icon[] icons = new Icon[3];
        State state = State.Ok;
        DateTime lastRuleCheck = DateTime.MinValue;
        volatile int ruleBad;
        volatile string ruleBadText = "";
        volatile bool checking;

        public TrayApp(bool startHidden, bool demo)
        {
            icons[(int)State.Ok] = MakeIcon(Color.FromArgb(46, 160, 67));
            icons[(int)State.RuleBad] = MakeIcon(Color.FromArgb(235, 170, 0));
            icons[(int)State.Paid] = MakeIcon(Color.FromArgb(214, 39, 40));
            form = new MainForm(monitor, settings, demo);
            form.Icon = icons[(int)State.Ok];
            IntPtr unused = form.Handle;      // 창을 띄우기 전에도 BeginInvoke가 되도록 핸들 생성

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripItem open = menu.Items.Add("열기");
            open.Click += delegate { ShowWindow(); };
            miStartup.CheckOnClick = true;
            miStartup.Click += delegate { SetStartup(miStartup.Checked); };
            menu.Items.Add(miStartup);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripItem exit = menu.Items.Add("종료");
            exit.Click += delegate { ExitApp(); };
            menu.Opening += delegate { miStartup.Checked = IsStartupEnabled(); };

            notify.Icon = icons[(int)State.Ok]; notify.Text = "NetMonitor"; notify.ContextMenuStrip = menu; notify.Visible = true;
            notify.DoubleClick += delegate { ShowWindow(); };

            monitor.Sample();
            timer.Interval = 1000;
            timer.Tick += delegate { Tick(); };
            timer.Start();

            form.RefreshRules();
            if (!startHidden) ShowWindow();
        }

        void Tick()
        {
            monitor.Sample();
            double rx, tx;
            monitor.Totals(out rx, out tx);
            StartRuleCheckIfDue();

            // 무료 목록(기본 Wi-Fi 4)에 없는 어댑터의 최근 3초 최대 송수신 속도
            double paid = 0; string paidName = null;
            foreach (Series s in monitor.Items.Values)
            {
                if (!s.Active || settings.IsFree(s)) continue;
                for (int i = Math.Max(0, s.Rx.Count - 3); i < s.Rx.Count; i++)
                {
                    double v = s.Rx[i] + s.Tx[i];
                    if (v > paid) { paid = v; paidName = s.Name; }
                }
            }
            bool paidActive = paid >= settings.PaidThresholdKBps * 1024;
            int bad = ruleBad;
            State next = paidActive ? State.Paid : (bad > 0 ? State.RuleBad : State.Ok);

            string tip, status; Color color;
            if (next == State.Paid)
            {
                string extra = bad > 0 ? " · 라우팅 이상 " + bad + "건" : "";
                tip = "유료망 " + paidName + " " + Store.FormatRate(paid);
                status = "● 유료망 사용 중: " + paidName + " " + Store.FormatRate(paid) + extra;
                color = Color.Firebrick;
            }
            else if (next == State.RuleBad)
            {
                tip = "라우팅 이상 " + bad + "건: " + ruleBadText;
                status = "● 라우팅 경로 이상 " + bad + "건: " + ruleBadText + "  (라우팅 규칙 탭에서 확인)";
                color = Color.Goldenrod;
            }
            else
            {
                tip = "정상 ↓" + Store.FormatRate(rx) + " ↑" + Store.FormatRate(tx);
                status = "● 정상 · 유료망 통신 없음 · ↓ " + Store.FormatRate(rx) + "  ↑ " + Store.FormatRate(tx);
                color = Color.SeaGreen;
            }
            notify.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;     // NotifyIcon.Text 한도
            form.SetStatus(status, color);

            if (next != state)
            {
                bool worse = next > state;
                state = next;
                notify.Icon = icons[(int)state];
                if (worse) notify.ShowBalloonTip(5000, "NetMonitor", status.Substring(2), ToolTipIcon.Warning);
            }
            form.RefreshThroughput();
        }

        // 규칙의 실제 경로 점검(DNS 포함)은 느릴 수 있어 30초마다 백그라운드에서 수행
        void StartRuleCheckIfDue()
        {
            if (checking || (DateTime.Now - lastRuleCheck).TotalSeconds < 30) return;
            checking = true; lastRuleCheck = DateTime.Now;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int count = 0; List<string> names = new List<string>();
                    foreach (Rule r in Store.LoadRulesWithStatus())
                        if (!r.Ok) { count++; if (names.Count < 2) names.Add(r.Target); }
                    ruleBadText = string.Join(", ", names.ToArray()) + (count > 2 ? " 외" : "");
                    ruleBad = count;
                }
                catch { }
                finally { checking = false; }
            });
        }

        void ShowWindow()
        {
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        void ExitApp()
        {
            timer.Stop();
            notify.Visible = false;
            form.AllowExit = true;
            form.Close();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { notify.Dispose(); timer.Dispose(); form.Dispose(); }
            base.Dispose(disposing);
        }

        // 시작프로그램 등록: HKCU Run 키 (관리자 권한 불필요)
        static bool IsStartupEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        static void SetStartup(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --tray");
                else k.DeleteValue(RunName, false);
            }
        }

        // 16x16 트레이 아이콘: 상태 색 바탕에 ↓ ↑ 화살표
        static Icon MakeIcon(Color back)
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (Brush bg = new SolidBrush(back))
                        g.FillEllipse(bg, 0, 0, 15, 15);
                    g.FillPolygon(Brushes.White, new PointF[] { new PointF(4, 7), new PointF(8, 7), new PointF(6, 11.5f) });
                    g.FillRectangle(Brushes.White, 5.2f, 3.5f, 1.6f, 4);
                    using (Brush o = new SolidBrush(Color.FromArgb(255, 170, 60)))
                    {
                        g.FillPolygon(o, new PointF[] { new PointF(9, 8), new PointF(13, 8), new PointF(11, 3.5f) });
                        g.FillRectangle(o, 10.2f, 8, 1.6f, 4);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { Native.DestroyIcon(h); }
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool tray = false, demo = false;
            foreach (string a in args)
            {
                if (a.Equals("--tray", StringComparison.OrdinalIgnoreCase) || a.Equals("-Tray", StringComparison.OrdinalIgnoreCase)) tray = true;
                if (a.Equals("--demo", StringComparison.OrdinalIgnoreCase)) demo = true;     // README 스크린샷용
            }

            // 중복 실행 방지 (데모 모드는 실행 중인 본 앱과 따로 뜰 수 있게 다른 이름 사용)
            bool created;
            using (Mutex mutex = new Mutex(true, demo ? @"Local\NetMonitor.Tim.Demo" : @"Local\NetMonitor.Tim", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(tray, demo));
            }
        }
    }
}
