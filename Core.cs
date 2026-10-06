using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
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
        public string Dns = "";             // 조회에 쓸 DNS 서버 (비어 있으면 시스템 기본)
        public bool AllRecords = true;      // 도메인의 A 레코드를 전부 라우트로 만들지(true) 첫 번째만 쓸지(false)
        public List<string> Ips = new List<string>();
        public string Actual = "";
        public bool Ok;

        // 표에 보여줄 요약: 하나면 그 IP, 여러 개면 "첫 IP 외 N개"
        public string Ip
        {
            get
            {
                if (Ips.Count == 0) return null;
                return Ips.Count == 1 ? Ips[0] : Ips[0] + " 외 " + (Ips.Count - 1) + "개";
            }
        }
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
                    if (d.ContainsKey("Dns") && d["Dns"] != null) r.Dns = Convert.ToString(d["Dns"]);
                    if (d.ContainsKey("AllRecords") && d["AllRecords"] != null) r.AllRecords = Convert.ToBoolean(d["AllRecords"]);
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
            Dictionary<int, string> names = AdapterNames();
            foreach (Rule r in rules)
            {
                // 규칙에 지정한 DNS 서버로 조회. WifiRoute.ps1과 같은 기준(전체/첫 번째, 최대 32개)으로 IP를 정한다.
                string err;
                List<IPAddress> ips = DnsLookup.Resolve(r.Target, r.Dns, out err);
                if (!r.AllRecords && ips.Count > 1) ips = ips.GetRange(0, 1);
                if (ips.Count > 32) ips = ips.GetRange(0, 32);
                if (ips.Count == 0) { r.Actual = "DNS 실패"; continue; }

                int bad = 0; string okName = null, badDesc = null;
                foreach (IPAddress ip in ips)
                {
                    r.Ips.Add(ip.ToString());
                    int idx = Native.BestInterface(ip);
                    string name;
                    if (idx < 0 || !names.TryGetValue(idx, out name)) { bad++; if (badDesc == null) badDesc = ip + " 경로 없음"; continue; }
                    if (r.Aliases.Contains(name)) { if (okName == null) okName = name; }
                    else { bad++; if (badDesc == null) badDesc = ip + " → " + name; }
                }
                r.Ok = bad == 0;
                if (ips.Count == 1)
                    r.Actual = r.Ok ? okName + " (정상)" : (badDesc.EndsWith("경로 없음") ? "경로 없음" : badDesc.Substring(badDesc.IndexOf("→") + 2) + " (불일치)");
                else
                    r.Actual = r.Ok ? okName + " (정상) · IP " + ips.Count + "개" : bad + "/" + ips.Count + "개 불일치: " + badDesc;
            }
            return rules;
        }

        // 인터페이스 인덱스 -> 어댑터 이름 (한 번에 만들어 여러 IP 판정에 재사용)
        public static Dictionary<int, string> AdapterNames()
        {
            Dictionary<int, string> map = new Dictionary<int, string>();
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    IPv4InterfaceProperties p = n.GetIPProperties().GetIPv4Properties();
                    if (p != null) map[p.Index] = n.Name;
                }
                catch { }
            }
            return map;
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
    // ── DNS 조회: 지정한 DNS 서버로 A 레코드 "전체"를 가져온다 ──────────
    // .NET Framework에는 서버를 지정하는 리졸버가 없어서 UDP 53으로 직접 질의한다.
    // 서버를 비우면 시스템 기본 DNS(Dns.GetHostAddresses)를 쓴다.
    public static class DnsLookup
    {
        public static List<IPAddress> Resolve(string host, string server, out string error)
        {
            error = null;
            List<IPAddress> res = new List<IPAddress>();
            IPAddress lit;
            if (IPAddress.TryParse(host, out lit)) { res.Add(lit); return res; }
            try
            {
                if (string.IsNullOrEmpty(server))
                {
                    foreach (IPAddress a in Dns.GetHostAddresses(host))
                        if (a.AddressFamily == AddressFamily.InterNetwork && !res.Contains(a)) res.Add(a);
                    if (res.Count == 0) error = "IPv4 주소가 없습니다";
                    return res;
                }
                IPAddress srv;
                if (!IPAddress.TryParse(server.Trim(), out srv) || srv.AddressFamily != AddressFamily.InterNetwork)
                { error = "DNS 서버는 IPv4 주소로 입력하세요: " + server; return res; }
                return Query(host, srv, 3000, out error);
            }
            catch (Exception ex) { error = ex.Message; return res; }
        }

        static List<IPAddress> Query(string host, IPAddress server, int timeoutMs, out string error)
        {
            error = null;
            List<IPAddress> res = new List<IPAddress>();
            string ascii;
            try { ascii = new System.Globalization.IdnMapping().GetAscii(host.Trim().TrimEnd('.')); }
            catch { error = "도메인 형식이 올바르지 않습니다"; return res; }

            ushort id = (ushort)new Random().Next(1, 65535);
            List<byte> q = new List<byte>();
            q.Add((byte)(id >> 8)); q.Add((byte)id);
            q.Add(0x01); q.Add(0x00);                                   // 플래그: 재귀 요청
            q.Add(0); q.Add(1); q.Add(0); q.Add(0); q.Add(0); q.Add(0); q.Add(0); q.Add(0);   // 질문 1개
            foreach (string label in ascii.Split('.'))
            {
                byte[] lb = Encoding.ASCII.GetBytes(label);
                if (lb.Length == 0 || lb.Length > 63) { error = "도메인 형식이 올바르지 않습니다"; return res; }
                q.Add((byte)lb.Length); q.AddRange(lb);
            }
            q.Add(0); q.Add(0); q.Add(1); q.Add(0); q.Add(1);          // 종류 A, 클래스 IN

            byte[] r;
            using (UdpClient udp = new UdpClient(AddressFamily.InterNetwork))
            {
                udp.Client.ReceiveTimeout = timeoutMs; udp.Client.SendTimeout = timeoutMs;
                udp.Connect(new IPEndPoint(server, 53));
                byte[] qb = q.ToArray();
                udp.Send(qb, qb.Length);
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                try { r = udp.Receive(ref ep); }
                catch (SocketException) { error = "DNS 서버 응답 시간 초과 (" + server + ")"; return res; }
            }

            try
            {
                if (r.Length < 12 || r[0] != (byte)(id >> 8) || r[1] != (byte)id) { error = "잘못된 DNS 응답"; return res; }
                int rcode = r[3] & 0x0F;
                if (rcode != 0) { error = rcode == 3 ? "존재하지 않는 도메인입니다 (NXDOMAIN)" : "DNS 오류 코드 " + rcode; return res; }
                int qd = (r[4] << 8) | r[5], an = (r[6] << 8) | r[7], pos = 12;
                for (int i = 0; i < qd; i++) pos = SkipName(r, pos) + 4;
                for (int i = 0; i < an; i++)
                {
                    pos = SkipName(r, pos);
                    int type = (r[pos] << 8) | r[pos + 1];
                    int rdlen = (r[pos + 8] << 8) | r[pos + 9];
                    pos += 10;
                    if (type == 1 && rdlen == 4)
                    {
                        IPAddress ip = new IPAddress(new byte[] { r[pos], r[pos + 1], r[pos + 2], r[pos + 3] });
                        if (!res.Contains(ip)) res.Add(ip);
                    }
                    pos += rdlen;
                }
                if ((r[2] & 0x02) != 0) error = "응답이 잘려서 일부 주소만 표시될 수 있습니다";
                if (res.Count == 0 && error == null) error = "IPv4(A) 레코드가 없습니다";
            }
            catch (IndexOutOfRangeException) { error = "DNS 응답을 해석하지 못했습니다"; }
            return res;
        }

        // 이름 필드를 건너뛴 다음 위치. 압축 포인터(상위 2비트 11)면 2바이트로 끝난다.
        static int SkipName(byte[] r, int pos)
        {
            while (true)
            {
                int len = r[pos];
                if (len == 0) return pos + 1;
                if ((len & 0xC0) == 0xC0) return pos + 2;
                pos += len + 1;
            }
        }
    }

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
        public bool Configured;      // 사용자가 무료/유료 어댑터를 한 번이라도 저장했는지

        static string PathOf { get { return Path.Combine(Store.Dir, "NetMonitor.settings.json"); } }

        public static Settings Load()
        {
            // 파일이 없으면 미설정(Configured=false): 어떤 어댑터가 무료인지 가정하지 않고,
            // 사용자가 설정 탭에서 저장하기 전까지 유료망 감지를 켜지 않는다.
            Settings s = new Settings();
            try
            {
                if (!File.Exists(PathOf)) return s;
                Dictionary<string, object> d = new JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(PathOf)) as Dictionary<string, object>;
                if (d == null) return s;
                // Configured 키가 없는 예전 파일은 이미 설정해 쓰던 것이므로 설정됨으로 간주
                s.Configured = !d.ContainsKey("Configured") || Convert.ToBoolean(d["Configured"]);
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
                Configured = true;
                d["Configured"] = true; d["FreeAdapters"] = list; d["PaidThresholdKBps"] = PaidThresholdKBps;
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
