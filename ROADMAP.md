# Hallow Remote roadmap

## Done

| Version | What it added |
|---|---|
| **1.0** | Phone remote over Tailscale or home Wi-Fi with a secret link and QR code: Hallow Mixer sliders, mic, presets and music; Hallow Hub games (tap to play); Hallow Toolkit game session; PC stats; New link retires the old one |
| **1.1** | QR code checked against the QR standard (format bits, Reed-Solomon, version choice) and given the lowest-penalty mask; Home Wi-Fi mode skips VPN / WSL / Hyper-V adapters and prefers the one with your router |
| **1.2** | Hallow Clips (save a clip, newest clips, watch them on the phone with seeking), Hallow Vault (back up now, last backup per game), Hallow Overlay (show / hide), sleep / shut down / lock with a confirm tap; Remote's own family pipe (ping, status) and `--cmd` |

## Next

### 1.3 - Proper app feel
- HTTPS through Tailscale's own certificates, so the phone treats it as a real app
- Live updates without refreshing

### Still to check by hand
- Scan the QR code with a real phone over Tailscale (the encoder passes an independent decoder, but no camera has read it yet)
- Use it from the phone on home Wi-Fi and check the Windows firewall question (whether Windows asks, and that private networks is enough)

### Later
- Notifications on the phone (game finished downloading, clip saved): needs HTTPS first
