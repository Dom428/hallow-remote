# Hallow Remote

Your phone as a remote for the Hallow apps - [Hallow Mixer](https://github.com/Dom428/hallow-mixer), [Hallow Hub](https://github.com/Dom428/hallow-hub), [Hallow Clips](https://github.com/Dom428/hallow-clips), [Hallow Vault](https://github.com/Dom428/hallow-vault), [Hallow Overlay](https://github.com/Dom428/hallow-overlay) and [Hallow Toolkit](https://github.com/Dom428/hallow-toolkit).

- **Mixer:** channel sliders and mutes, mic mute, presets, play / pause / skip
- **Hub:** your recent games with covers - tap to start one on the PC (through Hub, so its preset and game session apply)
- **Toolkit:** game session on / off
- **Clips:** save a clip, your newest clips - watch them on the phone
- **Vault:** back up now, last backup per game
- **Overlay:** show / hide
- **Power:** sleep, shut down or lock the PC (tap twice to confirm)
- **PC:** CPU, GPU, GPU temperature, RAM - and a Live stats page (with FPS, ping and server from Hallow Overlay when it shares them)
- **My buttons:** your own grid of the actions above
- **Media and apps:** PC volume and mute, what's playing and play / pause / skip (any player), and apps to open from a list you set up on the PC
- **Mouse:** trackpad, clicks, scrolling, typing and arrow keys (also a presentation clicker) - off until you turn it on in the PC window

The PC side is a small tray app that serves a phone web page (add it to your home screen) on your Tailscale or home-network address. Every request needs the secret code in the link; the phone can only do a fixed list of things (and only play clips Hallow Clips lists), and secrets on the PC (like app keys or a Discord link) are never sent. Scan the QR code it shows - it's made on the PC, never by an online service.

**HTTPS (optional):** turn on HTTPS Certificates in your Tailscale admin console (DNS page), then run the `tailscale cert` command the window shows; it writes `<pc>.<tailnet>.ts.net.crt` / `.key` into `%APPDATA%\HallowRemote\tls` and Hallow Remote switches to an https link within a minute. Without them it stays plain HTTP (still only inside your tailnet / home network).

## What's new

- **1.8** - Media without Hallow Mixer: play / pause / skip with the media keys, what's playing on the PC (Windows' own media info), and the PC's volume and mute. Open an app on the PC from a list you set up in the PC window (the phone only sees the names).
- **1.7** - Updates itself from GitHub (asks first, checks the download's SHA-256). Trackpad and keyboard on the phone's Mouse page - off by default, behind its own switch on the PC, never while the remote is paused.
- **1.6** - Live stats: a big-number page on the phone - FPS, ping and server region when Hallow Overlay shares them (plus its last match), and your PC's CPU, GPU, temperature and RAM. My buttons: build your own grid from the actions Remote already has (choose, order, rename); Sleep / Shut down / Lock still need a second tap.
- **1.5** - The Hallow accent: the colour you pick in Hallow Toolkit becomes Remote's accent on the PC and on your phone, live (setting "Use the Hallow accent", on by default). Crash reports are kept on this PC (`%APPDATA%\HallowRemote\crashes`, newest 20, never your settings or the link's code) with a notice on the next start - nothing is sent anywhere.
- **1.4** - One Hallow icon: when Hallow Hub 2.4+ hosts the family's tray icon, Remote hides its own and lives in Hub's menu (Show the phone code, Copy link, Pause the remote). Setting "Use the Hallow icon" (on by default). The link and its code never go to Hub - Copy link runs inside Remote.
- **1.3** - Live updates: the phone changes the moment something changes on the PC (mic, mixer, clips, backups...), no refreshing. Optional HTTPS with Tailscale's own certificate for your PC - the window shows how to turn it on; the old http link forwards to the new one.
- **1.2** - Hallow Clips (save a clip, watch your newest clips on the phone), Hallow Vault (back up now, last backup per game), Hallow Overlay (show / hide), and sleep / shut down / lock with a confirm tap. Remote joins the Hallow family link (`HallowRemote.exe --cmd ping` / `status`).
- **1.1** - The QR code now uses the pattern (mask) that cameras read most easily, checked against the QR standard; Home Wi-Fi mode picks your real home-network adapter (not a VPN, WSL or Hyper-V one).

Built with the C# compiler that ships with Windows (.NET Framework 4.8): run `build.ps1`. No admin needed.

## Credits

Icons: [Font Awesome Free](https://fontawesome.com) 7.3.1 by Fonticons, Inc., licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) (see FontAwesome-LICENSE.txt).