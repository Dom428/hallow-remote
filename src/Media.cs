// Hallow Remote v1.8 - media without Hallow Mixer: the PC's volume and mute (Windows Core Audio) and what's playing
// (Windows' own media sessions - the ones the volume flyout shows). Play / pause / skip use the media keys.
// In a test folder the volume is a stand-in kept in memory, so tests never change the PC's real volume.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

namespace HallowRemote
{
    public static class Media
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int mask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }
        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice { int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr notify);
            int UnregisterControlChangeNotify(IntPtr notify);
            int GetChannelCount(out uint count);
            int SetMasterVolumeLevel(float db, ref Guid ctx);
            int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            int GetMasterVolumeLevel(out float db);
            int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
            int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            int GetChannelVolumeLevel(uint ch, out float db);
            int GetChannelVolumeLevelScalar(uint ch, out float level);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
        static bool Test { get { return Input.Test; } }
        static int testVol = 50; static bool testMute;

        static IAudioEndpointVolume Endpoint()
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator(); IMMDevice dev;
            if (en.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out dev) != 0 || dev == null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID; object o;
            if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o) != 0) return null;
            return o as IAudioEndpointVolume;
        }
        // volume 0-100 and mute; -1 when there's no output device
        public static int Volume(out bool muted)
        {
            muted = false;
            if (Test) { muted = testMute; return testVol; }
            try { var ep = Endpoint(); if (ep == null) return -1; float v; ep.GetMasterVolumeLevelScalar(out v); ep.GetMute(out muted); return (int)Math.Round(v * 100); }
            catch { return -1; }
        }
        public static bool SetVolume(int v)
        {
            v = Math.Max(0, Math.Min(100, v));
            if (Test) { testVol = v; return true; }
            try { var ep = Endpoint(); if (ep == null) return false; var g = Guid.Empty; ep.SetMasterVolumeLevelScalar(v / 100f, ref g); return true; }
            catch { return false; }
        }
        public static bool ToggleMute()
        {
            if (Test) { testMute = !testMute; return true; }
            try { var ep = Endpoint(); if (ep == null) return false; bool m; ep.GetMute(out m); var g = Guid.Empty; ep.SetMute(!m, ref g); return true; }
            catch { return false; }
        }

        // ---- what's playing (read only)
        static GlobalSystemMediaTransportControlsSessionManager manager; static DateTime managerTried;
        static T Wait<T>(IAsyncOperation<T> op, int ms)
        {
            var t0 = Environment.TickCount;
            while (op.Status == AsyncStatus.Started && Environment.TickCount - t0 < ms) Thread.Sleep(5);
            return op.Status == AsyncStatus.Completed ? op.GetResults() : default(T);
        }
        public static System.Collections.Generic.Dictionary<string, object> NowPlaying()
        {
            try
            {
                if (manager == null) { if ((DateTime.UtcNow - managerTried).TotalSeconds < 30) return null; managerTried = DateTime.UtcNow; manager = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), 2000); }
                if (manager == null) return null;
                var s = manager.GetCurrentSession(); if (s == null) return null;
                var p = Wait(s.TryGetMediaPropertiesAsync(), 800); if (p == null || string.IsNullOrEmpty(p.Title)) return null;
                var info = s.GetPlaybackInfo();
                return new System.Collections.Generic.Dictionary<string, object> { { "title", p.Title }, { "artist", string.IsNullOrEmpty(p.Artist) ? p.AlbumArtist : p.Artist },
                    { "app", AppName(s.SourceAppUserModelId) }, { "playing", info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing } };
            }
            catch { return null; }
        }
        // "Spotify.exe" -> "Spotify", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" -> "ZuneMusic", "MSEdge" -> "Edge"
        static string AppName(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var n = id; var bang = n.IndexOf('!'); if (bang >= 0) n = n.Substring(bang + 1);
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            var dot = n.LastIndexOf('.'); if (dot >= 0 && dot < n.Length - 1) n = n.Substring(dot + 1);
            if (n.Equals("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
            if (n.Length > 0) n = char.ToUpperInvariant(n[0]) + n.Substring(1);
            return n;
        }
    }
}
