// Hallow Remote 1.13 - the phone as a home-screen app:
//   - Hallow Guard and Hallow Assist on the phone (status from their status.json while their pipe is there; fixed commands only:
//     Guard "scan quick" / "open", Assist "invoke gethelp" (opens its window - the person still clicks Get help) / "disconnect")
//   - app icons drawn here (any + maskable, 192 / 512, 180 for iPhones) and a fuller manifest, so "Add to Home Screen" makes a real app
//   - a lock for wrong links: an address that tries many different wrong codes is refused for a while (the right code too, from there)
//   - a Content-Security-Policy on the pages (scripts, pictures and requests only from this PC)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace HallowRemote
{
    public partial class Server
    {
        // ---- Hallow Guard: score, last scan, threats, quick scan
        static Dictionary<string, object> GuardState()
        {
            var s = Family.StatusFile("Guard"); if (s == null) return null;
            object score = Get(s, "score"), threats = Get(s, "threats");
            return new Dictionary<string, object> {
                { "score", score == null ? null : (object)(int)Math.Round(Num(score)) }, { "label", Get(s, "scoreLabel") as string },
                { "todo", Get(s, "todo") == null ? null : (object)(int)Num(Get(s, "todo")) },
                { "scanning", Get(s, "scanning") is bool && (bool)Get(s, "scanning") }, { "scanKind", Get(s, "scanKind") as string },
                { "lastScan", When(Get(s, "lastScan")) }, { "lastResult", Get(s, "lastGuardScanResult") as string }, { "lastKind", Get(s, "lastGuardScanKind") as string },
                { "lastGuardScan", Get(s, "lastGuardScan") as string },
                { "threats", threats == null ? null : (object)(int)Num(threats) }, { "watch", Get(s, "watchDownloads") is bool && (bool)Get(s, "watchDownloads") },
                { "familyScore", Get(s, "familyScore") == null ? null : (object)(int)Math.Round(Num(Get(s, "familyScore"))) }, { "report", GuardReport(s) }, { "hardening", GuardHardening(s) } };   // v1.15
        }
        // the message to show (null = not one of the fixed commands); err = Guard's refusal, or that it isn't running
        static string GuardDo(string d, out string err)
        {
            err = null;
            if (d == "scan") { err = Family.Error(Family.Send("Guard", "scan quick", 5000), "Hallow Guard isn't running"); return "Quick scan started (Microsoft Defender)"; }
            if (d == "open") { err = Family.Error(Family.Send("Guard", "open history", 5000), "Hallow Guard isn't running"); return "Hallow Guard is open on your PC"; }
            if (d == "report") { err = Family.Error(Family.Send("Guard", "open reports", 5000), "Hallow Guard isn't running"); return "The weekly report is open in Hallow Guard on your PC"; }   // v1.15
            if (d == "hardening") { err = Family.Error(Family.Send("Guard", "open hardening", 5000), "Hallow Guard isn't running"); return "Hardening is open in Hallow Guard on your PC"; }
            return null;
        }

        // ---- Hallow Assist: is someone connected? (never codes or passwords - Assist doesn't share them)
        static Dictionary<string, object> AssistState()
        {
            var s = Family.StatusFile("Assist"); if (s == null) return null;
            var session = Get(s, "session") as string ?? "none";
            if (session != "none" && session != "waiting" && session != "asking" && session != "connected") session = "none";
            return new Dictionary<string, object> { { "session", session }, { "helping", Get(s, "helping") is bool && (bool)Get(s, "helping") }, { "unattended", Get(s, "unattended") is bool && (bool)Get(s, "unattended") }, { "pcs", AssistPcs(s) } };   // v1.15: your own PCs (Assist 1.10+)
        }
        static string AssistDo(string d, out string err)
        {
            err = null;
            // there's deliberately no way to start a help session from outside: this only opens Assist's window on the PC
            if (d == "open") { err = Family.Error(Family.Send("Assist", "invoke gethelp", 5000), "Hallow Assist isn't running"); return "Hallow Assist is open on your PC - click Get help there"; }
            if (d == "disconnect") { err = Family.Error(Family.Send("Assist", "disconnect", 5000), "Hallow Assist isn't running"); return "Disconnected"; }
            return null;
        }

        // ---- the manifest (start_url and scope are this link; the code is already in the phone's address bar)
        string Manifest()
        {
            var b = "/" + S.Code + "/";
            Func<string, int, string, object> icon = (f, n, purpose) => new Dictionary<string, object> { { "src", b + f }, { "sizes", n + "x" + n }, { "type", "image/png" }, { "purpose", purpose } };
            return js.Serialize(new Dictionary<string, object> {
                { "id", b }, { "name", "Hallow Remote" }, { "short_name", "Hallow" }, { "description", "Your phone as a remote for your PC and the Hallow apps" },
                { "start_url", b + "?app=1" }, { "scope", b }, { "display", "standalone" }, { "orientation", "portrait" },
                { "background_color", "#16121c" }, { "theme_color", "#16121c" },
                { "icons", new[] { icon("app-192.png", 192, "any"), icon("app-512.png", 512, "any"), icon("app-mask-192.png", 192, "maskable"), icon("app-mask-512.png", 512, "maskable") } },
                { "shortcuts", new[] {
                    new Dictionary<string, object> { { "name", "Home" }, { "url", b + "?tab=dash" } },
                    new Dictionary<string, object> { { "name", "Remote" }, { "url", b + "?tab=home" } },
                    new Dictionary<string, object> { { "name", "My buttons" }, { "url", b + "?tab=mine" } } } } });
        }

        // ---- app icons, drawn once: the Hallow Remote phone with its levels and signal waves (v1.14). maskable = full-bleed background, art inside the safe circle
        static readonly Dictionary<string, byte[]> iconCache = new Dictionary<string, byte[]>();
        public static byte[] AppIconPng(string name)
        {
            int size; bool mask;
            switch (name)
            {
                case "app-192.png": size = 192; mask = false; break;
                case "app-512.png": size = 512; mask = false; break;
                case "app-mask-192.png": size = 192; mask = true; break;
                case "app-mask-512.png": size = 512; mask = true; break;
                case "apple-touch-icon.png": size = 180; mask = true; break;
                default: return null;
            }
            lock (iconCache)
            {
                byte[] b; if (iconCache.TryGetValue(name, out b)) return b;
                using (var bmp = DrawIcon(size, mask)) using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); b = ms.ToArray(); }
                iconCache[name] = b; return b;
            }
        }
        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }
        static Bitmap DrawIcon(int n, bool mask)
        {
            var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.Clear(Color.Transparent);
                float s = n / 256f;
                var bgRect = mask ? new RectangleF(0, 0, n, n) : new RectangleF(8 * s, 8 * s, 240 * s, 240 * s);
                using (var bg = new LinearGradientBrush(new PointF(0, bgRect.Top), new PointF(0, bgRect.Bottom + 1), Color.FromArgb(0x2a, 0x22, 0x38), Color.FromArgb(0x16, 0x12, 0x1c)))
                    if (mask) g.FillRectangle(bg, bgRect); else using (var p = Round(bgRect, 48 * s)) g.FillPath(bg, p);
                // the art: 70 % of the plain icon, 56 % of a maskable one (inside its safe circle)
                float k = mask ? 0.8f : 1f; Func<float, float> X = v => n / 2f + (v - 128) * s * k;
                // v1.14: the new Hallow Remote icon - the phone (orange -> purple) with the mixer's levels, and signal waves
                var phone = new RectangleF(X(38), X(77), 87 * s * k, 143 * s * k);
                using (var p = Round(phone, 18 * s * k)) using (var fill = new LinearGradientBrush(phone, Color.FromArgb(0xff, 0x9a, 0x45), Color.FromArgb(0xb4, 0x8b, 0xf0), 60f)) g.FillPath(fill, p);
                var scr = RectangleF.Inflate(phone, -11.5f * s * k, -15.4f * s * k);
                using (var p = Round(scr, 9 * s * k)) using (var fill = new SolidBrush(Color.FromArgb(0x14, 0x10, 0x1b))) g.FillPath(fill, p);
                var cols = new[] { Color.FromArgb(0xf0, 0x78, 0x20), Color.FromArgb(0xa9, 0x8b, 0xe0), Color.FromArgb(0x6e, 0xd2, 0x8c) }; var lv = new[] { 0.7f, 0.4f, 0.55f };
                float gap = scr.Width / 4, tw = 10 * s * k, top = scr.Y + scr.Height * 0.2f, bot = scr.Bottom - scr.Height * 0.18f;
                for (int i = 0; i < 3; i++)
                {
                    float x = scr.X + gap * (i + 1), ky = bot - (bot - top) * lv[i];
                    using (var p = Round(new RectangleF(x - tw / 2, ky, tw, bot - ky), tw / 2)) using (var fb = new SolidBrush(cols[i])) g.FillPath(fb, p);
                }
                float wx = phone.Right - 2.6f * s * k, wy = phone.Y + 35.8f * s * k; var radii = new[] { 33.3f, 58.9f, 84.5f };
                for (int i = 0; i < 3; i++)
                {
                    float r = radii[i] * s * k;
                    using (var pen = new Pen(Color.FromArgb(255 - i * 45, 0x6e, 0xd2, 0x8c), 14 * s * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(pen, wx - r, wy - r, 2 * r, 2 * r, -62, 70);
                }
            }
            return bmp;
        }

        // ---- wrong links: 10 different wrong codes from one address within 10 minutes -> that address is refused for 15 minutes.
        // Different codes only, so a phone still holding an old link (after New link) never locks itself out.
        class Misses { public DateTime Since; public HashSet<string> Codes = new HashSet<string>(); public DateTime BlockedUntil; }
        static readonly Dictionary<string, Misses> misses = new Dictionary<string, Misses>();
        static TimeSpan BlockFor { get { return RemoteSettings.TestDir != null ? TimeSpan.FromSeconds(6) : TimeSpan.FromMinutes(15); } }
        public Action<string> Blocked;   // the window says so (never the codes that were tried)
        static string Ip(System.Net.Sockets.TcpClient c) { try { return ((System.Net.IPEndPoint)c.Client.RemoteEndPoint).Address.ToString(); } catch { return "?"; } }
        static bool IsBlocked(string ip)
        {
            lock (misses) { Misses m; return misses.TryGetValue(ip, out m) && m.BlockedUntil > DateTime.UtcNow; }
        }
        // a request whose first part isn't the code; true = this address is now blocked
        bool Missed(string ip, string first)
        {
            // what browsers ask for by themselves (favicon.ico, apple-touch-icon.png, robots.txt...) never counts: a code has no dot
            if (string.IsNullOrEmpty(first) || first.IndexOf('.') >= 0 || first == "share") return false;
            bool now = false;
            lock (misses)
            {
                if (misses.Count > 500) foreach (var k in misses.Where(kv => kv.Value.BlockedUntil < DateTime.UtcNow && (DateTime.UtcNow - kv.Value.Since).TotalMinutes > 10).Select(kv => kv.Key).ToList()) misses.Remove(k);
                Misses m; if (!misses.TryGetValue(ip, out m) || (DateTime.UtcNow - m.Since).TotalMinutes > 10) misses[ip] = m = new Misses { Since = DateTime.UtcNow, BlockedUntil = m != null ? m.BlockedUntil : DateTime.MinValue };
                // only a short hash is kept, never the tried code itself
                using (var h = System.Security.Cryptography.SHA256.Create()) m.Codes.Add(Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(first)), 0, 12));
                if (m.Codes.Count >= 10 && m.BlockedUntil < DateTime.UtcNow) { m.BlockedUntil = DateTime.UtcNow + BlockFor; m.Codes.Clear(); m.Since = DateTime.UtcNow; now = true; }
            }
            if (now && Blocked != null) Blocked(ip);
            return now;
        }
        static void TooMany(Stream st)
        {
            var h = Encoding.ASCII.GetBytes("HTTP/1.1 429 Too Many Requests\r\nContent-Type: text/plain\r\nContent-Length: 17\r\nRetry-After: 900\r\nCache-Control: no-store\r\nConnection: close\r\n\r\nToo many requests");
            st.Write(h, 0, h.Length); st.Flush();
        }

        // ---- v1.13: the pages may only load scripts, pictures and data from this PC (inline handlers stay: the page is one file)
        const string Csp = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; media-src 'self' blob:; connect-src 'self'; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    }
}
