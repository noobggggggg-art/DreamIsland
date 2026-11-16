# DreamIsland

**English** | [Русский](README.ru.md)

A "Dynamic Island" for Windows 10/11, inspired by the iPhone: a small black capsule at the top of the screen that shows what is playing, the date and important events. Lightweight (tens of MB of RAM), written in C# and WPF.

> **Made for [DreamVPN](https://github.com/noobggggggg-art/DreamVPN).** The VPN indicator and the VPN notifications work **only with DreamVPN**. Other VPN clients are not supported (see [VPN](#vpn-dreamvpn-only)). Everything else works on its own.

## Features

**Music** (Spotify, YouTube Music, a browser, any player that appears in the Windows media panel)
- cover art, title and artist, a scrolling title for long names;
- a progress ring around the cover when collapsed, a progress bar with time and seeking when expanded;
- previous / play-pause / next buttons; click the island to pause, click the cover for the next track;
- the equalizer and glow take their colour from the cover art;
- **live equalizer**: the bars and the glow move to the real sound (bass / mids / highs).

**Indicators** (always visible next to the date or the music)
- keyboard layout, microphone (on / off), VPN (DreamVPN);
- **recording dot**: a blue circle means an app is using the microphone, a purple diamond means the camera;
- REC / LIVE from OBS Studio with elapsed time.

**Event notifications**
- volume (mouse wheel over the island; **Shift + wheel** changes the volume of SoundCloud only), microphone, Caps Lock, keyboard layout;
- headphones and flash drives connected / disconnected;
- VPN connected / disconnected;
- screenshot saved (with a thumbnail), text copied;
- in Explorer: how many files are selected, deleted or moved (shown for 1.5 seconds);
- an app started using the microphone or the camera.

**Tools**
- timer, stopwatch, pomodoro (25/5);
- clipboard history: copy something, hover the island within 8 seconds to see the last 5 items, click one to paste it;
- start with Windows;
- automatic updates from GitHub.

Every feature can be switched on and off from the menu (right-click the island).

## Requirements
- Windows 10 (build 19041+) or Windows 11;
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

## Install
1. Download `DreamIsland.exe` from the [latest release](https://github.com/noobggggggg-art/DreamIsland/releases/latest).
2. Put it in a permanent folder you can write to (not `Program Files`) and run it.
3. Optional: right-click the island → **Start with Windows**.

## Build from source
```
dotnet run
```
or build a single exe:
```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
Releases are built automatically by GitHub Actions when a `v*` tag is pushed.

## Updates

DreamIsland updates itself from this repository's [Releases](https://github.com/noobggggggg-art/DreamIsland/releases):
- 30 seconds after start and then every 12 hours it checks for a newer release;
- if there is one, it downloads `DreamIsland.exe` in the background and installs it **the next time you start the island**, so it never restarts in the middle of a game;
- the menu item **Check for updates** downloads and installs right away, with a restart.

The update source is built into the program and cannot be changed in the settings. Automatic installation only works for the built exe (a release or `dotnet publish`); when started with `dotnet run` the island only tells you that a new version exists. The downloaded file is verified (size, format and checksum when GitHub provides one), and the download address must be on github.com. Updates can be turned off in the menu.

To publish a new version:
```
git tag v1.0.1
git push origin v1.0.1
```

## VPN (DreamVPN only)

The VPN indicator and notifications are built for [DreamVPN](https://github.com/noobggggggg-art/DreamVPN) and **do not work with other VPN clients**. The island reads `%AppData%\DreamVPN\status.json`, which DreamVPN writes:

```json
{
  "state": "connected",
  "connected": true,
  "server": "server name",
  "country": "country",
  "core": "sing-box",
  "pid": 1234,
  "startedAt": "2026-01-01T12:00:00Z",
  "updatedAt": "2026-01-01T12:00:05Z"
}
```

`state` is `connected`, `connecting`, `disconnected` or `error`; `pid` is the DreamVPN process (so the island notices a crash). If the file does not exist, the island falls back to looking for running `sing-box`, `xray` and `tun2socks` processes. This is a best-effort fallback for DreamVPN's cores, not a supported way to detect other VPNs.

## Other setup

### OBS
1. In OBS: **Tools → WebSocket Server Settings** → enable the server (port 4455).
2. In the island menu enable **Follow OBS**; if OBS has a password, enter it in **OBS: password…**.

The password is stored in `%AppData%\DreamIsland\settings.txt` in plain text on your computer.

### SoundCloud volume (Shift + wheel)
Hold Shift and scroll over the island to change the volume of **SoundCloud only** (its own volume, as in the Windows Volume Mixer); the system volume is untouched. The island looks for the SoundCloud app; if it is not running, it uses the browser that is playing sound right now. Windows exposes volume per browser, not per tab, so in a browser this changes everything that browser plays.

### Microphone with a hardware mute button
Some microphones (for example Fifine AM8) mute themselves internally, and Windows does not know. For them enable **Follow microphone (by sound)**: the island treats the microphone as muted when it receives complete silence. **Show microphone level (debug)** helps to tune it.

## Privacy
- **Caps Lock / Delete**: a global keyboard hook is used, but it only looks at the Caps Lock and Delete keys (Delete helps tell "deleted" from "moved" in Explorer). Other keys are ignored and nothing is stored.
- **Clipboard**: history lives only in memory and is cleared on exit; anything a password manager marks as "do not save" is skipped. You can disable or clear it from the menu.
- **Live equalizer and microphone-by-sound**: sound is analysed on the fly (only loudness is computed); it is never recorded or sent anywhere.
- **Recording dot** reads the Windows privacy log (Settings → Privacy → Microphone / Camera).
- **Network**: the only internet request is the update check to `api.github.com` (and the download from GitHub). It can be turned off in the menu. OBS is contacted only on your own computer (`127.0.0.1`).

## Known limitations
- Windows does not draw windows above exclusive-fullscreen games; use "borderless window" mode.
- Wired headphones in the motherboard jack may not be detected (Windows keeps the same device name).
- Screenshots are detected in `Pictures\Screenshots` (where Win+PrintScreen saves them).
- Explorer plaques only see files leaving the open folder; with several tabs in one window the selection may come from the wrong tab.
- If a player does not report the track length, progress and seeking are unavailable.

## License
MIT, see [LICENSE](LICENSE).
