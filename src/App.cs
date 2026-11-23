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
[assembly: AssemblyVersion("1.20.0.0")]
[assembly: AssemblyFileVersion("1.20.0.0")]

namespace HallowRemote
{
    public static class Program
    {
        public const string Version = "1.20";
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
        // v1.16: the accent as text / thin lines - darkened in light mode so it reads on the light background
        public static string AccentText { get { return Look.IsLight ? Look.LightTextAccent(AccentHex) : AccentHex; } }   // v1.17: the family rule (readable on Panel 2 too)
        // v1.16: light mode - each dark colour of Theme.xaml (and the few in code) and its light one; the keys stay the same
        public static readonly Dictionary<string, string> LightMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "#16121C", Look.LBg }, { "#211B2A", Look.LPanel }, { "#2E2639", Look.LPanel2 }, { "#3A3248", Look.LBorder }, { "#ECE8F4", Look.LText }, { "#8F88A0", Look.LMuted },
            { "#C8414B", Look.LRed }, { "#E05A64", "#A82A35" }, { "#2A2333", Look.LPanel2 }, { "#55496A", "#B9AED0" }, { "#FEFEFE", "#1E1A26" }, { "#58566A", "#7F7890" },
            { "#CDCAD6", "#FFFFFF" }, { "#6ED28C", Look.LGreen }, { "#241E2E", Look.LPanel }, { "#1A1522", Look.LPanel }, { "#A98BE0", Look.LLilac }, { "#F0BE5A", Look.LAmber } };
        public static void LoadTheme(Application app)
        {
            string xaml;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("HallowRemote.Theme.xaml")) using (var r = new System.IO.StreamReader(s)) xaml = r.ReadToEnd();
            var set = RemoteSettings.Load();
            AccentHex = Accent.Current(set.UseHallowAccent);
            Look.IsLight = Look.Resolve(set.Look, Accent.FilePath);
            xaml = xaml.Replace("#F07821", AccentText);
            // v1.17: light fills (buttons, on chips) follow the family rule - violet gets a darker fill so its white text reads
            if (Look.IsLight) { var fill = Look.LightFill(AccentHex); xaml = Accent.ApplyTo(xaml, fill, Look.FillText(fill), true); } else xaml = Accent.ApplyTo(xaml, AccentHex);
            if (Look.IsLight) xaml = Look.Swap(xaml, LightMap);
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(xaml));
        }
    }

    public class MainWindow : Window
    {
        // v1.16: hard-coded colours follow light mode too (Program.LightMap)
        // status green / amber as small text are darkened like the accent in light (the family green / amber are under 4.5:1 on white)
        static SolidColorBrush B(string hex)
        {
            var h = Look.Pick(hex, Program.LightMap);
            if (Look.IsLight && (hex == "#6ED28C" || hex == "#F0BE5A")) h = Look.TextAccent(h);
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
        }

        readonly RemoteSettings S = RemoteSettings.Load();
        readonly Server server;
        readonly bool startHidden; bool exiting, toldTray, paused, hubHosts, balloonOwn;
        Image qr; TextBlock link, state, log, httpsText; TextBox httpsCmd; Border httpsNote; WF.NotifyIcon tray; StackPanel netChips;

        public MainWindow(bool hidden)
        {
            startHidden = hidden; server = new Server(S);
            Title = "Hallow Remote"; Width = Math.Max(520, Math.Min(1600, S.WinW)); Height = Math.Max(420, Math.Min(1400, S.WinH)); MinWidth = 520; MinHeight = 420; ResizeMode = ResizeMode.CanResize;   // v1.19: compact, resizable, remembered
            Background = (Brush)FindResource("Bg"); Foreground = (Brush)FindResource("Text"); FontFamily = new FontFamily("Segoe UI"); FontSize = 12.5;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            try { Icon = AppIcon(); } catch { }
            Content = Build();
            Look.Track(this);   // v1.16: the title bar matches light / dark
            var sizeSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; sizeSave.Tick += (s, e) => { sizeSave.Stop(); if (WindowState == WindowState.Normal && !Snap.On) { S.WinW = Math.Round(ActualWidth); S.WinH = Math.Round(ActualHeight); S.Save(); } };
            SizeChanged += (s, e) => { if (IsVisible) { sizeSave.Stop(); sizeSave.Start(); } };   // v1.19: the size you give it is remembered
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
            // v1.9: the phone's text goes onto the clipboard on this window's thread; each screen peek is announced on the PC
            server.SetClipboard = text =>
            {
                if (RemoteSettings.TestDir != null) { try { System.IO.File.WriteAllText(System.IO.Path.Combine(RemoteSettings.Dir, "clipboard-test.txt"), text); return true; } catch { return false; } }   // tests never touch the real clipboard
                bool ok = false;
                try { Dispatcher.Invoke(new Action(() => { for (int i = 0; i < 5 && !ok; i++) { try { Clipboard.SetText(text); ok = true; } catch { Thread.Sleep(60); } } }), TimeSpan.FromSeconds(3)); } catch { }
                return ok;
            };
            server.Peeked = () => Dispatcher.BeginInvoke(new Action(() => { log.Text = DateTime.Now.ToString("HH:mm") + "  Your phone peeked at the screen"; Notify("Hallow Remote", "Your phone just took a screen peek."); }));
            server.Log = m => Dispatcher.BeginInvoke(new Action(() => log.Text = DateTime.Now.ToString("HH:mm") + "  From your phone: " + m));
            // v1.13: an address that tried many different wrong links is refused for 15 minutes (the tried links are never shown or kept)
            server.Blocked = ip => Dispatcher.BeginInvoke(new Action(() => log.Text = DateTime.Now.ToString("HH:mm") + "  Blocked " + ip + " for 15 minutes - it tried many wrong links"));
            // v1.11: each file from the phone is announced (log line and a balloon - through the Hallow icon when Hub hosts it)
            server.Uploaded = (name, size) => Dispatcher.BeginInvoke(new Action(() =>
            {
                log.Text = DateTime.Now.ToString("HH:mm") + "  Your phone sent " + name + " (" + Files.Size(size) + ")";
                Notify("Hallow Remote", "Your phone sent " + name + " (" + Files.Size(size) + ") - it's in Downloads\\Hallow Remote.");
            }));
            // v1.11: drop files on this window to put them on the "Send to phone" list
            AllowDrop = true;
            DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Drop += (s, e) => { var files = e.Data.GetData(DataFormats.FileDrop) as string[]; if (files != null) AddOutgoing(files); };
            if (!Snap.On) { server.Start(); SetupTray(); Family.Serve("Remote", Command); }
            else { server.Address = "100.90.88.26"; }
            ShowLink();
            if (!Snap.On)
            {
                // Tailscale can connect after Windows starts: try again every 30 s until it's up
                var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                retry.Tick += (s, e) => { if (!paused && (server.Address == null || server.Error != null || Tls.Stamp() != server.TlsStamp)) { server.Start(); ShowLink(); } };   // v1.3: also picks up a new or renewed HTTPS certificate
                retry.Tick += (s, e) => RefreshOutbox();   // v1.11: "Send to phone" entries drop out after 24 hours
                retry.Start();
                // v1.4: one Hallow icon - every 5 s ask Hallow Hub whether it hosts the tray menu; hide this icon while it does
                var host = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; host.Tick += (s, e) => CheckHost(); host.Start(); CheckHost();
                // v1.5: the Hallow accent, live (the folder may only appear later, when Hallow Toolkit first saves a colour)
                accentWatch = Accent.Watch(() => Dispatcher.BeginInvoke(new Action(ApplyAccent)));
                Look.WatchSystem(Dispatcher, ApplyAccent);   // v1.16: "Same as Windows" follows the Windows light / dark setting live
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

        // v1.19: a compact window - the essentials on top (code, link, on / off, your phone), everything else in sections that open
        // when you click them (Phone permissions, Apps, Files, Advanced, Help). Fits a 1366x768 screen; the size you give it is remembered.
        CheckBox onSwitch; TextBlock phoneLine;
        StackPanel Section(StackPanel parent, string title, string summary, bool open)
        {
            var body = new StackPanel { Margin = new Thickness(14, 2, 0, 6), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
            var head = new Button { Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 6, 0, 0) };
            var ht = new TextBlock(); var arrow = new System.Windows.Documents.Run(open ? "▾  " : "▸  ");
            ht.Inlines.Add(arrow); ht.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold });
            if (summary != null) ht.Inlines.Add(new System.Windows.Documents.Run("   " + summary) { Foreground = (Brush)FindResource("Muted"), FontSize = 11.5 });
            head.Content = ht; head.SizeChanged += (s, e) => ht.Width = Math.Max(0, head.ActualWidth - 24);   // left-aligned, whatever the button template does
            head.Click += (s, e) => { var on = body.Visibility != Visibility.Visible; body.Visibility = on ? Visibility.Visible : Visibility.Collapsed; arrow.Text = on ? "▾  " : "▸  "; };
            parent.Children.Add(head); parent.Children.Add(body);
            return body;
        }
        TextBlock Muted(string text, double size = 11.5) { return new TextBlock { Text = text, Foreground = (Brush)FindResource("Muted"), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) }; }

        UIElement Build()
        {
            var sp = new StackPanel { Margin = new Thickness(20, 12, 20, 16) };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            try { title.Children.Add(new Image { Source = AppIcon(), Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0) }); } catch { }
            title.Children.Add(new TextBlock { Text = "HALLOW", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = B(Program.AccentText), VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = " REMOTE", FontSize = 19, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center });
            // v1.7: "Update to vX" appears here when GitHub has a newer Hallow Remote
            updateBtn = new Button { Style = (Style)FindResource("Accent"), Visibility = pending != null ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (pending != null) updateBtn.Content = "Update to v" + pending.Version.ToString(2);
            updateBtn.Click += (s, e) => InstallUpdate();
            title.Children.Add(updateBtn);
            sp.Children.Add(title);
            // ---- the essentials: the code, the link, on / off, your phone
            var row = new DockPanel();
            var qrBox = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(8), Width = 168, Height = 168, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
            qr = new Image { Stretch = Stretch.Uniform }; RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
            qrBox.Child = qr; DockPanel.SetDock(qrBox, Dock.Left); row.Children.Add(qrBox);
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = "Scan with your phone's camera", FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
            info.Children.Add(Muted("Then \"Add to Home screen\" so it opens like an app.", 12));
            link = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Text"), Margin = new Thickness(0, 4, 0, 8) };
            info.Children.Add(link);
            var btns = new WrapPanel();
            var copy = new Button { Content = "Copy link", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 6, 6) };
            copy.Click += (s, e) => { if (server.Link != null) { Clipboard.SetText(server.Link); state.Text = "Link copied."; } };
            var open = new Button { Content = "Open on this PC", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 6, 6) };
            open.Click += (s, e) => { if (server.Link != null) try { System.Diagnostics.Process.Start(server.Link); } catch { } };
            var renew = new Button { Content = "New link", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 6, 6), ToolTip = "Makes a new secret link - the old one stops working (scan the new code)" };
            renew.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Make a new link? The old one stops working right away, so you'll scan the new code on your phone.", "Hallow Remote", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                S.Code = RemoteSettings.NewCode(); S.Save(); server.Start(); ShowLink(); state.Text = "New link made - the old one no longer works.";
            };
            btns.Children.Add(copy); btns.Children.Add(open); btns.Children.Add(renew);
            info.Children.Add(btns);
            onSwitch = new CheckBox { Content = new TextBlock { Text = "Remote on (your phone can reach this PC)" }, IsChecked = !paused, Style = (Style)FindResource("Switch") };
            onSwitch.Click += (s, e) => { if ((onSwitch.IsChecked == true) == paused) Invoke("pause"); onSwitch.IsChecked = !paused; };
            info.Children.Add(onSwitch);
            phoneLine = new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 12 };
            info.Children.Add(phoneLine);
            row.Children.Add(info);
            sp.Children.Add(row);
            state = new TextBlock { Margin = new Thickness(0, 10, 0, 2), TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(state);
            log = new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(log);
            // network, in one line
            var nrow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            nrow.Children.Add(new TextBlock { Text = "Reach it", Width = 70, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); DockPanel.SetDock(nrow.Children[0], Dock.Left);
            netChips = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var o in new[] { Tuple.Create("tailscale", "Anywhere (Tailscale)"), Tuple.Create("lan", "Home Wi-Fi only") })
            {
                var key = o.Item1; var c = new ToggleButton { Content = o.Item2, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 0), IsChecked = S.Network == key, Padding = new Thickness(10, 4, 10, 4) };
                c.Click += (s, e) => { S.Network = key; S.Save(); foreach (ToggleButton x in netChips.Children) x.IsChecked = x == c; server.Start(); ShowLink(); };
                netChips.Children.Add(c);
            }
            nrow.Children.Add(netChips); sp.Children.Add(nrow);
            // v1.3: HTTPS - one line, the steps behind "How"
            var hn = new StackPanel();
            var hHead = new DockPanel();
            var how = new Button { Content = "How", Style = (Style)FindResource("Btn"), Padding = new Thickness(10, 2, 10, 2) }; DockPanel.SetDock(how, Dock.Right); hHead.Children.Add(how);
            hHead.Children.Add(new TextBlock { Text = "HTTPS is off - needed for Keys and Authenticator on the phone.", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            hn.Children.Add(hHead);
            var steps = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            httpsText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 6) };
            steps.Children.Add(httpsText);
            httpsCmd = new TextBox { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Background = Look.IsLight ? B(Look.LPanel2) : B("#1A1522"), Foreground = B("#ECE8F4"), BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6) };
            steps.Children.Add(httpsCmd);
            var copyCmd = new Button { Content = "Copy command", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Left };
            copyCmd.Click += (s, e) => { try { Clipboard.SetText(httpsCmd.Text); state.Text = "Command copied - paste it into PowerShell after turning on HTTPS in Tailscale."; } catch { } };
            steps.Children.Add(copyCmd); hn.Children.Add(steps);
            how.Click += (s, e) => steps.Visibility = steps.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            httpsNote = new Border { Child = hn, Background = B("#211B2A"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 10, 0, 4) };
            sp.Children.Add(httpsNote);
            // ---- Phone permissions (all off by default)
            int allowed = (S.AllowInput ? 1 : 0) + (S.AllowClipboard ? 1 : 0) + (S.AllowPeek ? 1 : 0) + (S.AllowUpload ? 1 : 0);
            var perm = Section(sp, "Phone permissions", allowed == 0 ? "all off" : allowed + " on", false);
            Action<StackPanel, string, bool, Action<bool>> sw = (into, text, on, set) =>
            {
                var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = on, Style = (Style)FindResource("Switch") };
                c.Click += (s, e) => { set(c.IsChecked == true); S.Save(); }; into.Children.Add(c);
            };
            // v1.7: trackpad and keyboard from the phone - off by default, with a clear warning
            sw(perm, "Mouse and keyboard (the Mouse page)", S.AllowInput, v => { S.AllowInput = v; S.Save(); server.Changed(); inputWarn.Visibility = v ? Visibility.Visible : Visibility.Collapsed; });
            inputWarn = new TextBlock { Text = "Anyone with your link can now use the mouse and type here. Pause the remote also stops it.",
                Foreground = B("#F0BE5A"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(50, -2, 0, 6), Visibility = S.AllowInput ? Visibility.Visible : Visibility.Collapsed };
            perm.Children.Add(inputWarn);
            // v1.9: clipboard and screen peek - both off by default
            sw(perm, "Send text to this PC's clipboard", S.AllowClipboard, v => { S.AllowClipboard = v; S.Save(); server.Changed(); });
            sw(perm, "Screen peek (one picture each time you ask)", S.AllowPeek, v => { S.AllowPeek = v; S.Save(); server.Changed(); peekWarn.Visibility = v ? Visibility.Visible : Visibility.Collapsed; });
            peekWarn = new TextBlock { Text = "The picture shows whatever is on your main screen. This PC tells you each time.",
                Foreground = B("#F0BE5A"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(50, -2, 0, 6), Visibility = S.AllowPeek ? Visibility.Visible : Visibility.Collapsed };
            perm.Children.Add(peekWarn);
            // v1.11: files from the phone into Downloads\Hallow Remote (off by default)
            var upSw = new CheckBox { Content = new TextBlock { Text = "Receive files (into Downloads\\Hallow Remote)", TextWrapping = TextWrapping.Wrap }, IsChecked = S.AllowUpload, Style = (Style)FindResource("Switch") };
            perm.Children.Add(upSw);
            var upRow = new WrapPanel { Margin = new Thickness(50, 0, 0, 6), Visibility = S.AllowUpload ? Visibility.Visible : Visibility.Collapsed };
            upRow.Children.Add(new TextBlock { Text = "Up to", Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var sizes = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var mb in new[] { 50, 200, 1024, 4096 })
            {
                var v = mb; var c = new ToggleButton { Content = mb < 1024 ? mb + " MB" : (mb / 1024) + " GB", Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3), IsChecked = S.UploadMaxMB == mb };
                c.Click += (s, e) => { S.UploadMaxMB = v; S.Save(); foreach (ToggleButton x in sizes.Children) x.IsChecked = x == c; server.Changed(); };
                sizes.Children.Add(c);
            }
            upRow.Children.Add(sizes);
            var openDl = new Button { Content = "Open the folder", Style = (Style)FindResource("Btn"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
            openDl.Click += (s, e) => { try { System.IO.Directory.CreateDirectory(Files.UploadDir); System.Diagnostics.Process.Start("explorer.exe", "\"" + Files.UploadDir + "\""); } catch { } };
            upRow.Children.Add(openDl);
            perm.Children.Add(upRow);
            upSw.Click += (s, e) => { S.AllowUpload = upSw.IsChecked == true; S.Save(); server.Changed(); upRow.Visibility = S.AllowUpload ? Visibility.Visible : Visibility.Collapsed; };
            // ---- v1.8: apps your phone can open - chosen here only; the phone sees their names, never paths
            int nApps; lock (S) nApps = S.Launchers.Count;
            var apps = Section(sp, "Apps your phone can open", nApps == 0 ? "none" : nApps.ToString(), false);
            var addApp = new Button { Content = "Add an app", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) }; addApp.Click += (s, e) => AddLauncher();
            apps.Children.Add(addApp);
            appsList = new StackPanel(); apps.Children.Add(appsList); RefreshApps();
            // ---- v1.11: the "Send to phone" list (drag files onto the window works with the section closed too)
            var files = Section(sp, "Send files to your phone", "or drag them onto this window", false);
            var addOut = new Button { Content = "Add a file", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) }; addOut.Click += (s, e) => PickOutgoing();
            files.Children.Add(addOut);
            outList = new StackPanel(); files.Children.Add(outList); RefreshOutbox();
            // ---- Advanced
            var adv = Section(sp, "Advanced", null, false);
            sw(adv, "Keep running in the tray when I close this window", S.TrayOnClose, v => S.TrayOnClose = v);
            sw(adv, "Start with Windows (in the tray)", StartsWithWindows(), v => SetStartWithWindows(v));
            sw(adv, "Use the Hallow icon (lives in Hallow Hub's tray menu)", S.UseHallowIcon, v => { S.UseHallowIcon = v; CheckHost(); });
            sw(adv, "Use the Hallow accent (from Hallow Toolkit)", S.UseHallowAccent, v => { S.UseHallowAccent = v; S.Save(); Dispatcher.BeginInvoke(new Action(ApplyAccent)); });
            // v1.16: light / dark - follows the Hallow setting (Hallow Toolkit) unless picked here
            var lookRow = new DockPanel { Margin = new Thickness(50, 0, 0, 8) };
            var lookLbl = new TextBlock { Text = "Look", Width = 50, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted") }; DockPanel.SetDock(lookLbl, Dock.Left); lookRow.Children.Add(lookLbl);
            var lookChips = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var o in new[] { Tuple.Create(Look.Follow, "Follow Hallow"), Tuple.Create(Look.Light, "Light"), Tuple.Create(Look.Dark, "Dark") })
            {
                var key = o.Item1; var c = new ToggleButton { Content = o.Item2, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 0), IsChecked = Look.Normalize(S.Look) == key, Padding = new Thickness(12, 4, 12, 4) };
                c.Click += (s, e) => { S.Look = key; S.Save(); foreach (ToggleButton x in lookChips.Children) x.IsChecked = x == c; Dispatcher.BeginInvoke(new Action(ApplyAccent)); };
                lookChips.Children.Add(c);
            }
            lookRow.Children.Add(lookChips); adv.Children.Add(lookRow);
            var upd = new Button { Content = "Check for updates", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) }; upd.Click += (s, e) => CheckForUpdate(true);
            adv.Children.Add(upd);
            // ---- Help (what used to be the long texts)
            var help = Section(sp, "Help", null, false);
            help.Children.Add(Muted("Your phone gets Hallow Mixer's sliders and mic, your Hallow Hub games, Hallow Clips, Vault and Overlay, the Hallow Toolkit game session, the newer apps, Hallow Keys and Hallow Authenticator, your PC's stats, files both ways and sleep / shut down / lock."));
            help.Children.Add(Muted("Only someone with the link can use it - keep it to yourself, and press New link if it ever gets out. It works only on your home network or through your own Tailscale. The first time, Windows may ask whether Hallow Remote can use the network: allow it for private networks."));
            help.Children.Add(Muted("Hallow Authenticator: your 2FA codes on the phone (Home > Authenticator on the phone, needs HTTPS). Encrypted backups the phone sends are kept, unopened, in %APPDATA%\\HallowAuthenticator\\backups."));
            sp.Children.Add(new TextBlock { Text = "Hallow Remote " + Program.Version + "  -  icons: Font Awesome Free (CC BY 4.0)", Foreground = (Brush)FindResource("Muted"), FontSize = 11, Margin = new Thickness(0, 10, 0, 0) });
            var phoneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; phoneTimer.Tick += (s, e) => ShowPhone(); phoneTimer.Start(); ShowPhone();
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        void ShowPhone()
        {
            if (phoneLine == null) return;
            var t = server.LastPhoneSeen;
            phoneLine.Text = t == DateTime.MinValue ? "Your phone: not connected since Remote started" : (DateTime.UtcNow - t).TotalSeconds < 15 ? "Your phone: connected now" : "Your phone: last seen " + t.ToLocalTime().ToString("HH:mm");
            if (Snap.On) phoneLine.Text = "Your phone: connected now";
        }

        void ShowLink()
        {
            var l = server.Link;
            link.Text = l ?? "-";
            try { qr.Source = l == null ? null : QrImage(l); } catch { qr.Source = null; }   // a link too long for a QR code: copy it instead
            state.Text = server.Error ?? (S.Network == "lan" ? "Ready on your home network. Your phone has to be on the same Wi-Fi." : "Ready through Tailscale - works wherever your phone has Tailscale on.");
            state.Foreground = server.Error != null ? B("#F0BE5A") : B("#6ED28C");
            if (server.Error == null && server.Https) state.Text = "Ready through Tailscale, with HTTPS (certificate good until " + server.CertUntil.ToString("d MMM yyyy") + ").";
            if (paused) { state.Text = "Paused - your phone can't reach this PC until you turn Remote on again."; state.Foreground = B("#F0BE5A"); }
            if (onSwitch != null) onSwitch.IsChecked = !paused;
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
        // ---- v1.8: the list of apps the phone may open
        StackPanel appsList;
        void RefreshApps()
        {
            appsList.Children.Clear();
            List<Launcher> list; lock (S) list = S.Launchers.ToList();
            if (list.Count == 0) appsList.Children.Add(new TextBlock { Text = "None yet - add a game launcher, Spotify, Discord... They show up on your phone under Open on your PC.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap });
            foreach (var l in list)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) }; var item = l;
                var rm = new Button { Content = "Remove", Style = (Style)FindResource("Btn"), Padding = new Thickness(10, 3, 10, 3) };
                rm.Click += (s, e) => { lock (S) { S.Launchers.Remove(item); S.Save(); } RefreshApps(); server.Changed(); };
                DockPanel.SetDock(rm, Dock.Right); row.Children.Add(rm);
                var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                tb.Inlines.Add(new System.Windows.Documents.Run(l.Name) { FontWeight = FontWeights.SemiBold });
                tb.Inlines.Add(new System.Windows.Documents.Run("   " + l.Path) { Foreground = (Brush)FindResource("Muted"), FontSize = 11 });
                row.Children.Add(tb); appsList.Children.Add(row);
            }
        }
        // ---- v1.11: the "Send to phone" list (the phone sees names and sizes, never paths; 24 hours each)
        StackPanel outList;
        void RefreshOutbox()
        {
            if (outList == null) return;
            outList.Children.Clear();
            var list = server.OutboxNow();
            if (list.Count == 0) outList.Children.Add(new TextBlock { Text = "Nothing yet - drag files onto this window, or Add a file. They show up on your phone's Files tab for 24 hours (names and sizes only).", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap });
            foreach (var o in list)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) }; var id = o.Id;
                var rm = new Button { Content = "Remove", Style = (Style)FindResource("Btn"), Padding = new Thickness(10, 3, 10, 3) };
                rm.Click += (s, e) => { server.RemoveOutgoing(id); RefreshOutbox(); };
                DockPanel.SetDock(rm, Dock.Right); row.Children.Add(rm);
                var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = o.Path };
                tb.Inlines.Add(new System.Windows.Documents.Run(o.Name) { FontWeight = FontWeights.SemiBold });
                var h = (int)Math.Ceiling(Files.HoursLeft(o));
                tb.Inlines.Add(new System.Windows.Documents.Run("   " + Files.Size(o.Size) + "  -  " + (h <= 1 ? "under an hour left" : h + " hours left")) { Foreground = (Brush)FindResource("Muted"), FontSize = 11 });
                row.Children.Add(tb); outList.Children.Add(row);
            }
        }
        void PickOutgoing()
        {
            var dlg = new OpenFileDialog { Title = "Pick files to send to your phone", Multiselect = true };
            if (dlg.ShowDialog(this) == true) AddOutgoing(dlg.FileNames);
        }
        void AddOutgoing(string[] paths)
        {
            string err = null; int added = 0;
            foreach (var p in paths) { var r = server.AddOutgoing(p); if (r == null) added++; else err = r; }
            RefreshOutbox();
            if (err != null) { state.Text = err + "."; state.Foreground = B("#F0BE5A"); }
            else if (added > 0) { state.Text = (added == 1 ? "1 file is" : added + " files are") + " ready on your phone's Files tab for 24 hours."; state.Foreground = B("#6ED28C"); }
        }
        void AddLauncher()
        {
            var dlg = new OpenFileDialog { Title = "Pick an app your phone may open", Filter = "Apps and shortcuts (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) };
            if (dlg.ShowDialog(this) != true) return;
            AddLauncherPath(dlg.FileName);
        }
        public void AddLauncherPath(string path)
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant(); if (ext != ".exe" && ext != ".lnk" && ext != ".url") return;
            string name = null;
            try { if (ext == ".exe") name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
            if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.Length > 40) name = name.Substring(0, 40);
            lock (S)
            {
                if (S.Launchers.Count >= 20) { state.Text = "That's 20 apps - remove one first."; return; }
                if (S.Launchers.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) return;
                S.Launchers.Add(new Launcher { Id = RemoteSettings.NewCode().Substring(0, 8), Name = name, Path = path }); S.Save();
            }
            RefreshApps(); server.Changed();
        }

        // ---- v1.7: updates from GitHub (asks first; the download is checked against GitHub's SHA-256)
        Button updateBtn; TextBlock inputWarn, peekWarn; GhRelease pending;
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
            var a = Accent.Current(S.UseHallowAccent); var light = Look.Resolve(S.Look, Accent.FilePath);
            if (a == Program.AccentHex && light == Look.IsLight) return;   // v1.16: a light / dark change re-themes too
            Program.LoadTheme(Application.Current);
            foreach (Window w in Application.Current.Windows) { w.Background = (Brush)FindResource("Bg"); w.Foreground = (Brush)FindResource("Text"); }
            Content = Build(); ShowLink(); server.Changed(); Look.TitleBars();
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
            Look.Track(w);
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
            if (!hubHosts) { if (RemoteSettings.TestDir == null) tray.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.None); return; }   // developer tests: no balloons on the screen
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

        // --snap: opens every section (to show nothing was lost)
        static void OpenAllSections(DependencyObject d)
        {
            if (d == null) return;
            var b = d as Button; var t = b == null ? null : b.Content as TextBlock;
            if (t != null && t.Inlines.FirstInline is System.Windows.Documents.Run && ((System.Windows.Documents.Run)t.Inlines.FirstInline).Text.StartsWith("▸")) b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var sv = d as ScrollViewer; if (sv != null) { OpenAllSections(sv.Content as DependencyObject); return; }
            var p = d as Panel; if (p != null) foreach (UIElement c in p.Children.Cast<UIElement>().ToList()) OpenAllSections(c);
            var bd = d as Border; if (bd != null) OpenAllSections(bd.Child);
        }
        public void SnapAll(string dir)
        {
            // never show the real secret in a picture
            link.Text = "http://100.90.88.26:5480/example-code/"; qr.Source = QrImage(link.Text);
            state.Text = "Ready through Tailscale - works wherever your phone has Tailscale on."; state.Foreground = B("#6ED28C");
            ShowHttpsNote("gaming-pc.tail1234.ts.net", null);
            ShowPhone();
            Snap.Save(this, System.IO.Path.Combine(dir, "main.png"), Width, Height);                 // v1.19: the default size (fits 1366x768)
            Snap.Save(this, System.IO.Path.Combine(dir, "main-1080.png"), 760, 960);              // made bigger on a 1920x1080 screen
            OpenAllSections(Content as DependencyObject);
            Snap.Save(this, System.IO.Path.Combine(dir, "main-open.png"), Width, 0);              // every section opened (all of it is still there)
            Snap.Save(CrashWindow(System.IO.Path.Combine(dir, "example.txt")), System.IO.Path.Combine(dir, "crash.png"), 440, 0);
        }
    }
}
