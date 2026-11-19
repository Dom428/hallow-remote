// Hallow Remote - a small QR code encoder (byte mode, error correction L, versions 1-6 = up to 134 characters),
// so the link never has to be sent to an online QR service. Follows the QR spec (ISO/IEC 18004) the same way Nayuki's reference code does.
using System;
using System.Collections.Generic;
using System.Text;

namespace HallowRemote
{
    public static class Qr
    {
        static readonly int[] EcLen = { 0, 7, 10, 15, 20, 26, 18 }, Blocks = { 0, 1, 1, 1, 1, 1, 2 }, Total = { 0, 26, 44, 70, 100, 134, 172 };
        static readonly int[][] Align = { null, new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 } };

        // true = dark module; [row, column]
        public static bool[,] Encode(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            int ver = 1; while (ver <= 6 && bytes.Length + 2 > Total[ver] - EcLen[ver] * Blocks[ver]) ver++;
            if (ver > 6) throw new ArgumentException("Too long for a QR code here");
            int size = ver * 4 + 17, dataLen = Total[ver] - EcLen[ver] * Blocks[ver];
            // data bits: mode 0100 (bytes), 8-bit count, the bytes, terminator, padding
            var bits = new List<bool>();
            Action<int, int> put = (v, n) => { for (int i = n - 1; i >= 0; i--) bits.Add(((v >> i) & 1) != 0); };
            put(4, 4); put(bytes.Length, 8); foreach (var b in bytes) put(b, 8);
            put(0, Math.Min(4, dataLen * 8 - bits.Count)); while (bits.Count % 8 != 0) bits.Add(false);
            for (int pad = 0xEC; bits.Count < dataLen * 8; pad ^= 0xEC ^ 0x11) put(pad, 8);
            var data = new byte[dataLen]; for (int i = 0; i < bits.Count; i++) if (bits[i]) data[i >> 3] |= (byte)(0x80 >> (i & 7));
            // error correction per block, then interleave
            int nb = Blocks[ver], ec = EcLen[ver], shortLen = Total[ver] / nb, numShort = nb - Total[ver] % nb;
            var div = Divisor(ec); var blocks = new List<byte[]>(); int k = 0;
            for (int i = 0; i < nb; i++)
            {
                int len = shortLen - ec + (i < numShort ? 0 : 1); var dat = new byte[len]; Array.Copy(data, k, dat, 0, len); k += len;
                var e = Remainder(dat, div); var blk = new byte[shortLen + 1];
                Array.Copy(dat, blk, len); Array.Copy(e, 0, blk, i < numShort ? len + 1 : len, ec);   // short blocks get a gap byte before their EC
                blocks.Add(blk);
            }
            var all = new List<byte>();
            for (int i = 0; i < shortLen + 1; i++) for (int j = 0; j < nb; j++) if (i != shortLen - ec || j >= numShort) all.Add(blocks[j][i]);
            // function patterns
            var m = new bool[size, size]; var fn = new bool[size, size];
            Action<int, int, bool> set = (x, y, d) => { m[y, x] = d; fn[y, x] = true; };
            for (int i = 0; i < size; i++) { set(6, i, i % 2 == 0); set(i, 6, i % 2 == 0); }
            foreach (var c in new[] { new[] { 3, 3 }, new[] { size - 4, 3 }, new[] { 3, size - 4 } })
                for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++)
                {
                    int x = c[0] + dx, y = c[1] + dy; if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy)); set(x, y, dist != 2 && dist != 4);
                }
            var al = Align[ver];
            for (int i = 0; i < al.Length; i++) for (int j = 0; j < al.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == al.Length - 1) || (i == al.Length - 1 && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) set(al[i] + dx, al[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
            Format(set, size, 0);   // reserve the format areas (real bits drawn after masking)
            // codewords in the zig-zag order, then mask 0 ((x + y) % 2 == 0)
            int bit = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j; bool up = ((right + 1) & 2) == 0; int y = up ? size - 1 - vert : vert;
                        if (!fn[y, x] && bit < all.Count * 8) { m[y, x] = ((all[bit >> 3] >> (7 - (bit & 7))) & 1) != 0; bit++; }
                    }
            }
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) if (!fn[y, x] && (x + y) % 2 == 0) m[y, x] = !m[y, x];
            Format(set, size, 0);
            return m;
        }

        // 15 format bits: error level L (01) + mask 0, BCH-protected and XORed with 0x5412
        static void Format(Action<int, int, bool> set, int size, int mask)
        {
            int data = (1 << 3) | mask, rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;
            Func<int, bool> b = i => ((bits >> i) & 1) != 0;
            for (int i = 0; i <= 5; i++) set(8, i, b(i));
            set(8, 7, b(6)); set(8, 8, b(7)); set(7, 8, b(8));
            for (int i = 9; i < 15; i++) set(14 - i, 8, b(i));
            for (int i = 0; i < 8; i++) set(size - 1 - i, 8, b(i));
            for (int i = 8; i < 15; i++) set(8, size - 15 + i, b(i));
            set(8, size - 8, true);
        }

        static int Mul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11D); z ^= ((y >> i) & 1) * x; }
            return z;
        }
        static byte[] Divisor(int degree)
        {
            var r = new byte[degree]; r[degree - 1] = 1; int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < r.Length; j++) { r[j] = (byte)Mul(r[j], root); if (j + 1 < r.Length) r[j] ^= r[j + 1]; }
                root = Mul(root, 0x02);
            }
            return r;
        }
        static byte[] Remainder(byte[] data, byte[] div)
        {
            var r = new byte[div.Length];
            foreach (var b in data)
            {
                int f = (b ^ r[0]) & 0xFF;
                Array.Copy(r, 1, r, 0, r.Length - 1); r[r.Length - 1] = 0;
                for (int i = 0; i < r.Length; i++) r[i] ^= (byte)Mul(div[i], f);
            }
            return r;
        }
    }
}
