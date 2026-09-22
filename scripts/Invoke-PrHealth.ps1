#!/usr/bin/env pwsh
<#
.SYNOPSIS
    The whole pull-request health recipe in one run, saved as one report.

.DESCRIPTION
    Runs the three checks a pull request must pass before it merges and writes a single markdown
    report a person or an AI assistant reads in one go:

      1. The CI checks (Watch-PrChecks.ps1): each check and its outcome; waits for them to settle
         unless -Snapshot.
      2. The SonarCloud gate and every open finding of any type on new code (Get-SonarPrFindings.ps1).
      3. The uncovered new lines (Find-UncoveredNewLines.ps1), computed from the CI run's own coverage
         artifacts for the PR's head commit.

    The report lands in .whizbang/cache/pr-health/pr-<n>-<timestamp>.md (the repository's ignored
    cache) with the raw JSON and text files beside it. The exit code is 0 when the PR is clean and 1
    when there is anything to fix, so the recipe can be a loop: run, read the report, fix, push, run.

.PARAMETER PullRequest
    The PR number.

.PARAMETER Repo
    owner/name; defaults to the current repository as gh resolves it.

.PARAMETER BaseRef
    The base ref for the coverage diff; defaults to origin/<the PR's base branch>.

.PARAMETER ReportDir
    Where reports are written; defaults to .whizbang/cache/pr-health under the repository root.

.PARAMETER Snapshot
    Do not wait for pending checks; report the current state.

.EXAMPLE
    pwsh scripts/Invoke-PrHealth.ps1 -PullRequest 732
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][int]$PullRequest,
  [string]$Repo,
  [string]$BaseRef,
  [string]$ReportDir,
  [switch]$Snapshot
)

$ErrorActionPreference = 'Stop'
$root = git rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0) { Write-Error "Run from inside the repository." }
Set-Location $root
$scripts = Join-Path $root 'scripts'
$repoArgs = if ($Repo) { @('--repo', $Repo) } else { @() }
if (-not $ReportDir) { $ReportDir = Join-Path $root '.whizbang/cache/pr-health' }
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$prefix = Join-Path $ReportDir "pr-$PullRequest-$stamp"

$pr = gh pr view $PullRequest @repoArgs --json headRefOid,baseRefName,title,url | ConvertFrom-Json
if (-not $BaseRef) { $BaseRef = "origin/$($pr.baseRefName)" }
git fetch --no-tags -q origin $pr.baseRefName 2>$null

# 1. Checks
$watchArgs = @('-PullRequest', $PullRequest, '-OutFile', "$prefix-checks.json")
if ($Repo) { $watchArgs += @('-Repo', $Repo) }
if ($Snapshot) { $watchArgs += '-Once' }
& pwsh (Join-Path $scripts 'Watch-PrChecks.ps1') @watchArgs | Out-Host
$checks = Get-Content "$prefix-checks.json" -Raw | ConvertFrom-Json

# 2. Sonar
$sonarExit = 0
& pwsh (Join-Path $scripts 'Get-SonarPrFindings.ps1') -PullRequest $PullRequest -OutFile "$prefix-sonar-findings.txt" -Json > "$prefix-sonar.json"
$sonarExit = $LASTEXITCODE
$sonar = Get-Content "$prefix-sonar.json" -Raw | ConvertFrom-Json

# 3. Coverage of new lines, from the CI run for the head commit. No artifacts (the build failed, or the
#    run is still going) is "unknown", never zero: absence of coverage must not read as full coverage.
$run = gh run list @repoArgs --commit $pr.headRefOid --workflow ci.yml --limit 1 --json databaseId --jq '.[0].databaseId'
$uncoveredCount = $null
$coverageNote = 'no CI run found for the head commit yet'
if ($run) {
  $covDir = Join-Path $ReportDir "coverage-$run"
  & pwsh (Join-Path $scripts 'Find-UncoveredNewLines.ps1') -CoverageRoot $covDir -BaseRef $BaseRef -DownloadFromRun $run -OutFile "$prefix-uncovered.txt" 2>&1 | Out-Host
  if (Test-Path "$prefix-uncovered.txt") {
    $uncoveredCount = @(Get-Content "$prefix-uncovered.txt" | Where-Object { $_ }).Count
  } else {
    $coverageNote = "unknown: CI run $run produced no coverage artifacts (build or tests failed?)"
  }
}

# 4. The report
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# PR #$PullRequest health, $stamp UTC")
$lines.Add("")
$lines.Add("$($pr.title)  ")
$lines.Add("$($pr.url)  ")
$lines.Add("head $($pr.headRefOid.Substring(0, 9)), base $($pr.baseRefName), CI run $run")
$lines.Add("")
$lines.Add("## Checks: pass=$($checks.pass) fail=$($checks.fail) skipping=$($checks.skipping) pending=$($checks.pending)")
foreach ($f in $checks.failed) { $lines.Add("- FAILED $($f.name)  $($f.link)") }
$lines.Add("")
$lines.Add("## SonarCloud gate: $($sonar.gate); open findings on new code: $(if ($sonar.gate -eq 'NOT_ANALYZED') { 'unknown, no analysis for this head commit yet' } else { $sonar.findings.Count })")
foreach ($c in $sonar.failing) { $lines.Add("- FAIL $($c.metric): actual $($c.actual), threshold $($c.comparator) $($c.threshold)") }
foreach ($f in $sonar.findings) { $lines.Add("- $($f.type) $($f.severity) $($f.rule) ``$($f.file):$($f.line)`` $($f.message)") }
$lines.Add("")
if ($null -eq $uncoveredCount) {
  $lines.Add("## Uncovered new lines: $coverageNote")
} else {
  $lines.Add("## Uncovered new lines: $uncoveredCount")
  foreach ($u in (Get-Content "$prefix-uncovered.txt" | Where-Object { $_ })) { $lines.Add("- ``$u``") }
}
$lines.Add("")
$clean = ($checks.fail -eq 0) -and ($checks.pending -eq 0) -and ($sonar.findings.Count -eq 0) -and ($sonar.gate -eq 'OK') -and ($uncoveredCount -eq 0)
$lines.Add($(if ($clean) { "## Verdict: clean. Ready to merge." } else { "## Verdict: not yet. Fix everything above, push, and run this again." }))
$report = "$prefix.md"
[System.IO.File]::WriteAllLines($report, $lines.ToArray())

Write-Host ""
Write-Host "Report: $report"
Write-Host ($lines[-1])
exit ($(if ($clean) { 0 } else { 1 }))
