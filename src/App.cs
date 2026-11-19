// Hallow Remote - your phone as a remote for the Hallow apps: Mixer sliders and mic, Hub games, Clips, Vault, Overlay, Toolkit game session, PC stats and power.
// The PC side is this small tray app; the phone side is a web page you add to your home screen.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

[assembly: AssemblyTitle("Hallow Remote")]
[assembly: AssemblyProduct("Hallow Remote")]
[assembly: AssemblyDescription("Your phone as a remote for the Hallow apps")]
[assembly: AssemblyVersion("1.7.0.0")]
[assembly: AssemblyFileVersion("1.7.0.0")]

namespace HallowRemote
{
    public static class Program
    {
        public const string Version = "1.7";
        [STAThread]
        public static void Main(string[] args)
        {
            int ci = Array.IndexOf(args, "--cmd");   // v1.2: send a line to the running copy (Hallow family link)
            if (ci >= 0) { Environment.Exit(Family.RunCmd(ci + 1 < args.Length ? args[ci + 1] : "")); return; }
            int si = Array.IndexOf(args, "--snap");
            if (si >= 0 && si + 1 < args.Length)
            {
                Snap.On = true; RemoteSettings.Frozen = true;
                var sa = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(sa);
                new MainWindow(true).SnapAll(args[si + 1]); return;
            }
            bool created;
            var mutex = new Mutex(true, @"Local\HallowRemote" + (RemoteSettings.TestDir != null ? "Test" : ""), out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HallowRemoteShow" + (RemoteSettings.TestDir != null ? "Test" : ""));
            if (!created) { showEvent.Set(); return; }
            Crash.Install();   // v1.5: crash reports, kept on this PC
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(app);
            app.DispatcherUnhandledException += (s, e) => { Crash.Write(e.Exception); e.Handled = true; Environment.Exit(1); };   // report saved; close without Windows' error dialog
            var w = new MainWindow(args.Contains("--tray"));
            new Thread(() => { while (showEvent.WaitOne()) w.Dispatcher.BeginInvoke(new Action(w.ShowFromTray)); }) { IsBackground = true }.Start();
            app.Run();
            GC.KeepAlive(mutex);
        }
        // v1.5: the accent in use ("#RRGGBB") - the Hallow accent from Hallow Toolkit, or Remote's own orange
        public static string AccentHex = Accent.Default;
        public static void LoadTheme(Application app)
        {
            string xaml;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("HallowRemote.Theme.xaml")) using (var r = new System.IO.StreamReader(s)) xaml = r.ReadToEnd();
            AccentHex = Accent.Current(RemoteSettings.Load().UseHallowAccent);
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Accent.ApplyTo(xaml, AccentHex)));
        }
    }

    public class MainWindow : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        static SolidColorBrush B(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }

        readonly RemoteSettings S = RemoteSettings.Load();
        readonly Server server;
        readonly bool startHidden; bool exiting, toldTray, paused, hubHosts, balloonOwn;
        Image qr; TextBlock link, state, log, httpsText; TextBox httpsCmd; Border httpsNote; WF.NotifyIcon tray; StackPanel netChips;

        public MainWindow(bool hidden)
        {
            startHidden = hidden; server = new Server(S);
            Title = "Hallow Remote"; Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanMinimize;
            Background = (Brush)FindResource("Bg"); Foreground = (Brush)FindResource("Text"); FontFamily = new FontFamily("Segoe UI"); FontSize = 12.5;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            try { Icon = AppIcon(); } catch { }
            Content = Build();
            SourceInitialized += (s, e) => { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4); };
            Closing += (s, e) =>
            {
                if (!exiting && S.TrayOnClose && !Snap.On)
                {
                    e.Cancel = true; Hide();
                    if (!toldTray && tray != null) { toldTray = true; Notify("Hallow Remote", hubHosts ? "Still running, so your phone can reach your PC. It's in the Hallow icon's menu." : "Still running, so your phone can reach your PC. Right-click the icon to quit."); }
                }
                else Quit();
            };
            Crash.Secret = S.Code;
            server.Log = m => Dispatcher.BeginInvoke(new Action(() => log.Text = DateTime.Now.ToString("HH:mm") + "  From your phone: " + m));
            if (!Snap.On) { server.Start(); SetupTray(); Family.Serve("Remote", Command); }
            else { server.Address = "100.90.88.26"; }
            ShowLink();
            if (!Snap.On)
            {
                // Tailscale can connect after Windows starts: try again every 30 s until it's up
                var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                retry.Tick += (s, e) => { if (!paused && (server.Address == null || server.Error != null || Tls.Stamp() != server.TlsStamp)) { server.Start(); ShowLink(); } };   // v1.3: also picks up a new or renewed HTTPS certificate
                retry.Start();
                // v1.4: one Hallow icon - every 5 s ask Hallow Hub whether it hosts the tray menu; hide this icon while it does
                var host = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; host.Tick += (s, e) => CheckHost(); host.Start(); CheckHost();
                // v1.5: the Hallow accent, live (the folder may only appear later, when Hallow Toolkit first saves a colour)
                accentWatch = Accent.Watch(() => Dispatcher.BeginInvoke(new Action(ApplyAccent)));
                // v1.7: look for a newer version 20 s after start and then every 6 hours (at most once a day); never in a test folder unless a stand-in is set
                if (RemoteSettings.TestDir == null || Environment.GetEnvironmentVariable("HALLOW_REMOTE_API") != null)
                {
                    var upd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                    upd.Tick += (s, e) => { upd.Interval = TimeSpan.FromHours(6); DateTime last; if (!DateTime.TryParse(S.LastUpdateCheck, out last) || (DateTime.Now - last).TotalHours >= 24 || RemoteSettings.TestDir != null) CheckForUpdate(false); };
                    upd.Start();
                }
                retry.Tick += (s, e) => { if (accentWatch == null) { accentWatch = Accent.Watch(() => Dispatcher.BeginInvoke(new Action(ApplyAccent))); if (accentWatch != null) ApplyAccent(); } };
                Loaded += (s, e) => ShowCrashNotice();
                if (startHidden) Dispatcher.BeginInvoke(new Action(ShowCrashNotice), DispatcherPriority.ApplicationIdle);
                if (RemoteSettings.TestDir != null && Environment.GetEnvironmentVariable("HALLOW_REMOTE_CRASHTEST") == "1")   // developer test only
                    Dispatcher.BeginInvoke(new Action(() => { throw new InvalidOperationException("Crash test - link " + server.Link); }), DispatcherPriority.ApplicationIdle);
                if (!startHidden) Show();
            }
        }

        static ImageSource AppIcon()
        {
            var ico = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            return Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }

        UIElement Build()
        {
            var sp = new StackPanel { Margin = new Thickness(26, 16, 26, 20) };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            try { title.Children.Add(new Image { Source = AppIcon(), Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) }); } catch { }
            title.Children.Add(new TextBlock { Text = "HALLOW", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = B(Program.AccentHex), VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = " REMOTE", FontSize = 22, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center });
            // v1.7: "Update to vX" appears here when GitHub has a newer Hallow Remote
            updateBtn = new Button { Style = (Style)FindResource("Accent"), Visibility = pending != null ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (pending != null) updateBtn.Content = "Update to v" + pending.Version.ToString(2);
            updateBtn.Click += (s, e) => InstallUpdate();
            title.Children.Add(updateBtn);
            sp.Children.Add(title);
            var row = new DockPanel();
            var qrBox = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(12), Width = 214, Height = 214, Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Top };
            qr = new Image { Stretch = Stretch.Uniform }; RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
            qrBox.Child = qr; DockPanel.SetDock(qrBox, Dock.Left); row.Children.Add(qrBox);
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = "Scan this with your phone's camera", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            info.Children.Add(new TextBlock { Text = "Then use your browser's \"Add to Home screen\" so it opens like an app. You get Hallow Mixer's sliders and mic, your Hallow Hub games, Hallow Clips, Vault and Overlay, the Hallow Toolkit game session, your PC's stats and sleep / shut down / lock.",
                Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            link = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = B("#ECE8F4"), Margin = new Thickness(0, 0, 0, 10) };
            info.Children.Add(link);
            var btns = new WrapPanel();
            var copy = new Button { Content = "Copy link", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 8) };
            copy.Click += (s, e) => { if (server.Link != null) { Clipboard.SetText(server.Link); state.Text = "Link copied."; } };
            var open = new Button { Content = "Open on this PC", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 8) };
            open.Click += (s, e) => { if (server.Link != null) try { System.Diagnostics.Process.Start(server.Link); } catch { } };
            var renew = new Button { Content = "New link", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 8), ToolTip = "Makes a new secret link - the old one stops working (scan the new code)" };
            renew.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Make a new link? The old one stops working right away, so you'll scan the new code on your phone.", "Hallow Remote", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                S.Code = RemoteSettings.NewCode(); S.Save(); server.Start(); ShowLink(); state.Text = "New link made - the old one no longer works.";
            };
            var upd = new Button { Content = "Check for updates", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 8) }; upd.Click += (s, e) => CheckForUpdate(true);
            btns.Children.Add(copy); btns.Children.Add(open); btns.Children.Add(renew); btns.Children.Add(upd);
            info.Children.Add(btns);
            row.Children.Add(info);
            sp.Children.Add(row);
            state = new TextBlock { Margin = new Thickness(0, 14, 0, 4), TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(state);
            // network
            var nrow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            nrow.Children.Add(new TextBlock { Text = "Reach it", Width = 90, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); DockPanel.SetDock(nrow.Children[0], Dock.Left);
            netChips = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var o in new[] { Tuple.Create("tailscale", "Anywhere (Tailscale)"), Tuple.Create("lan", "Home Wi-Fi only") })
            {
                var key = o.Item1; var c = new ToggleButton { Content = o.Item2, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 0), IsChecked = S.Network == key, Padding = new Thickness(12, 5, 12, 5) };
                c.Click += (s, e) => { S.Network = key; S.Save(); foreach (ToggleButton x in netChips.Children) x.IsChecked = x == c; server.Start(); ShowLink(); };
                netChips.Children.Add(c);
            }
            nrow.Children.Add(netChips); sp.Children.Add(nrow);
            var more = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            Action<string, bool, Action<bool>> sw = (text, on, set) =>
            {
                var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = on, Style = (Style)FindResource("Switch") };
                c.Click += (s, e) => { set(c.IsChecked == true); S.Save(); }; more.Children.Add(c);
            };
            sw("Keep running in the tray when I close this window", S.TrayOnClose, v => S.TrayOnClose = v);
            sw("Start with Windows (in the tray)", StartsWithWindows(), v => SetStartWithWindows(v));
            sw("Use the Hallow icon (when Hallow Hub runs, Hallow Remote lives in its tray menu instead of having its own icon)", S.UseHallowIcon, v => { S.UseHallowIcon = v; CheckHost(); });
            sw("Use the Hallow accent (the colour picked in Hallow Toolkit, here and on your phone)", S.UseHallowAccent, v => { S.UseHallowAccent = v; S.Save(); Dispatcher.BeginInvoke(new Action(ApplyAccent)); });
            // v1.7: trackpad and keyboard from the phone - off by default, with a clear warning
            sw("Let your phone use the mouse and keyboard (trackpad and typing on the Mouse page)", S.AllowInput, v => { S.AllowInput = v; S.Save(); server.Changed(); inputWarn.Visibility = v ? Visibility.Visible : Visibility.Collapsed; });
            inputWarn = new TextBlock { Text = "Anyone with your link can now move the mouse and type on this PC - like sitting at it. Turn it off when you don't need it; Pause the remote also stops it.",
                Foreground = B("#F0BE5A"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(50, -2, 0, 6), Visibility = S.AllowInput ? Visibility.Visible : Visibility.Collapsed };
            more.Children.Add(inputWarn);
            sp.Children.Add(more);
            log = new TextBlock { Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 10, 0, 0) };
            sp.Children.Add(log);
            // v1.3: how to turn on HTTPS (shown in Tailscale mode until a certificate is in place)
            var hn = new StackPanel();
            hn.Children.Add(new TextBlock { Text = "Turn on HTTPS (optional)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            httpsText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 8) };
            hn.Children.Add(httpsText);
            httpsCmd = new TextBox { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Background = B("#1A1522"), Foreground = B("#ECE8F4"), BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 8) };
            hn.Children.Add(httpsCmd);
            var copyCmd = new Button { Content = "Copy command", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Left };
            copyCmd.Click += (s, e) => { try { Clipboard.SetText(httpsCmd.Text); state.Text = "Command copied - paste it into PowerShell after turning on HTTPS in Tailscale."; } catch { } };
            hn.Children.Add(copyCmd);
            httpsNote = new Border { Child = hn, Background = B("#211B2A"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 12, 0, 0) };
            sp.Children.Add(httpsNote);            sp.Children.Add(new TextBlock { Text = "Only someone with the link can use it - keep it to yourself, and press New link if it ever gets out. It works only on your home network or through your own Tailscale. " +
                "The first time, Windows may ask whether Hallow Remote can use the network: allow it for private networks.",
                Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
            sp.Children.Add(new TextBlock { Text = "Hallow Remote " + Program.Version + "  -  icons: Font Awesome Free (CC BY 4.0)", Foreground = (Brush)FindResource("Muted"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
            return sp;
        }

        void ShowLink()
        {
            var l = server.Link;
            link.Text = l ?? "-";
            try { qr.Source = l == null ? null : QrImage(l); } catch { qr.Source = null; }   // a link too long for a QR code: copy it instead
            state.Text = server.Error ?? (S.Network == "lan" ? "Ready on your home network. Your phone has to be on the same Wi-Fi." : "Ready through Tailscale - works wherever your phone has Tailscale on.");
            state.Foreground = server.Error != null ? B("#F0BE5A") : B("#6ED28C");
            if (server.Error == null && server.Https) state.Text = "Ready through Tailscale, with HTTPS (certificate good until " + server.CertUntil.ToString("d MMM yyyy") + ").";
            if (paused) { state.Text = "Paused - your phone can't reach this PC until you resume it (tray menu)."; state.Foreground = B("#F0BE5A"); }
            ShowHttpsNote(Tls.TailscaleName(), server.TlsProblem);
        }
        void ShowHttpsNote(string name, string problem)
        {
            httpsNote.Visibility = S.Network == "tailscale" && !server.Https ? Visibility.Visible : Visibility.Collapsed;
            var n = name ?? "<this-pc>.<your-tailnet>.ts.net";
            httpsText.Text = (problem != null ? problem + "\n\n" : "") +
                "With HTTPS your phone treats Hallow Remote as a real app. 1) In the Tailscale admin console, open DNS and turn on MagicDNS and HTTPS Certificates. " +
                "2) Run this in PowerShell. Hallow Remote switches to an https link by itself within a minute (scan the new code; the old link forwards to it). " +
                "The certificate lasts 90 days - run the command again to renew it.";
            httpsCmd.Text = "& \"$env:ProgramFiles\\Tailscale\\tailscale.exe\" cert --cert-file \"$env:APPDATA\\HallowRemote\\tls\\" + n + ".crt\" --key-file \"$env:APPDATA\\HallowRemote\\tls\\" + n + ".key\" " + n;
        }
        static ImageSource QrImage(string text)
        {
            var m = Qr.Encode(text); int n = m.GetLength(0), quiet = 2;
            var g = new GeometryGroup();
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (m[y, x]) g.Children.Add(new RectangleGeometry(new Rect(x + quiet, y + quiet, 1, 1)));
            var dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, n + 2 * quiet, n + 2 * quiet))));
            dg.Children.Add(new GeometryDrawing(Brushes.Black, null, g));
            var di = new DrawingImage(dg); di.Freeze(); return di;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static bool StartsWithWindows() { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("HallowRemote") != null; }
        static void SetStartWithWindows(bool on)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) { if (on) k.SetValue("HallowRemote", "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray"); else k.DeleteValue("HallowRemote", false); } } catch { }
        }
        void SetupTray()
        {
            tray = new WF.NotifyIcon { Text = "Hallow Remote", Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new WF.ContextMenuStrip();
            var open = new WF.ToolStripMenuItem("Show the phone code"); open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold); open.Click += (s, e) => ShowFromTray(); menu.Items.Add(open);
            var copy = new WF.ToolStripMenuItem("Copy link"); copy.Click += (s, e) => Invoke("copy"); menu.Items.Add(copy);
            var pause = new WF.ToolStripMenuItem("Pause the remote"); pause.Click += (s, e) => Invoke("pause"); menu.Items.Add(pause);
            menu.Opening += (s, e) => { pause.Checked = paused; copy.Enabled = server.Link != null && !paused; };
            menu.Items.Add(new WF.ToolStripSeparator());
            var quit = new WF.ToolStripMenuItem("Quit"); quit.Click += (s, e) => Quit(); menu.Items.Add(quit);
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowFromTray(); };
        }
        public void ShowFromTray() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
        // ---- v1.7: updates from GitHub (asks first; the download is checked against GitHub's SHA-256)
        Button updateBtn; TextBlock inputWarn; GhRelease pending;
        void CheckForUpdate(bool manual)
        {
            if (manual) { state.Text = "Checking GitHub for a newer Hallow Remote..."; state.Foreground = (Brush)FindResource("Muted"); }
            new Thread(() =>
            {
                var r = Updater.Latest();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    S.LastUpdateCheck = DateTime.Now.ToString("o"); S.Save();
                    if (r == null) { if (manual) { state.Text = "Couldn't reach GitHub - try again later."; state.Foreground = B("#F0BE5A"); } return; }
                    if (!Updater.IsNewer(r)) { if (manual) { state.Text = "You have the newest version (v" + Program.Version + ")."; state.Foreground = B("#6ED28C"); } return; }
                    pending = r; updateBtn.Content = "Update to v" + r.Version.ToString(2); updateBtn.Visibility = Visibility.Visible;
                    updateBtn.ToolTip = (r.Notes ?? "").Length > 600 ? r.Notes.Substring(0, 600) + "..." : r.Notes;
                    state.Text = "Hallow Remote v" + r.Version.ToString(2) + " is out - click Update at the top (your phone link stays the same)."; state.Foreground = B("#6ED28C");
                    if (!IsVisible) Notify("Hallow Remote", "Version " + r.Version.ToString(2) + " is out - open Hallow Remote to update.");
                }));
            }) { IsBackground = true }.Start();
        }
        void InstallUpdate()
        {
            if (pending == null) return;
            if (MessageBox.Show(this, "Install Hallow Remote v" + pending.Version.ToString(2) + " now?\n\nIt downloads from GitHub, checks the file, and reopens by itself. Your phone link and settings stay the same.", "Update Hallow Remote", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            updateBtn.IsEnabled = false; updateBtn.Content = "Updating...";
            var r = pending;
            new Thread(() =>
            {
                var err = Updater.Install(r);
                Dispatcher.BeginInvoke(new Action(() => { if (err != null) { state.Text = err; state.Foreground = B("#F0BE5A"); updateBtn.IsEnabled = true; updateBtn.Content = "Update to v" + r.Version.ToString(2); } else state.Text = "Installing the update - Hallow Remote reopens in a few seconds."; }));
            }) { IsBackground = true }.Start();
        }

        // ---- v1.5: the Hallow accent changed (or the setting did): reload the theme and rebuild the window
        System.IO.FileSystemWatcher accentWatch;
        void ApplyAccent()
        {
            var a = Accent.Current(S.UseHallowAccent); if (a == Program.AccentHex) return;
            Program.LoadTheme(Application.Current);
            Content = Build(); ShowLink(); server.Changed();
        }
        // ---- v1.5: "Hallow Remote closed unexpectedly last time" - once per crash report
        bool crashShown;
        void ShowCrashNotice()
        {
            if (crashShown) return; crashShown = true;
            var f = Crash.Last(); if (f == null) return;
            var name = System.IO.Path.GetFileName(f); if (name == S.LastCrashShown) return;
            S.LastCrashShown = name; S.Save();
            CrashWindow(f).Show();
        }
        Window CrashWindow(string file)
        {
            var w = new Window { Title = "Hallow Remote", Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = (Brush)FindResource("Bg"), Foreground = (Brush)FindResource("Text"), FontFamily = new FontFamily("Segoe UI"), FontSize = 12.5, Topmost = true };
            try { w.Icon = AppIcon(); } catch { }
            w.SourceInitialized += (s, e) => { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref dark, 4); };
            var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Hallow Remote closed unexpectedly last time", FontSize = 15, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            DateTime when = DateTime.Now; try { when = System.IO.File.GetLastWriteTime(file); } catch { }
            sp.Children.Add(new TextBlock { Text = "On " + when.ToString("d MMM yyyy, HH:mm") + ". A report was saved on this PC - nothing was sent anywhere.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            var row = new WrapPanel();
            var details = new Button { Content = "Show details", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0) };
            details.Click += (s, e) => { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + file + "\""); } catch { } };
            var folder = new Button { Content = "Open folder", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0) };
            folder.Click += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { } };
            var ok = new Button { Content = "OK", Style = (Style)FindResource("Accent"), MinWidth = 70 }; ok.Click += (s, e) => w.Close();
            row.Children.Add(details); row.Children.Add(folder); row.Children.Add(ok);
            sp.Children.Add(row); w.Content = sp;
            return w;
        }

        void Quit() { exiting = true; server.Stop(); if (tray != null) { tray.Visible = false; tray.Dispose(); } Application.Current.Shutdown(); }

        // ---- v1.4: one Hallow icon (Hallow Hub hosts the tray menu). The link and its code never go over the pipe:
        // "Copy link" runs here and puts the link on this PC's clipboard.
        string Command(string line)
        {
            int sp = line.IndexOf(' '); var cmd = sp < 0 ? line : line.Substring(0, sp); var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            switch (cmd)
            {
                case "open": return OnUi(() => { ShowFromTray(); return "ok"; });
                case "menu": return OnUi(() => "ok " + Family.Json(new Dictionary<string, object> { { "app", "Remote" }, { "items", MenuItems() } }));
                case "invoke": return OnUi(() => Invoke(arg));
                case "quit":
                    new Thread(() => { Thread.Sleep(300); Dispatcher.BeginInvoke(new Action(Quit)); }) { IsBackground = true }.Start();   // after the reply is sent
                    return "ok";
                default: return server.Command(line);
            }
        }
        string OnUi(Func<string> f)
        {
            string r = "error no answer in time";
            var op = Dispatcher.BeginInvoke(new Action(() => { try { r = f(); } catch (Exception e) { r = "error " + Family.OneLine(e.Message); } }));
            return op.Wait(TimeSpan.FromSeconds(5)) == DispatcherOperationStatus.Completed ? r : "error no answer in time";
        }
        List<object> MenuItems()
        {
            return new List<object> {
                new Dictionary<string, object> { { "id", "show" }, { "label", "Show the phone code" } },
                new Dictionary<string, object> { { "id", "copy" }, { "label", "Copy link" }, { "enabled", server.Link != null && !paused } },
                new Dictionary<string, object> { { "id", "-" } },
                new Dictionary<string, object> { { "id", "pause" }, { "label", "Pause the remote" }, { "checked", paused } } };
        }
        string Invoke(string id)
        {
            switch (id)
            {
                case "show": ShowFromTray(); return "ok";
                case "copy":
                    if (server.Link == null || paused) return "error No link right now";
                    try { Clipboard.SetText(server.Link); } catch { return "error Couldn't use the clipboard"; }
                    state.Text = "Link copied."; return "ok";
                case "pause":
                    paused = !paused; server.Paused = paused;
                    if (paused) server.Stop(); else server.Start();
                    server.WriteStatus(); ShowLink();
                    if (log != null) log.Text = DateTime.Now.ToString("HH:mm") + (paused ? "  Remote paused" : "  Remote on again");
                    return "ok";
            }
            return "error unknown item";
        }
        // asks Hub (in the background) whether it hosts the tray menu; shows this icon again if Hub is gone, says no or doesn't answer
        void CheckHost()
        {
            if (tray == null) return;
            if (!S.UseHallowIcon) { SetHosted(false); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool on = false;
                try
                {
                    var r = Family.Send("Hub", "tray-host", 1500);
                    if (r != null && r.StartsWith("ok {")) { var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(r.Substring(3)); object o; on = d.TryGetValue("on", out o) && o is bool && (bool)o; }
                }
                catch { }
                Dispatcher.BeginInvoke(new Action(() => SetHosted(on && S.UseHallowIcon)));
            });
        }
        void SetHosted(bool on)
        {
            hubHosts = on;
            if (tray != null && !balloonOwn) tray.Visible = !on;
            if (server.OwnIcon != (tray != null && tray.Visible)) { server.OwnIcon = tray != null && tray.Visible; server.WriteStatus(); }
        }
        // a balloon: through Hub's icon while this one is hidden (falls back to this icon for a moment if Hub fails)
        void Notify(string title, string text)
        {
            if (tray == null) return;
            if (!hubHosts) { tray.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.None); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var r = Family.Send("Hub", "notify Remote\t" + title + "\t" + text, 2000);
                if (Family.Error(r, "no answer") == null) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    balloonOwn = true; tray.Visible = true; tray.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.None);
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                    t.Tick += (s, e) => { t.Stop(); balloonOwn = false; SetHosted(hubHosts); };
                    t.Start();
                }));
            });
        }

        public void SnapAll(string dir)
        {
            // never show the real secret in a picture
            link.Text = "http://100.90.88.26:5480/example-code/"; qr.Source = QrImage(link.Text);
            state.Text = "Ready through Tailscale - works wherever your phone has Tailscale on."; state.Foreground = B("#6ED28C");
            ShowHttpsNote("gaming-pc.tail1234.ts.net", null);
            Snap.Save(this, System.IO.Path.Combine(dir, "main.png"), 640, 0);
            Snap.Save(CrashWindow(System.IO.Path.Combine(dir, "example.txt")), System.IO.Path.Combine(dir, "crash.png"), 440, 0);
        }
    }
}
