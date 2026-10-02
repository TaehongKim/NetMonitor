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

        // 동기화 로그의 마지막 '동기화 종료' 시각. 줄 형식: [2026-10-02 12:20:34] ===== 동기화 종료 =====
        public static DateTime? LastSyncTime()
        {
            try
            {
                string path = Path.Combine(Dir, "WifiRoute.log");
                if (!File.Exists(path)) return null;
                string tail;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = Math.Min(fs.Length, 16384);
                    fs.Seek(-len, SeekOrigin.End);
                    byte[] buf = new byte[len];
                    int read = fs.Read(buf, 0, buf.Length);
                    tail = System.Text.Encoding.UTF8.GetString(buf, 0, read);
                }
                string[] lines = tail.Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string l = lines[i].Trim();
                    if (l.Length < 21 || l[0] != '[' || l.IndexOf("동기화 종료", StringComparison.Ordinal) < 0) continue;
                    DateTime dt;
                    if (DateTime.TryParseExact(l.Substring(1, 19), "yyyy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt)) return dt;
                }
            }
            catch { }
            return null;
        }

        // 작업이 SYSTEM 소유라 일반 권한에서는 schtasks 조회가 "Access is denied"로 막힘.
        // 그래서 (1) 로그가 최근 갱신됐는지, (2) 조회 결과가 '없음'이 아닌 '권한 거부'인지로 판단한다.
        public static string AutoSyncStatus()
        {
            DateTime? last = LastSyncTime();
            string when = last.HasValue ? " · 마지막 동기화 " + last.Value.ToString("MM-dd HH:mm") : "";
            if (last.HasValue && (DateTime.Now - last.Value).TotalMinutes <= 10) return "설치됨" + when;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", "/query /tn WifiRouteSync");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0) return "설치됨" + when;
                    string lower = text.ToLowerInvariant();
                    if (lower.Contains("denied") || text.Contains("거부") || text.Contains("액세스"))
                        return "설치된 것으로 추정 (권한 때문에 조회 불가, 최근 동기화 기록 없음)" + when;
                }
            }
            catch { }
            return "설치 안 됨" + when;
        }

        // MAC 주소를 AA-BB-CC-DD-EE-FF 형식으로 (없으면 빈 문자열)
        public static string MacOf(NetworkInterface nic)
        {
            try
            {
                byte[] b = nic.GetPhysicalAddress().GetAddressBytes();
                return b.Length == 0 ? "" : BitConverter.ToString(b);
            }
            catch { return ""; }
        }

        public static string FormatRate(double bps)
        {
            if (bps >= 1048576) return (bps / 1048576).ToString("N1") + " MB/s";
            if (bps >= 1024) return (bps / 1024).ToString("N1") + " KB/s";
            return bps.ToString("N0") + " B/s";
        }
    }

    // ── 설정: 무료 어댑터 / 유료 판정 임계 속도 (NetMonitor.settings.json) ──
    // 무료로 지정한 어댑터. 이름(Wi-Fi 2/3/4...)은 재연결 때 바뀔 수 있어서 MAC과 장치 설명도 함께 기억한다.
    public class FreeEntry
    {
        public string Name = "", Mac = "", Description = "";

        // MAC이 있으면 MAC으로 우선 판단(가장 안정적). 없으면 이름 일치 또는 설명 일부 포함으로 판단.
        public bool Matches(string name, string mac, string description)
        {
            if (!string.IsNullOrEmpty(Mac) && !string.IsNullOrEmpty(mac))
                return string.Equals(Mac, mac, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(Name) && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrEmpty(Description) && description != null &&
                   description.IndexOf(Description, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public class Settings
    {
        public List<FreeEntry> FreeAdapters = new List<FreeEntry>();
        public double PaidThresholdKBps = 5;

        static string PathOf { get { return Path.Combine(Store.Dir, "NetMonitor.settings.json"); } }

        public static Settings Load()
        {
            Settings s = new Settings();
            FreeEntry def = new FreeEntry(); def.Name = "Wi-Fi 4";
            s.FreeAdapters.Add(def);
            try
            {
                if (!File.Exists(PathOf)) { s.Save(); return s; }   // 처음 실행하면 기본값 파일 생성
                Dictionary<string, object> d = new JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(PathOf)) as Dictionary<string, object>;
                if (d == null) return s;
                object[] arr = d.ContainsKey("FreeAdapters") ? d["FreeAdapters"] as object[] : null;
                if (arr != null)
                {
                    s.FreeAdapters.Clear();
                    foreach (object o in arr)
                    {
                        FreeEntry e = new FreeEntry();
                        Dictionary<string, object> eo = o as Dictionary<string, object>;
                        if (eo != null)    // 객체 형식: {"Name":..,"Mac":..,"Description":..}
                        {
                            if (eo.ContainsKey("Name")) e.Name = Convert.ToString(eo["Name"]);
                            if (eo.ContainsKey("Mac")) e.Mac = Convert.ToString(eo["Mac"]);
                            if (eo.ContainsKey("Description")) e.Description = Convert.ToString(eo["Description"]);
                        }
                        else { e.Name = Convert.ToString(o); e.Description = e.Name; }   // 예전 문자열 형식
                        s.FreeAdapters.Add(e);
                    }
                }
                if (d.ContainsKey("PaidThresholdKBps")) s.PaidThresholdKBps = Convert.ToDouble(d["PaidThresholdKBps"]);
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                List<object> list = new List<object>();
                foreach (FreeEntry e in FreeAdapters)
                {
                    Dictionary<string, object> eo = new Dictionary<string, object>();
                    eo["Name"] = e.Name; eo["Mac"] = e.Mac; eo["Description"] = e.Description;
                    list.Add(eo);
                }
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["FreeAdapters"] = list; d["PaidThresholdKBps"] = PaidThresholdKBps;
                File.WriteAllText(PathOf, new JavaScriptSerializer().Serialize(d));
            }
            catch { }
        }

        public bool IsFree(string name, string mac, string description)
        {
            foreach (FreeEntry e in FreeAdapters) if (e.Matches(name, mac, description)) return true;
            return false;
        }

        public bool IsFree(Series s) { return IsFree(s.Name, s.Mac, s.Description); }
    }

    // ── 어댑터별 송수신 속도 샘플러 ─────────────────────────────
    public class Series
    {
        public string Name, Description, Mac = "";
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
                s.Mac = Store.MacOf(nic);
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
