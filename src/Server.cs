// Hallow Remote - settings, and a tiny web server for your phone (no admin needed: a plain TCP listener on one address).
// Every request must start with the secret code (/<code>/...); anything else gets "Not found".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace HallowRemote
{
    [DataContract]
    public class RemoteSettings
    {
        [DataMember] public string Code = "";          // the secret part of the link
        [DataMember] public string Network = "tailscale";   // tailscale (anywhere, through Tailscale) or lan (home network only)
        [DataMember] public int Port = 5480;
        [DataMember] public bool TrayOnClose = true;
        [OnDeserializing] void D(StreamingContext c) { Code = ""; Network = "tailscale"; Port = 5480; TrayOnClose = true; }
        // HALLOW_REMOTE_TESTDIR (developer tests only): settings and the other apps' files come from that folder instead, so tests never touch the real ones
        public static string TestDir { get { var t = Environment.GetEnvironmentVariable("HALLOW_REMOTE_TESTDIR"); return string.IsNullOrEmpty(t) ? null : t; } }
        public static string Dir { get { return TestDir != null ? Path.Combine(TestDir, "HallowRemote") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HallowRemote"); } }
        static string FilePath { get { return Path.Combine(Dir, "remote.json"); } }
        public static bool Frozen;
        public static RemoteSettings Load()
        {
            RemoteSettings s = null;
            try { if (File.Exists(FilePath)) using (var f = File.OpenRead(FilePath)) s = (RemoteSettings)new DataContractJsonSerializer(typeof(RemoteSettings)).ReadObject(f); } catch { }
            if (s == null) s = new RemoteSettings();
            if (string.IsNullOrEmpty(s.Code)) { s.Code = NewCode(); s.Save(); }
            return s;
        }
        public void Save()
        {
            if (Frozen) return;
            try { Directory.CreateDirectory(Dir); using (var f = File.Create(FilePath)) new DataContractJsonSerializer(typeof(RemoteSettings)).WriteObject(f, this); } catch { }
        }
        public static string NewCode()
        {
            const string a = "abcdefghjkmnpqrstuvwxyz23456789"; var b = new byte[14]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return new string(b.Select(x => a[x % a.Length]).ToArray());
        }
    }

    public static class Net
    {
        public static string Tailscale() { return Addresses().Where(a => a.Item1.StartsWith("100.") && IsCgnat(a.Item1)).Select(a => a.Item1).FirstOrDefault(); }
        // v1.1: the home network address is a private one on a real adapter (not a VPN, WSL, Hyper-V or VirtualBox one), preferring the adapter with the router (default gateway)
        public static string Lan()
        {
            var list = Addresses().Where(a => IsPrivate(a.Item1)).ToList();
            return list.Where(a => a.Item2).Select(a => a.Item1).FirstOrDefault() ?? list.Select(a => a.Item1).FirstOrDefault();
        }
        static bool IsCgnat(string a) { var p = a.Split('.'); int b; return p.Length == 4 && int.TryParse(p[1], out b) && b >= 64 && b <= 127; }
        static bool IsPrivate(string a)
        {
            var p = a.Split('.'); int b; if (p.Length != 4 || !int.TryParse(p[1], out b)) return false;
            return p[0] == "10" || (p[0] == "192" && b == 168) || (p[0] == "172" && b >= 16 && b <= 31);
        }
        static readonly string[] Skip = { "Nord", "OpenVPN", "WireGuard", "Hyper-V", "vEthernet", "VirtualBox", "VMware", "WSL", "Tailscale", "Loopback", "TAP-", "Wintun" };
        // (address, its adapter has a default gateway)
        static List<Tuple<string, bool>> Addresses()
        {
            var res = new List<Tuple<string, bool>>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var props = ni.GetIPProperties();
                bool virt = Skip.Any(k => ni.Description.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 || ni.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                bool tailscale = ni.Description.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0 || ni.Name.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0;
                if (virt && !tailscale) continue;
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var u in props.UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork) { var s = u.Address.ToString(); if (!tailscale || IsCgnat(s)) res.Add(Tuple.Create(s, gw && !tailscale)); }
            }
            return res;
        }
    }

    public class Server
    {
        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX { public uint dwLength = 64; public uint dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

        readonly RemoteSettings S; TcpListener listener;
        public string Address, Error;
        public Action<string> Log;
        double cpu = -1, gpu = -1, gpuTemp = -1;
        public Server(RemoteSettings s) { S = s; }

        public string Link { get { return Address == null ? null : "http://" + Address + ":" + S.Port + "/" + S.Code + "/"; } }

        public bool Start()
        {
            Stop(); Error = null;
            Address = S.Network == "lan" ? Net.Lan() : Net.Tailscale();
            var forced = Environment.GetEnvironmentVariable("HALLOW_REMOTE_ADDR"); if (forced == "127.0.0.1") Address = forced;   // testing on this PC only
            if (Address == null) { Error = S.Network == "lan" ? "No home network address found." : "Tailscale isn't connected on this PC."; WriteStatus(); return false; }
            try { listener = new TcpListener(IPAddress.Parse(Address), S.Port); listener.Start(); }
            catch (Exception e) { Error = "Couldn't listen on " + Address + ":" + S.Port + " - " + e.Message; listener = null; WriteStatus(); return false; }
            var l = listener; new Thread(() => Accept(l)) { IsBackground = true, Name = "remote accept" }.Start();
            if (!sampling) { sampling = true; new Thread(SamplePc) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start(); }
            WriteStatus();
            return true;
        }
        bool sampling;
        public void Stop() { try { if (listener != null) listener.Stop(); } catch { } listener = null; }

        // ---- v1.2: Remote in the Hallow family: status for the other apps (never the link's code)
        public string StatusJson()
        {
            return Family.Json(new Dictionary<string, object> { { "app", "Remote" }, { "version", Program.Version }, { "updated", Family.Time(DateTime.Now) }, { "running", true },
                { "network", S.Network }, { "address", Address }, { "port", S.Port }, { "listening", listener != null }, { "error", Error } });
        }
        public void WriteStatus() { if (!Snap.On) Family.WriteStatus(StatusJson()); }
        public string Command(string line)
        {
            if (line == "ping") return "ok " + Family.Json(new Dictionary<string, object> { { "app", "Remote" }, { "version", Program.Version } });
            if (line == "status") return "ok " + StatusJson();
            return "error unknown command";
        }

        void Accept(TcpListener l)
        {
            while (true)
            {
                TcpClient c; try { c = l.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Handle(c));
            }
        }

        void Handle(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 4000; client.SendTimeout = 4000;
                var st = client.GetStream(); var buf = new byte[8192]; int n = 0;
                while (n < buf.Length) { int r = st.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break; }
                var lines = Encoding.ASCII.GetString(buf, 0, n).Split('\n');
                var line = lines[0].Trim();
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in lines.Skip(1)) { var c = h.IndexOf(':'); if (c > 0) headers[h.Substring(0, c).Trim()] = h.Substring(c + 1).Trim(); }
                var parts = line.Split(' ');
                if (parts.Length < 2 || parts[0] != "GET") { Send(st, 405, "text/plain", Encoding.ASCII.GetBytes("Only GET")); return; }
                var target = parts[1]; var qi = target.IndexOf('?');
                var path = qi < 0 ? target : target.Substring(0, qi); var query = qi < 0 ? "" : target.Substring(qi + 1);
                var segs = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length == 0 || !Same(segs[0], S.Code)) { Send(st, 404, "text/plain", Encoding.ASCII.GetBytes("Not found")); return; }
                var rest = "/" + string.Join("/", segs.Skip(1));
                var q = Query(query);
                if (rest == "/") Send(st, 200, "text/html; charset=utf-8", Resource("index.html"));
                else if (rest == "/icon.png") Send(st, 200, "image/png", Resource("icon.png"));
                else if (rest == "/manifest.json") Send(st, 200, "application/manifest+json", Encoding.UTF8.GetBytes(
                    "{\"name\":\"Hallow\",\"short_name\":\"Hallow\",\"start_url\":\"/" + S.Code + "/\",\"display\":\"standalone\",\"background_color\":\"#16121c\",\"theme_color\":\"#16121c\",\"icons\":[{\"src\":\"/" + S.Code + "/icon.png\",\"sizes\":\"256x256\",\"type\":\"image/png\"}]}"));
                else if (rest == "/api/state") Send(st, 200, "application/json", Encoding.UTF8.GetBytes(State()));
                else if (rest == "/api/do") Send(st, 200, "application/json", Encoding.UTF8.GetBytes(Do(q)));
                else if (rest.StartsWith("/api/cover/")) { int i; var img = int.TryParse(rest.Substring(11), out i) ? Cover(i) : null; if (img != null) Send(st, 200, "image/jpeg", img, true); else Send(st, 404, "text/plain", Encoding.ASCII.GetBytes("No cover")); }
                else if (rest.StartsWith("/api/clip/") || rest.StartsWith("/api/thumb/"))
                {
                    // only a file Hallow Clips lists in its status, picked by its id - never a path from the phone
                    bool video = rest.StartsWith("/api/clip/"); var f = ClipFile(rest.Substring(video ? 10 : 11), video);
                    if (f == null) Send(st, 404, "text/plain", Encoding.ASCII.GetBytes("Not found"));
                    else if (video) { client.SendTimeout = 30000; string range; headers.TryGetValue("Range", out range); SendFile(st, f, "video/mp4", range); }
                    else Send(st, 200, "image/jpeg", File.ReadAllBytes(f), true);
                }
                else Send(st, 404, "text/plain", Encoding.ASCII.GetBytes("Not found"));
            }
            catch { }
            finally { try { client.Close(); } catch { } }
        }
        static bool Same(string a, string b)   // constant time, so the code can't be guessed letter by letter
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0;
        }
        static Dictionary<string, string> Query(string q)
        {
            var d = new Dictionary<string, string>();
            foreach (var p in q.Split('&')) { var kv = p.Split(new[] { '=' }, 2); if (kv[0].Length > 0) d[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : ""; }
            return d;
        }
        static readonly Dictionary<string, byte[]> res = new Dictionary<string, byte[]>();
        static byte[] Resource(string name)
        {
            lock (res)
            {
                byte[] b; if (res.TryGetValue(name, out b)) return b;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("HallowRemote." + name)) using (var m = new MemoryStream()) { s.CopyTo(m); b = m.ToArray(); }
                res[name] = b; return b;
            }
        }
        static void Send(Stream st, int code, string type, byte[] body, bool cache = false)
        {
            var head = "HTTP/1.1 " + code + (code == 200 ? " OK" : code == 404 ? " Not Found" : " Error") + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: " + (cache ? "max-age=3600" : "no-store") + "\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nConnection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head); st.Write(h, 0, h.Length); st.Write(body, 0, body.Length); st.Flush();
        }
        // v1.2: a file with HTTP Range support (phones fetch videos in pieces and seek with it)
        static void SendFile(Stream st, string path, string type, string range)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long len = f.Length, from = 0, to = len - 1; int code = 200;
                if (range != null && range.StartsWith("bytes=") && range.IndexOf(',') < 0)
                {
                    var r = range.Substring(6).Split('-'); long a, b;
                    if (r.Length == 2 && r[0].Length == 0 && long.TryParse(r[1], out b) && b > 0) { from = Math.Max(0, len - b); code = 206; }
                    else if (r.Length == 2 && long.TryParse(r[0], out a))
                    {
                        if (a >= len) { var bad = Encoding.ASCII.GetBytes("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" + len + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); st.Write(bad, 0, bad.Length); return; }
                        from = a; if (r[1].Length > 0 && long.TryParse(r[1], out b)) to = Math.Min(b, len - 1); code = 206;
                    }
                }
                if (to < from) to = from - 1;
                long count = to - from + 1;
                var head = "HTTP/1.1 " + (code == 206 ? "206 Partial Content" : "200 OK") + "\r\nContent-Type: " + type + "\r\nContent-Length: " + count + "\r\nAccept-Ranges: bytes\r\n" +
                    (code == 206 ? "Content-Range: bytes " + from + "-" + to + "/" + len + "\r\n" : "") +
                    "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nConnection: close\r\n\r\n";
                var h = Encoding.ASCII.GetBytes(head); st.Write(h, 0, h.Length);
                f.Position = from; var buf = new byte[65536];
                while (count > 0) { int n = f.Read(buf, 0, (int)Math.Min(buf.Length, count)); if (n <= 0) break; st.Write(buf, 0, n); count -= n; }
                st.Flush();
            }
        }

        // ---- what the phone shows
        void SamplePc()
        {
            PerformanceCounter pc = null; try { pc = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); pc.NextValue(); } catch { }
            var smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            while (true)
            {
                Thread.Sleep(2000);
                try { if (pc != null) cpu = Math.Min(100, pc.NextValue()); } catch { }
                if (File.Exists(smi))
                    try
                    {
                        var p = Process.Start(new ProcessStartInfo(smi, "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                        var l = p.StandardOutput.ReadLine(); p.WaitForExit(2000); var x = (l ?? "").Split(','); double u, t;
                        if (x.Length >= 2 && double.TryParse(x[0].Trim(), out u) && double.TryParse(x[1].Trim(), out t)) { gpu = u; gpuTemp = t; }
                    }
                    catch { }
            }
        }

        static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        static Dictionary<string, object> Mixer(string path) { return Mixer(path, 1500); }
        static Dictionary<string, object> Mixer(string path, int timeout)
        {
            try
            {
                if (DateTime.UtcNow < mixerDownUntil) return null;   // v1.2: Windows takes ~2 s to refuse a closed port, so a stopped Mixer is skipped for a few seconds
                if (RemoteSettings.TestDir == null && Process.GetProcessesByName("HallowMixer").Length == 0) return null;
                var f = Path.Combine(Family.DataDir("Mixer"), "api.json"); if (!File.Exists(f)) return null;
                var a = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + a["port"] + path); req.Headers["X-Hallow-Key"] = a["key"] as string; req.Timeout = timeout; req.ReadWriteTimeout = timeout; req.Proxy = null;
                using (var r = req.GetResponse()) using (var rd = new StreamReader(r.GetResponseStream())) return js.Deserialize<Dictionary<string, object>>(rd.ReadToEnd());
            }
            catch (WebException e) { if (e.Response == null && timeout <= 1500) mixerDownUntil = DateTime.UtcNow.AddSeconds(5); return null; }
            catch { return null; }
        }
        static DateTime mixerDownUntil;
        static Dictionary<string, object> HubRecent()
        {
            try
            {
                if (RemoteSettings.TestDir == null && Process.GetProcessesByName("HallowHub").Length == 0) return null;
                var f = Path.Combine(Family.DataDir("Hub"), "recent.json");
                return File.Exists(f) ? js.Deserialize<Dictionary<string, object>>(File.ReadAllText(f)) : null;
            }
            catch { return null; }
        }
        static string ToolkitDir { get { return RemoteSettings.TestDir != null ? Path.Combine(RemoteSettings.TestDir, "HallowToolkit") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "HallowToolkit"); } }
        static string ToolkitScript { get { var p = Path.Combine(ToolkitDir, "HallowToolkit.ps1"); return File.Exists(p) ? p : null; } }
        // only the GameSession part of Hallow Toolkit's settings is read (the file also holds things like a Discord link, which never leave the PC)
        static bool? SessionOn()
        {
            try
            {
                var f = Path.Combine(ToolkitDir, "settings.json");
                if (ToolkitScript == null || !File.Exists(f)) return null;
                var d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(f)); object g;
                return d.TryGetValue("GameSession", out g) && g is Dictionary<string, object> && ((Dictionary<string, object>)g).ContainsKey("Started");
            }
            catch { return null; }
        }

        // ---- v1.2: Hallow Clips, Vault and Overlay, through their family pipes. Only what the phone needs is passed on.
        static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        static System.Collections.ArrayList List(Dictionary<string, object> d, string k) { return Get(d, k) as System.Collections.ArrayList ?? new System.Collections.ArrayList(); }
        static double Num(object o) { try { return o == null ? 0 : Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; } }
        // "Today 20:01", "Yesterday 20:01", "Nov 17, 20:01"
        static string When(object iso)
        {
            DateTime t; var s = iso as string;
            if (s == null || !DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t)) return null;
            var days = (DateTime.Today - t.Date).TotalDays;
            return (days < 1 ? "Today" : days < 2 ? "Yesterday" : t.ToString("MMM d,", System.Globalization.CultureInfo.InvariantCulture)) + " " + t.ToString("HH:mm");
        }
        // a clip's id: a short hash of its file path, so the phone never sends a path
        static string ClipId(string file) { using (var h = SHA1.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(file.ToLowerInvariant()))).Replace("-", "").Substring(0, 16).ToLowerInvariant(); }
        static string ClipPath(Dictionary<string, object> status, Dictionary<string, object> clip, string key)
        {
            var f = Get(clip, key) as string; if (string.IsNullOrEmpty(f)) return null;
            try { if (!Path.IsPathRooted(f)) { var dir = Get(status, "folder") as string; if (dir == null) return null; f = Path.Combine(dir, f); } return Path.GetFullPath(f); } catch { return null; }
        }
        // the mp4 (or its thumbnail) of the clip with this id - only if Hallow Clips lists it right now
        static string ClipFile(string id, bool video)
        {
            var s = Family.Status("Clips"); if (s == null || id.Length != 16) return null;
            foreach (var o in List(s, "clips"))
            {
                var c = o as Dictionary<string, object>; var f = ClipPath(s, c, "file"); if (f == null || ClipId(f) != id) continue;
                var p = video ? f : ClipPath(s, c, "thumb");
                if (p == null || !p.EndsWith(video ? ".mp4" : ".jpg", StringComparison.OrdinalIgnoreCase) || !File.Exists(p)) return null;
                return p;
            }
            return null;
        }
        static Dictionary<string, object> Clips()
        {
            var s = Family.Status("Clips"); if (s == null) return null;
            var items = new List<object>();
            foreach (var o in List(s, "clips"))
            {
                var c = o as Dictionary<string, object>; var f = ClipPath(s, c, "file"); if (f == null || !f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || !File.Exists(f)) continue;
                var th = ClipPath(s, c, "thumb");
                items.Add(new Dictionary<string, object> { { "id", ClipId(f) }, { "game", Get(c, "game") as string }, { "when", When(Get(c, "time")) },
                    { "sec", Math.Round(Num(Get(c, "durationSec"))) }, { "mb", Math.Round(Num(Get(c, "sizeBytes")) / 1048576.0) }, { "thumb", th != null && File.Exists(th) }, { "fav", Get(c, "favorite") is bool && (bool)Get(c, "favorite") } });
                if (items.Count >= 12) break;
            }
            return new Dictionary<string, object> { { "recording", Get(s, "recording") is bool && (bool)Get(s, "recording") }, { "game", Get(s, "game") as string }, { "seconds", Num(Get(s, "seconds")) }, { "items", items } };
        }
        static Dictionary<string, object> Vault()
        {
            var s = Family.Status("Vault"); if (s == null) return null;
            var games = new List<object>();
            foreach (var o in List(s, "games"))
            {
                var g = o as Dictionary<string, object>; if (g == null || (Get(g, "off") is bool && (bool)Get(g, "off"))) continue;
                games.Add(new Dictionary<string, object> { { "name", Get(g, "name") as string }, { "last", When(Get(g, "lastBackup")) }, { "versions", Num(Get(g, "versions")) } });
            }
            return new Dictionary<string, object> { { "busy", Get(s, "busy") is bool && (bool)Get(s, "busy") }, { "games", games } };
        }
        static Dictionary<string, object> Overlay()
        {
            var s = Family.Status("Overlay"); if (s == null) return null;
            return new Dictionary<string, object> { { "visible", Get(s, "visible") is bool && (bool)Get(s, "visible") } };
        }

        string State()
        {
            double used = -1, total = -1;
            try { var m = new MEMORYSTATUSEX(); if (GlobalMemoryStatusEx(m)) { total = m.ullTotalPhys / 1073741824.0; used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0; } } catch { }
            var hub = HubRecent();
            var games = new List<object>();
            if (hub != null && hub.ContainsKey("games"))
                foreach (var g in (System.Collections.ArrayList)hub["games"]) { var d = g as Dictionary<string, object>; if (d != null) games.Add(new Dictionary<string, object> { { "name", d["name"] }, { "playing", d["playing"] } }); }
            var o = new Dictionary<string, object> {
                { "pc", Environment.MachineName },
                { "stats", new Dictionary<string, object> { { "cpu", Math.Round(cpu) }, { "gpu", Math.Round(gpu) }, { "gpuTemp", Math.Round(gpuTemp) }, { "ramUsed", Math.Round(used, 1) }, { "ramTotal", Math.Round(total) } } },
                { "mixer", Mixer("/state") },
                { "hub", hub != null }, { "games", games },
                { "session", SessionOn() },
                { "clips", Clips() }, { "vault", Vault() }, { "overlay", Overlay() },
                { "time", DateTime.Now.ToString("HH:mm") } };
            return js.Serialize(o);
        }

        // ---- what the phone can do: a fixed list, nothing else
        static readonly HashSet<string> Channels = new HashSet<string> { "game", "chat", "music", "media", "other" };
        string Do(Dictionary<string, string> q)
        {
            string a; q.TryGetValue("a", out a); string msg = null;
            Func<string, string> mix = p => { var r = Mixer(p); object m; return r != null && r.TryGetValue("msg", out m) ? m as string ?? "Done" : r != null ? "Done" : "Hallow Mixer isn't running"; };
            string ch; q.TryGetValue("ch", out ch); int v;
            switch (a)
            {
                case "set": if (ch != null && Channels.Contains(ch) && int.TryParse(q.ContainsKey("v") ? q["v"] : "", out v)) msg = mix("/set?ch=" + ch + "&v=" + Math.Max(0, Math.Min(100, v))); break;
                case "mute": if (ch != null && Channels.Contains(ch)) msg = mix("/mute?ch=" + ch + "&m=toggle"); break;
                case "mic": msg = mix("/mic?mute=toggle"); break;
                case "media": { string d; q.TryGetValue("do", out d); if (d == "playpause" || d == "next" || d == "prev") msg = mix("/media?do=" + d); break; }
                case "preset": { string n; if (q.TryGetValue("name", out n) && n.Length < 60) msg = mix("/preset?name=" + Uri.EscapeDataString(n)); break; }
                case "play":
                {
                    var hub = HubRecent(); int i;
                    if (hub == null) { msg = "Hallow Hub isn't running"; break; }
                    var list = (System.Collections.ArrayList)hub["games"];
                    if (int.TryParse(q.ContainsKey("i") ? q["i"] : "", out i) && i >= 0 && i < list.Count)
                    {
                        var g = (Dictionary<string, object>)list[i]; var exe = hub["exe"] as string;
                        if (exe != null && File.Exists(exe)) { Process.Start(new ProcessStartInfo(exe, "--play \"" + (g["id"] as string ?? "").Replace("\"", "") + "\"") { UseShellExecute = false }); msg = "Starting " + g["name"]; }
                    }
                    break;
                }
                case "session":
                {
                    var s = ToolkitScript; if (s == null) { msg = "Hallow Toolkit isn't installed"; break; }
                    var on = SessionOn() == true;
                    Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + s + "\" -Mode " + (on ? "SessionEnd" : "SessionStart")) { UseShellExecute = false, CreateNoWindow = true });
                    msg = on ? "Ending the game session" : "Game session on"; break;
                }
                // v1.2: the other Hallow apps - fixed commands only, never text from the phone
                case "clip":
                {
                    var r = Family.Send("Clips", "save", 20000);
                    msg = Family.Error(r, "Hallow Clips isn't running") ?? "Clip saved"; break;
                }
                case "backup":
                {
                    var r = Family.Send("Vault", "backup all", 5000);
                    msg = Family.Error(r, "Hallow Vault isn't running") ?? "Backing up your saves"; break;
                }
                case "overlay":
                {
                    string d; q.TryGetValue("do", out d); if (d != "show" && d != "hide") break;
                    var r = Family.Send("Overlay", d, 5000);
                    msg = Family.Error(r, "Hallow Overlay isn't running") ?? (d == "show" ? "Overlay shown" : "Overlay hidden"); break;
                }
                case "power":
                {
                    string d; q.TryGetValue("do", out d);
                    if (d == "sleep" || d == "shutdown" || d == "lock") msg = Power(d);
                    break;
                }
            }
            if (msg != null && Log != null) Log(msg);
            return js.Serialize(new Dictionary<string, object> { { "ok", msg != null }, { "msg", msg ?? "Not allowed" } });
        }

        // v1.2: sleep, shut down or lock this PC (the phone asks for a second tap first). A moment later, so the phone gets its answer.
        // HALLOW_REMOTE_DRYRUN=1 (or a test folder) only says what it would do.
        [DllImport("user32.dll")] static extern bool LockWorkStation();
        public static bool DryRun { get { return RemoteSettings.TestDir != null || Environment.GetEnvironmentVariable("HALLOW_REMOTE_DRYRUN") == "1"; } }
        static string Power(string what)
        {
            var label = what == "sleep" ? "Going to sleep" : what == "shutdown" ? "Shutting down" : "Locking the PC";
            if (DryRun) return label + " (test - not really)";
            new Thread(() =>
            {
                Thread.Sleep(1000);
                try
                {
                    if (what == "lock") LockWorkStation();
                    else if (what == "sleep") System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false);
                    else Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"), "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
                }
                catch { }
            }) { IsBackground = true }.Start();
            return label;
        }

        // the header art of Hub's recent game i (Steam's cache on this PC)
        static readonly Dictionary<string, string> coverPaths = new Dictionary<string, string>();
        static byte[] Cover(int i)
        {
            try
            {
                var hub = HubRecent(); if (hub == null) return null;
                var list = (System.Collections.ArrayList)hub["games"]; if (i < 0 || i >= list.Count) return null;
                var id = ((Dictionary<string, object>)list[i])["id"] as string; if (id == null || !id.StartsWith("steam:")) return null;
                string p;
                lock (coverPaths)
                    if (!coverPaths.TryGetValue(id, out p))
                    {
                        var steam = Library.SteamPath(); p = null;
                        var dir = steam == null ? null : Path.Combine(steam, @"appcache\librarycache", id.Substring(6));
                        if (dir != null && Directory.Exists(dir)) p = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).FirstOrDefault(f => Path.GetFileName(f).Equals("library_header.jpg", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("header.jpg", StringComparison.OrdinalIgnoreCase));
                        coverPaths[id] = p;
                    }
                return p != null && File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
            catch { return null; }
        }
    }
}
