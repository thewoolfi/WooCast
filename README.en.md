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

## Download

[**Ready-made builds**](https://github.com/thewoolfi/WooCast/releases) — a Windows installer
(the app, ffmpeg and the gamepad driver; no administrator rights needed) and an APK for a
set-top box, TV or phone.
## Features

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
