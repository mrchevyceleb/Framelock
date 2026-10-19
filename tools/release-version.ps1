# Picks the version for the next automatic release: one patch above the highest version already released (drafts
# included) or tagged. The <Version> in Framelock.csproj is the minimum, so raise it to start a new minor or major series.
# Writes version= and tag= to GITHUB_OUTPUT in GitHub Actions. Needs the GitHub CLI.
param([string]$Repository = $(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'mrchevyceleb/Framelock' }))
$ErrorActionPreference = 'Stop'
$csproj = Join-Path $PSScriptRoot '../src/Framelock/Framelock.csproj'
if ((Get-Content -Raw -LiteralPath $csproj) -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') {
    throw 'Framelock.csproj needs a stable <Version>MAJOR.MINOR.PATCH</Version>.'
}
$minimum = [version]$Matches[1]
$names = @(gh api --paginate "repos/$Repository/releases?per_page=100" --jq '.[].tag_name')
if ($LASTEXITCODE) { throw 'Could not list existing releases.' }
$names += @(gh api --paginate "repos/$Repository/tags?per_page=100" --jq '.[].name')
if ($LASTEXITCODE) { throw 'Could not list existing tags.' }
$highest = $names | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } | ForEach-Object { [version]$_.Substring(1) } |
    Sort-Object -Descending | Select-Object -First 1
$version = if ($highest -and $highest -ge $minimum) { '{0}.{1}.{2}' -f $highest.Major, $highest.Minor, ($highest.Build + 1) }
    else { $minimum.ToString(3) }
Write-Host "Next release: v$version (highest existing: $(if ($highest) { "v$highest" } else { 'none' }), csproj minimum: $minimum)"
if ($env:GITHUB_OUTPUT) { "version=$version", "tag=v$version" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
return $version
