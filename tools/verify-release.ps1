# Checks a packaged release folder before it is published: every required file is present, SHA256SUMS.txt lists
# every other file exactly once with its current hash, and FFmpeg-runtime-win-x64.zip holds the pinned FFmpeg runtime
# that the next release build restores from (see tools/restore-ffmpeg.ps1).
param(
    [Parameter(Mandatory)][string]$ReleaseDir,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$files = @(Get-ChildItem -LiteralPath $ReleaseDir -File)
if (Get-ChildItem -LiteralPath $ReleaseDir -Directory) { throw 'Unexpected folders in the release.' }
$required = 'Framelock-win-Setup.exe', 'Framelock-win-Portable.zip', "Framelock-$Version-full.nupkg", 'releases.win.json',
    'RELEASES', 'assets.win.json',
    'FFmpeg-runtime-win-x64.zip', 'FFmpeg-source.zip', 'FFmpeg-build-scripts.zip', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'SHA256SUMS.txt'
foreach ($name in $required) { if (!($files | Where-Object Name -eq $name)) { throw "Missing release file: $name" } }

$listed = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $ReleaseDir 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([a-f0-9]{64})  ([^/\\]+)$') { throw "Invalid SHA256SUMS.txt line: $line" }
    if ($listed.ContainsKey($Matches[2])) { throw "Listed twice in SHA256SUMS.txt: $($Matches[2])" }
    $listed[$Matches[2]] = $Matches[1]
}
foreach ($file in $files | Where-Object Name -ne 'SHA256SUMS.txt') {
    if (!$listed.ContainsKey($file.Name)) { throw "Missing from SHA256SUMS.txt: $($file.Name)" }
    if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $listed[$file.Name]) {
        throw "SHA256SUMS.txt does not match $($file.Name)"
    }
    $listed.Remove($file.Name)
}
if ($listed.Count) { throw "SHA256SUMS.txt lists files that are not in the release: $($listed.Keys -join ', ')" }

$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $ReleaseDir 'FFmpeg-runtime-win-x64.zip'))
try {
    foreach ($line in Get-Content -LiteralPath (Join-Path $repoRoot 'third_party/ffmpeg/SHA256SUMS.txt')) {
        if ($line -notmatch '^([a-f0-9]{64})  ([^/\\]+)$') { throw 'Invalid FFmpeg checksum manifest.' }
        $expected = $Matches[1]; $name = $Matches[2]
        $entry = $zip.GetEntry($name)
        if (!$entry) { throw "FFmpeg-runtime-win-x64.zip is missing $name" }
        $stream = $entry.Open()
        try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($actual -ne $expected) { throw "FFmpeg-runtime-win-x64.zip has a different $name than the pinned runtime." }
    }
}
finally { $zip.Dispose() }
Write-Host "Release $Version verified: $($files.Count) files, checksums exact, FFmpeg runtime restorable."
