// Hallow Remote 1.21 - automatic backups for Hallow Authenticator 1.1. After you add or change accounts (at most once a day) the phone
// sends its accounts, already encrypted on the phone: either the normal backup format (locked with the backup password of your latest
// backup) or "hallow-authenticator-vault" (the phone's own passcode wrap of its data key + the accounts sealed with that key).
// Remote never opens them: only the outer shape is checked (known keys, base64 where it should be), 2 MB at most, one per 30 s, and
// they go to %APPDATA%\HallowAuthenticator\autobackups - separate from the backups you make by hand, which are never touched. The newest
// 10 are kept; older automatic ones are removed (they're Remote's own rolling copies). api/autobackups lists them for a restore.
// Same checks as every Authenticator write: HTTPS, the link's code, the page's CSRF token, same origin, not paused.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HallowRemote
{
    public partial class Server
    {
        public const int KeepAutoBackups = 10;
        public static string AutoBackupDir { get { return Path.Combine(AuthDir, "autobackups"); } }
        static readonly Regex AutoName = new Regex(@"^auto-\d{8}-\d{6}(-\d{1,3})?\.hallowauth$");

        void AuthAutoBackup(Stream st, string json)
        {
            var m = SealedShape(json, new[] { "format", "v", "kdf", "iter", "salt", "iv", "ct", "made", "wiv", "wct" }, new[] { "format", "salt", "iv", "ct" });
            var format = m == null ? null : m["format"] as string;
            bool ok = m != null && (format == "hallow-authenticator-backup" && !m.ContainsKey("wiv") && !m.ContainsKey("wct")
                || format == "hallow-authenticator-vault" && m.ContainsKey("wiv") && m["wiv"] is string && m.ContainsKey("wct") && m["wct"] is string);
            if (!ok) { AuthJson(st, 400, R(false, "That isn't an encrypted Hallow Authenticator backup")); return; }
            var lim = RateLimited("auth:autobackup"); if (lim != null) { AuthJson(st, 429, R(false, lim)); return; }
            string name;
            try
            {
                Directory.CreateDirectory(AutoBackupDir);
                var stem = "auto-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"); name = stem + ".hallowauth";
                for (int i = 2; File.Exists(Path.Combine(AutoBackupDir, name)) && i < 1000; i++) name = stem + "-" + i + ".hallowauth";
                var f = Path.Combine(AutoBackupDir, name); var tmp = f + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false)); File.Move(tmp, f);
                PruneAutoBackups();
            }
            catch (Exception e) { AuthJson(st, 507, R(false, "Couldn't save it on the PC: " + e.Message)); return; }
            if (Log != null) Log("Authenticator: an automatic encrypted backup was saved (" + Math.Max(1, json.Length / 1024) + " KB, the newest " + KeepAutoBackups + " are kept)");
            Changed(); AuthJson(st, 200, Ok("name", name, "kept", AutoBackupCount()));
        }
        // keeps the newest 10 automatic backups (by name = time); backups made by hand are in another folder and never removed
        public static void PruneAutoBackups()
        {
            if (!Directory.Exists(AutoBackupDir)) return;
            foreach (var f in AutoFiles().Skip(KeepAutoBackups)) try { f.Delete(); } catch { }
        }
        static IEnumerable<FileInfo> AutoFiles()
        {
            return new DirectoryInfo(AutoBackupDir).GetFiles("auto-*.hallowauth").Where(f => AutoName.IsMatch(f.Name)).OrderByDescending(f => f.Name, StringComparer.Ordinal).ThenByDescending(f => f.LastWriteTimeUtc);
        }
        public static int AutoBackupCount() { try { return Directory.Exists(AutoBackupDir) ? AutoFiles().Count() : 0; } catch { return 0; } }
        static string LastAutoBackup() { try { var f = Directory.Exists(AutoBackupDir) ? AutoFiles().FirstOrDefault() : null; return f == null ? null : f.LastWriteTime.ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture); } catch { return null; } }
        void AutoBackupGet(Stream st, string api)
        {
            if (api == "autobackups")
            {
                List<object> items;
                try { items = Directory.Exists(AutoBackupDir) ? AutoFiles().Take(KeepAutoBackups).Select(f => (object)new Dictionary<string, object> { { "name", f.Name }, { "size", f.Length }, { "made", f.LastWriteTime.ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) } }).ToList() : new List<object>(); }
                catch { items = new List<object>(); }
                AuthJson(st, 200, Ok("items", items)); return;
            }
            var name = Uri.UnescapeDataString(api.Substring(12)); var file = Path.Combine(AutoBackupDir, name);
            if (!AutoName.IsMatch(name) || !File.Exists(file)) { AuthJson(st, 404, R(false, "No such backup")); return; }
            SendAuth(st, 200, "application/json", File.ReadAllBytes(file), "Content-Disposition: attachment; filename=\"" + name + "\"\r\n");
        }
    }
}
