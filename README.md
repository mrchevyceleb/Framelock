# Framelock

Frame-exact screen and gameplay recorder for Windows 10 (2004+) / 11.

## Download

Download **Framelock-win-Setup.exe** from the [latest GitHub release](https://github.com/mrchevyceleb/Framelock/releases/latest) and run it. The installer includes .NET and FFmpeg; no separate runtime installation is needed. A portable ZIP is also available.

Installed and packaged portable copies check for updates at startup and every six hours. Updates download in the background and install when you next open Framelock. A running recording is never restarted for an update. **General → Check for updates** checks immediately.

## Features

- Record a display, a single window, or any region at up to 120 fps. The region picker has exact-pixel boxes (4K 3840×2160, 1440p, 1080p, 720p, Shorts), and the video matches the box, so nothing is scaled.
- Output is locked to an exact size (4K, 1440p, 1080p, Shorts, or a custom size) with Fit, Fill or Stretch scaling.
- Uses the NVENC, AMD AMF or Quick Sync hardware encoders when they are available, and falls back to x264, x265 or SVT-AV1.
- Image and text overlays (logos, your @handle) that you drag into place on the live preview.
- Choose **Webcam** in the source bar to record only your camera, with a selectable recording frame rate. Camera selection, resolution and mirroring are in **Overlays**.
- Choose **Add webcam** in **Overlays** to add a movable, resizable camera view to a display, window or region recording. Its capture frame rate is independent of the screen recording rate. The actual camera mode is shown when the requested resolution or rate is unavailable; output keeps the selected recording frame rate by repeating camera frames when needed.
- Records desktop or game audio and your microphone, with separate tracks, a noise gate, push-to-talk and a sync offset.
- Instant replay buffer, markers that become chapters in the file (plus a YouTube chapter list), pause, and file splitting.
- Crash-safe recording: it writes MKV while recording, then remuxes to MP4.
- A Recordings tab lists your videos with thumbnails. **Fix audio** rebalances game and mic volume after recording: you hear the new balance live, then save it as a new file or replace the original. The video is copied untouched and only the mix track is rebuilt, so there is no quality loss.
- Global hotkeys, a tray icon and a floating HUD. Framelock keeps its own windows out of your recordings.

## Build

```powershell
tools\get-ffmpeg.ps1          # fetches the FFmpeg 9.0 shared libraries (too large for git)
cd src\Framelock
dotnet build -c Release       # or: dotnet publish -c Release -o ..\..\publish
```

This needs the .NET 10 SDK. The published folder is self-contained, so you can copy it anywhere and run `Framelock.exe`.

## Dev aids

- `Framelock.exe --selftest seconds=6 fps=120 w=3840 h=2160 encoder=auto pause=1 replay=1` runs a headless end-to-end recording and writes a report to `%APPDATA%\Framelock\logs\selftest.txt`. Add `gpuload=2400` to run it while a separate process keeps the GPU at 100% like a game (more iterations = a slower game), and `motion=0` to skip the small moving test window.
- Add `source=webcam fps=30` for a webcam-only selftest, or `webcam=1 camfps=30` for a screen recording with a webcam overlay. `fps` sets the recording rate; `camfps` only sets the overlay capture rate. Optional `camw=1280 camh=720 mirror=1 camera=<device-id>` selects the camera mode, mirroring and device.
- `Framelock.exe --uishot=<folder>` renders every settings tab to a PNG.
- `Framelock.exe --regionshot=<file.png>` renders the region picker's toolbar to a PNG without opening the full-screen picker.
- `Framelock.exe --remixcheck=<video>` runs Fix audio on a copy of a recording, checks the new mix against the separate tracks, and writes `%APPDATA%\Framelock\logs\remixcheck.txt`.
- `Framelock.exe --tray` starts hidden in the tray. This is the mode used by "Start with Windows".

Settings and logs are stored in `%APPDATA%\Framelock`.

## Releases

`tools/package-release.ps1 -Version 1.1.0` builds an installer, portable ZIP and update feed in an isolated `.dev/release-build` folder. The pinned Velopack tool is restored automatically. Push a version tag such as `v1.2.0` to build and publish the next release through GitHub Actions. Use stable `vMAJOR.MINOR.PATCH` tags. The workflow reuses the pinned FFmpeg runtime from the preceding release so upstream daily-build expiry does not break release builds.

## License

Framelock is open source under [GPL-3.0-or-later](LICENSE.txt). Bundled components keep their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md). FFmpeg source and build-script archives accompany each release.
