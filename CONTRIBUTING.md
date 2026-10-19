# Contributing to Framelock

Thanks for helping out. Bug reports, fixes and focused improvements are all welcome. For a bigger feature, open an issue first so we can agree on the shape before you spend time on it.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). Please report security problems privately as described in [SECURITY.md](SECURITY.md), not in a public issue.

## Build from source

Framelock is a Windows app (C#, .NET 10, WPF), so you build it on Windows.

You need:

- Windows 10 version 2004 (build 19041) or newer, or Windows 11, on an x64 PC
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) (`pwsh`) for the scripts in `tools/`
- Git, and optionally the [GitHub CLI](https://cli.github.com/)

```powershell
git clone https://github.com/mrchevyceleb/Framelock.git
cd Framelock
pwsh tools/get-ffmpeg.ps1
dotnet build src/Framelock/Framelock.csproj -c Release
```

`tools/get-ffmpeg.ps1` downloads the FFmpeg 9.0 shared libraries Framelock links against (a pinned BtbN GPL build, checked by SHA-256) into `third_party/ffmpeg`. They are too large for git, so they are fetched instead of committed. BtbN removes old daily builds after a while; if that download fails, run `pwsh tools/restore-ffmpeg.ps1` instead. It restores the exact same pinned files from the latest Framelock release.

The build lands in `src/Framelock/bin/Release/net10.0-windows10.0.26100.0/win-x64/`. Run `Framelock.exe` from there. The output is self-contained, so you can also copy the folder anywhere, or use `dotnet publish src/Framelock/Framelock.csproj -c Release -o publish` for a clean publish folder.

Settings and logs live in `%APPDATA%\Framelock`. A build from source uses the same settings as an installed copy.

## Self-test modes

Framelock has built-in check modes instead of a separate test project. They run the real app code, so use them to check your change on your own hardware. `Framelock.exe` is a Windows app and returns to the prompt straight away, so start it with `Start-Process -Wait` when you want to wait for the result:

```powershell
Start-Process .\Framelock.exe '--selftest seconds=6 fps=60 w=1920 h=1080' -Wait
Get-Content $env:APPDATA\Framelock\logs\selftest.txt
```

- `--selftest` records the primary display end to end, verifies the files with ffprobe and writes `%APPDATA%\Framelock\logs\selftest.txt` (it ends with `SELFTEST PASSED` or `SELFTEST FAILED`, and the exit code is 0 or 1). Options:
  - `seconds=6 fps=120 w=3840 h=2160` set the length, frame rate and output size; `native=1` uses the source size instead.
  - `encoder=auto` picks the best available encoder; name one to force it (`hevc_nvenc`, `h264_amf`, `h264_qsv`, `libx264`, `libx265`, `libsvtav1`, and so on).
  - `pause=1` pauses and resumes in the middle, `replay=1` also saves an instant replay, `crashsafe=0` records straight to MP4, `container=Mkv` keeps MKV.
  - `mic=0` and `desktop=0` turn off the microphone and desktop audio.
  - `source=region rx=100 ry=100 rw=1280 rh=720` records a region instead of the display.
  - `source=webcam fps=30` records only the webcam; `webcam=1 camfps=30` adds a webcam overlay to a screen recording. `camw=1280 camh=720 mirror=1 camera=<device-id>` choose the camera mode, mirroring and device.
  - `gpuload=2400` keeps the GPU at 100% in a separate process, like a game, while it records (more iterations = a slower game). `motion=0` skips the small flickering test window.
- `--uishot=<folder>` opens Framelock normally, renders every settings tab (and the overlay editor) to PNG files in the folder, then quits. Close any running Framelock first, and note that it shows your own saved settings.
- `--regionshot=<file.png>` renders the region picker's toolbar to a PNG without opening the full-screen picker. It can run beside a running Framelock.
- `--remixcheck=<video>` runs **Fix audio** on a copy of a recording, checks the new mix against the separate tracks and writes `%APPDATA%\Framelock\logs\remixcheck.txt`. It can run beside a running Framelock.
- `--tray` starts hidden in the tray, which is what "Start with Windows" uses.

## Pull requests

- Branch from `master` and open the pull request against `master`. Keep each pull request to one change.
- The **Build** check builds every pull request on a Windows runner and runs a short `--selftest` recording there with the CPU encoder. It has to pass.
- Runners have no GPU, camera or microphone, so that check can't cover hardware encoders, webcams or mic audio. Run the self-test modes that cover your change on your own PC, and say in the pull request what you ran and on what hardware (GPU and encoder). For UI changes, attach before and after `--uishot` images.
- Keep Framelock's look and behaviour unless changing them is the point of the pull request, and say so in the description when it is.
- Don't commit FFmpeg binaries, build output or anything from `.dev/`. `.gitignore` already skips them.
- Release notes are built from pull request titles, so write a title a user would understand, for example "Fix audio drift on 144 Hz displays".
- Contributions are licensed under the project's license, [GPL-3.0-or-later](LICENSE.txt).

## How releases work

Every push to `master` publishes a new release, so `master` must always be ready to ship, and merging a pull request releases it to every installed copy through automatic updates.

- The version is one patch above the latest release (1.1.5, then 1.1.6, and so on). To start a new minor or major series, raise `<Version>` in `src/Framelock/Framelock.csproj`; it is the minimum the next release can use.
- To push to `master` without releasing, put `[skip release]` in the latest commit message of the push (when merging a pull request, in the merge or squash commit message). The check matches anywhere in that message, including its body.
- Releases build one at a time. Pushes made while a release is building ship together in the next release. A commit that is already released, or older than the latest release (a re-run of an old job, for example), is never published.
- Only the small jobs that pick the version and publish can write to the repository, and neither restores packages. The build, which restores NuGet packages and runs the recorder, can only read.
- Maintainers can build any branch without releasing it: **Actions → Windows release → Run workflow**, pick the branch and untick **publish**, or run `gh workflow run release.yml --ref <branch> -f publish=false`. The installer, portable ZIP and update packages are attached to the run as an artifact. Running it on `master` with **publish** ticked publishes a release by hand.
- To package locally, run `pwsh tools/package-release.ps1 -Version 1.2.3`, then `pwsh tools/verify-release.ps1 -ReleaseDir <folder it prints> -Version 1.2.3`.

Each release attaches `FFmpeg-runtime-win-x64.zip`, the pinned FFmpeg runtime. The next release build restores FFmpeg from it, which keeps builds working after the upstream download expires, so the release workflow checks it on every run and it must stay in every release. If you intentionally update FFmpeg, update `tools/get-ffmpeg.ps1`, `third_party/ffmpeg/SHA256SUMS.txt`, the source archive commits in `tools/package-release.ps1` and `THIRD-PARTY-NOTICES.md` together.
