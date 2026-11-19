// Hallow Remote v1.11 - file transfer.
// Phone -> PC: POST api/upload?name=<file name> with the file as the body, streamed into Downloads\Hallow Remote (off by default,
// size cap, safe names, never overwrites, announced on the PC each time, stops while paused).
// PC -> phone: a "Send to phone" list filled on the PC only; the phone sees names, sizes and random ids (never paths); 24 hours each.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Web.Script.Serialization;

namespace HallowRemote
{
    [DataContract]
    public class Outgoing
    {
        [DataMember] public string Id = "";     // a random id the phone uses (never the path)
        [DataMember] public string Name = "";   // the file's name, shown on the phone
        [DataMember] public string Path = "";   // stays on the PC
        [DataMember] public long Size;
        [DataMember] public string Added = "";  // ISO time; the entry goes away 24 hours later
    }

    public static class Files
    {
        public const int Hours = 24, MaxOutgoing = 20;
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
        static readonly Guid DownloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        // Downloads\Hallow Remote (in developer tests: <test folder>\Downloads\Hallow Remote, so tests never touch the real Downloads)
        public static string UploadDir
        {
            get
            {
                if (RemoteSettings.TestDir != null) return Path.Combine(RemoteSettings.TestDir, "Downloads", "Hallow Remote");
                string dl = null; IntPtr p;
                try { if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out p) == 0) { dl = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p); } } catch { }
                if (string.IsNullOrEmpty(dl)) dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                return Path.Combine(dl, "Hallow Remote");
            }
        }

        static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        // a name from the phone -> a plain file name: no folders, no characters Windows refuses or hides, no reserved device names, not too long
        public static string SafeName(string raw, int max = 150)
        {
            var s = raw ?? "";
            int cut = Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\')); if (cut >= 0) s = s.Substring(cut + 1);
            var bad = Path.GetInvalidFileNameChars(); var sb = new StringBuilder();
            foreach (var ch in s)
            {
                var cat = char.GetUnicodeCategory(ch);
                sb.Append(char.IsControl(ch) || bad.Contains(ch) || cat == UnicodeCategory.Format ? '_' : ch);   // Format: e.g. right-to-left override tricks
            }
            s = sb.ToString().Trim().TrimEnd('.', ' ').TrimStart(' ');
            if (s.Trim('.').Length == 0) s = "file";
            var stem = s.Split('.')[0].TrimEnd(' ');
            if (Reserved.Contains(stem) || ((stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem.Length == 4 && "¹²³".IndexOf(stem[3]) >= 0)) s = "_" + s;
            max = Math.Max(24, Math.Min(150, max));
            if (s.Length > max)
            {
                var ext = Path.GetExtension(s); if (ext.Length > 20) ext = "";
                s = s.Substring(0, max - ext.Length).TrimEnd('.', ' ') + ext;
            }
            return s;
        }
        // "name.ext", or "name (2).ext", "name (3).ext"... - never an existing file
        public static string Unique(string dir, string name)
        {
            var p = Path.Combine(dir, name); if (!File.Exists(p) && !Directory.Exists(p)) return p;
            var ext = Path.GetExtension(name); var stem = name.Substring(0, name.Length - ext.Length);
            for (int i = 2; ; i++) { p = Path.Combine(dir, stem + " (" + i + ")" + ext); if (!File.Exists(p) && !Directory.Exists(p)) return p; }
        }
        public static string Size(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1048576) return Math.Round(b / 1024.0) + " KB";
            if (b < 1073741824) return (b < 10485760 ? Math.Round(b / 1048576.0, 1) : Math.Round(b / 1048576.0)).ToString(CultureInfo.InvariantCulture) + " MB";
            return Math.Round(b / 1073741824.0, 1).ToString(CultureInfo.InvariantCulture) + " GB";
        }
        public static DateTime AddedTime(Outgoing o) { DateTime t; return DateTime.TryParse(o.Added, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) ? t : DateTime.MinValue; }
        public static double HoursLeft(Outgoing o) { return Hours - (DateTime.Now - AddedTime(o)).TotalHours; }
    }

    public partial class Server
    {
        public Action<string, long> Uploaded;   // the window says it (log line and a balloon - through Hallow Hub's icon when it hosts)
        static readonly object uploadLock = new object();
        long UploadMax { get { return Math.Max(1, Math.Min(16384, S.UploadMaxMB)) * 1048576L; } }

        string Upload(TcpClient client, Stream st, Dictionary<string, string> headers, Dictionary<string, string> q, byte[] buf, int bodyAt, int have, out int code)
        {
            Func<int, string, string> fail = (c, m) => js.Serialize(new Dictionary<string, object> { { "ok", false }, { "msg", m } });
            if (!S.AllowUpload || Paused) { code = 403; return fail(403, "Sending files to this PC is off - turn it on in Hallow Remote on your PC"); }
            string te, cl, name, expect; long len;
            if (headers.TryGetValue("Transfer-Encoding", out te) && te.Length > 0 || !headers.TryGetValue("Content-Length", out cl) || !long.TryParse(cl, out len) || len < 0) { code = 411; return fail(411, "The file's size is missing"); }
            if (len == 0) { code = 400; return fail(400, "That file is empty"); }
            if (len > UploadMax) { code = 413; return fail(413, "Too big - this PC takes files up to " + Files.Size(UploadMax)); }
            var dir = Files.UploadDir; string tmp = null;
            q.TryGetValue("name", out name); name = Files.SafeName(name, 248 - dir.Length - 8);   // the whole path stays under Windows' 260 characters, with room for " (12)"
            try
            {
                Directory.CreateDirectory(dir);
                try { var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace; if (free < len + 1073741824L) { code = 507; return fail(507, "Not enough free space on the PC"); } } catch { }
                if (headers.TryGetValue("Expect", out expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase)) { var c100 = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); st.Write(c100, 0, c100.Length); st.Flush(); }
                tmp = Path.Combine(dir, ".hallow-upload-" + RemoteSettings.NewCode() + ".part");
                client.ReceiveTimeout = 30000; long got = 0;
                using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
                {
                    try { File.SetAttributes(tmp, FileAttributes.Hidden); } catch { }
                    int first = (int)Math.Min(have - bodyAt, len); if (first > 0) { f.Write(buf, bodyAt, first); got = first; }
                    var chunk = new byte[65536];
                    while (got < len)
                    {
                        if (Paused || !S.AllowUpload) { f.Close(); TryDelete(tmp); code = 403; return fail(403, "Stopped - the remote was paused or sending files was turned off on the PC"); }
                        int r = st.Read(chunk, 0, (int)Math.Min(chunk.Length, len - got)); if (r <= 0) break;
                        f.Write(chunk, 0, r); got += r;
                    }
                }
                if (got < len || Paused || !S.AllowUpload) { TryDelete(tmp); code = 400; return fail(400, "The file didn't arrive completely - try again"); }
                string final;
                lock (uploadLock) { final = Files.Unique(dir, name); File.Move(tmp, final); }
                tmp = null;
                try { File.SetAttributes(final, FileAttributes.Normal); } catch { }
                MarkFromInternet(final);
                var shown = Path.GetFileName(final);
                if (Input.Test) try { lock (uploadLock) File.AppendAllText(Input.TestLog, "upload " + shown + " " + len + "\n", Encoding.UTF8); } catch { }
                if (Uploaded != null) Uploaded(shown, len);
                code = 200;
                return js.Serialize(new Dictionary<string, object> { { "ok", true }, { "msg", "Saved on your PC as " + shown }, { "name", shown } });
            }
            catch (Exception e) { if (tmp != null) TryDelete(tmp); code = 400; return fail(400, "Couldn't save it: " + e.Message); }
        }
        static void TryDelete(string f) { try { File.Delete(f); } catch { } }   // only ever our own half-written .part file
        // Windows' "came from another computer" mark, so SmartScreen and Office treat a phone's file like a download
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(IntPtr h, byte[] b, int n, out int written, IntPtr ov);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        static void MarkFromInternet(string path)
        {
            try
            {
                var h = CreateFileW(path + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0x80, IntPtr.Zero);
                if (h == new IntPtr(-1)) return;
                var b = Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n"); int w; WriteFile(h, b, b.Length, out w, IntPtr.Zero); CloseHandle(h);
            }
            catch { }
        }

        // ---- the "Send to phone" list
        // entries older than 24 hours, or whose file is gone, drop out (saved when anything changed)
        public List<Outgoing> OutboxNow()
        {
            lock (S)
            {
                int before = S.Outbox.Count;
                S.Outbox.RemoveAll(o => Files.HoursLeft(o) <= 0 || !File.Exists(o.Path));
                if (S.Outbox.Count != before) S.Save();
                return S.Outbox.ToList();
            }
        }
        // a file picked or dropped on the PC (null = added; otherwise why not)
        public string AddOutgoing(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
                if (Directory.Exists(path)) return "Folders can't be sent - pick files";
                var fi = new FileInfo(path); if (!fi.Exists) return "File not found";
                lock (S)
                {
                    OutboxNow();
                    var old = S.Outbox.FirstOrDefault(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (old != null) S.Outbox.Remove(old);
                    else if (S.Outbox.Count >= Files.MaxOutgoing) return "That's " + Files.MaxOutgoing + " files - remove one first";
                    S.Outbox.Insert(0, new Outgoing { Id = RemoteSettings.NewCode(), Name = fi.Name, Path = path, Size = fi.Length, Added = DateTime.Now.ToString("o") });
                    S.Save();
                }
                Changed(); return null;
            }
            catch (Exception e) { return e.Message; }
        }
        public void RemoveOutgoing(string id) { lock (S) { S.Outbox.RemoveAll(o => o.Id == id); S.Save(); } Changed(); }
        Outgoing OutgoingById(string id)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(id ?? "", "^[a-z0-9]{14}$")) return null;
            return OutboxNow().FirstOrDefault(o => Same(o.Id, id));
        }
        Dictionary<string, object> FilesState()
        {
            return new Dictionary<string, object> { { "upload", S.AllowUpload && !Paused }, { "maxMB", (int)(UploadMax / 1048576) },
                { "outbox", OutboxNow().Select(o => (object)new Dictionary<string, object> { { "id", o.Id }, { "name", o.Name }, { "size", Files.Size(o.Size) }, { "hours", (int)Math.Ceiling(Files.HoursLeft(o)) } }).ToList() } };
        }
        // the type the phone gets: media and documents by their extension; anything a browser could run as a page here is a plain download
        static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".png", "image/png" }, { ".gif", "image/gif" }, { ".webp", "image/webp" }, { ".heic", "image/heic" }, { ".bmp", "image/bmp" },
            { ".mp4", "video/mp4" }, { ".mov", "video/quicktime" }, { ".webm", "video/webm" }, { ".mkv", "video/x-matroska" },
            { ".mp3", "audio/mpeg" }, { ".m4a", "audio/mp4" }, { ".wav", "audio/wav" }, { ".ogg", "audio/ogg" }, { ".flac", "audio/flac" },
            { ".pdf", "application/pdf" }, { ".txt", "text/plain; charset=utf-8" }, { ".csv", "text/csv; charset=utf-8" }, { ".json", "application/json" },
            { ".zip", "application/zip" }, { ".7z", "application/x-7z-compressed" }, { ".apk", "application/vnd.android.package-archive" },
            { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" }, { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
            { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" } };
        static string ContentType(string name) { string t; return Types.TryGetValue(Path.GetExtension(name) ?? "", out t) ? t : "application/octet-stream"; }
        // attachment; filename="plain ASCII fallback"; filename*=UTF-8''<the real name> (RFC 6266 / 5987)
        static string Disposition(string name)
        {
            var ascii = new string(name.Select(c => c < 32 || c > 126 || c == '"' || c == '\\' || c == ';' ? '_' : c).ToArray());
            var sb = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(name))
            {
                char c = (char)b;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "!#$&+-.^_`|~".IndexOf(c) >= 0) sb.Append(c); else sb.Append('%').Append(b.ToString("X2"));
            }
            return "attachment; filename=\"" + ascii + "\"; filename*=UTF-8''" + sb;
        }
    }
}
