// Hallow Remote - light and dark mode (Hallow family spec). The family mode is "mode" in %APPDATA%\Hallow\theme.json
// ("dark" | "light" | "system", written by Hallow Toolkit; missing = dark). This app's own setting (Look: follow / light / dark) wins.
// "system" follows Windows (HKCU ...\Themes\Personalize AppsUseLightTheme, 1 = light), polled every 3 s - read only, never written.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace HallowRemote
{
    public static class Look
    {
        public const string Follow = "follow", Light = "light", Dark = "dark";
        // the light palette (spec); dark = each app's own Theme.xaml values, unchanged
        public const string LBg = "#F6F4FA", LPanel = "#FFFFFF", LPanel2 = "#EEEAF5", LBorder = "#DAD3E6", LText = "#1E1A26", LMuted = "#6B6478", LLilac = "#7A5CC0",
            LGreen = "#1E8E4F", LAmber = "#A66A00", LRed = "#C2333F";

        public static bool IsLight;   // what the windows use now

        // "dark" | "light" | "system" from theme.json; anything else, a missing file or a broken one = "dark"
        public static string ReadFamilyMode(string themeFile)
        {
            try
            {
                if (string.IsNullOrEmpty(themeFile) || !File.Exists(themeFile)) return Dark;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(themeFile));
                object m; if (d == null || !d.TryGetValue("mode", out m)) return Dark;
                var s = ((m as string) ?? "").Trim().ToLowerInvariant();
                return s == Light || s == "system" ? s : Dark;
            }
            catch { return Dark; }
        }

        // Windows' "apps use light theme". HALLOW_SYSTEM_LIGHT=1/0 replaces it for tests and snaps (the real setting is only read).
        public static Func<bool> SystemLight = ReadSystemLight;
        public static bool ReadSystemLight()
        {
            var e = Environment.GetEnvironmentVariable("HALLOW_SYSTEM_LIGHT");
            if (e == "1") return true; if (e == "0") return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v == null ? true : Convert.ToInt32(v) == 1;   // not set = Windows' default, light
                }
            }
            catch { return false; }
        }

        // the app's own choice wins; "follow" (or anything unknown) uses the family mode
        public static bool Resolve(string appLook, string familyMode, Func<bool> systemLight)
        {
            var a = (appLook ?? "").Trim().ToLowerInvariant();
            if (a == Light) return true;
            if (a == Dark) return false;
            if (familyMode == Light) return true;
            if (familyMode == "system") return systemLight != null && systemLight();
            return false;
        }
        public static bool Resolve(string appLook, string themeFile) { return Resolve(appLook, ReadFamilyMode(themeFile), SystemLight); }
        public static string Normalize(string appLook) { var a = (appLook ?? "").Trim().ToLowerInvariant(); return a == Light || a == Dark ? a : Follow; }

        // ---- colours
        public static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        public static string H(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
        public static double Lum(Color c)
        {
            Func<byte, double> lin = v => { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
            return 0.2126 * lin(c.R) + 0.7152 * lin(c.G) + 0.0722 * lin(c.B);
        }
        public static double Contrast(string a, string b) { double x = Lum(C(a)), y = Lum(C(b)); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

        // the accent for TEXT and thin lines on light backgrounds: darkened in HSL until it reaches 4.5:1 on white and on the light
        // background (default orange -> about #B85410). Unchanged when it already does.
        public static string TextAccent(string hex)
        {
            var c = C(hex); double h, s, l; ToHsl(c, out h, out s, out l);
            var cur = hex.ToUpperInvariant();
            while ((Contrast(cur, LPanel) < 4.5 || Contrast(cur, LBg) < 4.5) && l > 0) { l = Math.Max(0, l - 0.005); cur = H(FromHsl(h, s, l)); }
            return cur;
        }
        static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            l = (mx + mn) / 2; h = s = 0; if (mx == mn) return;
            var d = mx - mn; s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
            if (mx == r) h = (g - b) / d + (g < b ? 6 : 0); else if (mx == g) h = (b - r) / d + 2; else h = (r - g) / d + 4;
            h /= 6;
        }
        static Color FromHsl(double h, double s, double l)
        {
            Func<double, double, double, double> f = (p, q, t) => { if (t < 0) t += 1; if (t > 1) t -= 1; if (t < 1 / 6.0) return p + (q - p) * 6 * t; if (t < 0.5) return q; if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6; return p; };
            double r, g, b; if (s == 0) r = g = b = l; else { var q = l < 0.5 ? l * (1 + s) : l + s - l * s; var p = 2 * l - q; r = f(p, q, h + 1 / 3.0); g = f(p, q, h); b = f(p, q, h - 1 / 3.0); }
            Func<double, byte> k = v => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
            return Color.FromRgb(k(r), k(g), k(b));
        }

        // ---- v1.17: the family accent rule (exactly as Hallow Toolkit 5.16: Get-LightTextAccent / Get-AccentFillText / Get-LightAccentFill)
        // darken(accent, min, on): HSL from System.Drawing (GetHue / GetSaturation / GetBrightness), L lowered 0.01 a step, H and S kept
        public static string Darken(string hex, double min, string on)
        {
            if (Contrast(hex, on) >= min) return hex.ToUpperInvariant();
            var a = System.Drawing.ColorTranslator.FromHtml(hex);
            double h = a.GetHue() / 360.0, s = a.GetSaturation(), l = a.GetBrightness(); var cur = hex.ToUpperInvariant();
            while (l > 0) { l = Math.Max(0.0, l - 0.01); cur = H(FamilyHsl(h, s, l)); if (Contrast(cur, on) >= min) break; }
            return cur;
        }
        static Color FamilyHsl(double h, double s, double l)
        {
            if (s == 0) { var v = (byte)(int)Math.Round(l * 255); return Color.FromRgb(v, v, v); }
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s; var p = 2 * l - q;
            Func<double, byte> f = t => { if (t < 0) t += 1; if (t > 1) t -= 1; double r; if (t < 1 / 6.0) r = p + (q - p) * 6 * t; else if (t < 0.5) r = q; else if (t < 2 / 3.0) r = p + (q - p) * (2 / 3.0 - t) * 6; else r = p; return (byte)(int)Math.Round(255 * r); };
            return Color.FromRgb(f(h + 1 / 3.0), f(h), f(h - 1 / 3.0));
        }
        // text on a filled accent: dark #1E1A26 or white, whichever reads better
        public static string FillText(string fill) { return Contrast(fill, LText) >= Contrast(fill, "#FFFFFF") ? LText : "#FFFFFF"; }
        // light mode's accent fill (buttons, on chips): the real accent, unless neither text colour reaches 4.5:1 on it (violet #8B5CF6) -
        // then the accent darkened until white text does
        public static string LightFill(string accent) { return Contrast(accent, FillText(accent)) >= 4.5 ? accent.ToUpperInvariant() : Darken(accent, 4.5, LPanel); }
        // light mode's accent as text / thin lines: readable on the darkest light surface it can sit on (Panel 2), so also on Bg and Panel
        public static string LightTextAccent(string accent) { return Darken(accent, 4.5, LPanel2); }

        // Theme.xaml text with each dark colour swapped for its light one (keys stay the same). Whole "#RRGGBB" / "#AARRGGBB" tokens only.
        public static string Swap(string xaml, IDictionary<string, string> darkToLight)
        {
            return Regex.Replace(xaml, "#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})\\b", m =>
            {
                string v; var key = m.Value.ToUpperInvariant();
                if (darkToLight.TryGetValue(key, out v)) return v;
                if (key.Length == 9 && darkToLight.TryGetValue("#" + key.Substring(3), out v)) return "#" + key.Substring(1, 2) + v.TrimStart('#');   // keep the alpha
                return m.Value;
            });
        }
        // one hard-coded colour from code, swapped the same way when light
        public static string Pick(string darkHex, IDictionary<string, string> darkToLight)
        {
            if (!IsLight) return darkHex; string v;
            return darkToLight.TryGetValue(darkHex.ToUpperInvariant(), out v) ? v : darkHex;
        }

        // ---- Windows title bar (DWMWA_USE_IMMERSIVE_DARK_MODE = 20)
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        public static void TitleBar(Window w)
        {
            try { var h = new WindowInteropHelper(w).Handle; if (h == IntPtr.Zero) return; int dark = IsLight ? 0 : 1; DwmSetWindowAttribute(h, 20, ref dark, 4); } catch { }
        }
        // sets it now (if the window has a handle) and whenever the window gets one
        public static void Track(Window w) { w.SourceInitialized += (s, e) => TitleBar(w); TitleBar(w); }
        public static void TitleBars() { try { foreach (Window w in Application.Current.Windows) TitleBar(w); } catch { } }

        // calls changed on the UI thread when Windows' light/dark setting flips (only matters while something uses "system")
        public static DispatcherTimer WatchSystem(Dispatcher ui, Action changed)
        {
            bool last = SystemLight();
            var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (s, e) => { bool now; try { now = SystemLight(); } catch { return; } if (now == last) return; last = now; try { changed(); } catch { } };
            t.Start(); return t;
        }
    }
}
