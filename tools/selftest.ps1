# Builds Framelock (Release) and runs its built-in self-test: records the display for a few seconds, pauses, saves an
# instant replay, remuxes and checks the output with ffprobe. Used by the Build check and before every release.
# GitHub runners have a desktop session (1024x768, software D3D11) but no GPU or microphone, so this records with the
# CPU encoder; a missing microphone is reported but doesn't fail the self-test. Needs the FFmpeg runtime restored first.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
dotnet build (Join-Path $repoRoot 'src/Framelock/Framelock.csproj') -c Release
if ($LASTEXITCODE) { throw 'Build failed.' }
$exe = (Get-ChildItem (Join-Path $repoRoot 'src/Framelock/bin/Release') -Recurse -Filter Framelock.exe | Select-Object -First 1).FullName
if (!$exe) { throw 'Framelock.exe was not built.' }
$logs = Join-Path $env:APPDATA 'Framelock/logs'
$p = [Diagnostics.Process]::Start($exe, [string[]]@('--selftest', 'seconds=4', 'fps=30', 'w=1280', 'h=720', 'pause=1', 'replay=1'))
if (!$p.WaitForExit(120000)) { $p.Kill(); $code = 'timeout' } else { $code = $p.ExitCode }
if (Test-Path "$logs/selftest.txt") { Get-Content "$logs/selftest.txt" } else { Write-Host 'No self-test report was written.' }
if ($code -ne 0) {
    Get-ChildItem $logs -Filter 'framelock-*.log' | Sort-Object Name | Select-Object -Last 1 | Get-Content -Tail 60
    throw "Self-test failed ($code)."
}
