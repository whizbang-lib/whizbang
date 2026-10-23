#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Decides the "Gate - Pull request type" check: the title is a Conventional Commit, and a breaking
    change records an upgrade note.

.DESCRIPTION
    The title becomes the squash commit, so it carries the change's type (CONTRIBUTING.md, "Commit
    Messages"): type(scope)?!?: description, the type one of feat, fix, docs, perf, refactor, test, ci,
    build, chore or revert. A `!` marks a breaking change, which must add a CHANGELOG.md entry with an
    **Upgrade:** note telling a consumer what to do.

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

function Get-PullRequestTypeVerdict {
  <# .SYNOPSIS The decision, with no I/O: Pass, Exempt, Type, Breaking, Reason and the problems found. #>
  param(
    [AllowEmptyString()][string]$Title = '',
    [bool]$AuthorIsBot = $false,
    [Parameter(Mandatory)][string]$BaseRef,
    [Parameter(Mandatory)][string]$HeadRef,
    [AllowEmptyCollection()][string[]]$ChangelogAdded = @()
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
  if ($breaking -and -not ($ChangelogAdded | Where-Object { $_ -match 'Upgrade' })) {
    $problem = 'This is a breaking change (! in the title), so it adds a CHANGELOG.md entry under Unreleased with an ' +
      '**Upgrade:** note saying what a consumer has to change.'
    return [pscustomobject]@{ Pass = $false; Exempt = $false; Type = $type; Breaking = $true; Reason = 'no upgrade note'; Problems = @($problem) }
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
$files = @(gh api --paginate "repos/$Repository/pulls/$number/files" --jq '.[] | select(.filename == "CHANGELOG.md") | .patch // ""')
if ($LASTEXITCODE -ne 0) { Write-Output "::error::Reading pull request #$number's files failed (gh exit $LASTEXITCODE)."; exit 1 }
$changelogAdded = @(Get-PatchAddedLines -Patch ($files -join "`n"))

$verdict = Get-PullRequestTypeVerdict -Title ([string]$pr.title) -AuthorIsBot ($pr.user.type -eq 'Bot') `
  -BaseRef ([string]$pr.base.ref) -HeadRef ([string]$pr.head.ref) -ChangelogAdded $changelogAdded
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
