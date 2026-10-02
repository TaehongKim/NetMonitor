using System;
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
        Icon appIcon;

        public TrayApp(bool startHidden)
        {
            appIcon = MakeIcon();
            form = new MainForm(monitor);
            form.Icon = appIcon;
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

            notify.Icon = appIcon; notify.Text = "NetMonitor"; notify.ContextMenuStrip = menu; notify.Visible = true;
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
            string tip = "NetMonitor\n↓ " + Store.FormatRate(rx) + "  ↑ " + Store.FormatRate(tx);
            notify.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;     // NotifyIcon.Text 한도
            form.RefreshThroughput();
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

        // 16x16 트레이 아이콘: 파란 바탕에 ↓(흰색) ↑(주황) 화살표
        static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (Brush bg = new SolidBrush(Color.FromArgb(31, 119, 180)))
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
            bool tray = false;
            foreach (string a in args)
                if (a.Equals("--tray", StringComparison.OrdinalIgnoreCase) || a.Equals("-Tray", StringComparison.OrdinalIgnoreCase)) tray = true;

            // 중복 실행 방지
            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\NetMonitor.Tim", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(tray));
            }
        }
    }
}
