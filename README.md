# Framelock

Frame-exact screen and gameplay recorder for Windows 10 (2004+) / 11.

- Record a display, a single window, or any region at up to 120 fps.
- Output is locked to an exact size (4K, 1440p, 1080p, Shorts, or a custom size) with Fit, Fill or Stretch scaling.
- Uses the NVENC, AMD AMF or Quick Sync hardware encoders when they are available, and falls back to x264, x265 or SVT-AV1.
- Image and text overlays (logos, your @handle) that you drag into place on the live preview.
- Records desktop or game audio and your microphone, with separate tracks, a noise gate, push-to-talk and a sync offset.
- Instant replay buffer, markers that become chapters in the file (plus a YouTube chapter list), pause, and file splitting.
- Crash-safe recording: it writes MKV while recording, then remuxes to MP4.
- Global hotkeys, a tray icon and a floating HUD. Framelock keeps its own windows out of your recordings.

## Build

```powershell
tools\get-ffmpeg.ps1          # fetches the FFmpeg 9.0 shared libraries (too large for git)
cd src\Framelock
dotnet build -c Release       # or: dotnet publish -c Release -o ..\..\publish
```

This needs the .NET 10 SDK. The published folder is self-contained, so you can copy it anywhere and run `Framelock.exe`.

## Dev aids

- `Framelock.exe --selftest seconds=6 fps=120 w=3840 h=2160 encoder=auto pause=1 replay=1` runs a headless end-to-end recording and writes a report to `%APPDATA%\Framelock\logs\selftest.txt`.
- `Framelock.exe --uishot=<folder>` renders every settings tab to a PNG.
- `Framelock.exe --tray` starts hidden in the tray. This is the mode used by "Start with Windows".

Settings and logs are stored in `%APPDATA%\Framelock`.
