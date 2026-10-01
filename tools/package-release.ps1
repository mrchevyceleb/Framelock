# Builds a self-contained Windows installer, portable app and automatic-update feed.
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$ReleaseNotes
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (!$dotnet -or !(& $dotnet --list-sdks | Where-Object { $_ -match '^10\.' })) {
    $dotnet = Join-Path $repoRoot '.dev/dotnet/dotnet.exe'
}
if (!(Test-Path -LiteralPath $dotnet)) { throw 'Install the .NET 10 SDK first.' }
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
$buildRoot = Join-Path $repoRoot ('.dev/release-build/' + [Guid]::NewGuid().ToString('N'))
$publishDir = Join-Path $buildRoot 'publish'
$releases = Join-Path $buildRoot 'releases'
New-Item -ItemType Directory -Force $publishDir, $releases | Out-Null

Push-Location $repoRoot
try {
    & $dotnet tool restore
    if ($LASTEXITCODE) { throw 'Could not restore the pinned packaging tool.' }
    if (!(Test-Path 'third_party/ffmpeg/avcodec-63.dll')) { & ./tools/get-ffmpeg.ps1 }
    $ffmpegFiles = @()
    foreach ($line in Get-Content third_party/ffmpeg/SHA256SUMS.txt) {
        if ($line -notmatch '^([a-f0-9]{64})  ([^/\\]+)$') { throw 'Invalid FFmpeg checksum manifest.' }
        $expected = $Matches[1]; $binaryPath = Join-Path 'third_party/ffmpeg' $Matches[2]
        if ((Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
            throw "Pinned FFmpeg checksum mismatch: $binaryPath"
        }
        $ffmpegFiles += (Resolve-Path -LiteralPath $binaryPath).Path
    }
    $actualFfmpegFiles = Get-ChildItem third_party/ffmpeg -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object FullName
    if (Compare-Object $ffmpegFiles $actualFfmpegFiles) { throw 'Unexpected or missing files in the pinned FFmpeg runtime.' }
    if (Get-ChildItem third_party/ffmpeg -Directory) { throw 'Unexpected directories in the pinned FFmpeg runtime.' }
    & $dotnet publish src/Framelock/Framelock.csproj -c Release -r win-x64 --self-contained -p:Version=$Version -o $publishDir -v quiet
    if ($LASTEXITCODE) { throw 'Release publish failed.' }
    # Include runtime license notices and the GPL license of the bundled FFmpeg build.
    Copy-Item -LiteralPath third_party/ffmpeg/LICENSE.txt -Destination (Join-Path $publishDir 'ffmpeg/LICENSE.txt')
    foreach ($notice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $noticePath = Join-Path $env:DOTNET_ROOT $notice
        if (Test-Path -LiteralPath $noticePath) { Copy-Item -LiteralPath $noticePath -Destination (Join-Path $publishDir ('DOTNET-' + $notice)) }
    }
    $packArgs = @('tool', 'run', 'vpk', '--', 'pack', '--packId', 'Framelock', '--packVersion', $Version,
        '--packTitle', 'Framelock', '--packAuthors', 'Matt Johnston', '--packDir', $publishDir,
        '--mainExe', 'Framelock.exe', '--runtime', 'win-x64', '--channel', 'win', '--outputDir', $releases,
        '--icon', 'src/Framelock/Assets/Framelock.ico')
    if ($ReleaseNotes) { $packArgs += @('--releaseNotes', (Resolve-Path -LiteralPath $ReleaseNotes).Path) }
    & $dotnet @packArgs
    if ($LASTEXITCODE) { throw 'Installer/update packaging failed.' }
    # Keep the pinned FFmpeg binary download available after upstream daily builds expire.
    # This asset is also used by future release builds; it is not an app update package.
    Compress-Archive -LiteralPath $ffmpegFiles -DestinationPath (Join-Path $releases 'FFmpeg-runtime-win-x64.zip')
    $sourceArchives = @{
        'FFmpeg-source.zip' = 'https://codeload.github.com/FFmpeg/FFmpeg/zip/b135b25c19b56f909b1d529dedfffbb14ddf170d'
        'FFmpeg-build-scripts.zip' = 'https://codeload.github.com/BtbN/FFmpeg-Builds/zip/58cc05f33c20e3ead0ce876b72531ab482d0f981'
    }
    foreach ($archive in $sourceArchives.GetEnumerator()) {
        Invoke-WebRequest -Uri $archive.Value -OutFile (Join-Path $releases $archive.Key)
    }
    Copy-Item -LiteralPath LICENSE.txt, THIRD-PARTY-NOTICES.md -Destination $releases
    $checksums = Get-ChildItem -LiteralPath $releases -File | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    [IO.File]::WriteAllLines((Join-Path $releases 'SHA256SUMS.txt'), $checksums)
    Write-Host "Release files: $releases"
    if ($env:GITHUB_OUTPUT) { "release_dir=$releases" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
    return $releases
}
finally { Pop-Location }
