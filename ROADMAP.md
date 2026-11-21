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
| **1.9** | Text from the phone onto the PC clipboard (opt-in, up to 2000 characters); Screen peek: one JPEG of the main screen per tap (opt-in, off by default, at most one every 2 s, announced on the PC each time); both stop while paused |
| **1.11** | Files both ways: phone -> `Downloads\Hallow Remote` (opt-in, off by default; size cap 200 MB by default; streamed to disk; safe names, never overwrites; marked as from another computer; announced on the PC each time; stops while paused), and a Send to phone list filled on the PC by drag and drop or a picker (the phone sees names, sizes and random ids, never paths; 24 hours each) |
| **1.12** | Hallow Keys 2.7 editor on the phone (add / edit / trash / generator / 2FA keys / folders and vaults): changes sealed on the phone (AES-GCM, phone key), POST api/keys/change checked for shape and size only and passed to Keys over the family pipe; one-time share links for .hkshare items at a random 128-bit /share/ path (memory only, served once, expiry, HTTPS only, no code in the link) with a page that opens them (Argon2id in plain JS, RFC 9106 vectors, + WebCrypto); family pipe reads long lines in chunks |
| **1.13** | A Home dashboard of big cards (game running with FPS / ping, mic, Hallow Guard score + Quick scan, now playing with controls, last clip, remote help, saves), swipe between tabs; Hallow Guard on the phone (score, last scan, threat count; only "scan quick" and "open history" go to Guard; status read from its status.json); Hallow Assist on the phone (session state, Disconnect with a second tap, "Get help on this PC" only opens Assist's window - no session can start from the phone); both in My buttons; notices while the page is open (clip saved, backup finished, damaged backup, threat found / scan finished, someone asking / connected) from the same long poll, optionally as phone notifications through the service worker (opt-in, HTTPS, no push servers); installable app: manifest with id / scope / shortcuts, drawn 192 / 512 icons (any + maskable) and a 180 px iPhone icon, a splash, Add to Home Screen steps (install button on Android), shell-only cache, opens offline; hardening: a Content-Security-Policy on the pages, and an address that tries 10 different wrong links in 10 minutes is refused for 15 (the tried links are never kept or shown; an old link asked again and again never counts) |
| **1.14** | New app icon (phone + signal waves) in the family style, every size drawn on its own (16-256 px); the same art for the phone's 192 / 512 / maskable / iPhone icons and the page's icon.png |
| **1.10** | Keys tab: Hallow Keys' phone copy served only over HTTPS (403 otherwise), decrypted on the phone with WebCrypto (PBKDF2-SHA256 + AES-GCM), view and copy only, TOTP codes, locks after 2 minutes or when the page is hidden; a service worker keeps the page and the encrypted copy for offline use |

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
- File transfer phone <-> PC - **buildable** (upload into Downloads, opt-in; a Send to phone list set up on the PC) -> 1.11
- Wake-on-LAN - **not buildable here**: a sleeping or off PC can't run Remote; it needs another always-on device on the network
- Phone notifications on the PC, SMS, ring my phone, phone battery - **not buildable**: a web page can't read the phone's notifications, texts or battery; it needs a native phone app
- Widgets, voice control, Siri, NFC, smartwatch - **not buildable**: native phone app features
- Streaming games with touch controls (Steam Link) - **not buildable** sensibly: needs a low-latency video encoder and stream; Steam Link already does it
- Works on networks that block device-to-device traffic (KDE Connect over OpenVPN) - **already covered** by Tailscale

### Still to check by hand
- 1.13: Add to Home Screen, the icons and splash, swipes and notices on a real iPhone / Android phone (tested in headless Edge at 412 x 915 with touch emulation, a self-signed certificate and stand-in Guard / Assist / Clips / Vault / Mixer pipes); phone notifications in the background on a real phone (iOS only allows them for a home-screen app); with the real Hallow Guard 1.0 and Assist
- 1.12: the Keys editor and a share link on a real phone (iPhone Safari / Android Chrome) over a real Tailscale certificate - tested in headless Edge in a phone-sized window with a self-signed test certificate, and with Node's WebCrypto; a share link opened on another device on your tailnet
- Sending files from a real phone (iOS / Android file pickers, big videos over Tailscale) and downloading them on the phone - tests used a local HTTP client and a desktop browser
- Trackpad, typing, media keys, PC volume, clipboard and screen peek on the real PC (tests only used stand-ins so nothing reached the real mouse, keyboard, volume, clipboard or screen)
- Live stats with a real Hallow Overlay: Overlay 1.8 shares its last match but not live FPS / ping / server in its status yet, so those tiles appear once it does
- One Hallow icon with the real Hallow Hub 2.4 (tested against a fake Hub pipe)
- HTTPS with a real Tailscale certificate on a real phone (tested with self-signed EC and RSA certificates only; needs HTTPS turned on in the Tailscale admin console)
- Scan the QR code with a real phone over Tailscale (the encoder passes an independent decoder, but no camera has read it yet)
- Use it from the phone on home Wi-Fi and check the Windows firewall question (whether Windows asks, and that private networks is enough)

### Later
- Hallow Assist's online dots for your PCs on the phone: Assist 1.x keeps them inside its window and doesn't share them in its status - once it does, they can show on the Remote help card
- Hallow Mixer 3.23's per-app volumes: Mixer's local API (state / set / mute / mic / media / preset / profile) doesn't expose apps yet - once it does, a per-app list fits the Remote tab
- Guard's threat names: Guard's status only shares the count, so the phone shows the count and opens Guard's History on the PC
- Notifications on the phone while the page is closed (game finished downloading, clip saved): Web Push goes through Google's / Apple's push servers and needs HTTPS - **1.13 does it while the page is open** (notices, and phone notifications from the service worker), without push servers
