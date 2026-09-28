#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Finds the green release-branch CI run that tested a commit, and its live package artifact, for the
    release workflow to promote.

.DESCRIPTION
    Used by release.yml "Plan - Locate the tested packages", after that step has proved the tree being
    tagged equals the tested tree. A release publishes exactly the packages its suites ran against, so
    this fails closed: it never picks a rebuild or a different run.

    The run search can lag or briefly miss a finished run (#926: a single empty answer failed a publish
    whose run had been green for 14 minutes). So each attempt asks twice, and the whole lookup retries
    with a short backoff before failing:

      1. ci.yml push runs filtered by head_sha, kept to release/v* branches (the newest one wins);
      2. if that is empty, ci.yml push runs of the release branch, matched to the commit locally.

    Outcomes, each with its own recovery hint:

      found            a green run with a live nuget-packages-<run> artifact: exit 0, outputs written.
      not-found        no push run for the commit on the release branch after every attempt.
      not-green        the run completed without success (a still-running run is retried first).
      artifact-missing the run is green but its artifact expired, or never appeared after every attempt.

.PARAMETER Sha
    The tested release-branch commit.

.PARAMETER Version
    The stable version being released (X.Y.Z); the release branch is release/vX.Y.Z.

.PARAMETER Repo
    owner/name.

.PARAMETER Delays
    Seconds to wait before each retry. Attempts = Delays.Count + 1. Default: about two minutes in total.

.EXAMPLE
    pwsh .github/scripts/Find-TestedPackages.ps1 -Sha "$SRC" -Version 0.2602.0 -Repo whizbang-lib/whizbang
#>

param(
    [Parameter(Mandatory)]
    [string]$Sha,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$Repo,

    [Parameter()]
    [int[]]$Delays = @(10, 20, 30, 60)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Calls the GitHub API; returns the parsed body, or $null when the call failed. Tests replace it.
function Invoke-GhApi {
  param([string]$Path)
  $json = gh api $Path 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $json) { return $null }
  return ($json | ConvertFrom-Json)
}

# The newest push run for the commit on a release branch, or $null. Addressed by workflow PATH, not by
# name: ci.yml sets a run-name, so no run is named "CI".
function Find-ReleaseRun {
  param([string]$Repo, [string]$Sha, [string]$Branch, [scriptblock]$GhApi)
  $byCommit = & $GhApi "repos/$Repo/actions/workflows/ci.yml/runs?head_sha=$Sha&event=push&per_page=10"
  if ($byCommit) {
    $run = @($byCommit.workflow_runs | Where-Object { $_.head_branch -like 'release/v*' }) | Select-Object -First 1
    if ($run) { return [pscustomobject]@{ Run = $run; Via = 'head_sha' } }
  }
  # The fallback does not depend on the head_sha filter: list the branch's push runs, match locally.
  $byBranch = & $GhApi "repos/$Repo/actions/workflows/ci.yml/runs?branch=$([uri]::EscapeDataString($Branch))&event=push&per_page=50"
  if ($byBranch) {
    $run = @($byBranch.workflow_runs | Where-Object { $_.head_sha -eq $Sha -and $_.head_branch -eq $Branch }) | Select-Object -First 1
    if ($run) { return [pscustomobject]@{ Run = $run; Via = 'branch' } }
  }
  return $null
}

<#
    The lookup with its retries. -GhApi and -Sleep are injected so tests run with no network and no wait.
    Returns Outcome, RunId, ArtifactName, Url, Via, Attempts and Message (the error with its recovery).
#>
function Find-TestedPackage {
  param(
      [Parameter(Mandatory)][string]$Sha,
      [Parameter(Mandatory)][string]$Version,
      [Parameter(Mandatory)][string]$Repo,
      [int[]]$Delays = @(10, 20, 30, 60),
      [scriptblock]$GhApi = { param($p) Invoke-GhApi -Path $p },
      [scriptblock]$Sleep = { param($s) Start-Sleep -Seconds $s },
      [string]$ThisRunId = ''
  )
  $branch = "release/v$Version"
  $attempts = $Delays.Count + 1
  $waited = ($Delays | Measure-Object -Sum).Sum
  $rerunThis = if ($ThisRunId) { "gh run rerun $ThisRunId --failed" } else { 'gh run rerun <this release run> --failed' }
  $dispatch = "gh workflow run release.yml --ref main -f version=$Version -f release_type=auto -f dry_run=false"
  $result = { param($outcome, $message, $run, $via, $n)
    [pscustomobject]@{
      Outcome = $outcome; Message = $message; Attempts = $n; Via = $via
      RunId = if ($run) { [string]$run.id } else { '' }
      ArtifactName = if ($run -and $outcome -eq 'found') { "nuget-packages-$($run.id)" } else { '' }
      Url = if ($run) { [string]$run.html_url } else { '' }
    }
  }

  $last = $null; $lastVia = ''; $artifactState = ''
  for ($n = 1; $n -le $attempts; $n++) {
    if ($n -gt 1) { & $Sleep $Delays[$n - 2] }
    $hit = Find-ReleaseRun -Repo $Repo -Sha $Sha -Branch $branch -GhApi $GhApi
    if (-not $hit) { continue }   # a run seen on an earlier attempt still counts as found
    $last = $hit.Run; $lastVia = $hit.Via

    if ($last.status -ne 'completed') { continue }   # still running: it may yet go green
    if ($last.conclusion -ne 'success') {
      # Completed and red is final; waiting will not change it.
      return & $result 'not-green' ("The release-branch CI run $($last.id) for $Sha ended '$($last.conclusion)', not success, so " +
        "its packages are not proven. Recovery: fix or re-run it (gh run rerun $($last.id) --failed; a flake is fixed in " +
        "its own PR too), wait for it to go green, then re-run this release: $rerunThis (or $dispatch).") $last $lastVia $n
    }

    $artifact = "nuget-packages-$($last.id)"
    $listed = & $GhApi "repos/$Repo/actions/runs/$($last.id)/artifacts?name=$artifact"
    if (-not $listed) { $artifactState = 'unreadable'; continue }
    $all = @($listed.artifacts)
    if (@($all | Where-Object { -not $_.expired }).Count -ge 1) {
      return & $result 'found' "Promoting $artifact from release-branch run $($last.id) ($Sha), found by $lastVia on attempt $n." $last $lastVia $n
    }
    if ($all.Count -ge 1) {
      # Listed and expired is final.
      return & $result 'artifact-missing' ("The tested packages ($artifact on green run $($last.id)) have expired (kept 7 days). " +
        "Recovery: re-run ALL jobs of run $($last.id) (gh run rerun $($last.id)), which rebuilds and re-tests them under the " +
        "same artifact name, wait for it to go green, then re-run this release: $rerunThis (or $dispatch).") $last $lastVia $n
    }
    $artifactState = 'absent'
  }

  $tried = "$attempts attempts over ${waited}s"
  if (-not $last) {
    return & $result 'not-found' ("No CI push run for $Sha on $branch was found after $tried (by head_sha, then by listing " +
      "$branch). Recovery: check with gh run list --workflow ci.yml --branch $branch --event push. If a green run for this " +
      "commit is listed, the search missed it: re-run this release ($rerunThis). If none is listed, nothing tested these " +
      "bytes and there is nothing to promote: do not publish; find out why the push run never ran (docs/RELEASING.md, Recovery).") $null '' $attempts
  }
  if ($last.status -ne 'completed') {
    return & $result 'not-green' ("The release-branch CI run $($last.id) for $Sha is still '$($last.status)' after $tried. " +
      "Recovery: wait for it to go green, then re-run this release: $rerunThis (or $dispatch).") $last $lastVia $attempts
  }
  return & $result 'artifact-missing' ("Green run $($last.id) for $Sha has no nuget-packages-$($last.id) artifact after $tried " +
    "(artifact list $artifactState). Recovery: re-run ALL jobs of run $($last.id) (gh run rerun $($last.id)), which rebuilds " +
    "and re-tests the packages, wait for it to go green, then re-run this release: $rerunThis (or $dispatch).") $last $lastVia $attempts
}

# Dot-sourcing for tests loads the functions without running the lookup.
if ($MyInvocation.InvocationName -eq '.') { return }

$found = Find-TestedPackage -Sha $Sha -Version $Version -Repo $Repo -Delays $Delays -ThisRunId "$env:GITHUB_RUN_ID"
if ($found.Outcome -ne 'found') {
  Write-Output "::error::$($found.Message)"
  exit 1
}
Write-Output $found.Message
if ($env:GITHUB_OUTPUT) {
  "run-id=$($found.RunId)", "artifact-name=$($found.ArtifactName)" | Add-Content -Path $env:GITHUB_OUTPUT
}
if ($env:GITHUB_STEP_SUMMARY) {
  "### Promoting the tested packages from [run $($found.RunId)]($($found.Url)), commit ``$Sha``" | Add-Content -Path $env:GITHUB_STEP_SUMMARY
}
exit 0
