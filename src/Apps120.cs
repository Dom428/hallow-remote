// Hallow Remote 1.20 - catches up with the newer apps' data:
//   VPN 1.2.6+: nextProtocol / needsUac / windowsLogin / target from status.json, and quick connects with "--keep-target" (the phone
//     never changes the server you picked on the PC); buttons that would make Windows ask (UAC) aren't offered.
//   Torrents 1.8: the add-magnet picker uses your real category names ("categories"), and searches use this phone's own
//     "--client remote:<session>" so another phone (or Hub) can't replace its results.
//   Gear 1.3.1+: "last known · 12 min ago", "waiting for its battery report", the held alert's device name, and Refresh (pipe "refresh":
//     Gear allows one every 30 s and never during a game).
//   Tasks 1.2+: live numbers from "quick" (a cached sample at most 5 s old), busy apps, Not responding (counts only).
//   Home 1.1+: the Feed now range (minPortions / maxPortions / gramsPerPortion), Demo mode.
//   Email 1.1: unread per account - its label only when you chose it and it isn't an address, else "Account 2" - and why it needs attention.
// Only checked fields leave the PC; nothing here is logged except what was done.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace HallowRemote
{
    public partial class Server
    {
        static bool VerAtLeast3(string v, int a, int b, int c)
        {
            int x = VerNum(v, 0), y = VerNum(v, 1), z = VerNum(v, 2);
            return x != a ? x > a : y != b ? y > b : z >= c;
        }
        // minutes since a local "yyyy-MM-ddTHH:mm:ss" time (null when there's none)
        static int? MinutesAgo(object iso)
        {
            DateTime t; var s = iso as string;
            if (s == null || s.Length > 40 || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t)) return null;
            return (int)Math.Max(0, Math.Floor((DateTime.Now - t).TotalMinutes));
        }

        // ---- VPN 1.2.6+
        static bool Vpn126(Dictionary<string, object> s) { return VerAtLeast3(Get(s, "version") as string, 1, 2, 6) && Get(s, "nextProtocol") is string; }
        static readonly Regex TargetRx = new Regex(@"^(?i:fastest|gaming|p2p|[a-z]{2}(-[a-z]{2})?([0-9]{1,5})?)$");   // a target word, a country code or a server name - never an address
        static void Vpn126State(Dictionary<string, object> o, Dictionary<string, object> s, bool on, bool ing, string proto, string next)
        {
            var u = Get(s, "needsUac") as Dictionary<string, object>;
            bool uc = u != null ? Flag(u, "connect") : next != "IKEv2", ud = u != null ? Flag(u, "disconnect") : proto == "NordLynx";
            var wl = FlagOrNull(s, "windowsLogin"); var t = Get(s, "target") as string;
            o["uacConnect"] = uc; o["uacDisconnect"] = ud; o["windowsLogin"] = wl;
            o["target"] = t != null && TargetRx.IsMatch(t) ? t : null; o["keepTarget"] = true;
            o["canConnect"] = !on && !ing && !uc && next == "IKEv2" && wl != false;
            o["canDisconnect"] = (on || ing) && !ud;
        }
        Dictionary<string, object> VpnConnect126(Dictionary<string, object> v, string d)
        {
            if ((bool)v["uacConnect"]) return R(false, "Windows would ask first (UAC) - connect from the PC");
            if (v["windowsLogin"] is bool && !(bool)v["windowsLogin"]) return R(false, "Windows doesn't have your Nord login saved yet - connect once from the PC");
            if (!(bool)v["canConnect"]) return R(false, "Connect from the PC");
            var r = Family.Send("VPN", "connect " + (d == "gaming" ? "gaming" : "fastest") + " --keep-target", 8000);
            var err = Family.Error(r, "Hallow VPN isn't running"); if (err != null) return R(false, Scrub(err));
            if (Log != null) Log("VPN connecting");
            return R(true, d == "gaming" ? "Connecting to the best server for gaming" : "Connecting to the fastest server");
        }

        // ---- Torrents 1.8
        static List<string> torCats; static DateTime torCatsAt;
        static List<string> TorCategories(string ver)
        {
            if (!VerAtLeast(ver, 1, 8)) return null;
            lock (CatRx)
            {
                if (torCats != null && (DateTime.UtcNow - torCatsAt).TotalSeconds < 30) return torCats;
                var j = TorPipe("categories", 3000); if (j == null) return torCats;
                torCats = List(j, "categories").OfType<string>().Where(c => CatRx.IsMatch(c)).Distinct().Take(40).ToList(); torCatsAt = DateTime.UtcNow;
                return torCats;
            }
        }
        static readonly Regex SidRx = new Regex("^[A-Za-z0-9_-]{8,40}$");
        // this phone's own search results (Torrents 1.8): "--client remote:<the page's session id>"
        static string TorClient(string ver, Dictionary<string, string> p)
        {
            if (!VerAtLeast(ver, 1, 8)) return "";
            string sid; p.TryGetValue("sid", out sid);
            return " --client remote" + (sid != null && SidRx.IsMatch(sid) ? ":" + sid : "");
        }

        // ---- Gear 1.3.1+ / refresh (1.2.2+)
        static bool GearGame(Dictionary<string, object> s) { var g = Get(s, "game"); return g is bool ? (bool)g : g is string && ((string)g).Trim().Length > 0; }
        static void Gear120(Dictionary<string, object> o, Dictionary<string, object> s)
        {
            bool game = GearGame(s);
            o["game"] = game; o["canRefresh"] = VerAtLeast3(Get(s, "version") as string, 1, 2, 2) ? (object)!game : null;   // null: a Gear without "refresh" (no button)
            var a = Get(s, "alert") as Dictionary<string, object>;
            o["alert"] = a == null ? null : new Dictionary<string, object> { { "deviceName", Short(Get(a, "deviceName") as string, 40) }, { "level", OneOf(Get(a, "level"), "low", "critical") ?? "low" } };
        }
        static string GearRefreshCheck()
        {
            var s = Family.StatusFile("Gear"); if (s == null) return "Hallow Gear isn't running";
            if (!VerAtLeast3(Get(s, "version") as string, 1, 2, 2)) return "Update Hallow Gear for Refresh";
            if (GearGame(s)) return "Not while a game is running - Hallow Gear reads batteries after it";
            return null;
        }
        Dictionary<string, object> GearRefresh()
        {
            var r = Family.Send("Gear", "refresh", 6000);
            var err = Family.Error(r, "Hallow Gear isn't running"); if (err != null) return R(false, Scrub(err));
            Dictionary<string, object> j = null; try { j = js.Deserialize<Dictionary<string, object>>(r.Substring(3)); } catch { }
            if (j == null) return R(false, "Hallow Gear sent something unexpected");
            if (Flag(j, "refreshing")) { if (Log != null) Log("Gear battery refresh"); return R(true, "Reading the batteries now"); }
            var why = Get(j, "reason") as string; var wait = IntOrNull(Get(j, "retryIn"));
            if (why == "too-soon") return R(false, "Hallow Gear read them just now - try again" + (wait != null && wait > 0 ? " in " + wait + " s" : " soon"));
            if (why == "game") return R(false, "Not while a game is running - Hallow Gear reads batteries after it");
            return R(false, "Hallow Gear didn't refresh");
        }

        // ---- Tasks 1.2+: "quick", asked at most every 2.5 s
        static Dictionary<string, object> tasksQuick; static DateTime tasksQuickAt; static readonly object tasksQuickLock = new object();
        static Dictionary<string, object> TasksQuick(string ver)
        {
            if (!VerAtLeast(ver, 1, 2)) return null;
            lock (tasksQuickLock)
            {
                if ((DateTime.UtcNow - tasksQuickAt).TotalSeconds < 2.5) return tasksQuick;
                tasksQuickAt = DateTime.UtcNow; tasksQuick = null;
                var r = Family.Send("Tasks", "quick", 1500);
                if (r != null && r.StartsWith("ok {")) try { tasksQuick = js.Deserialize<Dictionary<string, object>>(r.Substring(3)); } catch { }
                return tasksQuick;
            }
        }

        // ---- Home 1.1+: the Feed now range, Demo mode
        static bool PortionRange(Dictionary<string, object> f, out int lo, out int hi)
        {
            lo = 1; hi = 12; if (f == null) return false;
            var a = Get(f, "minPortions"); var b = Get(f, "maxPortions"); if (a == null || b == null || a is string || b is string || a is bool || b is bool) return false;
            int x = (int)Math.Round(Num(a)), y = (int)Math.Round(Num(b)); if (x < 1 || y < x || y > 12) return false;
            lo = x; hi = y; return true;
        }
        static void Home120(Dictionary<string, object> o, Dictionary<string, object> s, Dictionary<string, object> f)
        {
            o["demo"] = Flag(s, "demo");
            var fo = o["feeder"] as Dictionary<string, object>; int lo, hi;
            if (fo == null || !PortionRange(f, out lo, out hi)) return;
            fo["minPortions"] = lo; fo["maxPortions"] = hi;
            var g = NumOrNull(Get(f, "gramsPerPortion"), 1); fo["gramsPerPortion"] = g != null && g > 0 && g < 1000 ? g : null;
        }
        static void FeedRange(out int lo, out int hi)
        {
            var s = Family.StatusFile("Home"); PortionRange(Get(s, "feeder") as Dictionary<string, object>, out lo, out hi);
        }

        // ---- Email 1.1: per account
        static readonly string[] AttentionCodes = { "sign-in-failed", "app-password-needed", "server-unreachable", "quota", "error" };
        static readonly Regex LabelRx = new Regex(@"^[\p{L}\p{N}][\p{L}\p{N} &'()._+-]{0,23}$"), PlainText = new Regex(@"^[A-Za-z0-9][A-Za-z0-9 ,.;'()/-]{0,119}$");
        public static string AttentionText(string code, string text)
        {
            if (text != null && PlainText.IsMatch(text) && !text.Contains("://") && !Regex.IsMatch(text, @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}")) return text;
            switch (code)
            {
                case "sign-in-failed": return "Sign-in failed - check it in Hallow Email";
                case "app-password-needed": return "Needs an app password";
                case "server-unreachable": return "Can't reach the mail server";
                case "quota": return "Mailbox full or too many connections";
                default: return "Isn't syncing - check it in Hallow Email";
            }
        }
        public static string AccountLabel(Dictionary<string, object> a, int i)
        {
            var l = Get(a, "label") as string;
            bool ok = Flag(a, "labelUserChosen") && Get(a, "labelHasAddress") is bool && !Flag(a, "labelHasAddress") && l != null && !l.Contains("@") && LabelRx.IsMatch(l.Trim());
            return ok ? l.Trim() : "Account " + (i + 1);
        }
        static void Email120(Dictionary<string, object> o, Dictionary<string, object> s)
        {
            var raw = List(s, "accounts").OfType<Dictionary<string, object>>().ToList();
            if (!raw.Any(a => a.ContainsKey("labelUserChosen"))) return;   // Email 1.0: the total only, as before
            var list = new List<object>();
            for (int i = 0; i < raw.Count && list.Count < 10; i++)
            {
                var a = raw[i]; var code = OneOf(Get(a, "attention"), AttentionCodes); bool need = Flag(a, "needsAttention") || code != null;
                list.Add(new Dictionary<string, object> { { "label", AccountLabel(a, i) }, { "unread", IntOrNull(Get(a, "unread")) ?? 0 },
                    { "attention", need ? AttentionText(code, Get(a, "attentionText") as string) : null } });
            }
            o["accounts"] = list; o["needsAttention"] = list.Count(x => ((Dictionary<string, object>)x)["attention"] != null);
        }
    }
}
