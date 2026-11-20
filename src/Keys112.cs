// Hallow Remote 1.12 - Hallow Keys 2.7 on the phone, properly:
//   - POST api/keys/change: a change from the phone's Keys tab (add / edit / trash), sealed on the phone with the phone copy's key.
//     Remote can't open it: it only checks the size and hands it to Hallow Keys over the family pipe ("phone-change"), and returns
//     Keys' answer. HTTPS only, like the copy itself.
//   - one-time share links for Hallow Keys' .hkshare items: Keys asks over the family pipe ("keys-share <expires> <file>"), Remote keeps
//     the encrypted file in memory only (never on disk) under a random 128-bit path, https://<pc>/share/<id>. The page there asks for
//     the passphrase and opens the item in the browser (Argon2id in plain JavaScript + WebCrypto). The file is served once, then
//     forgotten; unopened links are forgotten when they run out or when Remote stops. The link carries no part of Remote's own code.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HallowRemote
{
    public partial class Server
    {
        class OneShare { public byte[] File; public DateTime Until; }
        static readonly Dictionary<string, OneShare> shares = new Dictionary<string, OneShare>();
        public const int MaxShareBytes = 512 * 1024, MaxChangeBytes = 1024 * 1024;

        // "keys-share <unix seconds> <base64 .hkshare>" from Hallow Keys -> "ok {"url":...}"
        string KeysShare(string arg)
        {
            if (cert == null || TlsName == null) return "error Hallow Remote needs HTTPS for share links (Turn on HTTPS in Remote)";
            if (Address == null || listener == null) return "error Hallow Remote isn't listening";
            var sp = arg.IndexOf(' '); long until; byte[] file;
            if (sp <= 0 || !long.TryParse(arg.Substring(0, sp), out until)) return "error bad request";
            try { file = Convert.FromBase64String(arg.Substring(sp + 1).Trim()); } catch { return "error bad request"; }
            if (file.Length < 40 || file.Length > MaxShareBytes || Encoding.ASCII.GetString(file, 0, 8) != "HKITEM1\0") return "error that isn't a Hallow Keys shared item (or it's over 512 KB)";
            var when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(until);
            if (when <= DateTime.UtcNow) return "error that link would already have run out";
            if (when > DateTime.UtcNow.AddDays(7)) when = DateTime.UtcNow.AddDays(7);
            var idb = new byte[16]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(idb);
            var id = string.Concat(idb.Select(b => b.ToString("x2")));
            lock (shares)
            {
                foreach (var k in shares.Where(kv => kv.Value.Until < DateTime.UtcNow).Select(kv => kv.Key).ToList()) shares.Remove(k);
                if (shares.Count >= 20) return "error too many links waiting to be opened (20)";
                shares[id] = new OneShare { File = file, Until = when };
            }
            if (Log != null) Log("share link made (opens once, until " + when.ToLocalTime().ToString("d MMM HH:mm") + ")");
            return "ok " + Family.Json(new Dictionary<string, object> { { "url", "https://" + TlsName + ":" + S.Port + "/share/" + id }, { "until", Family.Time(when.ToLocalTime()) } });
        }
        static bool ShareId(string s) { return s != null && s.Length == 32 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')); }
        // /share/<id> (the page) and /share/<id>/file (the encrypted file, once). true = handled
        bool ShareRequest(Stream st, string[] segs, bool plainWhileTls, string method)
        {
            if (segs.Length < 1 || segs[0] != "share") return false;
            if (cert == null || plainWhileTls || method != "GET") { Send(st, 404, "text/plain", Encoding.ASCII.GetBytes("Not found")); return true; }
            if (segs.Length == 2 && segs[1] == "argon2id.js") { Send(st, 200, "text/javascript", Resource("argon2id.js"), true); return true; }
            if (segs.Length < 2 || !ShareId(segs[1]) || segs.Length > 3 || (segs.Length == 3 && segs[2] != "file")) { Send(st, 404, "text/plain", Encoding.ASCII.GetBytes("Not found")); return true; }
            OneShare s;
            lock (shares) { if (shares.TryGetValue(segs[1], out s) && s.Until < DateTime.UtcNow) { shares.Remove(segs[1]); s = null; } }
            if (segs.Length == 2) { Send(st, 200, "text/html; charset=utf-8", Resource("share.html")); return true; }
            if (s == null) { Send(st, 404, "application/json", Encoding.UTF8.GetBytes("{\"ok\":false,\"msg\":\"This link has run out or was already opened - ask for a new one.\"}")); return true; }
            lock (shares) shares.Remove(segs[1]);   // once
            Send(st, 200, "application/octet-stream", s.File);
            if (Log != null) Log("share link opened (and forgotten)");
            Changed();
            return true;
        }
        public int SharesWaiting { get { lock (shares) return shares.Count(kv => kv.Value.Until >= DateTime.UtcNow); } }

        // POST api/keys/change: the sealed change goes to Hallow Keys as it is
        void KeysChange(System.Net.Sockets.TcpClient client, Stream st, Dictionary<string, string> headers, byte[] buf, int bodyAt, int have)
        {
            string cl; long len;
            if (cert == null) { Send(st, 403, "application/json", Encoding.UTF8.GetBytes("{\"ok\":false,\"msg\":\"Needs HTTPS\"}")); return; }
            if (!headers.TryGetValue("Content-Length", out cl) || !long.TryParse(cl, out len) || len <= 0) { Send(st, 411, "application/json", Encoding.UTF8.GetBytes("{\"ok\":false}")); return; }
            if (len > MaxChangeBytes) { Send(st, 413, "application/json", Encoding.UTF8.GetBytes("{\"ok\":false,\"msg\":\"Too big\"}")); return; }
            var body = new byte[len]; int got = Math.Min((int)len, Math.Max(0, have - bodyAt)); Buffer.BlockCopy(buf, bodyAt, body, 0, got);
            while (got < len) { int r = st.Read(body, got, (int)len - got); if (r <= 0) break; got += r; }
            if (got < len) { Send(st, 400, "application/json", Encoding.UTF8.GetBytes("{\"ok\":false}")); return; }
            // only the outer shape is looked at: {"v":1,"iv":"...","ct":"..."} - the contents stay sealed
            string json = Encoding.UTF8.GetString(body); Dictionary<string, object> m = null;
            try { m = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(json); } catch { }
            if (m == null || !m.ContainsKey("iv") || !m.ContainsKey("ct") || m.Keys.Any(k => k != "v" && k != "iv" && k != "ct")) { Send(st, 400, "application/json", Encoding.UTF8.GetBytes("{\"ok\":false,\"msg\":\"Not a change from Hallow Keys\"}")); return; }
            client.SendTimeout = 15000;
            var reply = Family.Send("Keys", "phone-change " + Convert.ToBase64String(body), 9000);
            Dictionary<string, object> res;
            if (reply == null) res = new Dictionary<string, object> { { "ok", false }, { "msg", "Hallow Keys isn't running on your PC (or didn't answer) - nothing was changed." } };
            else if (reply.StartsWith("ok "))
            {
                try { res = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reply.Substring(3)); } catch { res = new Dictionary<string, object>(); }
                res["ok"] = true;
            }
            else if (reply.StartsWith("error unknown command")) res = new Dictionary<string, object> { { "ok", false }, { "msg", "Hallow Keys on your PC is older than 2.7 - update it to edit from the phone." } };
            else res = new Dictionary<string, object> { { "ok", false }, { "msg", reply.StartsWith("error ") ? reply.Substring(6) : reply } };
            if (Log != null) Log("Keys: a change from the phone - " + ((bool)res["ok"] ? "saved" : "refused"));
            Send(st, 200, "application/json", Encoding.UTF8.GetBytes(Family.Json(res)));
        }
    }
}
