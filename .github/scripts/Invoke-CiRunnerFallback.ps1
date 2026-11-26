#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Falls back to GitHub-hosted runners when a self-hosted pool machine never starts a job or is lost during one.

.DESCRIPTION
    GitHub cannot move a queued job to another runner, nor re-run one job while its run is still going, so the
    fallback works at the run's edges (#1880):

      Watch  A job of the CI run (Plan · Watch self-hosted jobs), only when the picker sent a class to the pool.
             Every -PollSeconds it reads the run's jobs. A pool job is stuck when it has been queued -QueuedMinutes
             and no online machine carries its labels, or queued -CeilingMinutes whatever the machines say. Once a
             job is stuck AND no other job of the run is still running, it starts the re-runner (a dispatch of
             ci-runner-fallback.yml) and cancels the run. It never touches a running job, however slow: a slow
             but alive machine is left to the job's own timeout, and waiting for the running jobs first means the
             cancel throws away no work in progress. It ends once every other job has finished (two polls in a
             row, so the gap between one job finishing and the next starting is not mistaken for the end).
      Rerun  ci-runner-fallback.yml, on the CI run finishing (Trigger workflow_run) or asked by the watcher
             (Trigger watcher). It re-runs the run's failed jobs when the watcher canceled it, or when every job
             that failed was a pool job whose machine was lost (its annotations say it lost communication, was
             shut down, exceeded its time or was never acquired) or the gate that fails after it. A job that
             failed for any other reason means a re-run would not change the result, so nothing is re-run and no
             flaky test gets a second try. A re-run job takes ubuntu-latest, not the pool: every routed job
             takes the picker's plan only in the attempt it was made for. At most -MaxAttempts attempts.

    A merge-queue run is never re-run: the queue removes the pull request when its run fails or is canceled, and a
    re-run of that run cannot put it back.

    Setup: .github/WORKFLOWS.md, "Runner selection".
#>

param(
    [ValidateSet('Watch', 'Rerun', '')][string]$Mode = '',
    [string]$Repository = '',
    [string]$RunId = '',
    [string]$SelfName = 'Plan · Watch self-hosted jobs',
    [string]$DefaultBranch = 'develop',
    [string]$Organization = '',
    [string]$GroupName = 'Default',
    [ValidateSet('workflow_run', 'watcher', '')][string]$Trigger = '',
    [int]$QueuedMinutes = 10,
    [int]$CeilingMinutes = 45,
    [string]$SummaryFile = $env:GITHUB_STEP_SUMMARY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This script's own arguments, kept before the picker is dot-sourced: its param block shares this scope and resets
# the names the two scripts have in common.
$entry = @{
  Mode = $Mode; Repository = $Repository; RunId = $RunId; SelfName = $SelfName; DefaultBranch = $DefaultBranch
  Organization = $Organization; GroupName = $GroupName; Trigger = $Trigger; QueuedMinutes = $QueuedMinutes
  CeilingMinutes = $CeilingMinutes; SummaryFile = $SummaryFile
}

# Get-IdleRunner, Get-RunnerPoolState, New-GitHubApiInvoker and Write-RunnerFile live with the picker.
. (Join-Path $PSScriptRoot 'Select-CiRunner.ps1')

$script:FallbackWorkflow = 'ci-runner-fallback.yml'
$script:LostPatterns = @(
  'lost communication with the server'
  'received a shutdown signal'
  'exceeded the maximum execution time'
  'was not acquired by Runner'
)

# Whether a job asked for a self-hosted runner (the picker's pool label sets all start with self-hosted).
function Test-PoolJob {
  param([Parameter(Mandatory)]$Job)
  return @(@($Job.labels) | Where-Object { [string]$_ -eq 'self-hosted' }).Count -gt 0
}

# The pool jobs no machine will start: each with its name and why.
function Get-StuckPoolJob {
  param([object[]]$Jobs, [AllowNull()][object[]]$Runners, [datetime]$Now, [int]$QueuedMinutes, [int]$CeilingMinutes)
  foreach ($job in @($Jobs)) {
    if ([string]$job.status -ne 'queued' -or -not (Test-PoolJob -Job $job)) { continue }
    $created = [datetime]::Parse([string]$job.created_at, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]'AdjustToUniversal, AssumeUniversal')
    $minutes = [math]::Floor(($Now - $created).TotalMinutes)
    if ($minutes -lt $QueuedMinutes) { continue }
    $labels = @($job.labels | ForEach-Object { [string]$_ })
    $reason = ''
    if ($null -ne $Runners) {
      # Busy or idle: a busy machine is alive and will take the job once it is free.
      $alive = @(Get-IdleRunner -Runners @($Runners | ForEach-Object {
            [pscustomobject]@{ name = $_.name; status = $_.status; busy = $false; labels = $_.labels }
          }) -Labels $labels)
      if ($alive.Count -eq 0) { $reason = "queued $minutes minutes; no online runner carries $($labels -join ', ')" }
    }
    if (-not $reason -and $minutes -ge $CeilingMinutes) { $reason = "queued $minutes minutes, the most a pool job waits" }
    if ($reason) { [pscustomobject]@{ Name = [string]$job.name; Reason = $reason } }
  }
}

# cancel: a pool job is stuck and nothing else runs; done: every other job has finished; wait otherwise.
function Get-WatchAction {
  param([object[]]$Jobs, [object[]]$Stuck, [string]$SelfName)
  $others = @($Jobs | Where-Object { [string]$_.name -ne $SelfName })
  if (@($others | Where-Object { [string]$_.status -ne 'completed' }).Count -eq 0) { return 'done' }
  $running = @($others | Where-Object { [string]$_.status -eq 'in_progress' }).Count
  if (@($Stuck).Count -gt 0 -and $running -eq 0) { return 'cancel' }
  return 'wait'
}

function Invoke-RunnerWatch {
  param(
      [Parameter(Mandatory)][scriptblock]$InvokeApi, [string]$Repository, [string]$RunId, [string]$SelfName, [string]$DefaultBranch,
      [scriptblock]$GetRunners, [Parameter(Mandatory)][scriptblock]$Now, [Parameter(Mandatory)][scriptblock]$Sleep,
      [int]$PollSeconds = 30, [int]$QueuedMinutes = 10, [int]$CeilingMinutes = 45, [int]$MaxPolls = 340
  )
  $quiet = 0
  for ($poll = 1; $poll -le $MaxPolls; $poll++) {
    try {
      $jobs = @((& $InvokeApi 'GET' "/repos/$Repository/actions/runs/$RunId/jobs?filter=latest&per_page=100" $null).jobs)
      $runners = $null
      try { $runners = @(& $GetRunners) } catch { $runners = $null }
      $stuck = @(Get-StuckPoolJob -Jobs $jobs -Runners $runners -Now (& $Now) -QueuedMinutes $QueuedMinutes -CeilingMinutes $CeilingMinutes)
      switch (Get-WatchAction -Jobs $jobs -Stuck $stuck -SelfName $SelfName) {
        'cancel' {
          foreach ($s in $stuck) { Write-Host "::warning title=Runner fallback::$($s.Name): $($s.Reason)" }
          try {
            $null = & $InvokeApi 'POST' "/repos/$Repository/actions/workflows/$script:FallbackWorkflow/dispatches" ([ordered]@{ ref = $DefaultBranch; inputs = [ordered]@{ 'run-id' = $RunId } })
            Write-Host 'Canceling the run; ci-runner-fallback.yml re-runs its failed jobs on GitHub-hosted runners once it has stopped.'
          }
          catch {
            Write-Host "::warning title=Runner fallback::The re-runner could not be started ($($_.Exception.Message)); canceling anyway, re-run failed jobs by hand."
          }
          $null = & $InvokeApi 'POST' "/repos/$Repository/actions/runs/$RunId/cancel" $null
          return 'cancel'
        }
        'done' { $quiet++; if ($quiet -ge 2) { return 'done' } }
        default { $quiet = 0 }
      }
    }
    catch {
      $quiet = 0
      Write-Host "::warning title=Runner fallback::Could not read the run's jobs ($($_.Exception.Message)); still watching."
    }
    if ($poll -lt $MaxPolls) { & $Sleep $PollSeconds }
  }
  Write-Host "The watcher stopped watching after $MaxPolls polls."
  return 'timeout'
}

# Whether a job's annotations say its machine was lost rather than its steps failing.
function Test-RunnerLost {
  param([string[]]$Messages = @())
  foreach ($message in @($Messages)) {
    foreach ($pattern in $script:LostPatterns) { if ($message -like "*$pattern*") { return $true } }
  }
  return $false
}

# Whether to re-run the run's failed jobs, and why. Messages: job id -> its annotation messages.
function Get-FallbackDecision {
  param($Run, [object[]]$Jobs, [hashtable]$Messages = @{}, [string]$Trigger, [int]$MaxAttempts = 3, [string[]]$Aggregators = @())
  function No([string]$Why) { [pscustomobject]@{ Rerun = $false; Reason = $Why } }
  if ([string]$Run.status -ne 'completed') { return No 'the run has not finished' }
  if ([string]$Run.event -eq 'merge_group') {
    return No 'a merge-queue run: the merge queue has already removed the pull request, and a re-run cannot put it back; queue it again'
  }
  if ([int]$Run.run_attempt -ge $MaxAttempts) { return No "attempt $($Run.run_attempt): re-run $MaxAttempts times at most" }
  if ($Trigger -eq 'watcher') {
    if ([string]$Run.conclusion -eq 'cancelled') {
      return [pscustomobject]@{ Rerun = $true; Reason = 'the watcher canceled it because a pool job could not start' }
    }
    return No "the run concluded $($Run.conclusion), so the watcher's cancel did not end it"
  }
  if ([string]$Run.conclusion -ne 'failure') { return No "the run concluded $($Run.conclusion)" }

  $byId = @{}
  foreach ($k in $Messages.Keys) { $byId[[string]$k] = $Messages[$k] }
  $lost = @(); $blocking = @()
  foreach ($job in @($Jobs)) {
    $conclusion = [string]$job.conclusion
    if ($conclusion -notin @('failure', 'cancelled', 'timed_out')) { continue }
    $said = if ($byId.ContainsKey([string]$job.id)) { @($byId[[string]$job.id]) } else { @() }
    if ((Test-PoolJob -Job $job) -and (Test-RunnerLost -Messages $said)) { $lost += [string]$job.name; continue }
    # A canceled job never failed a test: it was stopped because another job failed.
    if ($conclusion -eq 'cancelled' -or $Aggregators -contains [string]$job.name) { continue }
    $blocking += [string]$job.name
  }
  if ($blocking.Count -gt 0) { return No "$($blocking -join ', ') failed for another reason, so a re-run would not change the result" }
  if ($lost.Count -eq 0) { return No 'no pool machine was lost' }
  return [pscustomobject]@{ Rerun = $true; Reason = "$($lost -join ', ') lost its pool machine" }
}

function Invoke-RunnerRerun {
  param(
      [Parameter(Mandatory)][scriptblock]$InvokeApi, [string]$Repository, [string]$RunId, [string]$Trigger, [string]$SummaryFile = '',
      [Parameter(Mandatory)][scriptblock]$Sleep, [int]$PollSeconds = 30, [int]$MaxPolls = 30,
      [int]$MaxAttempts = 3, [string[]]$Aggregators = @('Gate · CI result')
  )
  $runPath = "/repos/$Repository/actions/runs/$RunId"
  $note = ''
  try {
    # The watcher asks while its cancel is still stopping the run; wait for the run to finish.
    for ($poll = 1; $poll -le $MaxPolls; $poll++) {
      $run = & $InvokeApi 'GET' $runPath $null
      if ([string]$run.status -eq 'completed' -or $poll -eq $MaxPolls) { break }
      & $Sleep $PollSeconds
    }
    $jobs = @()
    $messages = @{}
    if ([string]$run.status -eq 'completed') {
      $jobs = @((& $InvokeApi 'GET' "$runPath/jobs?filter=latest&per_page=100" $null).jobs)
      foreach ($job in $jobs) {
        if ([string]$job.conclusion -notin @('failure', 'cancelled', 'timed_out') -or -not (Test-PoolJob -Job $job)) { continue }
        try { $messages[$job.id] = @(& $InvokeApi 'GET' "/repos/$Repository/check-runs/$($job.id)/annotations?per_page=50" $null | ForEach-Object { [string]$_.message }) }
        catch { $messages[$job.id] = @() }
      }
    }
    $decision = Get-FallbackDecision -Run $run -Jobs $jobs -Messages $messages -Trigger $Trigger -MaxAttempts $MaxAttempts -Aggregators $Aggregators
    if ($decision.Rerun) {
      try {
        $null = & $InvokeApi 'POST' "$runPath/rerun-failed-jobs" $null
        $note = "Re-ran the failed jobs on GitHub-hosted runners: $($decision.Reason)."
      }
      catch { $note = "Should re-run ($($decision.Reason)) but could not re-run the failed jobs: $($_.Exception.Message)" }
    }
    else { $note = "Nothing re-run: $($decision.Reason)." }
  }
  catch {
    $decision = [pscustomobject]@{ Rerun = $false; Reason = $_.Exception.Message }
    $note = "Nothing re-run: the run could not be read ($($_.Exception.Message))."
  }
  Write-RunnerFile -Path $SummaryFile -Lines @('### Runner fallback', '', "Run $RunId ($Trigger): $note")
  Write-Host $note
  return $decision
}

# A scriptblock that calls the GitHub API with a token: param($Method, $Path, $Body).
function New-GitHubApiClient {
  param([string]$Token, [string]$BaseUrl = 'https://api.github.com', [int]$TimeoutSec = 15)
  $base = $BaseUrl.TrimEnd('/'); $timeout = $TimeoutSec; $auth = $Token
  return {
    param([string]$Method, [string]$Path, $Body)
    $headers = @{ Authorization = "Bearer $auth"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $request = @{ Method = $Method; Uri = "$base$Path"; Headers = $headers; TimeoutSec = $timeout; MaximumRetryCount = 0 }
    if ($null -ne $Body) { $request.Body = (ConvertTo-Json -InputObject $Body -Depth 5 -Compress); $request.ContentType = 'application/json' }
    Invoke-RestMethod @request
  }.GetNewClosure()
}

<#
    The entry point. Never throws and never fails its job: whatever happens, CI's own result stands. Api and PoolApi
    default to clients built from -Token and -PoolToken; Now and Sleep are the clock and the wait.
#>
function Invoke-CiRunnerFallback {
  param(
      [Parameter(Mandatory)][hashtable]$Arguments, [string]$Token = '', [string]$PoolToken = '',
      [scriptblock]$Api, [scriptblock]$PoolApi, [Parameter(Mandatory)][scriptblock]$Now, [Parameter(Mandatory)][scriptblock]$Sleep
  )
  if (-not $Token) {
    Write-Host '::warning title=Runner fallback::No GH_TOKEN; nothing watched or re-run.'
    return 'skipped'
  }
  try {
    if (-not $Api) { $Api = New-GitHubApiClient -Token $Token }
    if ($Arguments.Mode -eq 'Watch') {
      if (-not $PoolApi) { $PoolApi = New-GitHubApiInvoker -BudgetSeconds ([int]::MaxValue) }
      # The closure sees only its captured variables, so the function it calls is captured too.
      $pool = $PoolApi; $poolToken = $PoolToken; $org = $Arguments.Organization; $group = $Arguments.GroupName
      $readPool = ${function:Get-RunnerPoolState}
      return Invoke-RunnerWatch -InvokeApi $Api -Repository $Arguments.Repository -RunId $Arguments.RunId -SelfName $Arguments.SelfName `
        -DefaultBranch $Arguments.DefaultBranch -QueuedMinutes $Arguments.QueuedMinutes -CeilingMinutes $Arguments.CeilingMinutes `
        -Now $Now -Sleep $Sleep -GetRunners {
          if (-not $poolToken) { throw 'no runner-pool token' }
          (& $readPool -InvokeApi $pool -Organization $org -GroupName $group -PoolToken $poolToken).Runners
        }.GetNewClosure()
    }
    return Invoke-RunnerRerun -InvokeApi $Api -Repository $Arguments.Repository -RunId $Arguments.RunId -Trigger $Arguments.Trigger `
      -SummaryFile $Arguments.SummaryFile -Sleep $Sleep
  }
  catch {
    Write-Host "::warning title=Runner fallback::$($_.Exception.Message)"
    return 'failed'
  }
}

# Dot-sourcing for tests loads the functions without doing anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$null = Invoke-CiRunnerFallback -Arguments $entry -Token $env:GH_TOKEN -PoolToken $env:RUNNER_POOL_TOKEN `
  -Now { [datetime]::UtcNow } -Sleep { param($s) Start-Sleep -Seconds $s }
exit 0
