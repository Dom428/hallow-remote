// Hallow Remote 1.15 - more of the family on the phone:
//   - Hallow Guard's weekly report (score then / now, change, fixed / new counts) and Windows hardening (score, warning count when shared)
//   - Hallow Shield's blocked counts (from its bridge's status.json - Shield is a browser extension, so there's no pipe; 1.6+)
//   - Hallow Assist's own PCs with an online state, and "Open on PC" (Assist's "connect <name>" - only a name Assist itself lists;
//     it opens Assist's window with that PC picked, the person still clicks Connect there)
//   - Hallow Mixer's per-app volumes (Mixer 3.24+: "apps" in /state, POST /app) - only keys Mixer itself lists, fixed fields only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace HallowRemote
{
    public partial class Server
    {
        static int? IntOrNull(object o) { return o == null ? (int?)null : (int)Math.Round(Num(o)); }
        static long? LongOrNull(object o) { return o == null ? (long?)null : (long)Math.Round(Num(o)); }

        // ---- Guard: the newest weekly report (counts only) and hardening
        static Dictionary<string, object> GuardReport(Dictionary<string, object> s)
        {
            var r = Get(s, "lastReport") as Dictionary<string, object>; if (r == null) return null;
            return new Dictionary<string, object> {
                { "week", Get(r, "week") as string }, { "score", IntOrNull(Get(r, "score")) }, { "previous", IntOrNull(Get(r, "previousScore")) }, { "delta", IntOrNull(Get(r, "delta")) },
                { "fixed", (int)Num(Get(r, "fixed")) }, { "newIssues", (int)Num(Get(r, "newIssues")) }, { "issues", (int)Num(Get(r, "issues")) },
                { "scans", (int)Num(Get(r, "scans")) }, { "threats", IntOrNull(Get(r, "threats")) },
                { "headline", Get(r, "headline") as string }, { "summary", Get(r, "summary") as string }, { "when", When(Get(r, "created")) } };
        }
        static Dictionary<string, object> GuardHardening(Dictionary<string, object> s)
        {
            object score = Get(s, "hardeningScore"), warn = Get(s, "hardeningWarnings");
            var h = Get(s, "hardening") as Dictionary<string, object>; if (warn == null && h != null) warn = Get(h, "warnings");
            if (score == null && warn == null) return null;
            return new Dictionary<string, object> { { "score", IntOrNull(score) }, { "warnings", IntOrNull(warn) } };
        }

        // ---- Shield: its status.json (no pipe). null = not there; {"sharing":false} = Guard found it in a browser but it doesn't share counts
        static Dictionary<string, object> ShieldState()
        {
            try
            {
                var f = Path.Combine(Family.DataDir("Shield"), "status.json");
                if (!File.Exists(f))
                {
                    var g = ReadJsonFile(Path.Combine(Family.DataDir("Guard"), "status.json"));
                    var fam = Get(g, "family") as Dictionary<string, object>; var st = Get(fam, "shield") as string;
                    return st != null && st != "missing" ? new Dictionary<string, object> { { "sharing", false } } : null;
                }
                var s = ReadJsonFile(f); if (s == null) return null;
                var browsers = new List<object>(); long t = 0, w = 0; bool anyT = false, anyW = false;
                foreach (var o in List(s, "browsers"))
                {
                    var b = o as Dictionary<string, object>; if (b == null) continue;
                    var bt = LongOrNull(Get(b, "blockedToday")); var bw = LongOrNull(Get(b, "blockedWeek"));
                    if (bt != null) { t += bt.Value; anyT = true; } if (bw != null) { w += bw.Value; anyW = true; }
                    browsers.Add(new Dictionary<string, object> { { "name", Short(Get(b, "name") as string, 40) }, { "profile", Short(Get(b, "profile") as string, 40) }, { "level", Cap(Short(Get(b, "level") as string, 20)) },
                        { "today", bt }, { "week", bw }, { "banners", LongOrNull(Get(b, "bannersToday")) } });
                }
                var today = LongOrNull(Get(s, "blockedToday")); var week = LongOrNull(Get(s, "blockedWeek"));
                if (today == null && anyT) today = t; if (week == null && anyW) week = w;
                DateTime u; var up = Get(s, "updated") as string;
                bool old = up != null && DateTime.TryParse(up, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out u) && (DateTime.Now - u).TotalHours > 24;
                return new Dictionary<string, object> { { "sharing", true }, { "today", today }, { "week", week }, { "banners", LongOrNull(Get(s, "bannersToday")) }, { "updated", When(up) }, { "old", old }, { "browsers", browsers } };
            }
            catch { return null; }
        }
        static string Cap(string s) { return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }
        static string Short(string s, int n) { if (s == null) return null; s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim(); return s.Length > n ? s.Substring(0, n) : s; }
        static Dictionary<string, object> ReadJsonFile(string f)
        {
            try
            {
                if (!File.Exists(f)) return null;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var r = new StreamReader(fs)) return js.Deserialize<Dictionary<string, object>>(r.ReadToEnd());
            }
            catch { return null; }
        }

        // ---- Assist: your own PCs (names and online state only)
        static List<object> AssistPcs(Dictionary<string, object> s)
        {
            var raw = Get(s, "pcs") as System.Collections.ArrayList; if (raw == null) return null;
            var r = new List<object>();
            foreach (var o in raw)
            {
                var p = o as Dictionary<string, object>; var name = Short(Get(p, "name") as string, 60); if (string.IsNullOrEmpty(name)) continue;
                var on = Get(p, "online") as string; if (on != "waiting" && on != "busy" && on != "offline") on = "unknown";
                r.Add(new Dictionary<string, object> { { "name", name }, { "online", on }, { "checked", When(Get(p, "checked")) } });
            }
            return r;
        }
        // "Open on PC": the name must be one Assist lists right now; nothing the phone types reaches Assist
        static string AssistConnect(string name, out string err)
        {
            err = null;
            var s = Family.StatusFile("Assist"); if (s == null) { err = "Hallow Assist isn't running"; return null; }
            var pcs = AssistPcs(s); if (pcs == null) { err = "Update Hallow Assist to 1.10 for this"; return null; }
            var hit = pcs.Cast<Dictionary<string, object>>().FirstOrDefault(p => (string)p["name"] == name);
            if (hit == null) { err = "That PC isn't in Hallow Assist"; return null; }
            err = Family.Error(Family.Send("Assist", "connect " + (string)hit["name"], 5000), "Hallow Assist isn't running");
            return "Hallow Assist is open on your PC with " + hit["name"] + " picked - click Connect there";
        }

        // ---- Mixer 3.24: per-app volume, mute and channel (POST /app with a JSON body, the same key as the other calls)
        static Dictionary<string, object> MixerPost(string path, string body)
        {
            try
            {
                if (RemoteSettings.TestDir == null && System.Diagnostics.Process.GetProcessesByName("HallowMixer").Length == 0) return null;
                var f = Path.Combine(Family.DataDir("Mixer"), "api.json"); if (!File.Exists(f)) return null;
                var a = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + a["port"] + path); req.Method = "POST"; req.ContentType = "application/json";
                req.Headers["X-Hallow-Key"] = a["key"] as string; req.Timeout = 2500; req.ReadWriteTimeout = 2500; req.Proxy = null;
                var b = Encoding.UTF8.GetBytes(body); req.ContentLength = b.Length;
                using (var st = req.GetRequestStream()) st.Write(b, 0, b.Length);
                using (var r = req.GetResponse()) using (var rd = new StreamReader(r.GetResponseStream())) return js.Deserialize<Dictionary<string, object>>(rd.ReadToEnd());
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr != null) try { using (var rd = new StreamReader(hr.GetResponseStream())) return js.Deserialize<Dictionary<string, object>>(rd.ReadToEnd()); } catch { }
                return null;
            }
            catch { return null; }
        }
        // the phone sends: key, and one of v (0-100), m (toggle / 1 / 0) or ch (a channel id)
        static Dictionary<string, object> MixerApp(Dictionary<string, string> q)
        {
            Func<bool, string, Dictionary<string, object>> res = (ok, text) => new Dictionary<string, object> { { "ok", ok }, { "msg", text } };
            string key; q.TryGetValue("key", out key);
            var st = Mixer("/state"); if (st == null) return res(false, "Hallow Mixer isn't running");
            var apps = Get(st, "apps") as System.Collections.ArrayList; if (apps == null) return res(false, "Update Hallow Mixer to 3.24 for app volumes");
            var app = apps.OfType<Dictionary<string, object>>().FirstOrDefault(x => key != null && (Get(x, "key") as string) == key);
            if (app == null) return res(false, "That app isn't playing sound any more");
            var name = Short(Get(app, "name") as string, 60) ?? "The app";
            var body = new Dictionary<string, object> { { "key", key } }; string said;
            string v, m, ch; int vol;
            if (q.TryGetValue("v", out v) && int.TryParse(v, out vol)) { vol = Math.Max(0, Math.Min(100, vol)); body["volume"] = vol; said = name + " " + vol + "%"; }
            else if (q.TryGetValue("m", out m) && (m == "toggle" || m == "1" || m == "0"))
            {
                bool now = Get(app, "muted") is bool && (bool)Get(app, "muted"); bool to = m == "toggle" ? !now : m == "1";
                body["muted"] = to; said = name + (to ? " muted" : " unmuted");
            }
            else if (q.TryGetValue("ch", out ch) && Channels.Contains(ch)) { body["channel"] = ch; said = name + " moved to " + ChannelLabel(st, ch); }
            else return res(false, "Not one of the app controls");
            var r = MixerPost("/app", js.Serialize(body));
            if (r == null) return res(false, "Hallow Mixer didn't answer");
            var okv = Get(r, "ok"); if (okv is bool && !(bool)okv) return res(false, Get(r, "msg") as string ?? "Hallow Mixer refused that");
            return res(true, said);
        }
        static string ChannelLabel(Dictionary<string, object> st, string id)
        {
            foreach (var o in List(st, "channels")) { var c = o as Dictionary<string, object>; if (c != null && (Get(c, "id") as string) == id) return (Get(c, "name") as string ?? id).Replace("System & other", "System"); }
            return id;
        }
    }
}
