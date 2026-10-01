# Third-party software

Framelock is copyright 2026 Matt Johnston, licensed under GNU GPL version 3 or later. See LICENSE.txt.

The app includes FFmpeg shared libraries from BtbN's GPL build, version
`n9.0.2-8-gb135b25c19`. Their GPL license is included in `ffmpeg/LICENSE.txt`.

- FFmpeg source: https://github.com/FFmpeg/FFmpeg/tree/b135b25c19b56f909b1d529dedfffbb14ddf170d
- Build scripts: https://github.com/BtbN/FFmpeg-Builds/tree/58cc05f33c20e3ead0ce876b72531ab482d0f981
- Binary build: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-09-25-15-37
- Reproducible download and checksum: tools/get-ffmpeg.ps1 in the Framelock source repository.

Source archives are included alongside the GitHub release downloads.

Other included components retain their respective licenses:

- .NET and Windows desktop runtime: MIT, https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
- Velopack: MIT, https://github.com/velopack/velopack
- NAudio: MIT, https://github.com/naudio/NAudio
- FFmpeg.AutoGen: MIT, https://github.com/Ruslan-B/FFmpeg.AutoGen
- Vortice.Windows and Vortice.Mathematics: MIT, https://github.com/amerkoleci/Vortice.Windows
- SharpGenTools runtime: MIT, https://github.com/SharpGenTools/SharpGenTools
- Windows SDK .NET and CsWinRT runtime: MIT, https://github.com/microsoft/CsWinRT
