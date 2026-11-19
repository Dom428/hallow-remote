// Hallow Remote v1.7 - the phone as a trackpad and keyboard (SendInput). Off by default; only while "Let your phone use the
// mouse and keyboard" is on in the PC window and the remote isn't paused. In a test folder (or with HALLOW_REMOTE_DRYRUN=1)
// nothing reaches Windows: every event is written to input-test.log instead.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HallowRemote
{
    public static class Input
    {
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

        const uint MOVE = 0x1, LDOWN = 0x2, LUP = 0x4, RDOWN = 0x8, RUP = 0x10, MDOWN = 0x20, MUP = 0x40, WHEEL = 0x800, HWHEEL = 0x1000;
        const uint KEYUP = 0x2, UNICODE = 0x4, EXTENDED = 0x1;

        public static bool Test { get { return RemoteSettings.TestDir != null || Environment.GetEnvironmentVariable("HALLOW_REMOTE_DRYRUN") == "1"; } }
        public static string TestLog { get { return Path.Combine(RemoteSettings.Dir, "input-test.log"); } }
        static readonly object gate = new object();
        static void Send(string what, params INPUT[] inputs)
        {
            if (Test) { lock (gate) File.AppendAllText(TestLog, what + "\n", Encoding.UTF8); return; }   // test sink: never real input
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
        static INPUT M(uint flags, int dx, int dy, int data) { return new INPUT { type = 0, u = new UNION { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = (uint)data, dwFlags = flags } } }; }
        static INPUT K(ushort vk, ushort scan, uint flags) { return new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } }; }

        public static void Move(int dx, int dy) { dx = Math.Max(-500, Math.Min(500, dx)); dy = Math.Max(-500, Math.Min(500, dy)); if (dx != 0 || dy != 0) Send("move " + dx + " " + dy, M(MOVE, dx, dy, 0)); }
        public static bool Button(string b, string state)   // state: click, down, up
        {
            uint down, up;
            if (b == "left") { down = LDOWN; up = LUP; } else if (b == "right") { down = RDOWN; up = RUP; } else if (b == "middle") { down = MDOWN; up = MUP; } else return false;
            if (state == "down") Send(b + " down", M(down, 0, 0, 0));
            else if (state == "up") Send(b + " up", M(up, 0, 0, 0));
            else if (state == "double") Send(b + " double", M(down, 0, 0, 0), M(up, 0, 0, 0), M(down, 0, 0, 0), M(up, 0, 0, 0));
            else Send(b + " click", M(down, 0, 0, 0), M(up, 0, 0, 0));
            return true;
        }
        public static void Scroll(int dy, int dx)
        {
            dy = Math.Max(-20, Math.Min(20, dy)); dx = Math.Max(-20, Math.Min(20, dx));
            if (dy != 0) Send("scroll " + dy, M(WHEEL, 0, 0, dy * 120));
            if (dx != 0) Send("hscroll " + dx, M(HWHEEL, 0, 0, dx * 120));
        }
        // text as Unicode key presses (works for any language and emoji; no shortcuts can be made this way)
        public static void Text(string t)
        {
            var list = new List<INPUT>();
            foreach (var ch in t) { if (ch == '\r') continue; if (ch == '\n') { list.Add(K(0x0D, 0, 0)); list.Add(K(0x0D, 0, KEYUP)); continue; } list.Add(K(0, ch, UNICODE)); list.Add(K(0, ch, UNICODE | KEYUP)); }
            if (list.Count > 0) Send("text " + t.Replace("\n", "\\n"), list.ToArray());
        }
        // a fixed set of keys (no Windows key, no Ctrl/Alt combinations)
        public static readonly Dictionary<string, ushort> Keys = new Dictionary<string, ushort> {
            { "enter", 0x0D }, { "backspace", 0x08 }, { "tab", 0x09 }, { "esc", 0x1B }, { "space", 0x20 }, { "delete", 0x2E },
            { "left", 0x25 }, { "up", 0x26 }, { "right", 0x27 }, { "down", 0x28 }, { "home", 0x24 }, { "end", 0x23 }, { "pgup", 0x21 }, { "pgdn", 0x22 }, { "f5", 0x74 }, { "f11", 0x7A },
            // v1.8: media keys (also used without the trackpad switch)
            { "playpause", 0xB3 }, { "next", 0xB0 }, { "prev", 0xB1 }, { "stop", 0xB2 }, { "mute", 0xAD }, { "volup", 0xAF }, { "voldown", 0xAE } };
        static readonly HashSet<ushort> Ext = new HashSet<ushort> { 0x2E, 0x25, 0x26, 0x27, 0x28, 0x24, 0x23, 0x21, 0x22, 0xB3, 0xB0, 0xB1, 0xB2, 0xAD, 0xAF, 0xAE };
        public static bool Key(string name)
        {
            ushort vk; if (name == null || !Keys.TryGetValue(name, out vk)) return false;
            var f = Ext.Contains(vk) ? EXTENDED : 0;
            Send("key " + name, K(vk, 0, f), K(vk, 0, f | KEYUP));
            return true;
        }
    }
}
