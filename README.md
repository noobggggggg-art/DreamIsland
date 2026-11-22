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

**Video** (any player that reports playback to Windows, plus videos in a browser)
- the **Emotions** island: bars, glow and flashes follow the sound and the beat, the island bounces on the kick;
- colour and flashes follow the **picture** of the video (dominant colour, scene cuts, strobe lights);
- two animated **speakers** slide into the black side bars of vertical videos and pulse with the music.

**TeamSpeak 3**
- who is talking, microphone / sound mute state, server name, user count, volume with Alt + wheel.

**Downloads**
- percentage, speed and a "done" notice for **Steam** and **Epic Games Launcher** downloads.

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
- an app started using the microphone or the camera;
- a Steam / Epic download started, reached the next 10 %, or finished.

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
git tag v1.2.0
git push origin v1.2.0
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

### Islands
DreamIsland has four "islands" that replace each other:
1. **Default** (date and indicators), when nothing is playing;
2. **Music**, when something is playing;
3. **TeamSpeak 3**, while you are connected to a server (replaces the music island);
4. **Emotions**, while a video is playing (hover the island to get the music controls back).

### TeamSpeak 3
Shows who is talking, whether your microphone / sound are muted, and the server name (you can give it your own short name from the menu). While someone talks, the island glows green and "breathes". Hover for the full date and time, VPN, layout and both microphones (TeamSpeak and Windows). **Alt + wheel** over the island changes the volume of TeamSpeak itself.
1. In TeamSpeak 3: **Tools → Options → Addons → Plugins** → enable **ClientQuery**, press **Settings** and copy the **API key**.
2. In the island menu: **TeamSpeak 3: API key…** → paste it, then enable **Follow TeamSpeak 3**.

Everything goes through the local ClientQuery plugin (`127.0.0.1:25639`); nothing leaves your computer.

### Emotions (video)
When a video plays (the island recognises it by the player's media type, the file extension such as `.mov`, or the player name), the island turns into a live visualiser: bars follow the spectrum, the colour follows the timbre of the sound, the glow pulses on the kick drum and the island flashes on snare hits. Pick the animation in the menu: *Auto, Spectrum, Pulse, Flash*. The island listens to the system sound only (nothing is recorded).

**Picture analysis** (menu: *Colour and flashes from the video picture*): while a video plays, the island takes tiny 96×54 frames of the primary screen about 8 times a second, only to pick the dominant colour, detect scene cuts / strobe flashes and find black side bars. Frames are never saved or sent anywhere. If the video is DRM-protected and the screen shows black, the island falls back to colours from the sound.

**Speakers** (menu: *Speakers in the black side bars*): when the video has black bars at the sides (for example a phone video on a wide screen), two small speaker widgets slide into the bars, take the video colour and pulse on the kick drum. They are click-through and are excluded from screen capture, so they will not show up in OBS recordings. Bars narrower than about 56 px are ignored. With stereo audio each speaker follows its own channel (a guitar hit on the left kicks the left speaker harder). When the music keeps building for several seconds the speakers charge up (rings contract, glow builds), and on the release (the drop) both speakers and the island flash at once. Extras: each speaker takes its colour from its own edge of the picture (pale, smoky footage keeps the music colour), a soft coloured light spreads over the black bar, 8–12 small sparks fly off on the hardest hits, and when the video is paused the speakers stay, dim and breathe slowly. Right-click menu → *Emo: speaker style* switches between Neon, Retro and Minimal. None of this uses blur; with *Save video memory* on, the light and the sparks are turned off.

**Speaker effects** (Neon has all of them, Retro the softer ones, Minimal only the lull, rings and sparks): LED strips of 16 spectrum bands for the speaker's own channel light up on both sides of the cabinet; spotlight beams fan out of the speaker, sway, flicker with the highs and open wide on the drop; ripples (cymatics) travel across the cone; a runner light circles the cabinet frame, speeds up while the music builds and closes into a flashing outline on the drop; fireflies are pulled into the speaker during a build and burst out like fireworks on the drop; the cabinet glitches for a few frames with coloured stripes on a snare; and the drop sends a shockwave of two rings with a red/blue colour split plus a bright flare. With *Save video memory* on, the beams, fireflies, glitch and shockwave are turned off; the LED strips, ripples and runner stay.

**Lull before the drop.** When a loud track suddenly goes quiet (a breakdown), the speakers and the island dim and freeze, the light is drawn inward (an "inhale"), and the first hard hit after the lull makes the drop noticeably stronger than usual.

**Sensitivity** (menu *Emo: sensitivity*): *Quiet* reacts only to the strongest moments, *Normal* matches the old behaviour, *Hard* reacts to almost everything. The preset changes the build, drop, lull and spark thresholds (they used to be constants in the code) and is remembered.

**Speakers over the picture** (menu *Emo: speakers over the picture when there are no black bars*, off by default): if the video has no bars, two small semi-transparent speakers hang at the left and right edges of the screen over the picture.

**Debug panel** (menu *Emo: debug panel with numbers*, always off after a restart): a small window in the top-left corner shows loudness, intensity, build, energy growth and its threshold, burst and the drop threshold, lull, how long a drop can still be caught, the drop count, channel balance and the beat counters. Use it to pick a preset for your own videos. Click the panel to copy the numbers plus the last minute of history to the clipboard. It never takes focus and is excluded from screen capture.

**DreamIslands interface.** A right click on the island opens a custom interface instead of the usual menu: four cubes appear in the middle of the screen, join up, turn into a rectangle, grow wider and then taller, and the sections appear from top to bottom. It is black with white text, purple edges on sections, buttons and switches, and the title DreamIslands at the top centre. Sections: **Clock** (timer, stopwatch, pomodoro, pause/resume, stop), **Sound** (turn the microphone on by sound, microphone debug), **Convenience** (live equalizer, emotions, player), **Services** (TeamSpeak 3, Steam/Epic, Explorer, clipboard, screenshots, OBS), **Statistics** (time in the programs you choose, break reminder), **Settings** (show connected devices, start with Windows, version, auto-update, recording dot, save video memory). Esc, the cross or a click outside closes it. The old context menu is still in the code as the "engine": the interface drives its items, so all the logic is unchanged.

**Volume mixer** (Sound section): every program gets its own slider and a "Mute" button; playing ones are on top (with a lit dot). Programs are read from all output devices, and Windows applies a program's volume before the sound reaches Voicemeeter, so the mixer works through it too.

**Statistics** (tab): you choose which programs to track. Minecraft and Roblox come ready and can be switched on or off; any other running program can be added with the "Choose from running" button (and removed with the cross). The island notices a launch by process name, never touches the program, shows the time today and over 7 days for each program and in total, and reminds you to take a break when one program runs longer than the chosen time (off, 30 minutes, 1 hour, 1.5 hours, 2 hours). Java Minecraft is recognised by its window title (Minecraft, Lunar Client, Badlion, Feather).

### Steam and Epic downloads
While Steam or the Epic Games Launcher is downloading something, the island shows a toast with the game name, percentage and speed: at the start, every 10 %, and when it finishes. *Downloads: show now* in the menu shows the current download on demand; *Show Steam / Epic downloads* toggles it.

The island only reads the launchers' own files and sends nothing over the network. Steam's percentage is exact (from `appmanifest_*.acf`) but Steam writes it to disk irregularly, so it updates in jumps. Epic has no official progress file, so its percentage is an estimate: the size of the half-installed game folder relative to its full size.

### Low video memory
**Low video memory (after restart)** switches WPF to software rendering, removes the glow blur and limits animations to 30 fps. Useful if a game is short on video memory.

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
- **Screen picture** (video mode only): while the Emotions island is active, tiny 96×54 frames of the primary screen are taken in memory about 8 times a second to pick a colour and find black bars. They are never saved or sent anywhere. It can be turned off in the menu.
- **Steam / Epic downloads**: only the launchers' own files on your computer are read.
- **Network**: the only internet request is the update check to `api.github.com` (and the download from GitHub). It can be turned off in the menu. OBS is contacted only on your own computer (`127.0.0.1`).

## Known limitations
- Windows does not draw windows above exclusive-fullscreen games; use "borderless window" mode.
- Wired headphones in the motherboard jack may not be detected (Windows keeps the same device name).
- Screenshots are detected in `Pictures\Screenshots` (where Win+PrintScreen saves them).
- Explorer plaques only see files leaving the open folder; with several tabs in one window the selection may come from the wrong tab.
- If a player does not report the track length, progress and seeking are unavailable.
- The speakers are hidden from screen capture on purpose, so they do not appear in OBS or screenshots.
- Epic Games Launcher has no official progress file: its percentage is an estimate and may be missing for updates of already installed games.
- Steam writes download progress to disk irregularly, so its percentage updates in jumps.
- Only the primary screen is analysed in video mode.

## License
MIT, see [LICENSE](LICENSE).
