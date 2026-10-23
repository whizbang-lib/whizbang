#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Runs the deterministic review checks on a pull request and writes the review-checks comment.

.DESCRIPTION
    The checks (scripts/lib/ReviewFindings.psm1) read the pull request's added lines, its description
    and closing references, its target branch, and its commits' DCO sign-offs. The comment has a table
    for people and a hidden findings block that scripts/Invoke-PrReviewDraft.ps1 turns into a
    maintainer's draft review.

    Report only: the gates decide what merges. A line containing "review-checks:allow" is skipped, as
    is any path matching a glob in the ignore file.

.EXAMPLE
    pwsh .github/scripts/Get-ReviewChecks.ps1 -Base $BASE_SHA -Head $HEAD_SHA -BaseRef develop -HeadRef fix/x `
      -PullRequest 1409 -BodyFile body.md -ClosingIssueCount 0 -RunUrl $RUN_URL -OutFile comment.md
#>
param(
  [Parameter(Mandatory)][string]$Base,
  [Parameter(Mandatory)][string]$Head,
  [Parameter(Mandatory)][string]$BaseRef,
  [Parameter(Mandatory)][string]$HeadRef,
  [Parameter(Mandatory)][int]$PullRequest,
  [Parameter(Mandatory)][string]$BodyFile,
  [Parameter(Mandatory)][int]$ClosingIssueCount,
  [Parameter(Mandatory)][string]$RunUrl,
  [Parameter(Mandatory)][string]$OutFile,
  [string]$IgnoreFile = '.github/review-checks-ignore.txt',
  # A bot opened the pull request, so it is not asked for a linked issue (as the gate exempts it).
  [switch]$AuthorIsBot,
  # The pull request's title, whose Conventional Commit type decides what the change must contain.
  [string]$Title = '',
  # The labels of the issues the pull request closes (a fix closes one labeled bug).
  [string[]]$ClosingIssueLabels = @(),
  # The pull request's own labels, comma-separated (docs:not-needed).
  [string[]]$Labels = @(),
  # The docs-site pull requests found for it, as Get-DocsPullRequests.ps1 prints them.
  [string]$DocsPullRequestsJson = '[]'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..' '..'
Import-Module (Join-Path $repoRoot 'scripts' 'lib' 'ReviewFindings.psm1') -Force
# The DCO rules live in one place; dot-sourcing loads them without running the gate.
. (Join-Path $PSScriptRoot 'Test-DcoSignoff.ps1') -Base $Base -Head $Head

$ignore = @()
if (Test-Path -LiteralPath $IgnoreFile) {
  $ignore = @(Get-Content -LiteralPath $IgnoreFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
}

# Three dots: the changes since the branch left its base, which is what the pull request shows.
$diff = (git diff --no-color --unified=3 --find-renames "$Base...$Head" | Out-String)
if ($LASTEXITCODE -ne 0) { throw "git diff $Base...$Head failed (exit $LASTEXITCODE)" }

# An edited migration is read whole on both sides, so an edit that changes no executable SQL (a comment, a
# header, whitespace) is told from one that does. The old side is where the branch left its base, as the
# three-dot diff above compares.
$mergeBase = (git merge-base $Base $Head | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "git merge-base $Base $Head failed (exit $LASTEXITCODE)" }
$readMigrationVersions = {
  param($OldPath, $NewPath)
  $old = git show "${mergeBase}:$OldPath" 2>$null | Out-String
  $oldRead = $LASTEXITCODE -eq 0
  $new = git show "${Head}:$NewPath" 2>$null | Out-String
  if (-not $oldRead -or $LASTEXITCODE -ne 0) { return $null }
  [pscustomobject]@{ Old = $old; New = $new }
}.GetNewClosure()

$raw = git log --format="%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%B%x1e" "$Base..$Head"
if ($LASTEXITCODE -ne 0) { throw "git log $Base..$Head failed (exit $LASTEXITCODE)" }
$verdict = Get-DcoVerdict -Commits @(ConvertFrom-GitLog -Raw (($raw | Out-String))) -AdoptedOn $script:DcoAdoptedOn

$body = if (Test-Path -LiteralPath $BodyFile) { Get-Content -LiteralPath $BodyFile -Raw } else { '' }
if ($null -eq $body) { $body = '' }

# The workflow passes the labels as one comma-separated value.
$labels = @($ClosingIssueLabels | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$prLabels = @($Labels | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$docs = @(($DocsPullRequestsJson | ConvertFrom-Json) | Where-Object { $_ } |
    ForEach-Object { [pscustomobject]@{ Number = [int]$_.number; State = [string]$_.state; ReferencesThis = [bool]$_.referencesThis } })

$findings = @(
  ConvertTo-DcoFindings -Verdict $verdict -BaseRef $BaseRef
  Find-PullRequestFindings -Body $body -ClosingIssueCount $ClosingIssueCount -BaseRef $BaseRef -HeadRef $HeadRef -AuthorIsBot:$AuthorIsBot
  Find-PullRequestTypeFindings -Title $Title -Body $body -ChangedPaths @(Get-DiffFiles -DiffText $diff | ForEach-Object Path) `
    -ClosingIssueLabels $labels -Labels $prLabels -DocsPullRequests $docs -BaseRef $BaseRef -HeadRef $HeadRef -AuthorIsBot:$AuthorIsBot
  Find-DiffFindings -DiffText $diff -IgnorePaths $ignore -ReadMigrationVersions $readMigrationVersions
)

$comment = Format-ReviewChecksComment -Sha $Head -RunUrl $RunUrl -Findings $findings -PullRequest $PullRequest
Set-Content -LiteralPath $OutFile -Value $comment -NoNewline
# The gate step reads this after the comment is posted, so the comment always lands, findings or not.
if ($env:GITHUB_OUTPUT) { "findings=$($findings.Count)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
Write-Output "Review checks: $($findings.Count) finding(s) on $($Head.Substring(0, 9))."
exit 0
