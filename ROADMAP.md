# Hallow Remote roadmap

## Done

| Version | What it added |
|---|---|
| **1.0** | Phone remote over Tailscale or home Wi-Fi with a secret link and QR code: Hallow Mixer sliders, mic, presets and music; Hallow Hub games (tap to play); Hallow Toolkit game session; PC stats; New link retires the old one |
| **1.1** | QR code checked against the QR standard (format bits, Reed-Solomon, version choice) and given the lowest-penalty mask; Home Wi-Fi mode skips VPN / WSL / Hyper-V adapters and prefers the one with your router |
| **1.2** | Hallow Clips (save a clip, newest clips, watch them on the phone with seeking), Hallow Vault (back up now, last backup per game), Hallow Overlay (show / hide), sleep / shut down / lock with a confirm tap; Remote's own family pipe (ping, status) and `--cmd` |
| **1.3** | Live updates (long poll, woken at once by Hallow Mixer's own long poll, by phone actions, or a 1 s check of the other apps); HTTPS with Tailscale's certificate when its files are in the tls folder (one port, plain links forward to https), with the steps in the PC window |
| **1.4** | One Hallow icon: pipe `open` / `menu` (Show the phone code, Copy link, Pause the remote) / `invoke` / `quit`; "Use the Hallow icon" (default on) hides Remote's tray icon while Hub answers `tray-host` on:true and shows it again otherwise; balloons through Hub `notify`; the link and code never cross the pipe |
| **1.5** | The Hallow accent from Hallow Toolkit (theme.json, watched live) on the PC window and the phone page (`--accent` CSS variable from the state); crash reports kept on this PC with a once-per-crash notice and `lastCrash` in status |
| **1.6** | Live stats page (FPS / ping / server region and last match from Hallow Overlay's status when it reports them, otherwise Remote's PC stats; 1 s refresh while open); My buttons: a grid built only from the fixed action list (choose, order, rename), saved in remote.json, power keeps its confirm tap |
| **1.7** | Updates from GitHub (asks first, SHA-256 checked, numeric version compare); trackpad, clicks, drag, scroll, typing and a fixed set of keys on the phone (SendInput) - off by default, its own switch with a warning, never while paused |
| **1.8** | Media keys, PC volume and mute (Core Audio) and now playing (Windows media sessions) without Hallow Mixer; apps to open from a list set up on the PC only (phone sees names and random ids, never paths); both in My buttons |

## Next

### Gaps vs Unified Remote
What Unified Remote, KDE Connect and Steam Link's phone controls do that Hallow Remote doesn't (yet):
- Mouse trackpad and keyboard from the phone - **buildable** (SendInput on the PC; opt-in) -> 1.7
- Updates itself - **buildable** (GitHub releases with checksum, like Hallow Hub) -> 1.7
- Media keys, PC volume and now playing without Hallow Mixer - **buildable** (media keys, Windows Core Audio, Windows' media sessions) -> 1.8
- Open an app on the PC from the phone - **buildable** (a list set up on the PC only; the phone can't add commands) -> 1.8
- Send text to the PC clipboard - **buildable** (opt-in) -> 1.9
- Screen viewer - **buildable as a one-shot screen peek** (opt-in, off by default, the PC says each time) -> 1.9; a live screen stream is not planned (bandwidth and privacy)
- Presentation remote - **buildable** (arrow keys on the keyboard page) -> 1.7
- App-specific remotes (90+ in Unified Remote) - **partly**: My buttons covers fixed actions; per-app remotes would need an action for each app - later
- File transfer phone <-> PC - **buildable** later (upload into Downloads), not in this round
- Wake-on-LAN - **not buildable here**: a sleeping or off PC can't run Remote; it needs another always-on device on the network
- Phone notifications on the PC, SMS, ring my phone, phone battery - **not buildable**: a web page can't read the phone's notifications, texts or battery; it needs a native phone app
- Widgets, voice control, Siri, NFC, smartwatch - **not buildable**: native phone app features
- Streaming games with touch controls (Steam Link) - **not buildable** sensibly: needs a low-latency video encoder and stream; Steam Link already does it
- Works on networks that block device-to-device traffic (KDE Connect over OpenVPN) - **already covered** by Tailscale

### 1.9 - Clipboard and screen peek
- Send text from the phone to the PC clipboard (opt-in)
- Screen peek: a one-shot screenshot, opt-in and off by default
### Still to check by hand
- Live stats with a real Hallow Overlay: Overlay 1.8 shares its last match but not live FPS / ping / server in its status yet, so those tiles appear once it does
- One Hallow icon with the real Hallow Hub 2.4 (tested against a fake Hub pipe)
- HTTPS with a real Tailscale certificate on a real phone (tested with self-signed EC and RSA certificates only; needs HTTPS turned on in the Tailscale admin console)
- Scan the QR code with a real phone over Tailscale (the encoder passes an independent decoder, but no camera has read it yet)
- Use it from the phone on home Wi-Fi and check the Windows firewall question (whether Windows asks, and that private networks is enough)

### Later
- Notifications on the phone (game finished downloading, clip saved): Web Push goes through Google's / Apple's push servers and needs HTTPS
