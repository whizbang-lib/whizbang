#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Decides the "Gate - Pull request type" check: the title is a Conventional Commit, a breaking change
    records an upgrade note, and a changelog entry is written where the release cut gathers it.

.DESCRIPTION
    The title becomes the squash commit, so it carries the change's type (CONTRIBUTING.md, "Commit
    Messages"): type(scope)?!?: description, the type one of feat, fix, docs, perf, refactor, test, ci,
    build, chore or revert. A `!` marks a breaking change, which must add a changelog entry with an
    **Upgrade:** note telling a consumer what to do.

    Changelog entries (#2168): a pull request into develop writes its entry as a fragment,
    changelog.d/<issue>.md, which Start Release gathers into the version's section; one that adds lines under
    [Unreleased] in CHANGELOG.md is refused, so two pull requests never conflict over that file. The sync back
    from main (sync/*) is exempt. A pull request into a release branch edits CHANGELOG.md's section for that
    version instead, and a fragment there is refused: the cut that gathers fragments has already run.

    Exempt, and reported as exempt: pull requests opened by a bot, and the release pull request into main.

    The pull request is read from the API at run time, so retitling it and re-running this job is enough.
    In the merge queue the pull request number is read from the queue branch.

    Exit 0 on pass, 1 on fail.

.PARAMETER Repository
    owner/name.

.PARAMETER PullRequest
    The pull request number (pull request runs).

.PARAMETER HeadRef
    The queue branch (merge queue runs).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Repository,
  [int]$PullRequest = 0,
  [string]$HeadRef = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The queue-branch reading lives with the linked-issue gate; dot-sourcing loads it without running that gate.
# Dot-sourcing also runs its param() block in this scope, so pass this script's own values through, or its
# defaults (PullRequest 0, HeadRef '') replace them.
. (Join-Path $PSScriptRoot 'Test-LinkedIssue.ps1') -Repository $Repository -PullRequest $PullRequest -HeadRef $HeadRef

$script:ConventionalTypes = @('feat', 'fix', 'docs', 'perf', 'refactor', 'test', 'ci', 'build', 'chore', 'revert')
$script:TitlePattern = '^(?<type>[a-z]+)(?:\((?<scope>[a-z0-9][a-z0-9._/-]*)\))?(?<breaking>!)?: \S'

function Get-PatchAddedLines {
  <# .SYNOPSIS The lines a unified-diff patch adds, without the leading plus. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Patch)
  foreach ($line in $Patch -split "`n") {
    if ($line.StartsWith('+') -and -not $line.StartsWith('+++')) { $line.Substring(1) }
  }
}

function Test-ChangelogFragmentPath {
  <# .SYNOPSIS Whether a repository path is a changelog fragment (changelog.d/*.md other than the README). #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Path)
  return ($Path -match '^changelog\.d/[^/]+\.md$') -and ($Path -ne 'changelog.d/README.md')
}

function Get-UnreleasedAddedLines {
  <#
  .SYNOPSIS
      The non-blank lines a CHANGELOG.md patch adds under ## [Unreleased], given the file as the pull request
      leaves it (each hunk header gives the new line numbers; the head file says where [Unreleased] ends).
  #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Patch, [Parameter(Mandatory)][AllowEmptyString()][string]$Content)
  $lines = @($Content -split "\r?\n")
  $start = -1
  for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## \[Unreleased\]') { $start = $i + 1; break } }
  if ($start -lt 0) { return }
  $end = $lines.Count + 1
  for ($i = $start; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { $end = $i + 1; break } }
  # 1-based: [Unreleased] holds the lines after its heading ($start) and before the next one ($end).
  $number = 0
  foreach ($line in $Patch -split "\n") {
    $hunk = [regex]::Match($line, '^@@ -\d+(?:,\d+)? \+(?<new>\d+)')
    if ($hunk.Success) { $number = [int]$hunk.Groups['new'].Value; continue }
    if ($line.StartsWith('-') -or $line.StartsWith('\')) { continue }
    if ($line.StartsWith('+')) {
      $text = $line.Substring(1)
      if ($number -gt $start -and $number -lt $end -and $text.Trim()) { $text }
    }
    $number++
  }
}

function Get-PullRequestTypeVerdict {
  <# .SYNOPSIS The decision, with no I/O: Pass, Exempt, Type, Breaking, Reason and the problems found. #>
  param(
    [AllowEmptyString()][string]$Title = '',
    [bool]$AuthorIsBot = $false,
    [Parameter(Mandatory)][string]$BaseRef,
    [Parameter(Mandatory)][string]$HeadRef,
    # Lines the pull request adds to CHANGELOG.md, and of them those under [Unreleased].
    [AllowEmptyCollection()][string[]]$ChangelogAdded = @(),
    [AllowEmptyCollection()][string[]]$UnreleasedAdded = @(),
    # The changelog fragments the pull request adds or changes, and the lines it adds to them.
    [AllowEmptyCollection()][string[]]$FragmentPaths = @(),
    [AllowEmptyCollection()][string[]]$FragmentAdded = @()
  )
  if ($AuthorIsBot) {
    return [pscustomobject]@{ Pass = $true; Exempt = $true; Type = ''; Breaking = $false; Reason = 'opened by a bot'; Problems = @() }
  }
  if ($BaseRef -eq 'main' -and $HeadRef -like 'release/*') {
    return [pscustomobject]@{ Pass = $true; Exempt = $true; Type = ''; Breaking = $false; Reason = 'the release pull request into main'; Problems = @() }
  }
  $allowed = $script:ConventionalTypes -join ', '
  $match = [regex]::Match($Title, $script:TitlePattern)
  if (-not $match.Success -or $match.Groups['type'].Value -notin $script:ConventionalTypes) {
    $problem = "The title '$Title' is not a Conventional Commit. Write it as type(scope): description, the type one of " +
      "$allowed, with ! before the colon for a breaking change (CONTRIBUTING.md, ""Commit Messages""). It becomes " +
      'the commit message. Retitle the pull request and re-run this job.'
    return [pscustomobject]@{ Pass = $false; Exempt = $false; Type = ''; Breaking = $false; Reason = 'title'; Problems = @($problem) }
  }
  $type = $match.Groups['type'].Value
  $breaking = $match.Groups['breaking'].Success
  $intoRelease = $BaseRef -like 'release/*'
  $problems = [System.Collections.Generic.List[string]]::new()
  $reasons = [System.Collections.Generic.List[string]]::new()
  if ($BaseRef -eq 'develop' -and $HeadRef -notlike 'sync/*' -and $UnreleasedAdded.Count -gt 0) {
    $problems.Add('This pull request adds lines under [Unreleased] in CHANGELOG.md. A pull request into develop writes its ' +
      'entry as a changelog fragment instead, changelog.d/<issue>.md (changelog.d/README.md), which cutting a release ' +
      'gathers into the version''s section; two pull requests then never conflict over CHANGELOG.md. Move the entry ' +
      'into a fragment and re-run this job.')
    $reasons.Add('changelog')
  }
  if ($intoRelease -and $FragmentPaths.Count -gt 0) {
    $problems.Add("This pull request into $BaseRef adds $($FragmentPaths -join ', '). The release was cut, so no gather " +
      "would put it in this release: write the entry in CHANGELOG.md's section for this version instead.")
    $reasons.Add('changelog')
  }
  $notes = if ($intoRelease) { $ChangelogAdded } else { @($FragmentAdded) + @($ChangelogAdded) }
  if ($breaking -and -not ($notes | Where-Object { $_ -match 'Upgrade' })) {
    $where = if ($intoRelease) { "an entry in CHANGELOG.md's section for this version" } else { 'a changelog fragment, changelog.d/<issue>.md,' }
    $problems.Add("This is a breaking change (! in the title), so it adds $where with an **Upgrade:** note saying what a " +
      'consumer has to change.')
    $reasons.Add('no upgrade note')
  }
  if ($problems.Count -gt 0) {
    return [pscustomobject]@{ Pass = $false; Exempt = $false; Type = $type; Breaking = $breaking; Reason = ($reasons | Select-Object -Unique) -join ', '; Problems = $problems.ToArray() }
  }
  return [pscustomobject]@{ Pass = $true; Exempt = $false; Type = $type; Breaking = $breaking; Reason = $type + $(if ($breaking) { ' (breaking)' } else { '' }); Problems = @() }
}

# Dot-sourcing for tests loads the functions without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$number = if ($PullRequest -gt 0) { $PullRequest } else { Get-QueuedPullRequestNumber -HeadRef $HeadRef }
if ($number -le 0) {
  Write-Output "::error::No pull request to check: pass -PullRequest, or run in the merge queue (head ref '$HeadRef')."
  exit 1
}

$pr = gh api "repos/$Repository/pulls/$number" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { Write-Output "::error::Reading pull request #$number failed (gh exit $LASTEXITCODE)."; exit 1 }
$files = @(gh api --paginate "repos/$Repository/pulls/$number/files" --jq '.[] | {filename, status, patch: (.patch // "")} | @json' | ForEach-Object { $_ | ConvertFrom-Json })
if ($LASTEXITCODE -ne 0) { Write-Output "::error::Reading pull request #$number's files failed (gh exit $LASTEXITCODE)."; exit 1 }
$changelog = @($files | Where-Object { $_.filename -eq 'CHANGELOG.md' -and $_.status -ne 'removed' })
$fragments = @($files | Where-Object { $_.status -ne 'removed' -and (Test-ChangelogFragmentPath -Path $_.filename) })
$changelogAdded = @($changelog | ForEach-Object { Get-PatchAddedLines -Patch $_.patch })
$fragmentAdded = @($fragments | ForEach-Object { Get-PatchAddedLines -Patch $_.patch })
$unreleasedAdded = @()
if ($changelog.Count -gt 0 -and [string]$pr.base.ref -eq 'develop') {
  # Where [Unreleased] ends is read from the file as the pull request leaves it.
  $content = gh api "repos/$Repository/contents/CHANGELOG.md?ref=$($pr.head.sha)" -H 'Accept: application/vnd.github.raw'
  if ($LASTEXITCODE -ne 0) { Write-Output "::error::Reading CHANGELOG.md at $($pr.head.sha) failed (gh exit $LASTEXITCODE)."; exit 1 }
  $unreleasedAdded = @(Get-UnreleasedAddedLines -Patch $changelog[0].patch -Content ($content -join "`n"))
}

$verdict = Get-PullRequestTypeVerdict -Title ([string]$pr.title) -AuthorIsBot ($pr.user.type -eq 'Bot') `
  -BaseRef ([string]$pr.base.ref) -HeadRef ([string]$pr.head.ref) -ChangelogAdded $changelogAdded `
  -UnreleasedAdded $unreleasedAdded -FragmentPaths @($fragments | ForEach-Object filename) -FragmentAdded $fragmentAdded
$summary = if ($verdict.Exempt) { "Pull request type: exempt, $($verdict.Reason)." }
  elseif ($verdict.Pass) { "Pull request type: $($verdict.Reason)." }
  else { "Pull request type: failed, $($verdict.Reason)." }
if ($env:GITHUB_STEP_SUMMARY) {
  $summary | Add-Content -Path $env:GITHUB_STEP_SUMMARY
  $verdict.Problems | ForEach-Object { "- $_" } | Add-Content -Path $env:GITHUB_STEP_SUMMARY
}
Write-Output $summary
if (-not $verdict.Pass) {
  $verdict.Problems | ForEach-Object { Write-Output "::error::$_" }
  exit 1
}
exit 0
