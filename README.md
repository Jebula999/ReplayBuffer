# Replay Buffer

**Made by [Jebula999](https://github.com/Jebula999).** A free, no-nonsense replay recorder for Windows. It quietly keeps the last few minutes of your game and saves a clip when you press a key. No account, no overlay, no watermark, no upload, no paywall.

> [!IMPORTANT]
> **This app was vibe coded.** It was written entirely by [Claude](https://claude.ai) (Anthropic's AI, through Claude Code), directed by a human who described what they wanted and tested the results. The human didn't write the code by hand. It works well on the machine it was built and tested on, but it hasn't had the review or long-term testing of a commercial recorder. Use it at your own risk, and please [open an issue](https://github.com/Jebula999/ReplayBuffer/issues) if something breaks.

## Features

- **Instant replay**: keeps the last 30 seconds to 30 minutes and saves it as an MP4 when you press your shortcut. A short chime confirms the press.
- **Records only the game window**, so notifications, Discord/Steam overlays, other windows and alt-tabbing stay out of clips.
- **Separate audio tracks**: game, Discord (or any other app) and your mic each get their own track for editing. An extra "All audio" mix track comes first, so normal video players play everything.
- **Starts by itself**: buffers only while one of your games is running, or always if you prefer.
- **Clips are sorted per game**: `Replays\Wardogs\Replay 2026-10-27 14-39-56.mp4`.
- **720p / 1080p / 1440p** at **30 / 60 / 144 fps**, or **Match game** to record at whatever frame rate the game is actually running. H.264, encoded on your graphics card.
- **CBR, VBR or CQP rate control** (like OBS): fixed bitrate, a variable bitrate that drops in quiet scenes, or constant quality.
- **Buffer in RAM (default) or on disk.**
- **Doesn't touch your games**: no overlay, no hooks, nothing injected. AMD FSR, frame generation and driver features are unaffected.

## Requirements

- Windows 10 (version 2004 or later) or Windows 11, 64-bit.
- A graphics card with a hardware H.264 encoder (any reasonably recent AMD Radeon, NVIDIA GeForce or Intel GPU), with up-to-date drivers.
- Nothing else. .NET and FFmpeg are included.

| GPU | Status |
|---|---|
| AMD Radeon | Tested (AMF encoder) |
| NVIDIA GeForce | Should work (uses NVENC through the same Windows API), **not yet tested** |
| Intel Arc / UHD | Should work (Quick Sync), **not yet tested** |

If you try it on NVIDIA or Intel, please report how it went in an issue. Open **Activity log** at the bottom of the window to see which encoder it picked.

## Install

Download from the [**Releases**](https://github.com/Jebula999/ReplayBuffer/releases) page. There are two options:

### Installer (recommended)

1. Download `ReplayBuffer-Setup-x.y.z.exe` and run it.
2. Windows SmartScreen may warn you because the app isn't code-signed. Click **More info → Run anyway**.
3. Click **Install**. No admin rights are needed: it installs for your user only, in `%LOCALAPPDATA%\Programs\ReplayBuffer`.
4. It adds **Replay Buffer** to the Start menu and to **Settings → Apps → Installed apps**, where you can uninstall it.

To update, run a newer setup over the top; your settings are kept. Uninstalling never deletes your clips.

### Portable

1. Download `ReplayBuffer-Portable-x.y.z.zip` and extract it anywhere: a USB stick, a games drive, wherever.
2. Run `ReplayBuffer.exe` from the extracted folder.

The portable copy keeps its settings and log in a `Data` folder next to the exe, so it leaves nothing behind on the PC. Delete the folder to remove it. (If you delete `portable.txt`, it uses `%APPDATA%\ReplayBuffer` like the installed version.)

## Quick start

1. **Launch Replay Buffer** from the Start menu (or `ReplayBuffer.exe`). The settings window opens.
2. **Add your game** (see below).
3. **Add your audio** (see below).
4. **Play.** When the game starts, the status changes to **Buffering** and the tray icon turns red.
5. **Press your save shortcut** (default **Alt + F10**) right after something cool happens. You'll hear a chime, and the last few minutes are saved to your clips folder.

Closing the window doesn't quit the app. It keeps running in the **system tray** (the red/grey record icon by the clock). Left-click the icon to open the window. Right-click it for **Save clip now**, **Start/Stop buffer**, **Open clips folder** and **Exit**.

## Adding games

The **Games** section is the list of programs that start the buffer automatically. It's also used to record only the game's window and to name the clip folders.

**Easiest way: while the game is running**
1. Start the game, then open Replay Buffer (alt-tab, or click the tray icon).
2. Under **Games**, click **+ Running program**.
3. Pick the game from the list (programs with a window are listed first, by window title) and click **OK**.

**Without starting the game**
1. Click **Browse for .exe**.
2. Find the game's executable. For Steam games: right-click the game in Steam → **Manage → Browse local files**.
3. For Unreal Engine games, the real game exe is often inside `<Game>\Binaries\Win64\`, for example `WardogsClient-Win64-Shipping.exe`. Pick that one, not the small launcher in the top folder.

Each game shows a short name (for example **Wardogs** for `WardogsClient-Win64-Shipping.exe`), which is also the name of its clips folder. Remove a game with its bin icon.

**When to buffer**
- **While a game runs** (default): buffering starts when a listed game launches and stops when it closes.
- **Always**: buffers all the time. When no listed game is running it records the chosen **Display**, and clips go into a `Desktop` folder.

The **Start buffer / Stop buffer** button at the top overrides this until the game next starts or stops.

## Adding audio inputs (tracks)

Every entry under **Audio tracks** is saved as its own track in the clip, so you can turn your voice, your friends or the game up and down separately in an editor (DaVinci Resolve, Premiere, Shotcut and so on). Clips also start with an **All audio** track that mixes everything, which is what normal video players play.

| Button | What it records | Use it for |
|---|---|---|
| **+ Game audio** | Only the sound of the listed game that's running (other apps' sounds are excluded) | Game sound |
| **+ App** | Only the sound of one program you pick from the running programs | Discord voice chat, Spotify, a browser... Pick `Discord.exe` while Discord is open. |
| **+ Device** | Everything going through one sound device. **Input** = a microphone, **Output** = everything you hear on that device | Your mic, or a Voicemeeter bus |

Tips:
- **Your microphone**: **+ Device** → pick the `Input (microphone)` entry for your mic. With **Voicemeeter**, pick the Voicemeeter **Out** bus your mic is routed to (for example `Voicemeeter Out B1`).
- **Discord** is recorded per app, so the game track never contains your friends' voices and vice versa.
- **App and game tracks** need Windows 10 version 2004 or later.
- If a mic track sounds silent between sentences, that's normal when you use push-to-talk or a noise gate. The track only has sound when the mic is open.
- Remove a track with its bin icon. Track changes restart the buffer.

## Settings explained

| Setting | What it does |
|---|---|
| **Resolution / Frame rate** | Size and smoothness of saved clips. The game itself is unaffected. **Match game** records every frame the game shows (up to your monitor's refresh rate), so a game running at 97 fps gives a 97 fps clip. |
| **Rate control** | **CBR** (default): the same bitrate all the time; predictable file sizes. **VBR**: aims for the target bitrate, uses less when little moves and up to the max bitrate in busy scenes. **CQP**: the same quality all the time; quiet scenes are tiny, action is big. |
| **Bitrate / Target bitrate** | CBR and VBR. Quality vs. file size. 15–20 Mbps looks good at 1080p/1440p60; go higher for 144 fps or very fast games. |
| **Max bitrate** | VBR only. The ceiling in busy scenes. About 1.5–2× the target is a good start. |
| **CQ level** | CQP only. Lower is better quality and bigger files: 18–23 suits most games, 14–16 is near lossless, 28+ is small. |
| **Encoder preset** | Speed / Balanced / Quality. Quality spends a little more of the GPU's encoder time per frame (it doesn't use the GPU cores the game uses). |
| **Record only the game window** | On: only the game is recorded while it runs. Off: the whole display is recorded. |
| **Display** | Which monitor to record when no game window is being recorded. |
| **Clip length** | How far back a saved clip goes (30 s – 30 min). |
| **Keep the buffer in** | **Memory (RAM)**: nothing is written to your drive until you save (default). **Disk**: uses temp files instead of RAM, for long buffers on PCs with less memory. |
| **Estimated size** | Roughly how big each clip will be, and how much RAM (or disk) the buffer uses. For VBR it shows the maximum; for CQP it's a rough guess, because the size depends on the game. |
| **Save shortcut** | Click the box and press any key or combo (Num \*, F9, Ctrl + Shift + S...). It works while the game is focused. |
| **Clips folder** | Where clips are saved (default `Videos\Replays`). Each game gets a subfolder. |
| **Save sound** | Chime on save. A falling tone means the save failed (see the Activity log). |
| **Start with Windows** | Starts minimised to the tray when you sign in. |

**Command line:** `ReplayBuffer.exe --save` saves a clip from the running app. Handy for a Stream Deck or a mouse macro button. `--minimized` starts in the tray.

## Troubleshooting

- **Nothing happens when I press the shortcut**: another program may own that key combo. The app tells you under **Save shortcut**, so pick a different one. The buffer also needs to be running: the status must say **Buffering**.
- **Clip is black**: the game might use old-style *exclusive* fullscreen. Switch the game to **borderless / windowed fullscreen**, or turn off **Record only the game window**.
- **Only hear the game in my clip**: your player is playing the first track only. Clips made since the "All audio" track was added play everything. Editors show every track.
- **Dropped frames**: the Activity log reports "fps dropped". Lower the frame rate or resolution, or close other GPU-heavy apps. Clips always stay at the right speed: frames are dropped rather than slowed down.
- **Editor shows odd frame rates or stutter with Match game clips**: Match game clips have a variable frame rate. Players handle that fine, but some editors (Premiere, older Vegas) prefer a fixed rate. Pick 60 or 144 fps if you edit in one of those, or let the editor convert the clip.
- **Something else**: open **Activity log** (bottom of the window), copy it, and include it in an [issue](https://github.com/Jebula999/ReplayBuffer/issues).

Settings and log live in `%APPDATA%\ReplayBuffer` (installed) or the `Data` folder (portable).

## How it works

- `capture.exe` (C++): grabs the game window with Windows.Graphics.Capture, or the whole monitor with Desktop Duplication. It converts and scales on the GPU and encodes H.264 with the GPU's hardware encoder through Media Foundation. Frames never go through the CPU. It runs at high GPU scheduling priority so a GPU-bound game can't starve it, and if it ever falls behind it drops frames instead of slowing time.
- `audio.exe` (C++): one per track. WASAPI device capture, loopback for outputs, or per-process capture for game and app tracks, at 48 kHz stereo.
- Video and audio are stamped against the same high-resolution clock, so tracks stay in sync (measured within about 20 ms).
- `ffmpeg.exe` only packages the streams. The app keeps the stream in 2-second keyframe-aligned chunks in RAM (or temp files). Saving pipes the last N seconds back through FFmpeg into an MP4 with no re-encoding of video, so saves take about a second.
- `ReplayBuffer.exe` (C# / WPF, .NET 9): the window, tray, global hotkey, game detection and settings. Its UI is software-rendered, so it never competes with the game for the GPU.

## Building from source

Needs Visual Studio 2022 (Desktop development with C++) and the .NET 9 SDK.

```bat
build.bat
```

This builds the helpers and a self-contained `ReplayBuffer.exe` into `dist\ReplayBuffer`, and downloads FFmpeg (gyan.dev release essentials) if it's missing.

```powershell
powershell -ExecutionPolicy Bypass -File package.ps1
```

This builds everything and writes `release\ReplayBuffer-Setup-<ver>.exe` and `release\ReplayBuffer-Portable-<ver>.zip`. The installer (`installer\Setup.cs`) is compiled with the .NET Framework C# compiler that ships with Windows, so no installer tooling is required.

## Credits & licenses

- Made by [Jebula999](https://github.com/Jebula999): designed, directed and tested by Jebula999, code written by Claude (Anthropic) via Claude Code.
- Bundles [FFmpeg](https://ffmpeg.org) (GPLv3 build by [gyan.dev](https://www.gyan.dev/ffmpeg/builds/)). See `THIRD-PARTY-NOTICES.txt`.
