# Downloads the FFmpeg 9.0 shared libraries Framelock links against (BtbN GPL build) into third_party/ffmpeg.
# The DLLs are too large for git (avcodec is ~120 MB), so they are fetched instead of committed.
# The build is pinned and verified by SHA-256. BtbN prunes old daily builds; if the download 404s, pick a newer
# n9.0 "win64-gpl-shared-9.0" asset from https://github.com/BtbN/FFmpeg-Builds/releases and pass -Url/-Sha256
# (the release page lists each asset's sha256 digest).
param(
    [string]$Url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-25-15-37/ffmpeg-n9.0.2-8-gb135b25c19-win64-gpl-shared-9.0.zip',
    [string]$Sha256 = 'aa74448a7183ff18bfddecd45664ec4ec9418b48f7a9a77a6a6ee03ba2d88ef2'
)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot '..\third_party\ffmpeg'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tmp = [IO.Path]::GetFullPath((Join-Path $tempRoot ('framelock-ffmpeg-' + [Guid]::NewGuid().ToString('N'))))
if (!$tmp.StartsWith($tempRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'FFmpeg temporary directory is outside the expected workspace.'
}
New-Item -ItemType Directory -Force $tmp, $dest | Out-Null
try {
Write-Host "Downloading $Url"
Invoke-WebRequest $Url -OutFile "$tmp\ffmpeg.zip"
$actual = (Get-FileHash "$tmp\ffmpeg.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $Sha256.ToLowerInvariant()) {
    throw "SHA-256 mismatch for the FFmpeg download (expected $Sha256, got $actual). Not installing it."
}
Expand-Archive "$tmp\ffmpeg.zip" $tmp
$root = Get-ChildItem $tmp -Directory | Select-Object -First 1
Copy-Item "$($root.FullName)\bin\*.dll", "$($root.FullName)\bin\ffprobe.exe", "$($root.FullName)\LICENSE.txt" $dest -Force
Write-Host "FFmpeg installed to $((Resolve-Path $dest).Path)"
}
finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
