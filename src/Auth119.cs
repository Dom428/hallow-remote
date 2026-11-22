// Hallow Remote 1.19 - hosts Hallow Authenticator, the family's 2FA app for your phone (repo Dom428/hallow-authenticator).
//   - /<code>/auth/ serves the app's own files (embedded from src\web\auth, a copy of the Authenticator release) with a strict
//     Content-Security-Policy: no inline script or style, nothing from anywhere but this PC. The app keeps itself on the phone (service
//     worker), so it opens with this PC off. Your codes never come here.
//   - api/backup: an ENCRYPTED backup from the phone (its own backup password - Remote can't open it), only the outer shape is checked,
//     size-limited, written to %APPDATA%\HallowAuthenticator\backups. api/backups lists them / gives one back for a restore.
//   - api/xfer: Hallow Keys' one-time transfer. Keys hands Remote (over the family pipe) a lookup id and a blob sealed with a key that
//     only the PC screen and the phone know; Remote keeps it in memory only, gives it out once, and tells Keys when the phone says done.
//   - api/mirror: the optional PC mirror - a sealed blob only Hallow Keys can open, written to %APPDATA%\HallowAuthenticator\mirror.bin.
// Every POST needs the link's code, the page's CSRF token, the same origin; all of it needs HTTPS; writes are rate-limited.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HallowRemote
{
    public partial class Server
    {
        public const int MaxBackupBytes = 2 * 1024 * 1024, MaxMirrorBytes = 512 * 1024, MaxXferBytes = 1024 * 1024, MaxBackups = 500;
        const string AuthCsp = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self'; worker-src 'self'; manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'";
        public static string AuthDir { get { return Family.DataDir("Authenticator"); } }
        public static string BackupDir { get { return Path.Combine(AuthDir, "backups"); } }
        public static string MirrorFile { get { return Path.Combine(AuthDir, "mirror.bin"); } }
        static readonly Regex BackupName = new Regex(@"^backup-\d{8}-\d{6}(-\d{1,3})?\.hallowauth$");
        static readonly Regex Hex32 = new Regex("^[0-9a-f]{32}$"), Hex64 = new Regex("^[0-9a-f]{64}$");

        // the app's files, embedded at build time (names are fixed - nothing from the request is used as a path)
        static readonly Dictionary<string, string> AuthFiles = new Dictionary<string, string> {
            { "", "text/html; charset=utf-8" }, { "index.html", "text/html; charset=utf-8" }, { "app.css", "text/css; charset=utf-8" }, { "app.js", "text/javascript; charset=utf-8" },
            { "otp.js", "text/javascript; charset=utf-8" }, { "vault.js", "text/javascript; charset=utf-8" }, { "qr.js", "text/javascript; charset=utf-8" }, { "importers.js", "text/javascript; charset=utf-8" },
            { "scrypt.js", "text/javascript; charset=utf-8" }, { "sw.js", "text/javascript; charset=utf-8" }, { "manifest.json", "application/manifest+json" },
            { "icon-192.png", "image/png" }, { "icon-512.png", "image/png" }, { "icon-mask-512.png", "image/png" }, { "apple-touch-icon.png", "image/png" } };
        public static byte[] AuthResource(string name) { return Resource("auth." + (name == "" ? "index.html" : name)); }
        public static string AuthVersion
        {
            get { try { var m = Regex.Match(Encoding.UTF8.GetString(AuthResource("sw.js")), "VERSION = '([0-9.]+)'"); return m.Success ? m.Groups[1].Value : null; } catch { return null; } }
        }

        static void SendAuth(Stream st, int code, string type, byte[] body, string extra = "")
        {
            var head = "HTTP/1.1 " + code + (code == 200 ? " OK" : code == 301 ? " Moved Permanently" : code == 404 ? " Not Found" : code == 403 ? " Forbidden" : code == 400 ? " Bad Request" : code == 405 ? " Method Not Allowed" : code == 411 ? " Length Required" : code == 413 ? " Payload Too Large" : code == 429 ? " Too Many Requests" : code == 507 ? " Insufficient Storage" : " Error") +
                "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length + "\r\nCache-Control: no-cache\r\nContent-Security-Policy: " + AuthCsp +
                "\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nCross-Origin-Opener-Policy: same-origin\r\nPermissions-Policy: camera=(self), microphone=(), geolocation=(), publickey-credentials-get=(self), publickey-credentials-create=(self)\r\n" + extra + "Connection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head); st.Write(h, 0, h.Length); st.Write(body, 0, body.Length); st.Flush();
        }
        static void AuthJson(Stream st, int code, Dictionary<string, object> o) { SendAuth(st, code, "application/json", Encoding.UTF8.GetBytes(js.Serialize(o))); }
        static Dictionary<string, object> Ok(params object[] kv) { var d = new Dictionary<string, object> { { "ok", true } }; for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }

        // rest = "/auth" or "/auth/..." (after the code)
        void AuthRequest(TcpClient client, Stream st, Dictionary<string, string> headers, string method, string rest, string target, byte[] buf, int bodyAt, int n)
        {
            if (rest == "/auth") { var to = "/" + S.Code + "/auth/"; SendAuth(st, 301, "text/plain", new byte[0], "Location: " + to + "\r\n"); return; }
            var sub = rest.Substring(6);   // after "/auth/"
            if (!sub.StartsWith("api/"))
            {
                string type;
                if (method != "GET" || !AuthFiles.TryGetValue(sub, out type)) { SendAuth(st, 404, "text/plain", Encoding.ASCII.GetBytes("Not found")); return; }
                byte[] body; try { body = AuthResource(sub); } catch { SendAuth(st, 404, "text/plain", Encoding.ASCII.GetBytes("Not found")); return; }
                SendAuth(st, 200, type, body); return;
            }
            var api = sub.Substring(4);
            if (cert == null) { AuthJson(st, 403, R(false, "Hallow Authenticator needs HTTPS - turn it on in Hallow Remote on your PC")); return; }
            bool post = method == "POST";
            if (!post && api == "hello") { AuthJson(st, 200, Ok("version", Program.Version, "app", AuthVersion, "csrf", CsrfToken, "accent", Program.AccentHex, "pc", Environment.MachineName, "backups", BackupCount())); return; }
            if (!post && api == "backups") { AuthJson(st, 200, Ok("items", BackupList())); return; }
            if (!post && api.StartsWith("backups/"))
            {
                var name = Uri.UnescapeDataString(api.Substring(8)); var f = Path.Combine(BackupDir, name);
                if (!BackupName.IsMatch(name) || !File.Exists(f)) { AuthJson(st, 404, R(false, "No such backup")); return; }
                SendAuth(st, 200, "application/json", File.ReadAllBytes(f), "Content-Disposition: attachment; filename=\"" + name + "\"\r\n"); return;
            }
            if (!post || (api != "backup" && api != "xfer" && api != "xfer-done" && api != "mirror" && api != "mirror-off")) { AuthJson(st, post ? 404 : 405, R(false, "Not found")); return; }
            string why; if (!CsrfOk(headers, out why)) { AuthJson(st, 403, R(false, why)); return; }
            if (Paused) { AuthJson(st, 403, R(false, "The remote is paused on your PC")); return; }
            int max = api == "backup" ? MaxBackupBytes : api == "mirror" ? MaxMirrorBytes : 4096, code;
            var body2 = ReadBody(st, headers, buf, bodyAt, n, max, out code);
            if (body2 == null) { AuthJson(st, code, R(false, code == 413 ? "Too big" : "Bad request")); return; }
            client.SendTimeout = 15000;
            var text = Encoding.UTF8.GetString(body2);
            if (api == "backup") { AuthBackup(st, text); return; }
            if (api == "mirror") { AuthMirror(st, text); return; }
            if (api == "mirror-off") { try { if (File.Exists(MirrorFile)) File.Delete(MirrorFile); } catch { } if (Log != null) Log("Authenticator: the PC mirror was turned off from the phone"); AuthJson(st, 200, Ok()); return; }
            var p = Query(text); string id; p.TryGetValue("id", out id);
            if (id == null || !Hex32.IsMatch(id)) { AuthJson(st, 400, R(false, "Bad request")); return; }
            if (api == "xfer")
            {
                var lim = RateLimited("auth:xfer"); if (lim != null) { AuthJson(st, 429, R(false, lim)); return; }
                Offer o; lock (offers) { Expire(); offers.TryGetValue(id, out o); if (o != null && o.State != "waiting") o = null; if (o != null) o.State = "fetched"; }
                if (o == null) { AuthJson(st, 404, R(false, "Nothing waiting for that code - make a new one in Hallow Keys (they last 10 minutes and open once)")); return; }
                if (Log != null) Log("Authenticator: the phone picked up a transfer from Hallow Keys");
                SendAuth(st, 200, "application/json", Encoding.UTF8.GetBytes("{\"ok\":true,\"blob\":" + o.Blob + "}")); lock (offers) o.Blob = null; return;
            }
            // xfer-done
            string proof; p.TryGetValue("proof", out proof);
            if (proof == null || !Hex64.IsMatch(proof)) { AuthJson(st, 400, R(false, "Bad request")); return; }
            Offer d; lock (offers) { offers.TryGetValue(id, out d); if (d != null && d.State == "fetched") { d.State = "done"; d.Proof = proof; } else d = null; }
            AuthJson(st, d == null ? 404 : 200, d == null ? R(false, "That transfer isn't open any more") : Ok());
        }
        static byte[] ReadBody(Stream st, Dictionary<string, string> headers, byte[] buf, int bodyAt, int n, int max, out int code)
        {
            code = 200; string cl; long len;
            if (!headers.TryGetValue("Content-Length", out cl) || !long.TryParse(cl, out len) || len < 0) { code = 411; return null; }
            if (len > max) { code = 413; return null; }
            var body = new byte[len]; int got = (int)Math.Min(len, Math.Max(0, n - bodyAt)); Buffer.BlockCopy(buf, bodyAt, body, 0, got);
            while (got < len) { int r = st.Read(body, got, (int)len - got); if (r <= 0) break; got += r; }
            if (got < len) { code = 400; return null; }
            return body;
        }
        // only the outer shape of what the phone sent: these keys, base64 where it should be. The contents stay sealed.
        static Dictionary<string, object> SealedShape(string json, string[] allowed, string[] required)
        {
            Dictionary<string, object> m;
            try { m = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(json); } catch { return null; }
            if (m == null || m.Keys.Any(k => !allowed.Contains(k)) || required.Any(k => !(m.ContainsKey(k) && m[k] is string))) return null;
            foreach (var k in new[] { "iv", "ct", "salt" }) if (m.ContainsKey(k)) { try { Convert.FromBase64String((string)m[k]); } catch { return null; } }
            return m;
        }
        void AuthBackup(Stream st, string json)
        {
            var m = SealedShape(json, new[] { "format", "v", "kdf", "iter", "salt", "iv", "ct", "made" }, new[] { "format", "salt", "iv", "ct" });
            if (m == null || (string)m["format"] != "hallow-authenticator-backup") { AuthJson(st, 400, R(false, "That isn't an encrypted Hallow Authenticator backup")); return; }
            var lim = RateLimited("auth:backup"); if (lim != null) { AuthJson(st, 429, R(false, lim)); return; }
            if (BackupCount() >= MaxBackups) { AuthJson(st, 507, R(false, "There are " + MaxBackups + " backups on the PC already - tidy up %APPDATA%\\HallowAuthenticator\\backups first")); return; }
            string name;
            try
            {
                Directory.CreateDirectory(BackupDir);
                var stem = "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"); name = stem + ".hallowauth";
                for (int i = 2; File.Exists(Path.Combine(BackupDir, name)) && i < 1000; i++) name = stem + "-" + i + ".hallowauth";
                var f = Path.Combine(BackupDir, name); var tmp = f + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false)); File.Move(tmp, f);
            }
            catch (Exception e) { AuthJson(st, 507, R(false, "Couldn't save it on the PC: " + e.Message)); return; }
            if (Log != null) Log("Authenticator: an encrypted backup was saved (" + Math.Max(1, json.Length / 1024) + " KB)");
            Changed(); AuthJson(st, 200, Ok("name", name));
        }
        void AuthMirror(Stream st, string json)
        {
            var m = SealedShape(json, new[] { "v", "iv", "ct" }, new[] { "iv", "ct" });
            if (m == null) { AuthJson(st, 400, R(false, "That isn't a sealed mirror")); return; }
            var lim = RateLimited("auth:mirror"); if (lim != null) { AuthJson(st, 429, R(false, lim)); return; }
            try { Directory.CreateDirectory(AuthDir); var tmp = MirrorFile + ".tmp"; File.WriteAllText(tmp, json, new UTF8Encoding(false)); if (File.Exists(MirrorFile)) File.Replace(tmp, MirrorFile, null); else File.Move(tmp, MirrorFile); }
            catch (Exception e) { AuthJson(st, 507, R(false, "Couldn't save it on the PC: " + e.Message)); return; }
            AuthJson(st, 200, Ok());
        }
        public static int BackupCount() { try { return Directory.Exists(BackupDir) ? Directory.GetFiles(BackupDir, "backup-*.hallowauth").Count(f => BackupName.IsMatch(Path.GetFileName(f))) : 0; } catch { return 0; } }
        static List<object> BackupList()
        {
            try
            {
                if (!Directory.Exists(BackupDir)) return new List<object>();
                return new DirectoryInfo(BackupDir).GetFiles("backup-*.hallowauth").Where(f => BackupName.IsMatch(f.Name)).OrderByDescending(f => f.LastWriteTime).Take(50)
                    .Select(f => (object)new Dictionary<string, object> { { "name", f.Name }, { "size", f.Length }, { "made", f.LastWriteTime.ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) } }).ToList();
            }
            catch { return new List<object>(); }
        }

        // ---- Hallow Keys' transfers (memory only; gone when picked up and done, after 10 minutes, or when Remote stops)
        class Offer { public string Blob; public DateTime Until; public string State = "waiting"; public string Proof; }
        static readonly Dictionary<string, Offer> offers = new Dictionary<string, Offer>();
        static void Expire() { foreach (var k in offers.Where(kv => kv.Value.Until < DateTime.UtcNow).Select(kv => kv.Key).ToList()) offers.Remove(k); }
        // "auth-offer <id> <unix until> <base64 of the sealed JSON>" / "auth-offer-state <id>" / "auth-offer-cancel <id>"
        string AuthOfferCmd(string cmd, string arg)
        {
            var parts = arg.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || !Hex32.IsMatch(parts[0])) return "error bad request";
            var id = parts[0];
            if (cmd == "auth-offer-state")
            {
                Offer o; lock (offers) { Expire(); offers.TryGetValue(id, out o); }
                return "ok " + Family.Json(new Dictionary<string, object> { { "state", o == null ? "gone" : o.State }, { "proof", o == null ? null : o.Proof } });
            }
            if (cmd == "auth-offer-cancel") { lock (offers) offers.Remove(id); return "ok"; }
            if (cert == null || TlsName == null) return "error Hallow Remote needs HTTPS for Hallow Authenticator (Turn on HTTPS in Remote)";
            if (Address == null || listener == null) return "error Hallow Remote isn't listening";
            long until; if (parts.Length < 3 || !long.TryParse(parts[1], out until)) return "error bad request";
            string blob; try { var raw = Convert.FromBase64String(parts[2]); if (raw.Length > MaxXferBytes) return "error too big (1 MB)"; blob = Encoding.UTF8.GetString(raw); } catch { return "error bad request"; }
            if (SealedShape(blob, new[] { "v", "iv", "ct" }, new[] { "iv", "ct" }) == null) return "error that isn't a sealed transfer";
            var when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(until);
            if (when <= DateTime.UtcNow) return "error that would already have run out";
            if (when > DateTime.UtcNow.AddMinutes(15)) when = DateTime.UtcNow.AddMinutes(15);
            lock (offers) { Expire(); if (offers.Count >= 5) return "error too many transfers waiting (5)"; offers[id] = new Offer { Blob = blob, Until = when }; }
            if (Log != null) Log("Authenticator: Hallow Keys has a transfer waiting for your phone (sealed; 10 minutes)");
            return "ok";
        }
        public int OffersWaiting { get { lock (offers) { Expire(); return offers.Count; } } }
        Dictionary<string, object> AuthState() { return new Dictionary<string, object> { { "https", cert != null }, { "backups", BackupCount() } }; }
    }
}
