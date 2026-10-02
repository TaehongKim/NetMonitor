using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

namespace NetMonitor
{
    // ── Win32 (IP Helper) ───────────────────────────────────────
    public class TcpRow
    {
        public IPAddress Local, Remote;
        public int LocalPort, RemotePort, State, Pid;
        public string StateName
        {
            get
            {
                string[] n = { "", "Closed", "Listen", "SynSent", "SynReceived", "Established", "FinWait1", "FinWait2",
                               "CloseWait", "Closing", "LastAck", "TimeWait", "DeleteTcb" };
                return State >= 0 && State < n.Length ? n[State] : State.ToString();
            }
        }
    }

    public static class Native
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll")]
        static extern int GetBestInterface(uint destAddr, out uint ifIndex);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        static int Port(uint p) { return (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF)); }

        // IPv4 TCP 연결 + 소유 PID (TCP_TABLE_OWNER_PID_ALL)
        public static List<TcpRow> GetTcp()
        {
            List<TcpRow> rows = new List<TcpRow>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 5, 0);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, true, 2, 5, 0) != 0) return rows;
                int n = Marshal.ReadInt32(buf);
                long p = buf.ToInt64() + 4;
                for (int i = 0; i < n; i++, p += 24)
                {
                    IntPtr q = new IntPtr(p);
                    TcpRow r = new TcpRow();
                    r.State = Marshal.ReadInt32(q, 0);
                    r.Local = new IPAddress((long)(uint)Marshal.ReadInt32(q, 4));
                    r.LocalPort = Port((uint)Marshal.ReadInt32(q, 8));
                    r.Remote = new IPAddress((long)(uint)Marshal.ReadInt32(q, 12));
                    r.RemotePort = Port((uint)Marshal.ReadInt32(q, 16));
                    r.Pid = Marshal.ReadInt32(q, 20);
                    rows.Add(r);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return rows;
        }

        // 해당 목적지로 나갈 때 Windows가 실제로 고르는 인터페이스 인덱스 (실패 시 -1)
        public static int BestInterface(IPAddress dest)
        {
            uint idx;
            uint raw = BitConverter.ToUInt32(dest.GetAddressBytes(), 0);
            return GetBestInterface(raw, out idx) == 0 ? (int)idx : -1;
        }
    }

    // ── 라우팅 규칙/설정 (WifiRoute.ps1과 같은 파일 사용) ────────────
    public class Rule
    {
        public string Target;
        public List<string> Aliases = new List<string>();
        public int Metric = 1;
        public string Ip;
        public string Actual = "";
        public bool Ok;
    }

    public static class Store
    {
        public static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        public static string RouteScript { get { return Path.Combine(Dir, "WifiRoute.ps1"); } }
        static string ConfigPath { get { return Path.Combine(Dir, "WifiRoute.config.json"); } }
        static string DefaultPath { get { return Path.Combine(Dir, "WifiRoute.default.json"); } }

        public static List<Rule> LoadRules()
        {
            List<Rule> list = new List<Rule>();
            if (!File.Exists(ConfigPath)) return list;
            try
            {
                string raw = File.ReadAllText(ConfigPath);
                if (raw.Trim().Length == 0) return list;
                object parsed = new JavaScriptSerializer().DeserializeObject(raw);
                object[] arr = parsed as object[];
                if (arr == null) return list;
                foreach (object o in arr)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("Target")) continue;
                    Rule r = new Rule();
                    r.Target = Convert.ToString(d["Target"]);
                    if (d.ContainsKey("Metric")) r.Metric = Convert.ToInt32(d["Metric"]);
                    object a = d.ContainsKey("InterfaceAlias") ? d["InterfaceAlias"] : null;
                    object[] aa = a as object[];
                    if (aa != null) foreach (object x in aa) r.Aliases.Add(Convert.ToString(x));
                    else if (a != null) r.Aliases.Add(Convert.ToString(a));
                    list.Add(r);
                }
            }
            catch { }
            return list;
        }

        public static string DefaultInterface()
        {
            if (!File.Exists(DefaultPath)) return null;
            try
            {
                Dictionary<string, object> d = new JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(DefaultPath)) as Dictionary<string, object>;
                if (d != null && d.ContainsKey("InterfaceAlias")) return Convert.ToString(d["InterfaceAlias"]);
            }
            catch { }
            return null;
        }

        public static IPAddress Resolve(string target)
        {
            IPAddress ip;
            if (IPAddress.TryParse(target, out ip)) return ip;
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(target))
                    if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            }
            catch { }
            return null;
        }

        // 인덱스 -> 어댑터 이름
        public static string AdapterNameByIndex(int index)
        {
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    IPv4InterfaceProperties p = n.GetIPProperties().GetIPv4Properties();
                    if (p != null && p.Index == index) return n.Name;
                }
                catch { }
            }
            return null;
        }

        // 느린 작업(DNS) 포함 - 백그라운드 스레드에서 호출
        public static List<Rule> LoadRulesWithStatus()
        {
            List<Rule> rules = LoadRules();
            foreach (Rule r in rules)
            {
                IPAddress ip = Resolve(r.Target);
                if (ip == null) { r.Actual = "DNS 실패"; continue; }
                r.Ip = ip.ToString();
                string name = AdapterNameByIndex(Native.BestInterface(ip));
                if (name == null) { r.Actual = "경로 없음"; continue; }
                r.Ok = r.Aliases.Contains(name);
                r.Actual = name + (r.Ok ? " (정상)" : " (불일치)");
            }
            return rules;
        }

        // WifiRoute.ps1 실행 (스크립트가 필요 시 UAC로 스스로 승격)
        public static void RunRouteTool(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + RouteScript + "\" " + args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi)) { p.WaitForExit(); }
        }

        public static bool AutoSyncInstalled()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", "/query /tn WifiRouteSync");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        public static string FormatRate(double bps)
        {
            if (bps >= 1048576) return (bps / 1048576).ToString("N1") + " MB/s";
            if (bps >= 1024) return (bps / 1024).ToString("N1") + " KB/s";
            return bps.ToString("N0") + " B/s";
        }
    }

    // ── 어댑터별 송수신 속도 샘플러 ─────────────────────────────
    public class Series
    {
        public string Name, Description;
        public List<double> Rx = new List<double>(), Tx = new List<double>();
        public long LastRx, LastTx, TotalRx, TotalTx;
        public bool Active, WasUp;
        public Color Color;
    }

    public class ThroughputMonitor
    {
        public const int MaxPoints = 60;
        static readonly Color[] Palette = {
            Color.FromArgb(31, 119, 180), Color.FromArgb(214, 39, 40), Color.FromArgb(44, 160, 44),
            Color.FromArgb(255, 127, 14), Color.FromArgb(148, 103, 189), Color.FromArgb(23, 190, 207) };

        public readonly Dictionary<string, Series> Items = new Dictionary<string, Series>();
        readonly Stopwatch clock = Stopwatch.StartNew();
        double last;

        public void Sample()
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = now - last; if (dt <= 0) dt = 1;
            last = now;
            foreach (Series s in Items.Values) s.Active = false;

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                IPv4InterfaceStatistics st;
                try { st = nic.GetIPv4Statistics(); } catch { continue; }

                Series s;
                if (!Items.TryGetValue(nic.Name, out s))
                {
                    s = new Series();
                    s.Name = nic.Name; s.Color = Palette[Items.Count % Palette.Length];
                    Items[nic.Name] = s;
                }
                s.Description = nic.Description; s.Active = true;
                // 처음 보이거나 재연결된 직후에는 기준점만 잡고 0으로 기록 (누적 카운터 전체가 속도로 튀는 것 방지)
                double rx = 0, tx = 0;
                if (s.WasUp)
                {
                    rx = Math.Max(0, (st.BytesReceived - s.LastRx) / dt);
                    tx = Math.Max(0, (st.BytesSent - s.LastTx) / dt);
                }
                s.WasUp = true;
                s.Rx.Add(rx); s.Tx.Add(tx);
                s.LastRx = st.BytesReceived; s.LastTx = st.BytesSent;
                s.TotalRx = st.BytesReceived; s.TotalTx = st.BytesSent;
            }
            foreach (Series s in Items.Values)
            {
                if (!s.Active) { s.Rx.Add(0); s.Tx.Add(0); s.WasUp = false; s.TotalRx = 0; s.TotalTx = 0; }
                while (s.Rx.Count > MaxPoints) { s.Rx.RemoveAt(0); s.Tx.RemoveAt(0); }
            }
        }

        public void Totals(out double rx, out double tx)
        {
            rx = 0; tx = 0;
            foreach (Series s in Items.Values)
                if (s.Active && s.Rx.Count > 0) { rx += s.Rx[s.Rx.Count - 1]; tx += s.Tx[s.Tx.Count - 1]; }
        }
    }
}
