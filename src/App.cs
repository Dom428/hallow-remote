// Hallow Remote - your phone as a remote for the Hallow apps: Mixer sliders and mic, Hub games, Toolkit game session, PC stats.
// The PC side is this small tray app; the phone side is a web page you add to your home screen.
using System;
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
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace HallowRemote
{
    public static class Program
    {
        public const string Version = "1.1";
        [STAThread]
        public static void Main(string[] args)
        {
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
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(app);
            var w = new MainWindow(args.Contains("--tray"));
            new Thread(() => { while (showEvent.WaitOne()) w.Dispatcher.BeginInvoke(new Action(w.ShowFromTray)); }) { IsBackground = true }.Start();
            app.Run();
            GC.KeepAlive(mutex);
        }
        static void LoadTheme(Application app)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("HallowRemote.Theme.xaml"))
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(s));
        }
    }

    public class MainWindow : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        static SolidColorBrush B(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }

        readonly RemoteSettings S = RemoteSettings.Load();
        readonly Server server;
        readonly bool startHidden; bool exiting, toldTray;
        Image qr; TextBlock link, state, log; WF.NotifyIcon tray; StackPanel netChips;

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
                    if (!toldTray && tray != null) { toldTray = true; tray.ShowBalloonTip(4000, "Hallow Remote", "Still running, so your phone can reach your PC. Right-click the icon to quit.", WF.ToolTipIcon.None); }
                }
                else Quit();
            };
            server.Log = m => Dispatcher.BeginInvoke(new Action(() => log.Text = DateTime.Now.ToString("HH:mm") + "  From your phone: " + m));
            if (!Snap.On) { server.Start(); SetupTray(); }
            else { server.Address = "100.90.88.26"; }
            ShowLink();
            if (!Snap.On)
            {
                // Tailscale can connect after Windows starts: try again every 30 s until it's up
                var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                retry.Tick += (s, e) => { if (server.Address == null || server.Error != null) { server.Start(); ShowLink(); } };
                retry.Start();
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
            title.Children.Add(new TextBlock { Text = "HALLOW", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = B("#F07820"), VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = " REMOTE", FontSize = 22, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(title);
            var row = new DockPanel();
            var qrBox = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(12), Width = 214, Height = 214, Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Top };
            qr = new Image { Stretch = Stretch.Uniform }; RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
            qrBox.Child = qr; DockPanel.SetDock(qrBox, Dock.Left); row.Children.Add(qrBox);
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = "Scan this with your phone's camera", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            info.Children.Add(new TextBlock { Text = "Then use your browser's \"Add to Home screen\" so it opens like an app. You get Hallow Mixer's sliders and mic, your Hallow Hub games, the Hallow Toolkit game session and your PC's stats.",
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
            btns.Children.Add(copy); btns.Children.Add(open); btns.Children.Add(renew);
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
            sp.Children.Add(more);
            log = new TextBlock { Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 10, 0, 0) };
            sp.Children.Add(log);
            sp.Children.Add(new TextBlock { Text = "Only someone with the link can use it - keep it to yourself, and press New link if it ever gets out. It works only on your home network or through your own Tailscale. " +
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
            menu.Items.Add(new WF.ToolStripSeparator());
            var quit = new WF.ToolStripMenuItem("Quit"); quit.Click += (s, e) => Quit(); menu.Items.Add(quit);
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowFromTray(); };
        }
        public void ShowFromTray() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
        void Quit() { exiting = true; server.Stop(); if (tray != null) { tray.Visible = false; tray.Dispose(); } Application.Current.Shutdown(); }

        public void SnapAll(string dir)
        {
            // never show the real secret in a picture
            link.Text = "http://100.90.88.26:5480/example-code/"; qr.Source = QrImage(link.Text);
            state.Text = "Ready through Tailscale - works wherever your phone has Tailscale on."; state.Foreground = B("#6ED28C");
            Snap.Save(this, System.IO.Path.Combine(dir, "main.png"), 640, 0);
        }
    }
}
