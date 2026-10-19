# A release only ever moves the update feed forward. Refuses a commit that is already released, or one that isn't built
# on top of the latest release (a re-run of an old job, for example), so installed copies never update to older code.
param(
    [Parameter(Mandatory)][string]$Commit,
    [string]$Repository = $(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'mrchevyceleb/Framelock' })
)
$ErrorActionPreference = 'Stop'
$latest = gh api "repos/$Repository/releases/latest" --jq .tag_name 2>$null
if ($LASTEXITCODE) {
    # Only "no stable release at all" is fine; any other failure stops the release.
    $stable = gh api --paginate "repos/$Repository/releases?per_page=100" --jq '.[] | select((.draft or .prerelease) | not) | .tag_name'
    if ($LASTEXITCODE -or $stable) { throw 'Could not read the latest release.' }
    $global:LASTEXITCODE = 0
    return
}
$target = gh api "repos/$Repository/git/ref/tags/$latest" | ConvertFrom-Json
if ($LASTEXITCODE) { throw "Could not read the $latest tag." }
$target = $target.object
while ($target.type -eq 'tag') { $target = (gh api "repos/$Repository/git/tags/$($target.sha)" | ConvertFrom-Json).object }
if ($target.type -ne 'commit') { throw "$latest does not point to a commit." }
if ($target.sha -eq $Commit) { throw "$($Commit.Substring(0, 7)) is already released as $latest." }
$status = gh api "repos/$Repository/compare/$($target.sha)...$Commit" --jq .status
if ($LASTEXITCODE) { throw 'Could not compare this commit with the latest release.' }
if ($status -ne 'ahead') { throw "$($Commit.Substring(0, 7)) is not newer than $latest ($status). Push a new commit to master to release." }
Write-Host "$($Commit.Substring(0, 7)) is ahead of $latest."
