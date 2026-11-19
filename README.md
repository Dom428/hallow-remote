# Hallow Remote

Your phone as a remote for the Hallow apps - [Hallow Mixer](https://github.com/Dom428/hallow-mixer), [Hallow Hub](https://github.com/Dom428/hallow-hub) and [Hallow Toolkit](https://github.com/Dom428/hallow-toolkit).

- **Mixer:** channel sliders and mutes, mic mute, presets, play / pause / skip
- **Hub:** your recent games with covers - tap to start one on the PC (through Hub, so its preset and game session apply)
- **Toolkit:** game session on / off
- **PC:** CPU, GPU, GPU temperature, RAM

The PC side is a small tray app that serves a phone web page (add it to your home screen) on your Tailscale or home-network address. Every request needs the secret code in the link; the phone can only do a fixed list of things, and secrets on the PC (like app keys or a Discord link) are never sent. Scan the QR code it shows - it's made on the PC, never by an online service.

## What's new

- **1.1** - The QR code now uses the pattern (mask) that cameras read most easily, checked against the QR standard; Home Wi-Fi mode picks your real home-network adapter (not a VPN, WSL or Hyper-V one).

Built with the C# compiler that ships with Windows (.NET Framework 4.8): run `build.ps1`. No admin needed.

## Credits

Icons: [Font Awesome Free](https://fontawesome.com) 7.3.1 by Fonticons, Inc., licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) (see FontAwesome-LICENSE.txt).