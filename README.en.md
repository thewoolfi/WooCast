<div align="center">

# WooCast

**Stream your screen or a single window from your PC to your TV — over the local network, with no cloud and no accounts.**

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-4C8DFF.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/PC-Windows%20%7C%20Linux%20(planned)-4C8DFF.svg)](#status)
[![Platform](https://img.shields.io/badge/TV-Android%20TV-3DDC84.svg)](#status)
[![Protocol](https://img.shields.io/badge/Protocol-WCP%2F1-8B5CF6.svg)](docs/protocol.md)


</div>

<div align="center">

[Русский](README.md) · **English**

</div>

---

## What it is

WooCast has two parts:

| Part | Where it runs | What it does |
|---|---|---|
| **WooCast Desktop** | Windows (Linux — planned) | Finds set-top boxes on the network on its own or connects by IP, captures the screen or a window, encodes it as H.264 and sends the stream |
| **WooCast TV** | Android TV / TV box | Receives the stream and decodes it in hardware, rendering it full screen |

No internet, no sign-up, no telemetry: everything travels directly between your devices
on the local network over our own protocol, [WCP/1](docs/protocol.md).

## Features

- 🔍 **Automatic discovery** — set-top boxes show up in the list by themselves, within a second of launch.
- ⌨️ **Manual connection** — type `192.168.1.50` or `192.168.1.50:47811` when broadcast is blocked on your network.
- 🪟 **Whole screen or a single window** — cast the entire monitor or just one application.
  The window can be dragged around: the capture follows it. Resolution adapts to the window size.
- ⚡ **Hardware encoding** — NVENC / Quick Sync / AMF, with automatic selection of a working
  encoder and a fallback to libx264.
- 🎛️ **Quality profiles** — from "maximum for a wired network" (0.32 bits per pixel) down to a lean one.
- 🩺 **Adaptive tuning** — if the set-top box can't keep up with decoding, the frame rate
  drops in steps (60 → 30 → 15); if the network can't keep up, the bitrate drops instead.
  The PC tells those two cases apart and reports exactly what it changed. Once the link recovers,
  the settings climb back to the configured values in the same steps — more slowly than they fell,
  so they don't start oscillating.
- 💾 **Settings are remembered** — the set-top box, source, window, resolution, quality
  and audio are restored the next time you launch the app.
- 🔊 **System audio** — captured through WASAPI loopback (no "Stereo Mix" required),
  AAC-LC at 160 kbps. The receiver keeps time by the audio and renders video against the audio
  timestamp, so the picture never drifts behind the sound.
- 📺 **Feedback from the set-top box** — the receiver reports the real capabilities of its decoder
  and the latency it measured; the PC displays both and trims its settings to fit those limits.
- 🎮 **Gamepad control** — the controller connects to the set-top box or a phone,
  while a virtual gamepad appears on the PC, and the game sees it as an ordinary one. Xbox 360
  and DualShock 4 are supported. The driver installer ships with the app, so there's nothing to hunt down.
- 🔄 **One-click updates** — the app learns about a new version on its own through GitHub
  Releases, downloads it, verifies the checksum and restarts itself updated.
  The previous build stays right there: if the new one won't start, you have something to fall back on.
- 📦 **Installer** — a single file sets up everything at once: the app, ffmpeg and the driver installer.
  No administrator rights needed, and shortcuts are created automatically.
- 🌙 **Minimal interface** — dark theme, one consistent look on PC and TV, nothing superfluous.
- 💚 **Support the project** — [Boosty](https://boosty.to/andrewwoolfi): there's a button both on the PC and on the set-top box.
- 🔓 **Apache 2.0** — the code is open, the dependencies are transparent.

## How it works

```
┌──────────────────────────┐                    ┌──────────────────────────┐
│   WooCast Desktop (PC)   │                    │  WooCast TV (TV box)     │
│                          │  UDP 47810         │                          │
│  Discovery ──────────────┼─── announce ──────►│  UDP listener            │
│  Control   ──────────────┼─── JSON Lines ────►│  TCP 47811               │
│  Media     ──────────────┼─── H.264/TCP ─────►│  TCP 47812 → MediaCodec  │
│  Capture: ddagrab        │                    │  Render: SurfaceView     │
│  Encode:  NVENC/QSV/AMF  │                    │  Decode: hardware        │
└──────────────────────────┘                    └──────────────────────────┘
```

More detail: [docs/protocol.md](docs/protocol.md) · [docs/architecture.md](docs/architecture.md) · [docs/roadmap.md](docs/roadmap.md)

## Verified in practice

End-to-end test "PC → Android TV" (Android TV 14 emulator, 720p30, wired network):

| Metric | Result |
|---|---|
| Frame rate | 30.0 fps exactly, 1018 consecutive frames with no drops |
| Bitrate | 2.7 Mbps against a target of 2.8 |
| End-to-end latency | **9–31 ms** (measured by the receiver and reported back to the PC) |
| Round-trip time | 3–7 ms |
| Send queue | 0 frames — the network is not the bottleneck |
| fps drift | no latency accumulation over 40 seconds of continuous operation |

<div align="center">
</div>

## Quick start

If you'd rather not build it yourself, ready-made builds live in `dist/`:
`WooCast-Desktop-<version>\WooCast.exe` (runs as is, no .NET required)
and `WooCast-TV-<version>.apk`. To rebuild them: `powershell -File tools\publish.ps1 -Android`.

### 1. Set-top box

Build the APK and install it on the box (or grab a ready one from Releases):

```powershell
cd android
.\gradlew.bat assembleDebug     # APK: android/app/build/outputs/apk/debug/app-debug.apk
adb install -r app\build\outputs\apk\debug\app-debug.apk
```

Launch WooCast TV — the screen shows the device name, IP and ports. You only need them
if automatic discovery doesn't work on your network.

### 2. PC

```powershell
git clone https://github.com/<your-account>/WooCast.git
cd WooCast

# ffmpeg is required for capture and encoding
pwsh -File tools/fetch-ffmpeg.ps1

# development environment: .NET, JDK, Android SDK, Gradle
. .\tools\dev-env.ps1

dotnet run --project src/WooCast.Desktop
```

In the app: pick the set-top box from the list (or enter an IP) → **Транслировать** (the "Start broadcast" button).

### Console alternative

For debugging without a GUI there's `woocast`:

```powershell
woocast probe                                   # check the environment and the encoders
woocast discover                                # find set-top boxes
woocast windows                                 # list windows with their ids
woocast send --to 192.168.1.50 --fps 60 --quality maximum
woocast send --to 192.168.1.50 --window-id 4    # cast a single window
woocast send --to 192.168.1.50 --window-id 4 --gdigrab   # force the capture fallback
woocast receive --dump stream.h264              # test receiver: exercise the protocol with no TV
woocast receive --simulate-fps 20               # "weak decoder" receiver — exercises the adaptation
woocast receive --caps 1280x720@30              # receiver with a limited decoder — exercises the trimming
woocast audio                                   # check system audio capture and the signal level
woocast send --to 192.168.1.50 --no-audio       # cast without audio
```

## Ready-made builds

`tools/publish.ps1` produces builds you can run straight away:

```powershell
powershell -File tools\publish.ps1              # WooCast.exe for Windows
powershell -File tools\publish.ps1 -Android     # plus the APK for the set-top box
powershell -File tools\publish.ps1 -WithFfmpeg  # self-contained build: ffmpeg next to the exe
```

**`WooCast.exe`** is self-contained: the .NET runtime is inside, no installation required.
It's about 100 MB, and a `README.txt` is placed next to it.

**ffmpeg** is not copied by default — it weighs about 160 MB, and the app finds an installed
copy without it anyway (see `FfmpegLocator`: the `WOOCAST_FFMPEG` variable, the `ffmpeg`
folder next to the exe, the installer cache, `PATH`). The `-WithFfmpeg` switch makes the build
fully self-contained.

The output lands in `dist/`: `WooCast-Desktop-<version>/` and `WooCast-TV-<version>.apk`.

## Build requirements

| Component | Version | Where from |
|---|---|---|
| .NET SDK | 10.0 | `tools/dev-env.ps1` prints the path |
| JDK | 17 | Adoptium |
| Android SDK | platform 37, build-tools 37 | cmdline-tools |
| Gradle | 9.8 (via the wrapper) | downloaded automatically |
| AGP / Kotlin | 9.4.1 / 2.4.20 | `android/gradle/libs.versions.toml` |
| ffmpeg | 6.0+ with `ddagrab` | `tools/fetch-ffmpeg.ps1` |

> **Paths with Cyrillic characters.** AGP refuses to build the project from a directory whose
> name contains non-ASCII characters (for example, "Рабочий стол"). The check is disabled in
> `android/gradle.properties` — building from such a path has been tested and works.
>
> **Cleanup.** `pwsh -File tools/clean.ps1 -Report` shows how much space the build artifacts,
> the NuGet cache and the toolchain take up; `-Build` (the default) removes the artifacts,
> `-Toolchain` removes the entire installed toolchain.

## Repository layout

```
WooCast/
├── src/
│   ├── WooCast.Protocol/     # WCP/1 protocol: discovery, control, media framing (net10.0)
│   ├── WooCast.Capture/      # Capture and encoding: ffmpeg, encoder selection, pipeline
│   ├── WooCast.Desktop/      # Avalonia UI (sender)
│   └── WooCast.Cli/          # Console sender and test receiver
├── android/
│   └── app/                  # WooCast TV: Kotlin, Compose for TV, MediaCodec
├── docs/                     # Protocol, architecture, roadmap, work journal
├── dist/                     # Ready-made builds: Windows exe and set-top box APK
├── tools/                    # Environment, publishing and emulator scripts
└── WooCast.slnx              # .NET 10 solution
```

## Status

Task-by-task progress lives in [docs/journal.md](docs/journal.md): what's done, what proves it,
what turned up along the way and what is still unverified.

| Component | State |
|---|---|
| WCP/1 protocol | ✅ Implemented and verified by an end-to-end test |
| WooCast Desktop (Windows) | ✅ Working: automatic discovery, manual IP, quality profiles, telemetry |
| WooCast TV (Android TV) | ✅ Working: installation, launch from the remote, hardware decoding |
| Single-window casting | ✅ Working: capture via Windows.Graphics.Capture, follows the window as it moves and resizes |
| Capturing occluded windows | ✅ Working: the window's own content is captured, not whatever covers it |
| Adaptive tuning | ✅ Works both ways: scaling down under load and returning to the configured settings |
| Settings persistence | ✅ Working: `%APPDATA%\WooCast\settings.json` |
| System audio | ✅ On the TV and the set-top box. ⚠️ Not yet on phones: the cause is being investigated |
| Gamepad control | ✅ The chain is verified: receiver → network → virtual gamepad → read back through XInput. ⏳ Not yet tested with a real controller on a phone |
| Updates | ✅ Version check and download with checksum verification. ⏳ The full install cycle on a real release has not been tested |
| Installer | ✅ Builds: a single file, no administrator rights. ⏳ Install and uninstall have not been tested hands-on |
| WooCast Desktop (Linux) | 📋 Planned |
| Session encryption | 📋 Planned (v1.2) |

**About the check marks.** ✅ means the work has been verified and confirmed; ⏳ means
it is done but has not been tested on a real device; 📋 means it is only planned.
The journal marks entries the same way and states what each one was confirmed by.

The current plan is in [docs/roadmap.md](docs/roadmap.md).

## About

**Version 0.1.0**

Author: **Andrew Woolfi** | Андрей Павлов

| | |
|---|---|
| Repository | https://github.com/thewoolfi/WooCast |
| Clone | `git clone https://github.com/thewoolfi/WooCast.git` |
| Issues and discussions | [Issues](https://github.com/thewoolfi/WooCast/issues) |
| Support the project | [Boosty](https://boosty.to/andrewwoolfi) |

The app updates itself: it asks GitHub about new releases of this repository,
downloads the build, verifies the checksum and restarts. There is no server behind
it — only GitHub releases.

## License

[Apache 2.0](LICENSE) © WooCast contributors

The project uses [FFmpeg](https://ffmpeg.org/) (as a separate executable, without linking),
[Avalonia UI](https://avaloniaui.net/) and [AndroidX / Compose](https://developer.android.com/jetpack).
