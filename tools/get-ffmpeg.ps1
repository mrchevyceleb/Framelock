# Downloads the FFmpeg 9.0 shared libraries Framelock links against (BtbN GPL build) into third_party/ffmpeg.
# The DLLs are too large for git (avcodec is ~120 MB), so they are fetched instead of committed.
$ErrorActionPreference = 'Stop'
$url  = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip'
$dest = Join-Path $PSScriptRoot '..\third_party\ffmpeg'
$tmp  = Join-Path ([IO.Path]::GetTempPath()) 'framelock-ffmpeg'
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $tmp, $dest | Out-Null
Write-Host "Downloading $url"
Invoke-WebRequest $url -OutFile "$tmp\ffmpeg.zip"
Expand-Archive "$tmp\ffmpeg.zip" $tmp
$root = Get-ChildItem $tmp -Directory | Select-Object -First 1
Copy-Item "$($root.FullName)\bin\*.dll", "$($root.FullName)\bin\ffprobe.exe", "$($root.FullName)\LICENSE.txt" $dest -Force
Remove-Item $tmp -Recurse -Force
Write-Host "FFmpeg installed to $((Resolve-Path $dest).Path)"
