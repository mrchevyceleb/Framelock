# Restores the pinned FFmpeg runtime into third_party/ffmpeg from the latest Framelock release.
# Every release attaches FFmpeg-runtime-win-x64.zip, and the next build restores it from there, so builds keep working
# after BtbN prunes the daily build that tools/get-ffmpeg.ps1 downloads. Each file must match
# third_party/ffmpeg/SHA256SUMS.txt; if the asset is missing or doesn't match, this falls back to tools/get-ffmpeg.ps1.
# Uses the GitHub CLI when it is installed (CI sets GH_TOKEN), otherwise the public download link.
param([string]$Repository = 'mrchevyceleb/Framelock')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Force .dev, third_party/ffmpeg | Out-Null
    $zip = '.dev/FFmpeg-runtime-win-x64.zip'
    $manifest = @(foreach ($line in Get-Content third_party/ffmpeg/SHA256SUMS.txt) {
        if ($line -notmatch '^([a-f0-9]{64})  ([^/\\]+)$') { throw 'Invalid FFmpeg checksum manifest.' }
        [pscustomobject]@{ Hash = $Matches[1]; Name = $Matches[2] }
    })
    # Any download, archive or checksum problem falls back to the pinned download instead of breaking the build.
    $files = @()
    try {
        if (Get-Command gh -ErrorAction SilentlyContinue) {
            gh release download --repo $Repository --pattern FFmpeg-runtime-win-x64.zip --dir .dev --clobber
            if ($LASTEXITCODE) { throw 'No FFmpeg runtime in the latest release.' }
        }
        else {
            Invoke-WebRequest "https://github.com/$Repository/releases/latest/download/FFmpeg-runtime-win-x64.zip" -OutFile $zip
        }
        Remove-Item .dev/previous-ffmpeg -Recurse -Force -ErrorAction SilentlyContinue
        Expand-Archive $zip .dev/previous-ffmpeg -Force
        foreach ($entry in $manifest) {
            $path = Join-Path '.dev/previous-ffmpeg' $entry.Name
            if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Hash) {
                throw "The release copy of $($entry.Name) doesn't match the pinned runtime."
            }
            $files += $path
        }
    }
    catch {
        Write-Host "Could not restore FFmpeg from the latest release: $($_.Exception.Message)"
        $files = @()
    }
    if ($files.Count) { Copy-Item -LiteralPath $files -Destination third_party/ffmpeg -Force; Write-Host 'Restored the pinned FFmpeg runtime from the latest release.' }
    else { & (Join-Path $PSScriptRoot 'get-ffmpeg.ps1') }
    # The release download can fail (for example before the first release); its fallback succeeded.
    $global:LASTEXITCODE = 0
}
finally { Pop-Location }
