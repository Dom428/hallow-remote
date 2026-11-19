// Hallow Remote v1.3 - HTTPS with the certificate Tailscale makes for this PC ("tailscale cert"), so the phone treats the page as a real app.
// Nothing is turned on here: HTTPS needs HTTPS switched on in your Tailscale admin console and the cert files in %APPDATA%\HallowRemote\tls.
// Without them everything stays plain HTTP, as before.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web.Script.Serialization;

namespace HallowRemote
{
    public static class Tls
    {
        public static string Dir { get { return Path.Combine(RemoteSettings.Dir, "tls"); } }
        public static string KeyName { get { return RemoteSettings.TestDir != null ? "HallowRemoteTlsTest" : "HallowRemoteTls"; } }

        // this PC's name on the tailnet, e.g. "gaming-pc.tail1234.ts.net" (from the Tailscale app; null if it can't tell)
        static string tsName; static DateTime tsAsked;
        public static string TailscaleName()
        {
            if (tsName != null || (DateTime.UtcNow - tsAsked).TotalSeconds < 60) return tsName;
            tsAsked = DateTime.UtcNow;
            try
            {
                var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Tailscale\tailscale.exe");
                if (!File.Exists(exe)) return null;
                var p = Process.Start(new ProcessStartInfo(exe, "status --json") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 });
                var outText = p.StandardOutput.ReadToEndAsync(); if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return null; }
                var d = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(outText.Result);
                object o; var certs = d.TryGetValue("CertDomains", out o) ? o as System.Collections.ArrayList : null;
                if (certs != null && certs.Count > 0 && certs[0] is string) return tsName = ((string)certs[0]).TrimEnd('.');
                var self = d.TryGetValue("Self", out o) ? o as Dictionary<string, object> : null;
                var dns = self != null && self.TryGetValue("DNSName", out o) ? o as string : null;
                if (!string.IsNullOrEmpty(dns)) tsName = dns.TrimEnd('.');
            }
            catch { }
            return tsName;
        }

        // the .crt/.key pair to use: the one named after this PC's tailnet name, or else the only *.ts.net pair in the folder
        public static string PairName()
        {
            try
            {
                if (!Directory.Exists(Dir)) return null;
                var name = TailscaleName();
                if (name != null && File.Exists(Path.Combine(Dir, name + ".crt")) && File.Exists(Path.Combine(Dir, name + ".key"))) return name;
                var all = Directory.GetFiles(Dir, "*.ts.net.crt").Select(f => Path.GetFileName(f)).Select(f => f.Substring(0, f.Length - 4))
                    .Where(n => File.Exists(Path.Combine(Dir, n + ".key"))).ToList();
                return all.Count == 1 ? all[0] : null;
            }
            catch { return null; }
        }
        // a stamp of the files, so a renewed certificate is picked up
        public static string Stamp()
        {
            var n = PairName(); if (n == null) return "";
            try { return n + File.GetLastWriteTimeUtc(Path.Combine(Dir, n + ".crt")).Ticks + "/" + File.GetLastWriteTimeUtc(Path.Combine(Dir, n + ".key")).Ticks; } catch { return ""; }
        }

        // loads the pair; null (and a reason) when it's missing, broken or out of date
        public static X509Certificate2 Load(out string name, out string problem)
        {
            problem = null; name = PairName(); if (name == null) return null;
            try
            {
                var crt = Pem(File.ReadAllText(Path.Combine(Dir, name + ".crt")), "CERTIFICATE");
                if (crt.Count == 0) { problem = "The certificate file has no certificate in it."; return null; }
                var leaf = new X509Certificate2(crt[0]);
                if (leaf.NotAfter < DateTime.Now) { problem = "Your HTTPS certificate ran out on " + leaf.NotAfter.ToString("d MMM yyyy") + " - run the tailscale cert command again."; return null; }
                var keyText = File.ReadAllText(Path.Combine(Dir, name + ".key"));
                List<byte[]> k; AsymmetricAlgorithm key;
                if ((k = Pem(keyText, "PRIVATE KEY")).Count > 0) key = FromPkcs8(k[0]);
                else if ((k = Pem(keyText, "EC PRIVATE KEY")).Count > 0) key = FromSec1(k[0], null);
                else if ((k = Pem(keyText, "RSA PRIVATE KEY")).Count > 0) key = FromPkcs1(k[0]);
                else key = null;
                if (key == null) { problem = "The key file isn't a private key Hallow Remote can read."; return null; }
                // Windows' TLS can't use a key that only lives in this app's memory, so it goes into one named key in your user key store
                // ("HallowRemoteTls"), replaced on every start - never piling up
                using (key)
                {
                    bool rsa = key is RSA; var ephemeral = rsa ? ((RSACng)key).Key : ((ECDsaCng)key).Key;
                    var blobType = rsa ? "RSAFULLPRIVATEBLOB" : "ECCPRIVATEBLOB";
                    var blob = ephemeral.Export(new CngKeyBlobFormat(blobType));
                    var alg = rsa ? CngAlgorithm.Rsa : ephemeral.Algorithm;
                    var cp = new CngKeyCreationParameters { KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.AllUsages };
                    cp.Parameters.Add(new CngProperty(blobType, blob, CngPropertyOptions.None));
                    var named = CngKey.Create(alg, KeyName, cp);
                    return rsa ? leaf.CopyWithPrivateKey(new RSACng(named)) : leaf.CopyWithPrivateKey(new ECDsaCng(named));
                }
            }            catch (Exception e) { problem = "Couldn't use the HTTPS certificate: " + e.Message; return null; }
        }

        static List<byte[]> Pem(string text, string label)
        {
            var res = new List<byte[]>(); string begin = "-----BEGIN " + label + "-----", end = "-----END " + label + "-----"; int i = 0;
            while ((i = text.IndexOf(begin, i, StringComparison.Ordinal)) >= 0)
            {
                int s = i + begin.Length, e = text.IndexOf(end, s, StringComparison.Ordinal); if (e < 0) break;
                res.Add(Convert.FromBase64String(new string(text.Substring(s, e - s).Where(c => !char.IsWhiteSpace(c)).ToArray())));
                i = e + end.Length;
            }
            return res;
        }
        // ---- private keys: PKCS#8 ("PRIVATE KEY"), SEC1 ("EC PRIVATE KEY", what tailscale cert writes) and PKCS#1 ("RSA PRIVATE KEY"),
        // read into key parameters (an imported key blob can't be handed on to Windows' TLS, parameters can)
        static readonly byte[] OidRsa = { 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01 }, OidEc = { 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01 },
            OidP256 = { 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07 }, OidP384 = { 0x2B, 0x81, 0x04, 0x00, 0x22 };
        static AsymmetricAlgorithm FromPkcs8(byte[] der)
        {
            var top = Seq(der); if (top.Count < 3 || top[1].Item1 != 0x30 || top[2].Item1 != 4) return null;
            var alg = Seq(Der(0x30, top[1].Item2)); if (alg.Count < 1 || alg[0].Item1 != 6) return null;
            if (alg[0].Item2.SequenceEqual(OidRsa)) return FromPkcs1(top[2].Item2);
            if (alg[0].Item2.SequenceEqual(OidEc)) return FromSec1(top[2].Item2, alg.Count > 1 && alg[1].Item1 == 6 ? alg[1].Item2 : null);
            return null;
        }
        static AsymmetricAlgorithm FromSec1(byte[] der, byte[] curveOid)
        {
            var s = Seq(der); byte[] d = null, pub = null;
            foreach (var e in s)
            {
                if (e.Item1 == 4) d = e.Item2;
                else if (e.Item1 == 0xA0) { var inner = Seq(Der(0x30, e.Item2)); if (inner.Count > 0 && inner[0].Item1 == 6) curveOid = inner[0].Item2; }
                else if (e.Item1 == 0xA1) { var inner = Seq(Der(0x30, e.Item2)); if (inner.Count > 0 && inner[0].Item1 == 3) pub = inner[0].Item2; }
            }
            if (d == null || pub == null || pub.Length < 3 || pub[1] != 4) return null;   // BIT STRING: 0 unused bits, 04 = uncompressed point
            ECCurve curve;
            if (curveOid == null || curveOid.SequenceEqual(OidP256)) curve = ECCurve.NamedCurves.nistP256;
            else if (curveOid.SequenceEqual(OidP384)) curve = ECCurve.NamedCurves.nistP384;
            else return null;
            int n = (pub.Length - 2) / 2;
            var p = new ECParameters { Curve = curve, D = Fit(d, n), Q = new ECPoint { X = pub.Skip(2).Take(n).ToArray(), Y = pub.Skip(2 + n).Take(n).ToArray() } };
            var ec = new ECDsaCng(); ec.ImportParameters(p); return ec;
        }
        static AsymmetricAlgorithm FromPkcs1(byte[] der)
        {
            var s = Seq(der); if (s.Count < 9) return null;
            var m = Fit(s[1].Item2, 0); int half = (m.Length + 1) / 2;
            var p = new RSAParameters { Modulus = m, Exponent = Fit(s[2].Item2, 0), D = Fit(s[3].Item2, m.Length), P = Fit(s[4].Item2, half), Q = Fit(s[5].Item2, half),
                DP = Fit(s[6].Item2, half), DQ = Fit(s[7].Item2, half), InverseQ = Fit(s[8].Item2, half) };
            var rsa = new RSACng(); rsa.ImportParameters(p); return rsa;
        }
        // a DER INTEGER's bytes without the sign byte, left-padded to len (0 = as is)
        static byte[] Fit(byte[] v, int len)
        {
            int i = 0; while (i < v.Length - 1 && v[i] == 0) i++;
            var b = v.Skip(i).ToArray(); if (len <= b.Length) return b;
            var r = new byte[len]; Array.Copy(b, 0, r, len - b.Length, b.Length); return r;
        }
        // the children (tag, contents) of a DER SEQUENCE
        static List<Tuple<int, byte[]>> Seq(byte[] d)
        {
            var res = new List<Tuple<int, byte[]>>(); int p = 0;
            if (d.Length < 2 || d[0] != 0x30) return res;
            int end = ReadLen(d, ref p); end += p;
            while (p < end && p < d.Length)
            {
                int tag = d[p]; int len = ReadLen(d, ref p);
                if (len < 0 || p + len > d.Length) break;
                var body = new byte[len]; Array.Copy(d, p, body, 0, len); p += len;
                res.Add(Tuple.Create(tag, body));
            }
            return res;
        }
        // skips the tag at p, reads the length; p ends at the contents
        static int ReadLen(byte[] d, ref int p)
        {
            p++; int len = d[p++];
            if ((len & 0x80) != 0) { int n = len & 0x7F; if (n > 3) return -1; len = 0; for (int i = 0; i < n; i++) len = (len << 8) | d[p++]; }
            return len;
        }
        static byte[] Der(int tag, byte[] body)
        {
            var l = new List<byte> { (byte)tag };
            if (body.Length < 128) l.Add((byte)body.Length);
            else { var b = BitConverter.GetBytes(body.Length).Reverse().SkipWhile(x => x == 0).ToArray(); l.Add((byte)(0x80 | b.Length)); l.AddRange(b); }
            l.AddRange(body); return l.ToArray();
        }    }

    // a stream that gives back one byte already read (to tell a TLS hello from plain HTTP on the same port)
    public class PrefixStream : Stream
    {
        readonly Stream inner; int first;
        public PrefixStream(Stream s, int b) { inner = s; first = b; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            if (first >= 0) { buffer[offset] = (byte)first; first = -1; return 1; }
            return inner.Read(buffer, offset, count);
        }
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); }
        public override void Flush() { inner.Flush(); }
        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
        public override void SetLength(long v) { throw new NotSupportedException(); }
        public override int ReadTimeout { get { return inner.ReadTimeout; } set { inner.ReadTimeout = value; } }
        public override int WriteTimeout { get { return inner.WriteTimeout; } set { inner.WriteTimeout = value; } }
        public override bool CanTimeout { get { return true; } }
    }
}
