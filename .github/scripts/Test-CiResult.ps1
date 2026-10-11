#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Decides the "Gate - CI result" check: passes only when every test suite either ran green in this run
    or was skipped on a path that proves the same tree was tested green elsewhere.

.DESCRIPTION
    The gate is the one required check on develop and main, so a suite skipped for no proven reason must
    fail it. Treating every skip as fine let a release PR reach main with no suite result anywhere (#905):
    the release-branch run reused develop's results, and the release PR yielded to that run.

    A run takes exactly one path, chosen from the planning jobs' outputs, and each path has its own evidence:

      yielded       release-pr: a release PR, or the main push after one, left the suites to another run.
                    That run's own gate must end green (this waits for it), and Quality, which never
                    yields, must be green here.
      fast-forward  ff-validated: a queue run over a tree its PR run already built, tested and analyzed.
      reused        queue-validated: a develop push or release cut whose tree the queue (or PR) run tested.
                    Build, verify-rebuild and reupload-reports must all be green; build is not required
                    when the change detector found no code (a docs-only merge builds nothing).
      docs-only     a push or PR whose change detector found only inert paths.
      tested here   anything else: build, every suite and Quality must be green. A skip fails.

    On every path, any failed or canceled job fails the gate. A push to a release branch must also have
    built and packed, whatever path it took (#1208): that run is the one the stable publish promotes
    packages from and the release PR takes coverage from, so a release-branch run with no packages is a
    failure here rather than at publish time.

    Exit 0 on pass, 1 on fail. Needs GH_TOKEN and REPO only when the path is "yielded".

.PARAMETER Needs
    The workflow's toJSON(needs).

.PARAMETER EventName
    github.event_name.

.PARAMETER Actor
    github.actor (a Dependabot PR runs without Sonar secrets, so its Quality is skipped by design).

.PARAMETER Ref
    github.ref. A push to refs/heads/release/v* must have built and packed.

.EXAMPLE
    pwsh .github/scripts/Test-CiResult.ps1 -Needs "$RESULTS" -EventName pull_request -Actor someone
#>

param(
    [Parameter(Mandatory)]
    [string]$Needs,

    [Parameter(Mandatory)]
    [string]$EventName,

    [Parameter()]
    [string]$Actor = '',

    [Parameter()]
    [string]$Ref = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The suites whose results the gate stands for. A job renamed here without the workflow (or the other way
# round) reads as "missing", which fails every path that requires it: loud, not silent.
$script:Suites = @('unit-tests', 'component-tests', 'postgres-integration', 'inmemory-integration', 'rabbitmq-integration',
    'servicebus-integration', 'azureblob-integration', 'general-integration')

# The gate job's name in the run a release PR yields to. Same workflow, so the same name.
$script:GateJobName = "Gate $([char]0x00B7) CI result"   # the middle dot, kept ASCII-only in source

function Get-NeedResult {
  param([hashtable]$Needs, [string]$Job)
  $needs = $Needs; $job = $Job
  if ($needs.ContainsKey($job) -and $needs[$job] -and $needs[$job].ContainsKey('result')) { return [string]$needs[$job].result }
  return 'missing'
}

function Get-NeedOutput {
  param([hashtable]$Needs, [string]$Job, [string]$Name)
  $needs = $Needs; $job = $Job; $name = $Name
  if (-not $needs.ContainsKey($job) -or -not $needs[$job] -or -not $needs[$job].ContainsKey('outputs')) { return '' }
  $outputs = $needs[$job].outputs
  if ($outputs -and $outputs.ContainsKey($name)) { return [string]$outputs[$name] }
  return ''
}

# Which path the run took. Order matters only for the impossible combinations; each planning job is
# event-exclusive (release-pr: release PR or main push; ff-validated: merge_group; queue-validated: push).
function Get-CiPath {
  param([hashtable]$Needs, [string]$EventName)
  $needs = $Needs; $eventName = $EventName
  if ((Get-NeedOutput -Needs $needs -Job 'release-pr' -Name 'skip') -eq 'true') { return 'yielded' }
  if ((Get-NeedOutput -Needs $needs -Job 'ff-validated' -Name 'skip') -eq 'true') { return 'fast-forward' }
  if ((Get-NeedOutput -Needs $needs -Job 'queue-validated' -Name 'validated') -eq 'true') { return 'reused' }
  if ($eventName -in @('push', 'pull_request') -and (Get-NeedResult -Needs $needs -Job 'changes') -eq 'success' -and
      (Get-NeedOutput -Needs $needs -Job 'changes' -Name 'code') -ne 'true') { return 'docs-only' }
  return 'tested here'
}

<#
    The decision, with no I/O: the caller resolves the yielded-to run's gate conclusion first
    ($CoveringGate) and passes it in. Returns Pass, Path and the list of problems found.
#>
function Get-CiVerdict {
  param(
      [Parameter(Mandatory)][hashtable]$NeedsTable,
      [Parameter(Mandatory)][string]$EventName,
      [string]$Actor = '',
      [string]$CoveringGate = '',
      [string]$Ref = ''
  )
  $problems = [System.Collections.Generic.List[string]]::new()

  foreach ($job in ($NeedsTable.Keys | Sort-Object)) {
    $result = Get-NeedResult -Needs $NeedsTable -Job $job
    if ($result -in @('failure', 'cancelled')) { $problems.Add("$job $result") }
  }

  $path = Get-CiPath -Needs $NeedsTable -EventName $EventName
  $require = {
    param([string]$job, [string]$why)
    $result = Get-NeedResult -Needs $NeedsTable -Job $job
    if ($result -ne 'success' -and $result -notin @('failure', 'cancelled')) { $problems.Add("$job is $result, but $why") }
  }

  switch ($path) {
    'yielded' {
      $run = Get-NeedOutput -Needs $NeedsTable -Job 'release-pr' -Name 'coverage-run-id'
      if ($CoveringGate -ne 'success') {
        $problems.Add("the suites ran in run $run, whose gate ended '$(if ($CoveringGate) { $CoveringGate } else { 'unknown' })', not success")
      }
      & $require 'quality' 'Quality never yields to another run'
    }
    'fast-forward' {
      & $require 'ff-validated' 'the fast-forward verdict must come from a green check'
    }
    'reused' {
      $why = 'reusing another run''s results needs this run''s own build and proof'
      # A docs-only merge skips the build by design: there is nothing to build, and the tree it would
      # prove is the one the queue already tested. The rebuild check and the reports still must be green.
      if ((Get-NeedOutput -Needs $NeedsTable -Job 'changes' -Name 'code') -eq 'true') {
        & $require 'build' $why
      }
      & $require 'verify-rebuild' $why
      & $require 'reupload-reports' $why
    }
    'docs-only' { }
    default {
      $why = 'nothing proves this tree was tested anywhere else'
      & $require 'build' $why
      foreach ($suite in $script:Suites) { & $require $suite $why }
      if (-not ($EventName -eq 'pull_request' -and $Actor -eq 'dependabot[bot]')) {
        & $require 'quality' $why
      }
    }
  }

  if ($EventName -eq 'push' -and $Ref -like 'refs/heads/release/v*') {
    $why = 'a release-branch run is what the stable publish promotes and the release PR takes coverage from'
    foreach ($job in @('build', 'pack')) {
      if (-not ($problems | Where-Object { $_ -like "$job is *" })) { & $require $job $why }
    }
  }

  return [pscustomobject]@{ Pass = ($problems.Count -eq 0); Path = $path; Problems = @($problems) }
}

# Dot-sourcing for tests loads the functions without running the gate.
if ($MyInvocation.InvocationName -eq '.') { return }

$table = $Needs | ConvertFrom-Json -AsHashtable
foreach ($job in ($table.Keys | Sort-Object)) { Write-Output "${job}: $(Get-NeedResult -Needs $table -Job $job)" }

# The yielded-to run is usually still running on a release PR (its push run starts alongside), so wait
# for its gate. Quality already waited for that run's coverage, so this is normally minutes, not the
# whole matrix. A gate that never appears (the run was canceled before it) counts as not success.
$covering = ''
if ((Get-CiPath -Needs $table -EventName $EventName) -eq 'yielded') {
  $run = Get-NeedOutput -Needs $table -Job 'release-pr' -Name 'coverage-run-id'
  $deadline = (Get-Date).AddMinutes(90)
  while ($true) {
    $json = gh api "repos/$env:REPO/actions/runs/$run/jobs?per_page=100" 2>$null
    if ($LASTEXITCODE -eq 0 -and $json) {
      $data = $json | ConvertFrom-Json
      $gate = @($data.jobs | Where-Object { $_.name -eq $script:GateJobName }) | Select-Object -First 1
      if ($gate -and $gate.status -eq 'completed') { $covering = [string]$gate.conclusion; break }
      $state = (gh api "repos/$env:REPO/actions/runs/$run" --jq .status 2>$null)
      if (-not $gate -and $state -eq 'completed') { $covering = 'absent'; break }
    }
    if ((Get-Date) -gt $deadline) { $covering = 'still running after 90 minutes'; break }
    Start-Sleep -Seconds 30
  }
  Write-Output "yielded to run $run; its gate: $covering"
}

$verdict = Get-CiVerdict -NeedsTable $table -EventName $EventName -Actor $Actor -CoveringGate $covering -Ref $Ref
$summary = if ($verdict.Pass) { "Gate passed on the **$($verdict.Path)** path." } else { "Gate failed on the **$($verdict.Path)** path." }
if ($env:GITHUB_STEP_SUMMARY) {
  $summary | Add-Content -Path $env:GITHUB_STEP_SUMMARY
  $verdict.Problems | ForEach-Object { "- $_" } | Add-Content -Path $env:GITHUB_STEP_SUMMARY
}
if (-not $verdict.Pass) {
  $verdict.Problems | ForEach-Object { Write-Output "::error::$_" }
  exit 1
}
Write-Output $summary
exit 0
