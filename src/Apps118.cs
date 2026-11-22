// Hallow Remote 1.18 - your phone controls the newer apps:
//   Hallow VPN, Torrents, Gear, Home, Browser, Tasks, Email and Financials (+ Shield's count, read since 1.15).
// Each card shows only when that app is installed. Only each app's documented pipe commands and status.json are used,
// and only a fixed, checked list of fields leaves the PC (never keys, tokens, addresses, account names, subjects or amounts).
// Every new action is a POST to api/act: the link's code (as always), plus a per-run CSRF token in a header the page reads
// from api/state, a same-origin check, and a rate limit. Torrent names reach only the phone page - they are never logged.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HallowRemote
{
    public partial class Server
    {
        // ---- installed? (a test folder: the app's data folder is there)
        public static bool Installed(string app)
        {
            if (RemoteSettings.TestDir != null) return Directory.Exists(Family.DataDir(app));
            try { return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Hallow " + app, "Hallow" + app + ".exe")); } catch { return false; }
        }
        static bool Flag(Dictionary<string, object> d, string k) { var v = Get(d, k); return v is bool && (bool)v; }
        static bool? FlagOrNull(Dictionary<string, object> d, string k) { var v = Get(d, k); return v is bool ? (bool?)(bool)v : null; }
        static double? NumOrNull(object o, int digits) { if (o == null || o is string || o is bool) return null; return Math.Round(Num(o), digits); }
        static string OneOf(object o, params string[] allowed) { var s = o as string; return s != null && allowed.Contains(s) ? s : null; }
        static int VerNum(string v, int part) { try { var p = (v ?? "").Split('.'); int n; return p.Length > part && int.TryParse(p[part], out n) ? n : 0; } catch { return 0; } }
        static bool VerAtLeast(string v, int major, int minor) { int a = VerNum(v, 0), b = VerNum(v, 1); return a > major || (a == major && b >= minor); }
        // error text from another app, made safe for the phone: one line, short, no addresses / e-mail addresses / long hex
        public static string Scrub(string s)
        {
            if (s == null) return null;
            s = Short(Family.OneLine(s), 200);
            s = Regex.Replace(s, @"\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b", "(address)");
            s = Regex.Replace(s, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "(address)");
            s = Regex.Replace(s, @"\b[0-9a-fA-F]{24,}\b", "(id)");
            s = Regex.Replace(s, @"(?i)\b(https?|magnet):\S+", "(link)");
            return s;
        }
        static Dictionary<string, object> AppBase(string app, out Dictionary<string, object> s)
        {
            s = null; if (!Installed(app)) return null;
            s = Family.StatusFile(app);
            return new Dictionary<string, object> { { "running", s != null }, { "version", s == null ? null : Short(Get(s, "version") as string, 16) } };
        }

        // ---- VPN: connected / server name / protocol, kill switch, leak check. Never the Tailscale exit node, adapter or any address.
        static string vpnNextCache; static DateTime vpnNextAt;
        static string VpnNext(Dictionary<string, object> s)
        {
            if (!Flag(s, "connected") && !Flag(s, "connecting")) { var p = OneOf(Get(s, "protocol"), "IKEv2", "NordLynx"); if (p != null) return p; }
            lock (typeof(Server))
            {
                if (vpnNextCache != null && (DateTime.UtcNow - vpnNextAt).TotalSeconds < 10) return vpnNextCache == "?" ? null : vpnNextCache;
                var r = Family.Send("VPN", "protocol", 2000); string next = null;
                if (r != null && r.StartsWith("ok {")) try { next = OneOf(Get(js.Deserialize<Dictionary<string, object>>(r.Substring(3)), "next"), "IKEv2", "NordLynx"); } catch { }
                vpnNextCache = next ?? "?"; vpnNextAt = DateTime.UtcNow; return next;
            }
        }
        static Dictionary<string, object> VpnState()
        {
            Dictionary<string, object> s; var o = AppBase("VPN", out s); if (o == null || s == null) return o;
            bool on = Flag(s, "connected"), ing = Flag(s, "connecting");
            var proto = OneOf(Get(s, "protocol"), "IKEv2", "NordLynx"); var next = VpnNext(s);
            o["connected"] = on; o["connecting"] = ing; o["paused"] = Flag(s, "paused");
            o["server"] = on ? Short(Get(s, "server") as string, 24) : null; o["country"] = on ? Short(Get(s, "country") as string, 40) : null;
            o["protocol"] = proto; o["next"] = next;
            o["killSwitch"] = Flag(s, "killSwitch"); o["leakCheck"] = on ? OneOf(Get(s, "leakCheck"), "ok", "warn", "fail") : null;
            o["account"] = Get(s, "account") is bool ? Flag(s, "account") : true;
            o["canConnect"] = next == "IKEv2" && !on && !ing; o["canDisconnect"] = (on || ing) && proto == "IKEv2";
            return o;
        }

        // ---- Torrents: counts / speeds on the Apps tab (status.json); the list itself only on api/torrents
        static Dictionary<string, object> TorrentsSummary()
        {
            Dictionary<string, object> s; var o = AppBase("Torrents", out s); if (o == null || s == null) return o;
            foreach (var k in new[] { "total", "active", "seeding", "paused", "queued", "done", "flagged" }) o[k] = IntOrNull(Get(s, k));
            o["vpnLocked"] = Flag(s, "vpnLocked"); o["held"] = OneOf(Get(s, "held"), "vpn", "game");
            return o;
        }
        static Dictionary<string, object> TorrentsLive(Dictionary<string, object> sum)
        {
            if (sum == null || !(sum["running"] is bool) || !(bool)sum["running"]) return null;
            var s = Family.StatusFile("Torrents");
            return new Dictionary<string, object> { { "down", NumOrNull(Get(s, "downKBps"), 1) }, { "up", NumOrNull(Get(s, "upKBps"), 1) } };
        }
        // the phone never gets an info hash: a short id made from it, matched again on the PC
        static string TorId(string hash) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes("hr-tor:" + (hash ?? "").ToLowerInvariant()))).Replace("-", "").Substring(0, 16).ToLowerInvariant(); }
        static readonly string[] TorStatus = { "downloading", "seeding", "waiting-vpn", "paused", "game", "queued", "metadata", "checking", "starting", "done", "error" };
        static Dictionary<string, object> TorPipe(string line, int ms)
        {
            var r = Family.Send("Torrents", line, ms);
            if (r == null || !r.StartsWith("ok {")) return null;
            try { return js.Deserialize<Dictionary<string, object>>(r.Substring(3)); } catch { return null; }
        }
        string TorrentsJson()
        {
            Func<bool, string, string> res = (ok, m) => js.Serialize(new Dictionary<string, object> { { "ok", ok }, { "msg", m } });
            if (!Installed("Torrents")) return res(false, "Hallow Torrents isn't installed");
            var pong = Family.Send("Torrents", "ping", 1500); if (pong == null) return js.Serialize(new Dictionary<string, object> { { "ok", true }, { "running", false } });
            string ver = null; try { ver = Get(js.Deserialize<Dictionary<string, object>>(pong.Substring(3)), "version") as string; } catch { }
            if (!VerAtLeast(ver, 1, 4)) return js.Serialize(new Dictionary<string, object> { { "ok", true }, { "running", true }, { "old", true }, { "version", Short(ver, 16) } });
            var l = TorPipe("list", 5000); if (l == null) return res(false, "Hallow Torrents didn't answer");
            var list = new List<object>();
            foreach (var x in List(l, "torrents"))
            {
                var t = x as Dictionary<string, object>; if (t == null) continue;
                var hash = Get(t, "hash") as string; if (string.IsNullOrEmpty(hash)) continue;
                list.Add(new Dictionary<string, object> {
                    { "id", TorId(hash) }, { "name", Short(Get(t, "name") as string, 160) }, { "progress", NumOrNull(Get(t, "progress"), 4) ?? 0 },
                    { "down", NumOrNull(Get(t, "downBps"), 0) ?? 0 }, { "up", NumOrNull(Get(t, "upBps"), 0) ?? 0 },
                    { "state", Short(Get(t, "state") as string, 40) }, { "status", OneOf(Get(t, "status"), TorStatus) ?? "queued" }, { "category", Short(Get(t, "category") as string, 40) },
                    { "size", NumOrNull(Get(t, "size"), 0) }, { "eta", NumOrNull(Get(t, "eta"), 0) }, { "seeds", IntOrNull(Get(t, "seeds")) }, { "peers", IntOrNull(Get(t, "peers")) },
                    { "paused", Flag(t, "paused") }, { "complete", Flag(t, "complete") }, { "flagged", Flag(t, "flagged") }, { "vpnLocked", Flag(t, "vpnLocked") } });
                if (list.Count >= 200) break;
            }
            var st = TorPipe("stats", 3000); Dictionary<string, object> stats = null;
            if (st != null)
            {
                Func<object, Dictionary<string, object>> part = p => { var d = p as Dictionary<string, object>; return d == null ? null : new Dictionary<string, object> { { "down", NumOrNull(Get(d, "down"), 0) }, { "up", NumOrNull(Get(d, "up"), 0) }, { "ratio", NumOrNull(Get(d, "ratio"), 3) } }; };
                stats = new Dictionary<string, object> { { "session", part(Get(st, "session")) }, { "allTime", part(Get(st, "allTime")) }, { "torrents", IntOrNull(Get(st, "torrents")) }, { "downloading", IntOrNull(Get(st, "downloading")) }, { "seeding", IntOrNull(Get(st, "seeding")) } };
            }
            return js.Serialize(new Dictionary<string, object> { { "ok", true }, { "running", true }, { "version", Short(ver, 16) }, { "canSearch", VerAtLeast(ver, 1, 3) },
                { "vpnLocked", Flag(l, "vpnLocked") }, { "held", OneOf(Get(l, "held"), "vpn", "game") }, { "game", Short(Get(l, "game") as string, 60) },
                { "down", NumOrNull(Get(l, "downBps"), 0) ?? 0 }, { "up", NumOrNull(Get(l, "upBps"), 0) ?? 0 }, { "torrents", list }, { "stats", stats },
                { "vpn", Installed("VPN") }, { "categories", new[] { "Movies", "TV", "Music", "Games", "Other" } } });
        }

        // ---- Gear: battery per device (never the Bluetooth key in "alert")
        static readonly string[] GearKinds = { "mouse", "keyboard", "headset", "earbuds", "controller", "other" };
        static Dictionary<string, object> GearState()
        {
            Dictionary<string, object> s; var o = AppBase("Gear", out s); if (o == null || s == null) return o;
            var devs = new List<object>();
            foreach (var x in List(s, "devices"))
            {
                var d = x as Dictionary<string, object>; if (d == null) continue;
                devs.Add(new Dictionary<string, object> { { "kind", OneOf(Get(d, "kind"), GearKinds) ?? "other" }, { "name", Short(Get(d, "name") as string, 40) }, { "battery", IntOrNull(Get(d, "battery")) },
                    { "charging", Flag(d, "charging") }, { "full", Flag(d, "full") }, { "asleep", Flag(d, "asleep") }, { "connected", Flag(d, "connected") }, { "wired", Flag(d, "wired") } });
                if (devs.Count >= 16) break;
            }
            o["devices"] = devs; o["lowAt"] = IntOrNull(Get(s, "lowAt")) ?? 20;
            return o;
        }

        // ---- Home: Home Assistant link, cameras, feeder / fountain, lock (shown only), alerts. Feed now with a confirm step.
        static readonly string[] HomeFlags = { "jam", "food-low", "water-low", "pump", "filter", "intake-low", "camera-offline", "motion-away" };
        static Dictionary<string, object> HomeState()
        {
            Dictionary<string, object> s; var o = AppBase("Home", out s); if (o == null || s == null) return o;
            o["configured"] = Flag(s, "configured"); o["connected"] = Flag(s, "connected"); o["live"] = Flag(s, "live");
            o["cameras"] = IntOrNull(Get(s, "cameras")); o["camerasOnline"] = IntOrNull(Get(s, "camerasOnline"));
            var f = Get(s, "feeder") as Dictionary<string, object>;
            o["feeder"] = f == null ? null : new Dictionary<string, object> { { "online", Flag(f, "online") }, { "nextFeed", When(Get(f, "nextFeed")) }, { "todayMeals", IntOrNull(Get(f, "todayMeals")) },
                { "todayGrams", NumOrNull(Get(f, "todayGrams"), 0) }, { "foodLow", Flag(f, "foodLow") }, { "canFeed", Flag(f, "canFeed") } };
            var w = Get(s, "fountain") as Dictionary<string, object>;
            o["fountain"] = w == null ? null : new Dictionary<string, object> { { "online", Flag(w, "online") }, { "waterLow", Flag(w, "waterLow") }, { "waterPct", NumOrNull(Get(w, "waterPct"), 0) },
                { "pumpOn", FlagOrNull(w, "pumpOn") }, { "filterDue", Flag(w, "filterDue") }, { "cleaningDue", Flag(w, "cleaningDue") } };
            o["lock"] = OneOf(Get(s, "lock"), "bluetooth-only", "unavailable", "locked", "unlocked", "jammed") ?? "unknown";
            o["alerts"] = IntOrNull(Get(s, "alerts")) ?? 0;
            o["alertFlags"] = List(s, "alertFlags").OfType<string>().Where(x => HomeFlags.Contains(x)).Distinct().ToList();
            o["away"] = Flag(s, "away");
            return o;
        }

        // ---- Browser, Tasks, Email, Financials: a few numbers / flags each
        static Dictionary<string, object> BrowserState()
        {
            Dictionary<string, object> s; var o = AppBase("Browser", out s); if (o == null || s == null) return o;
            o["tabs"] = IntOrNull(Get(s, "tabs")); o["asleep"] = IntOrNull(Get(s, "asleep")); o["windows"] = IntOrNull(Get(s, "windows"));
            o["shield"] = OneOf(Get(s, "shield"), "extension", "fallback", "missing", "starting"); o["shieldVersion"] = Short(Get(s, "shieldVersion") as string, 16);
            o["gameMode"] = Flag(s, "gameMode");
            return o;
        }
        static Dictionary<string, object> TasksState()
        {
            Dictionary<string, object> s; var o = AppBase("Tasks", out s); if (o == null || s == null) return o;
            o["sampled"] = When(Get(s, "sampled")); o["game"] = Short(Get(s, "game") as string, 60);
            return o;
        }
        static Dictionary<string, object> TasksLive(Dictionary<string, object> t)
        {
            if (t == null || !(bool)t["running"]) return null;
            var s = Family.StatusFile("Tasks");
            return new Dictionary<string, object> { { "cpu", NumOrNull(Get(s, "cpu"), 0) }, { "ram", NumOrNull(Get(s, "ram"), 0) }, { "gpu", NumOrNull(Get(s, "gpu"), 0) } };
        }
        static Dictionary<string, object> EmailState()
        {
            Dictionary<string, object> s; var o = AppBase("Email", out s); if (o == null || s == null) return o;
            o["unread"] = IntOrNull(Get(s, "unread"));   // the total only - no account labels, never subjects or senders
            return o;
        }
        static Dictionary<string, object> FinancialsState()
        {
            Dictionary<string, object> s; var o = AppBase("Financials", out s); if (o == null || s == null) return o;
            o["vault"] = Flag(s, "vault"); o["locked"] = Flag(s, "locked");   // locked / unlocked only
            return o;
        }
        Dictionary<string, object> AppsState()
        {
            var o = new Dictionary<string, object>();
            Action<string, Func<Dictionary<string, object>>> add = (k, f) => { try { var v = f(); if (v != null) o[k] = v; } catch { } };
            add("vpn", VpnState); add("torrents", TorrentsSummary); add("gear", GearState); add("home", HomeState);
            add("browser", BrowserState); add("tasks", TasksState); add("email", EmailState); add("financials", FinancialsState);
            return o;
        }
        // numbers that change every second (kept out of the "something changed" check, like the PC stats)
        static Dictionary<string, object> AppsLive(Dictionary<string, object> apps)
        {
            var o = new Dictionary<string, object>();
            try { var t = TorrentsLive(Get(apps, "torrents") as Dictionary<string, object>); if (t != null) o["torrents"] = t; } catch { }
            try { var t = TasksLive(Get(apps, "tasks") as Dictionary<string, object>); if (t != null) o["tasks"] = t; } catch { }
            return o;
        }

        // ---- api/act: CSRF + same origin + rate limit, then one of a fixed list of actions
        public readonly string CsrfToken = NewToken();
        static string NewToken() { var b = new byte[24]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }
        bool CsrfOk(Dictionary<string, string> headers, out string why)
        {
            why = null; string t, origin, site, host;
            if (!headers.TryGetValue("X-Hallow-Csrf", out t) || !Same(t, CsrfToken)) { why = "Reload the page and try again"; return false; }
            headers.TryGetValue("Host", out host);
            if (headers.TryGetValue("Origin", out origin))
            {
                Uri u; if (origin == "null" || !Uri.TryCreate(origin, UriKind.Absolute, out u) || host == null || !string.Equals(u.Authority, host, StringComparison.OrdinalIgnoreCase)) { why = "Not from this page"; return false; }
            }
            if (headers.TryGetValue("Sec-Fetch-Site", out site) && site != "same-origin") { why = "Not from this page"; return false; }
            return true;
        }
        // rate limits: 30 actions a minute in all, and a gap per kind of action
        readonly Queue<DateTime> actTimes = new Queue<DateTime>(); readonly Dictionary<string, DateTime> actLast = new Dictionary<string, DateTime>();
        public static TimeSpan Gap(string kind)
        {
            switch (kind)
            {
                case "home:feed": return TimeSpan.FromSeconds(60);
                case "vpn": return TimeSpan.FromSeconds(5);
                case "tor:search": return TimeSpan.FromSeconds(4);
                case "tor:add": return TimeSpan.FromSeconds(2);
                case "fin": return TimeSpan.FromSeconds(2);
                case "auth:backup": return TimeSpan.FromSeconds(10);   // v1.19
                case "auth:mirror": return TimeSpan.FromSeconds(2);
                case "auth:xfer": return TimeSpan.Zero;   // counted in the 30 a minute only (the id is 128 random bits)
                default: return TimeSpan.FromMilliseconds(300);
            }
        }
        string RateLimited(string kind)
        {
            var now = DateTime.UtcNow;
            lock (actTimes)
            {
                while (actTimes.Count > 0 && (now - actTimes.Peek()).TotalSeconds > 60) actTimes.Dequeue();
                if (actTimes.Count >= 30) return "Too many actions - wait a minute";
                DateTime l; var gap = Gap(kind);
                if (actLast.TryGetValue(kind, out l) && now - l < gap) return "Wait " + Math.Max(1, (int)Math.Ceiling((gap - (now - l)).TotalSeconds)) + " s before doing that again";
                actTimes.Enqueue(now); actLast[kind] = now; return null;
            }
        }
        static string ReadSmallBody(Stream st, Dictionary<string, string> headers, byte[] buf, int bodyAt, int n, out int code)
        {
            code = 200; string cl; long len;
            if (!headers.TryGetValue("Content-Length", out cl) || !long.TryParse(cl, out len) || len < 0) { code = 411; return null; }
            if (len > 16384) { code = 413; return null; }
            string ct; if (len > 0 && (!headers.TryGetValue("Content-Type", out ct) || !ct.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))) { code = 400; return null; }
            var body = new MemoryStream(); int have = Math.Max(0, Math.Min(n - bodyAt, (int)len)); body.Write(buf, bodyAt, have);
            var b = new byte[4096];
            while (body.Length < len) { int r = st.Read(b, 0, (int)Math.Min(b.Length, len - body.Length)); if (r <= 0) break; body.Write(b, 0, r); }
            if (body.Length < len) { code = 400; return null; }
            return Encoding.UTF8.GetString(body.ToArray());
        }
        void ActRequest(TcpClient client, Stream st, Dictionary<string, string> headers, Dictionary<string, string> q, byte[] buf, int bodyAt, int n)
        {
            Action<int, bool, string> send = (sc, ok, m) => Send(st, sc, "application/json", Encoding.UTF8.GetBytes(js.Serialize(new Dictionary<string, object> { { "ok", ok }, { "msg", m } })));
            string why; if (!CsrfOk(headers, out why)) { send(403, false, why); return; }
            int code; var body = ReadSmallBody(st, headers, buf, bodyAt, n, out code);
            if (body == null) { send(code, false, "Bad request"); return; }
            var p = new Dictionary<string, string>(q); foreach (var kv in Query(body)) p[kv.Key] = kv.Value;
            if (Paused) { send(403, false, "The remote is paused on your PC"); return; }
            client.SendTimeout = 10000;
            int status; var res = Act(p, out status);
            Changed();
            Send(st, status, "application/json", Encoding.UTF8.GetBytes(js.Serialize(res)));
        }
        static Dictionary<string, object> R(bool ok, string msg) { return new Dictionary<string, object> { { "ok", ok }, { "msg", msg } }; }
        static readonly Regex CatRx = new Regex(@"^[A-Za-z0-9][A-Za-z0-9 &'()_.-]{0,39}$");
        // the kind used for the rate limit (null = not an action)
        static string ActKind(string app, string d)
        {
            switch (app)
            {
                case "vpn": return d == "fastest" || d == "gaming" || d == "disconnect" ? "vpn" : null;
                case "tor": return d == "pause" || d == "resume" ? "tor:one" : d == "pauseall" || d == "resumeall" ? "tor:all" : d == "add" || d == "searchadd" ? "tor:add" : d == "search" ? "tor:search" : null;
                case "home": return d == "feed" ? "home:feed" : null;
                case "fin": return d == "lock" ? "fin" : null;
            }
            return null;
        }
        Dictionary<string, object> Act(Dictionary<string, string> p, out int status)
        {
            status = 200;
            string app, d; p.TryGetValue("app", out app); p.TryGetValue("do", out d);
            var kind = ActKind(app, d); if (kind == null) return R(false, "Not allowed");
            // checks that refuse before anything is sent (and before the rate limit counts it)
            Dictionary<string, object> pre = ActCheck(app, d, p); if (pre != null) return pre;
            var lim = RateLimited(kind); if (lim != null) { status = 429; return R(false, lim); }
            try { return ActRun(app, d, p); } catch { return R(false, "That didn't work"); }
        }
        static Dictionary<string, object> ActCheck(string app, string d, Dictionary<string, string> p)
        {
            if (!Installed(app == "vpn" ? "VPN" : app == "tor" ? "Torrents" : app == "home" ? "Home" : "Financials")) return R(false, "That app isn't installed");
            if (app == "home")
            {
                string c, ns; int portions; p.TryGetValue("confirm", out c); p.TryGetValue("n", out ns);
                if (c != "yes") return R(false, "Confirm on the phone first");
                if (!int.TryParse(ns ?? "", out portions) || portions < 1 || portions > 12) return R(false, "Pick 1 to 12 portions");
            }
            if (app == "tor")
            {
                string id, cat; p.TryGetValue("id", out id); p.TryGetValue("cat", out cat);
                if ((d == "pause" || d == "resume") && (id == null || !Regex.IsMatch(id, "^[0-9a-f]{16}$"))) return R(false, "Not one of your torrents");
                if (d == "searchadd" && (id == null || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,16}$"))) return R(false, "Search again");
                if ((d == "add" || d == "searchadd" || d == "search") && !string.IsNullOrEmpty(cat) && !CatRx.IsMatch(cat)) return R(false, "Not a category name");
                if (d == "add")
                {
                    string m; p.TryGetValue("magnet", out m); m = (m ?? "").Trim();
                    if (m.Length > 4000 || m.Any(ch => char.IsControl(ch) || ch == ' ') || !Regex.IsMatch(m, @"^magnet:\?(.*&)?xt=urn:bt(ih|mh):[A-Za-z0-9]{32,68}(&|$)", RegexOptions.IgnoreCase)) return R(false, "Paste a magnet link (magnet:?xt=urn:btih:...)");
                }
                if (d == "search")
                {
                    string w; p.TryGetValue("q", out w); w = Words(w);
                    if (w.Length < 2) return R(false, "Type what to search for");
                }
            }
            return null;
        }
        // search words: one line, no "--" flags of our own, at most 120 characters
        static string Words(string w) { w = Regex.Replace(Family.OneLine(w ?? "").Replace("--", " "), @"\s+", " ").Trim(); w = new string(w.Where(ch => !char.IsControl(ch)).ToArray()); return w.Length > 120 ? w.Substring(0, 120).Trim() : w; }
        static readonly Dictionary<string, string> SearchCats = new Dictionary<string, string> { { "movies", "movies" }, { "tv", "tv" }, { "music", "music" }, { "games", "games" }, { "software", "software" }, { "books", "books" }, { "other", "other" } };
        Dictionary<string, object> ActRun(string app, string d, Dictionary<string, string> p)
        {
            string err;
            if (app == "fin")
            {
                err = Family.Error(Family.Send("Financials", "lock", 6000), "Hallow Financials isn't running");
                if (err != null) return R(false, Scrub(err));
                if (Log != null) Log("Hallow Financials locked"); return R(true, "Hallow Financials is locked");
            }
            if (app == "home")
            {
                var s = Family.StatusFile("Home"); if (s == null) return R(false, "Hallow Home isn't running");
                var f = Get(s, "feeder") as Dictionary<string, object>;
                if (f == null) return R(false, "Hallow Home hasn't found a feeder");
                if (!Flag(f, "canFeed")) return R(false, "The feeder can't feed right now - check it in Hallow Home");
                int portions = int.Parse(p["n"]);
                var r = Family.Send("Home", "feed " + portions + " confirm", 8000);
                err = Family.Error(r, "Hallow Home isn't running"); if (err != null) return R(false, Scrub(err));
                if (Log != null) Log("feeding " + portions + (portions == 1 ? " portion" : " portions"));
                return R(true, "Feeding " + portions + (portions == 1 ? " portion" : " portions"));
            }
            if (app == "vpn")
            {
                var v = VpnState(); if (v == null || !(bool)v["running"]) return R(false, "Hallow VPN isn't running");
                if (d == "disconnect")
                {
                    if (!(bool)v["connected"] && !(bool)v["connecting"]) return R(false, "Hallow VPN isn't connected");
                    if (!(bool)v["canDisconnect"]) return R(false, "NordLynx needs a click on the PC (Windows asks first) - disconnect from the PC");
                    err = Family.Error(Family.Send("VPN", "disconnect", 8000), "Hallow VPN isn't running"); if (err != null) return R(false, Scrub(err));
                    if (Log != null) Log("VPN disconnecting"); return R(true, "Disconnecting");
                }
                if ((bool)v["connected"] || (bool)v["connecting"]) return R(false, "Hallow VPN is already " + ((bool)v["connected"] ? "connected" : "connecting"));
                lock (typeof(Server)) vpnNextCache = null;
                var next = VpnNext(Family.StatusFile("VPN") ?? new Dictionary<string, object>());
                if (next != "IKEv2") return R(false, next == "NordLynx" ? "The next connect uses NordLynx, which needs a click on the PC - connect from the PC" : "Hallow VPN didn't say which protocol it would use - connect from the PC");
                var r = Family.Send("VPN", "connect " + (d == "gaming" ? "gaming" : "fastest"), 8000);
                err = Family.Error(r, "Hallow VPN isn't running"); if (err != null) return R(false, Scrub(err));
                if (Log != null) Log("VPN connecting"); return R(true, d == "gaming" ? "Connecting to the best server for gaming" : "Connecting to the fastest server");
            }
            // torrents
            var pong = Family.Send("Torrents", "ping", 1500); if (pong == null) return R(false, "Hallow Torrents isn't running");
            string ver = null; try { ver = Get(js.Deserialize<Dictionary<string, object>>(pong.Substring(3)), "version") as string; } catch { }
            if (!VerAtLeast(ver, 1, 4)) return R(false, "Update Hallow Torrents to 1.4 or newer for this");
            string cat; p.TryGetValue("cat", out cat); var catPart = string.IsNullOrEmpty(cat) ? "" : " --category " + cat;
            if (d == "pauseall" || d == "resumeall")
            {
                err = Family.Error(Family.Send("Torrents", d == "pauseall" ? "pause-all" : "resume-all", 5000), "Hallow Torrents isn't running"); if (err != null) return R(false, Scrub(err));
                if (Log != null) Log(d == "pauseall" ? "torrents paused" : "torrents resumed"); return R(true, d == "pauseall" ? "All torrents paused" : "All torrents resumed - they run only over the VPN");
            }
            if (d == "pause" || d == "resume")
            {
                var l = TorPipe("list", 5000); if (l == null) return R(false, "Hallow Torrents didn't answer");
                var hit = List(l, "torrents").OfType<Dictionary<string, object>>().Select(t => Get(t, "hash") as string).FirstOrDefault(h => h != null && TorId(h) == p["id"]);
                if (hit == null || !Regex.IsMatch(hit, "^[0-9a-fA-F]{8,64}$")) return R(false, "That torrent isn't in the list any more");
                var r = Family.Send("Torrents", d + " " + hit, 5000);
                err = Family.Error(r, "Hallow Torrents isn't running"); if (err != null) return R(false, Scrub(err));
                bool locked = r != null && r.Contains("\"vpnLocked\":true");
                if (Log != null) Log(d == "pause" ? "a torrent paused" : "a torrent resumed");
                return R(true, d == "pause" ? "Paused" : locked ? "Resumed - waiting for the VPN" : "Resumed");
            }
            if (d == "add")
            {
                var m = p["magnet"].Trim();
                var r = Family.Send("Torrents", "add " + m + " --direct" + catPart, 15000);
                err = Family.Error(r, "Hallow Torrents isn't running"); if (err != null) return R(false, Scrub(err));
                if (Log != null) Log("a torrent added");
                return R(true, AddedMsg(r));
            }
            if (d == "searchadd")
            {
                if (!VerAtLeast(ver, 1, 3)) return R(false, "Update Hallow Torrents for search");
                var r = Family.Send("Torrents", "search-add " + p["id"] + catPart, 45000);
                err = Family.Error(r, "Hallow Torrents isn't running"); if (err != null) return R(false, Scrub(err));
                if (Log != null) Log("a torrent added");
                return R(true, AddedMsg(r));
            }
            if (d == "search")
            {
                if (!VerAtLeast(ver, 1, 3)) return R(false, "Update Hallow Torrents for search");
                string sc; p.TryGetValue("scat", out sc); string scv;
                var line = "search " + Words(p["q"]) + " --n 20" + (sc != null && SearchCats.TryGetValue(sc, out scv) ? " --cat " + scv : "");
                var r = Family.Send("Torrents", line, 45000);
                err = Family.Error(r, "Hallow Torrents didn't answer in time"); if (err != null) return R(false, Scrub(err));
                Dictionary<string, object> sr = null; try { sr = js.Deserialize<Dictionary<string, object>>(r.Substring(3)); } catch { }
                if (sr == null) return R(false, "Hallow Torrents sent something unexpected");
                var results = new List<object>();
                foreach (var x in List(sr, "results"))
                {
                    var t = x as Dictionary<string, object>; if (t == null) continue; var id = Get(t, "id") as string; if (id == null || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,16}$")) continue;
                    results.Add(new Dictionary<string, object> { { "id", id }, { "title", Short(Get(t, "title") as string, 200) }, { "size", NumOrNull(Get(t, "size"), 0) }, { "seeders", IntOrNull(Get(t, "seeders")) }, { "peers", IntOrNull(Get(t, "peers")) },
                        { "category", Short(Get(t, "category") as string, 30) }, { "resolution", Short(Get(t, "resolution") as string, 12) }, { "source", Short(Get(t, "source") as string, 16) }, { "when", When(Get(t, "published")) },
                        { "flags", List(t, "flags").OfType<string>().Select(f => Short(f, 60)).Take(4).ToList() }, { "inList", Flag(t, "inList") } });
                    if (results.Count >= 30) break;
                }
                var res = R(true, results.Count + (results.Count == 1 ? " result" : " results"));
                res["results"] = results; res["total"] = IntOrNull(Get(sr, "total"));
                return res;
            }
            return R(false, "Not allowed");
        }
        static string AddedMsg(string reply)
        {
            try
            {
                var j = js.Deserialize<Dictionary<string, object>>(reply.Substring(3)); var c = Short(Get(j, "category") as string, 40);
                if (Flag(j, "asking")) return "Hallow Torrents is asking on the PC";
                return "Added" + (string.IsNullOrEmpty(c) ? "" : " to " + c) + " - it runs only over the VPN";
            }
            catch { return "Added"; }
        }
    }
}
