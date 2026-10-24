#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Chooses where each class of CI job runs: an idle machine in the team's self-hosted pool, then
    Blacksmith, then a GitHub-hosted runner.

.DESCRIPTION
    Writes one output per job class to $GITHUB_OUTPUT, each a JSON value a job reads with
    `runs-on: ${{ fromJSON(needs.pick-runner.outputs.<class>) }}`: a label set such as
    ["self-hosted","whizbang","Linux","X64"], or a single quoted label such as "ubuntu-latest". A line per
    class goes to $GITHUB_STEP_SUMMARY saying which tier was chosen and why each higher tier was skipped.

    Tiers, in order; the first whose every check passes wins:

      1. Self-hosted pool. The run is trusted (a push, a merge-queue run, a dispatch, a schedule, or a pull
         request from a branch of this repository that Dependabot did not open); the RUNNER_POOL_TOKEN can
         read the runner group; the group is open to this repository; an online, idle runner carries every
         label of the class; and the jobs of other runs already queued for those runners leave more than
         -QueueThreshold of them spare.
      2. Blacksmith. CI_BLACKSMITH_RUNNER (or CI_BLACKSMITH_RUNNER_<CLASS>) names a label that fits the class;
         the run is trusted, or it is a fork or Dependabot pull request and CI_BLACKSMITH_FORKS is 'true';
         and, when CI_BLACKSMITH_MAX_QUEUED is set above 0, fewer jobs than that are queued for the label.
      3. ubuntu-latest.

    Fallback is the default: a missing token, an unset or malformed variable, an API error, a timeout, an
    unexpected response, or an exception anywhere sends the class to the next tier, and in the end to
    ubuntu-latest. The script never fails the job; it always exits 0 and always writes every output.

    The compile of a push run stays on GitHub-hosted runners whatever the tiers say: a push run is the one
    whose build is packed and published, with provenance attested by GitHub-hosted infrastructure.

    Setup and the label convention: .github/WORKFLOWS.md, "Runner selection".

.PARAMETER EventName
    github.event_name.

.PARAMETER Repository
    github.repository (owner/name).

.PARAMETER HeadRepository
    The pull request's head repository (owner/name); empty for any other event.

.PARAMETER Actor
    github.actor.

.PARAMETER PrAuthor
    The pull request author's login; empty for any other event.

.PARAMETER RepositoryPrivate
    'true' when the repository is private (github.event.repository.private).

.PARAMETER Organization
    The organization that owns the runner group.

.PARAMETER GroupName
    The runner group holding the pool. Default: Default.

.PARAMETER RunId
    github.run_id; this run's own jobs are not counted as queued ahead of it.

.PARAMETER QueueThreshold
    Pool machines that must stay spare after the jobs already queued for them. Default 0: route when at
    least one idle machine is left over.

.PARAMETER Disabled
    'true' turns the self-hosted tier off (the RUNNER_POOL_DISABLED repository variable).

.PARAMETER OutputFile
    Where outputs are written. Default: $GITHUB_OUTPUT.

.PARAMETER SummaryFile
    Where the summary is written. Default: $GITHUB_STEP_SUMMARY.

.EXAMPLE
    pwsh .github/scripts/Select-CiRunner.ps1 -EventName push -Repository whizbang-lib/whizbang -Organization whizbang-lib

    Tokens come from the environment only: RUNNER_POOL_TOKEN (the GitHub App installation token that can
    read the organization's runners) and GH_TOKEN (this repository's actions, for the queue). The
    CI_BLACKSMITH_* variables come from the environment as well.
#>

param(
    [string]$EventName = '',
    [string]$Repository = '',
    [string]$HeadRepository = '',
    [string]$Actor = '',
    [string]$PrAuthor = '',
    [string]$RepositoryPrivate = 'false',
    [string]$Organization = '',
    [string]$GroupName = 'Default',
    [string]$RunId = '',
    [int]$QueueThreshold = 0,
    [string]$Disabled = '',
    [string]$OutputFile = $env:GITHUB_OUTPUT,
    [string]$SummaryFile = $env:GITHUB_STEP_SUMMARY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:HostedRunner = 'ubuntu-latest'
$script:SetupHint = 'See .github/WORKFLOWS.md, "Runner selection".'
$script:BlacksmithVariables = @('CI_BLACKSMITH_RUNNER', 'CI_BLACKSMITH_RUNNER_ANY_OS', 'CI_BLACKSMITH_RUNNER_LINUX',
  'CI_BLACKSMITH_RUNNER_COMPILE', 'CI_BLACKSMITH_RUNNER_LINUX_DOCKER', 'CI_BLACKSMITH_FORKS', 'CI_BLACKSMITH_MAX_QUEUED')

<#
    The job classes, in output order. Labels pick pool machines; a machine is never named. Linux classes
    carry X64 because their jobs run, or produce, the Linux x64 build (its test hosts are x64 executables).
    Os is what a Blacksmith label must fit; HostedOn lists the events whose jobs of the class skip tiers 1
    and 2.
#>
function Get-CiRunnerClass {
  $classes = [ordered]@{}
  $classes['any-os'] = [pscustomobject]@{ Labels = @('self-hosted', 'whizbang'); Os = ''; X64 = $false; HostedOn = @() }
  $classes['linux'] = [pscustomobject]@{ Labels = @('self-hosted', 'whizbang', 'Linux', 'X64'); Os = 'Linux'; X64 = $true; HostedOn = @() }
  $classes['compile'] = [pscustomobject]@{ Labels = @('self-hosted', 'whizbang', 'Linux', 'X64'); Os = 'Linux'; X64 = $true; HostedOn = @('push') }
  $classes['linux-docker'] = [pscustomobject]@{ Labels = @('self-hosted', 'whizbang', 'Linux', 'X64', 'docker'); Os = 'Linux'; X64 = $true; HostedOn = @() }
  return $classes
}

<#
    Whether the run may use the pool: Kind is 'trusted', 'external' (a fork or Dependabot pull request,
    which only Blacksmith may serve, and only when allowed), or 'ineligible'. Author association is not
    used: a private organization member shows as CONTRIBUTOR.
#>
function Get-CiRunTrust {
  param([string]$EventName, [string]$Repository, [string]$HeadRepository, [string]$Actor, [string]$PrAuthor)
  $dependabot = 'dependabot[bot]'
  $byDependabot = ($Actor -eq $dependabot) -or ($PrAuthor -eq $dependabot)
  if ($EventName -in @('push', 'merge_group', 'workflow_dispatch', 'schedule')) {
    if ($byDependabot) { return [pscustomobject]@{ Kind = 'external'; Reason = "a $EventName by Dependabot" } }
    return [pscustomobject]@{ Kind = 'trusted'; Reason = "a $EventName run is trusted" }
  }
  if ($EventName -eq 'pull_request') {
    if (-not $HeadRepository -or $HeadRepository -ne $Repository) {
      return [pscustomobject]@{ Kind = 'external'; Reason = "a pull request from a fork ($(if ($HeadRepository) { $HeadRepository } else { 'unknown' }))" }
    }
    if ($byDependabot) { return [pscustomobject]@{ Kind = 'external'; Reason = 'a Dependabot pull request' } }
    return [pscustomobject]@{ Kind = 'trusted'; Reason = 'a pull request from a branch of this repository is trusted' }
  }
  return [pscustomobject]@{ Kind = 'ineligible'; Reason = "the '$EventName' event is not eligible" }
}

# Whether the runner group lets this repository's jobs onto its runners.
function Get-RunnerGroupAccess {
  param($Group, [string]$Repository, [bool]$RepositoryPrivate, [string[]]$SelectedRepositories)
  if ($null -eq $Group) { return [pscustomobject]@{ Open = $false; Reason = 'runner group not found' } }
  if ($Group.restricted_to_workflows) {
    return [pscustomobject]@{ Open = $false; Reason = "runner group '$($Group.name)' is restricted to selected workflows" }
  }
  if (-not $RepositoryPrivate -and -not $Group.allows_public_repositories) {
    return [pscustomobject]@{ Open = $false; Reason = "runner group '$($Group.name)' does not allow public repositories" }
  }
  switch ([string]$Group.visibility) {
    'all' { return [pscustomobject]@{ Open = $true; Reason = '' } }
    'selected' {
      if ($SelectedRepositories -contains $Repository) { return [pscustomobject]@{ Open = $true; Reason = '' } }
      return [pscustomobject]@{ Open = $false; Reason = "$Repository is not one of the repositories runner group '$($Group.name)' selects" }
    }
    'private' {
      if ($RepositoryPrivate) { return [pscustomobject]@{ Open = $true; Reason = '' } }
      return [pscustomobject]@{ Open = $false; Reason = "runner group '$($Group.name)' is for private repositories" }
    }
    default { return [pscustomobject]@{ Open = $false; Reason = "runner group '$($Group.name)' has visibility '$($Group.visibility)'" } }
  }
}

# The online, idle runners carrying every one of the labels (labels compare without case, as GitHub's do).
function Get-IdleRunner {
  param([object[]]$Runners, [string[]]$Labels)
  foreach ($runner in @($Runners)) {
    if ($runner.status -ne 'online' -or $runner.busy) { continue }
    $has = @($runner.labels | ForEach-Object { [string]$_.name })
    if (@($Labels | Where-Object { $has -notcontains $_ }).Count -eq 0) { $runner }
  }
}

# The queued self-hosted jobs that one of the idle runners could take, and so will take before this run.
function Get-CompetingJobCount {
  param([object[]]$QueuedJobs, [object[]]$IdleRunners)
  $count = 0
  foreach ($job in @($QueuedJobs)) {
    if ($job.status -ne 'queued') { continue }
    $wants = @($job.labels | ForEach-Object { [string]$_ })
    if ($wants -notcontains 'self-hosted') { continue }
    foreach ($runner in @($IdleRunners)) {
      $has = @($runner.labels | ForEach-Object { [string]$_.name })
      if (@($wants | Where-Object { $has -notcontains $_ }).Count -eq 0) { $count++; break }
    }
  }
  return $count
}

# The queued jobs that ask for the label.
function Get-LabelQueueCount {
  param([object[]]$QueuedJobs, [string]$Label)
  return @($QueuedJobs | Where-Object { $_.status -eq 'queued' -and @($_.labels) -contains $Label }).Count
}

# A runs-on value as JSON: a single label as a string, a label set as an array.
function ConvertTo-RunsOnJson {
  param([Parameter(Mandatory)][object]$Value)
  if ($Value -is [array]) { return (ConvertTo-Json -InputObject @($Value) -Compress) }
  return (ConvertTo-Json -InputObject ([string]$Value) -Compress)
}

<#
    The Blacksmith settings, from repository variables (name -> value). Problem is set when a value is
    malformed, which turns the tier off.
#>
function Get-BlacksmithConfig {
  param([hashtable]$Variables)
  $read = { param($name) if ($Variables.ContainsKey($name) -and $null -ne $Variables[$name]) { ([string]$Variables[$name]).Trim() } else { '' } }
  $perClass = @{}
  foreach ($class in (Get-CiRunnerClass).Keys) {
    $perClass[$class] = & $read ('CI_BLACKSMITH_RUNNER_' + $class.ToUpperInvariant().Replace('-', '_'))
  }
  $problem = ''
  $maxQueued = 0
  $rawMax = & $read 'CI_BLACKSMITH_MAX_QUEUED'
  if ($rawMax) {
    if ($rawMax -match '^\d+$') { $maxQueued = [int]$rawMax }
    else { $problem = "CI_BLACKSMITH_MAX_QUEUED '$rawMax' is not a whole number" }
  }
  return [pscustomobject]@{
    Default   = & $read 'CI_BLACKSMITH_RUNNER'
    PerClass  = $perClass
    Forks     = (& $read 'CI_BLACKSMITH_FORKS') -eq 'true'
    MaxQueued = $maxQueued
    Problem   = $problem
  }
}

# The Blacksmith label for a class: its own variable, else the default; empty when neither is set.
function Get-BlacksmithLabel {
  param([Parameter(Mandatory)]$Config, [Parameter(Mandatory)][string]$Class)
  if ($Config.PerClass.ContainsKey($Class) -and $Config.PerClass[$Class]) { return $Config.PerClass[$Class] }
  return $Config.Default
}

# Why a Blacksmith label cannot serve a class, or empty when it can.
function Test-BlacksmithLabel {
  param([string]$Label, [string]$Class)
  if ($Label -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { return "'$Label' is not a runner label" }
  $spec = (Get-CiRunnerClass)[$Class]
  if ($spec.Os -eq 'Linux' -and $Label -notmatch 'ubuntu') { return "'$Label' is not a Linux (ubuntu) label, which $Class needs" }
  if ($spec.X64 -and $Label -match '-arm') { return "'$Label' is an ARM label, and $Class needs X64" }
  return ''
}

<#
    The decision, with no I/O. Returns class -> RunsOn (JSON), Tier and Reason.

    PoolState: Group, SelectedRepositories, Runners; $null when it could not be read ($PoolUnavailable says
    why). QueuedJobs: the queued jobs of other runs; $null when they could not be read ($QueueUnavailable).
#>
function Select-CiRunner {
  param(
      [Parameter(Mandatory)][hashtable]$Context,
      [AllowNull()]$PoolState,
      [string]$PoolUnavailable = '',
      [AllowNull()][object[]]$QueuedJobs,
      [string]$QueueUnavailable = '',
      [int]$QueueThreshold = 0,
      [Parameter(Mandatory)]$Blacksmith
  )
  $trust = Get-CiRunTrust -EventName $Context.EventName -Repository $Context.Repository -HeadRepository $Context.HeadRepository `
    -Actor $Context.Actor -PrAuthor $Context.PrAuthor

  # Why the pool cannot serve any class, before looking at labels.
  $poolSkip = ''
  if ($trust.Kind -ne 'trusted') { $poolSkip = $trust.Reason }
  elseif ($null -eq $PoolState) { $poolSkip = $PoolUnavailable }
  elseif ($null -eq $QueuedJobs) { $poolSkip = "the queue could not be read: $QueueUnavailable" }
  else {
    $access = Get-RunnerGroupAccess -Group $PoolState.Group -Repository $Context.Repository `
      -RepositoryPrivate ([bool]$Context.RepositoryPrivate) -SelectedRepositories @($PoolState.SelectedRepositories)
    if (-not $access.Open) { $poolSkip = $access.Reason }
  }

  $decisions = [ordered]@{}
  $classes = Get-CiRunnerClass
  foreach ($name in $classes.Keys) {
    $class = $classes[$name]
    if ($class.HostedOn -contains $Context.EventName) {
      $decisions[$name] = [pscustomobject]@{
        RunsOn = ConvertTo-RunsOnJson $script:HostedRunner; Tier = 'github-hosted'
        Reason = "GitHub-hosted: a $($Context.EventName) run publishes what it compiles, so its compile stays on GitHub-hosted runners"
      }
      continue
    }

    # Tier 1: the self-hosted pool.
    $why = $poolSkip
    if (-not $why) {
      $idle = @(Get-IdleRunner -Runners @($PoolState.Runners) -Labels $class.Labels)
      $competing = Get-CompetingJobCount -QueuedJobs @($QueuedJobs) -IdleRunners $idle
      $counts = "$($idle.Count) idle, $competing queued ahead, threshold $QueueThreshold"
      if ($idle.Count -eq 0) { $why = "no idle runner with labels $($class.Labels -join ', ')" }
      elseif ($idle.Count - $competing -le $QueueThreshold) { $why = "no spare runner ($counts)" }
      else {
        $decisions[$name] = [pscustomobject]@{
          RunsOn = ConvertTo-RunsOnJson $class.Labels; Tier = 'self-hosted'; Reason = "self-hosted pool: $counts"
        }
        continue
      }
    }
    $poolWhy = "self-hosted pool skipped: $why"

    # Tier 2: Blacksmith.
    $label = Get-BlacksmithLabel -Config $Blacksmith -Class $name
    $bsWhy = ''
    $queued = 0
    if (-not $label) { $bsWhy = 'CI_BLACKSMITH_RUNNER is not set' }
    elseif ($Blacksmith.Problem) { $bsWhy = $Blacksmith.Problem }
    elseif ($trust.Kind -eq 'ineligible') { $bsWhy = $trust.Reason }
    elseif ($trust.Kind -eq 'external' -and -not $Blacksmith.Forks) { $bsWhy = "$($trust.Reason), and CI_BLACKSMITH_FORKS is not 'true'" }
    else {
      $bsWhy = Test-BlacksmithLabel -Label $label -Class $name
      if (-not $bsWhy -and $Blacksmith.MaxQueued -gt 0) {
        if ($null -eq $QueuedJobs) { $bsWhy = "the queue could not be read: $QueueUnavailable" }
        else {
          $queued = Get-LabelQueueCount -QueuedJobs $QueuedJobs -Label $label
          if ($queued -ge $Blacksmith.MaxQueued) { $bsWhy = "$queued queued for $label, and CI_BLACKSMITH_MAX_QUEUED is $($Blacksmith.MaxQueued)" }
        }
      }
    }
    if (-not $bsWhy) {
      $limit = if ($Blacksmith.MaxQueued -gt 0) { "; $queued queued, limit $($Blacksmith.MaxQueued)" } else { '' }
      $decisions[$name] = [pscustomobject]@{
        RunsOn = ConvertTo-RunsOnJson $label; Tier = 'blacksmith'; Reason = "Blacksmith ($label)$limit; $poolWhy"
      }
      continue
    }

    # Tier 3.
    $decisions[$name] = [pscustomobject]@{
      RunsOn = ConvertTo-RunsOnJson $script:HostedRunner; Tier = 'github-hosted'
      Reason = "GitHub-hosted ($script:HostedRunner); $poolWhy; Blacksmith skipped: $bsWhy"
    }
  }
  return $decisions
}

<#
    A scriptblock that GETs a GitHub API path with a token: param($Path, $Token). Each request times out
    after -TimeoutSec, and once -BudgetSeconds have passed since the invoker was made, every request throws
    before it is sent, so the whole picker is bounded however many calls it makes.
#>
function New-GitHubApiInvoker {
  param([string]$BaseUrl = 'https://api.github.com', [int]$TimeoutSec = 10, [int]$BudgetSeconds = 60)
  $clock = [System.Diagnostics.Stopwatch]::StartNew()
  $base = $BaseUrl.TrimEnd('/'); $timeout = $TimeoutSec; $budget = $BudgetSeconds
  return {
    param([string]$Path, [string]$Token)
    if ($clock.Elapsed.TotalSeconds -ge $budget) { throw "the picker's $budget-second budget for API calls is spent" }
    $headers = @{ Authorization = "Bearer $Token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    Invoke-RestMethod -Uri "$base$Path" -Headers $headers -TimeoutSec $timeout -MaximumRetryCount 0
  }.GetNewClosure()
}

# Group, selected repositories and runners of the named runner group; Group is $null when there is none.
function Get-RunnerPoolState {
  param([Parameter(Mandatory)][scriptblock]$InvokeApi, [string]$Organization, [string]$GroupName, [string]$PoolToken)
  $groups = & $InvokeApi "/orgs/$Organization/actions/runner-groups?per_page=100" $PoolToken
  $group = @($groups.runner_groups | Where-Object { $_.name -eq $GroupName }) | Select-Object -First 1
  if ($null -eq $group) { return [pscustomobject]@{ Group = $null; SelectedRepositories = @(); Runners = @() } }
  $selected = @()
  if ($group.visibility -eq 'selected') {
    $repos = & $InvokeApi "/orgs/$Organization/actions/runner-groups/$($group.id)/repositories?per_page=100" $PoolToken
    $selected = @($repos.repositories | ForEach-Object { [string]$_.full_name })
  }
  $list = & $InvokeApi "/orgs/$Organization/actions/runner-groups/$($group.id)/runners?per_page=100" $PoolToken
  $runners = @($list.runners | ForEach-Object {
      # Touch every field the decision reads, so a runner of another shape fails here, inside the try.
      [pscustomobject]@{ name = [string]$_.name; status = [string]$_.status; busy = [bool]$_.busy; labels = @($_.labels | ForEach-Object { [pscustomobject]@{ name = [string]$_.name } }) }
    })
  return [pscustomobject]@{ Group = $group; SelectedRepositories = $selected; Runners = $runners }
}

# The jobs of this repository's other queued and running runs (the most recent -MaxRuns of them).
function Get-QueuedJob {
  param([Parameter(Mandatory)][scriptblock]$InvokeApi, [string]$Repository, [string]$RunId, [string]$RepoToken, [int]$MaxRuns = 10)
  $runs = foreach ($status in 'queued', 'in_progress') {
    @((& $InvokeApi "/repos/$Repository/actions/runs?status=$status&per_page=20" $RepoToken).workflow_runs)
  }
  $others = @($runs | Where-Object { [string]$_.id -ne $RunId }) | Select-Object -First $MaxRuns
  foreach ($run in $others) {
    $jobs = & $InvokeApi "/repos/$Repository/actions/runs/$($run.id)/jobs?filter=latest&per_page=100" $RepoToken
    foreach ($job in @($jobs.jobs)) {
      if ([string]$job.status -ne 'queued') { continue }
      [pscustomobject]@{ status = [string]$job.status; labels = @($job.labels | ForEach-Object { [string]$_ }) }
    }
  }
}

# The CI_BLACKSMITH_* variables from the environment.
function Get-EnvironmentVariables {
  $table = @{}
  foreach ($name in $script:BlacksmithVariables) { $table[$name] = [Environment]::GetEnvironmentVariable($name) }
  return $table
}

# Appends lines to a file the runner gave us; a write that fails is reported and never stops the picker.
function Write-RunnerFile {
  param([string]$Path, [string[]]$Lines)
  if (-not $Path) { return }
  try { Add-Content -LiteralPath $Path -Value $Lines -ErrorAction Stop }
  catch { Write-Host "::warning title=Runner selection::Could not write $Path ($($_.Exception.Message)); the jobs default to $script:HostedRunner." }
}

<#
    Reads what it can, decides, and writes every output. Never throws: any failure inside sends every class
    to ubuntu-latest with the failure as the reason.
#>
function Invoke-SelectCiRunner {
  param(
      [string]$EventName, [string]$Repository, [string]$HeadRepository = '', [string]$Actor = '', [string]$PrAuthor = '',
      [string]$RepositoryPrivate = 'false', [string]$Organization = '', [string]$GroupName = 'Default', [string]$RunId = '',
      [string]$PoolToken = '', [string]$RepoToken = '', [string]$Disabled = '', [int]$QueueThreshold = 0,
      [int]$BudgetSeconds = 60, [hashtable]$Variables = @{}, [string]$OutputFile = '', [string]$SummaryFile = '',
      [scriptblock]$InvokeApi
  )
  try {
    if (-not $InvokeApi) { $InvokeApi = New-GitHubApiInvoker -BudgetSeconds $BudgetSeconds }
    $context = @{
      EventName = $EventName; Repository = $Repository; HeadRepository = $HeadRepository; Actor = $Actor
      PrAuthor = $PrAuthor; RepositoryPrivate = ($RepositoryPrivate -eq 'true')
    }
    $trust = Get-CiRunTrust -EventName $EventName -Repository $Repository -HeadRepository $HeadRepository -Actor $Actor -PrAuthor $PrAuthor
    $blacksmith = Get-BlacksmithConfig -Variables $Variables

    $poolState = $null; $poolUnavailable = ''
    if ($Disabled -eq 'true') { $poolUnavailable = 'the RUNNER_POOL_DISABLED variable is true' }
    elseif ($trust.Kind -ne 'trusted') { $poolUnavailable = $trust.Reason }
    elseif (-not $PoolToken) { $poolUnavailable = "no runner-pool token (the RUNNER_POOL_APP_ID and RUNNER_POOL_APP_PRIVATE_KEY secrets, or the app's token). $script:SetupHint" }
    elseif (-not $RepoToken) { $poolUnavailable = 'no repository token to read the queue' }
    elseif (-not $Organization) { $poolUnavailable = 'no organization given' }
    else {
      try { $poolState = Get-RunnerPoolState -InvokeApi $InvokeApi -Organization $Organization -GroupName $GroupName -PoolToken $PoolToken }
      catch { $poolUnavailable = "the runner group could not be read: $($_.Exception.Message)" }
      if ($poolState -and $null -eq $poolState.Group) { $poolState = $null; $poolUnavailable = "runner group '$GroupName' not found" }
    }

    # The queue matters to the pool (when it could serve) and to a Blacksmith queue limit.
    $queuedJobs = $null; $queueUnavailable = 'not read'
    $needQueue = $poolState -or ($blacksmith.MaxQueued -gt 0 -and $trust.Kind -ne 'ineligible')
    if ($needQueue -and $RepoToken) {
      try { $queuedJobs = @(Get-QueuedJob -InvokeApi $InvokeApi -Repository $Repository -RunId $RunId -RepoToken $RepoToken) }
      catch { $queueUnavailable = $_.Exception.Message }
    } elseif ($needQueue) { $queueUnavailable = 'no repository token' }

    $decisions = Select-CiRunner -Context $context -PoolState $poolState -PoolUnavailable $poolUnavailable -QueuedJobs $queuedJobs `
      -QueueUnavailable $queueUnavailable -QueueThreshold $QueueThreshold -Blacksmith $blacksmith
  }
  catch {
    $message = $_.Exception.Message
    $decisions = [ordered]@{}
    foreach ($name in (Get-CiRunnerClass).Keys) {
      $decisions[$name] = [pscustomobject]@{
        RunsOn = ConvertTo-RunsOnJson $script:HostedRunner; Tier = 'github-hosted'
        Reason = "GitHub-hosted ($script:HostedRunner); the picker failed: $message"
      }
    }
  }

  Write-RunnerFile -Path $OutputFile -Lines @($decisions.Keys | ForEach-Object { "$_=$($decisions[$_].RunsOn)" })
  $summary = @('### Runner selection', '', '| Job class | runs-on | Why |', '|---|---|---|')
  $summary += @($decisions.Keys | ForEach-Object { "| $_ | ``$($decisions[$_].RunsOn)`` | $($decisions[$_].Reason -replace '\|', '/') |" })
  Write-RunnerFile -Path $SummaryFile -Lines $summary
  foreach ($name in $decisions.Keys) { Write-Host "$name -> $($decisions[$name].RunsOn): $($decisions[$name].Reason)" }
  return $decisions
}

# Dot-sourcing for tests loads the functions without choosing anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$null = Invoke-SelectCiRunner -EventName $EventName -Repository $Repository -HeadRepository $HeadRepository -Actor $Actor `
  -PrAuthor $PrAuthor -RepositoryPrivate $RepositoryPrivate -Organization $Organization -GroupName $GroupName -RunId $RunId `
  -PoolToken $env:RUNNER_POOL_TOKEN -RepoToken $env:GH_TOKEN -Disabled $Disabled -QueueThreshold $QueueThreshold `
  -Variables (Get-EnvironmentVariables) -OutputFile $OutputFile -SummaryFile $SummaryFile
exit 0
