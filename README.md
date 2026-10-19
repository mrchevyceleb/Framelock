# Framelock

[![Build](https://github.com/mrchevyceleb/Framelock/actions/workflows/build.yml/badge.svg)](https://github.com/mrchevyceleb/Framelock/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/mrchevyceleb/Framelock)](https://github.com/mrchevyceleb/Framelock/releases/latest)
[![License: GPL-3.0-or-later](https://img.shields.io/badge/license-GPL--3.0--or--later-blue)](LICENSE.txt)

Frame-exact screen and gameplay recorder for Windows 10 (2004+) and Windows 11.

Framelock records your screen, a single window or any region at up to 120 fps and writes the video at the exact size you pick, like 4K, 1440p, 1080p or a vertical Shorts frame, ready to upload. It uses your GPU's hardware encoder, keeps your game and mic audio editable after recording, and is free and open source.

## Download

Download **Framelock-win-Setup.exe** from the [latest release](https://github.com/mrchevyceleb/Framelock/releases/latest) and run it. The installer includes .NET and FFmpeg, so there is nothing else to install. A portable ZIP (**Framelock-win-Portable.zip**) is also available.

The installer isn't code-signed yet, so Windows SmartScreen may warn the first time. Choose **More info → Run anyway**. Every release lists SHA-256 checksums for its files in `SHA256SUMS.txt`.

Installed and packaged portable copies check for updates at startup and every six hours. Updates download in the background and install when you next open Framelock. A running recording is never restarted for an update. **General → Check for updates** checks immediately.

## Features

- Record a display, a single window, or any region at up to 120 fps. The region picker has exact-pixel boxes (4K 3840×2160, 1440p, 1080p, 720p, Shorts), and the video matches the box, so nothing is scaled.
- Output is locked to an exact size (4K, 1440p, 1080p, Shorts, or a custom size) with Fit, Fill or Stretch scaling.
- Uses the NVENC, AMD AMF or Quick Sync hardware encoders when they are available, and falls back to x264, x265 or SVT-AV1.
- Image and text overlays (logos, your @handle) that you drag into place on the live preview.
- Choose **Webcam** in the source bar to record only your camera, with a selectable recording frame rate. Camera selection, resolution and mirroring are in **Overlays**.
- Choose **Add webcam** in **Overlays** to add a movable, resizable camera view to a display, window or region recording. Its capture frame rate is independent of the screen recording rate. The actual camera mode is shown when the requested resolution or rate is unavailable; output keeps the selected recording frame rate by repeating camera frames when needed.
- Records desktop or game audio and your microphone in one combined playback track, so sharing a video never depends on a player choosing the right audio track. Includes a noise gate, push-to-talk and a sync offset.
- **Keep separate game and mic audio for editing** saves a small audio-only companion (`video.mp4.audio.mka`) beside the video. **Fix audio** uses it automatically; keep both files for editing, and send/upload just the video for playback. Recordings, replays and audio remixes all use this layout. Older recordings with embedded separate tracks remain editable.
- **Recordings → Export for sharing** creates a copy of an older recording with only its combined audio track in the video. Video and audio are copied without re-encoding; the original remains intact.
- Instant replay buffer, markers that become chapters in the file (plus a YouTube chapter list), pause, and file splitting.
- Crash-safe recording: it writes MKV while recording, then remuxes to MP4.
- A Recordings tab lists your videos with thumbnails. **Fix audio** rebalances game and mic volume after recording: you hear the new balance live, then save it as a new file or replace the original. The video is copied untouched and only the mix track is rebuilt, so there is no quality loss.
- Global hotkeys, a tray icon and a floating HUD. Framelock keeps its own windows out of your recordings.
- Silent recording controls: start, stop, pause, resume, markers, replay saves and screenshots produce no sound effects.
- **Copy video** in the recording-finished notification copies the video file to your clipboard. Paste it into a folder or an app that accepts file attachments.

## System requirements

- Windows 10 version 2004 (build 19041) or newer, or Windows 11, 64-bit (x64).
- A DirectX 11 graphics card. NVIDIA (NVENC), AMD (AMF) and Intel (Quick Sync) hardware encoders are used when present; otherwise Framelock encodes on the CPU, which needs a fast processor for high resolutions and frame rates.
- About 600 MB of disk space for the app and its update package, plus room for your recordings.

Settings and logs are stored in `%APPDATA%\Framelock`. `Framelock.exe --tray` starts hidden in the tray, which is what "Start with Windows" uses.

## Build from source

You need Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and PowerShell 7.

```powershell
git clone https://github.com/mrchevyceleb/Framelock.git
cd Framelock
pwsh tools/get-ffmpeg.ps1          # fetches the pinned FFmpeg 9.0 libraries (too large for git)
dotnet build src/Framelock/Framelock.csproj -c Release
```

[CONTRIBUTING.md](CONTRIBUTING.md) covers running your build, the built-in self-test modes (`--selftest`, `--uishot`, `--regionshot`, `--remixcheck`) and what a pull request needs.

## How releases work

Every push to `master` ships. GitHub Actions builds the app on a clean Windows runner, packages it with [Velopack](https://velopack.io), and publishes a GitHub release one patch version above the last one (the `<Version>` in `src/Framelock/Framelock.csproj` is the minimum, so raising it starts a new minor or major series). Installed copies pick the release up through automatic updates. A push whose latest commit message contains `[skip release]` doesn't release.

Each release contains:

- `Framelock-win-Setup.exe`, the installer, and `Framelock-win-Portable.zip`, the portable app
- the Velopack update feed (`releases.win.json`, `RELEASES`, `assets.win.json` and the full `.nupkg` package)
- `FFmpeg-runtime-win-x64.zip`, the exact FFmpeg build inside the app, which the next release build reuses so builds keep working after the upstream download expires
- `FFmpeg-source.zip` and `FFmpeg-build-scripts.zip`, the matching FFmpeg source and build scripts
- `LICENSE.txt`, `THIRD-PARTY-NOTICES.md`, and `SHA256SUMS.txt` with a checksum for every other file

## Contributing

Issues and pull requests are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md), and please follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report security problems privately as described in [SECURITY.md](SECURITY.md).

## License

Framelock is free software under the [GNU General Public License, version 3 or later](LICENSE.txt). Copyright 2026 Matt Johnston.

Framelock ships FFmpeg 9.0 shared libraries from [BtbN's GPL build](https://github.com/BtbN/FFmpeg-Builds) (it includes GPL components such as x264 and x265), so the app as distributed is covered by the GPL. The FFmpeg license is installed beside the app in `ffmpeg/LICENSE.txt`, and every release includes the exact FFmpeg source and build scripts for the bundled build. Other bundled components (.NET, Velopack, NAudio, FFmpeg.AutoGen, Vortice.Windows and others) keep their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Framelock is an independent project and isn't affiliated with FFmpeg, OBS Studio or any game or hardware vendor.
