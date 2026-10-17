#!/usr/bin/env pwsh
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
  [string]$IgnoreFile = '.github/review-checks-ignore.txt'
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

$raw = git log --format="%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%B%x1e" "$Base..$Head"
if ($LASTEXITCODE -ne 0) { throw "git log $Base..$Head failed (exit $LASTEXITCODE)" }
$verdict = Get-DcoVerdict -Commits @(ConvertFrom-GitLog -Raw (($raw | Out-String))) -AdoptedOn $script:DcoAdoptedOn

$body = if (Test-Path -LiteralPath $BodyFile) { Get-Content -LiteralPath $BodyFile -Raw } else { '' }
if ($null -eq $body) { $body = '' }

$findings = @(
  ConvertTo-DcoFindings -Verdict $verdict -BaseRef $BaseRef
  Find-PullRequestFindings -Body $body -ClosingIssueCount $ClosingIssueCount -BaseRef $BaseRef -HeadRef $HeadRef
  Find-DiffFindings -DiffText $diff -IgnorePaths $ignore
)

$comment = Format-ReviewChecksComment -Sha $Head -RunUrl $RunUrl -Findings $findings -PullRequest $PullRequest
Set-Content -LiteralPath $OutFile -Value $comment -NoNewline
Write-Output "Review checks: $($findings.Count) finding(s) on $($Head.Substring(0, 9))."
exit 0
