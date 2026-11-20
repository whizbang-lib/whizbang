#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Chooses where each class of CI job runs: an idle machine in the team's self-hosted pool, else a
    GitHub-hosted runner.

.DESCRIPTION
    Writes one output per job class to $GITHUB_OUTPUT, each a JSON value a job reads with
    `runs-on: ${{ fromJSON(needs.pick-runner.outputs.<class>) }}`: a label set such as
    ["self-hosted","whizbang","Linux","X64"], or a single quoted label such as "ubuntu-latest". A line per
    class goes to $GITHUB_STEP_SUMMARY saying which tier was chosen and, for GitHub-hosted, why the pool was
    skipped.

    Tiers, in order; the first whose every check passes wins:

      1. Self-hosted pool. The run is trusted (a push, a merge-queue run, a dispatch, a schedule, or a pull
         request from a branch of this repository that Dependabot did not open); the RUNNER_POOL_TOKEN can
         read the runner group; the group is open to this repository; an online, idle runner carries every
         label of the class and has a recent passing self-check showing what the class needs; and the jobs of other runs already queued for those runners leave more than
         -QueueThreshold of them spare.
      2. ubuntu-latest.

    Fallback is the default: a missing token, an API error, a timeout, an unexpected response, or an
    exception anywhere sends the class to ubuntu-latest. The script never fails the job; it always exits 0 and always writes every output.

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

.PARAMETER MinUploadMBps
    The upload speed, in MB/s, a machine's self-check must have measured for it to take the compile (the
    RUNNER_POOL_MIN_UPLOAD_MBPS repository variable). Default 10.

.PARAMETER CheckMaxAgeHours
    How old a machine's latest passing self-check may be (the RUNNER_POOL_CHECK_MAX_AGE_HOURS repository variable).
    Default 24.

.PARAMETER OutputFile
    Where outputs are written. Default: $GITHUB_OUTPUT.

.PARAMETER SummaryFile
    Where the summary is written. Default: $GITHUB_STEP_SUMMARY.

.EXAMPLE
    pwsh .github/scripts/Select-CiRunner.ps1 -EventName push -Repository whizbang-lib/whizbang -Organization whizbang-lib

    Tokens come from the environment only: RUNNER_POOL_TOKEN (the GitHub App installation token that can
    read the organization's runners) and GH_TOKEN (this repository's actions, for the queue).
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
    [int]$MinUploadMBps = 10,
    [int]$CheckMaxAgeHours = 24,
    [string]$OutputFile = $env:GITHUB_OUTPUT,
    [string]$SummaryFile = $env:GITHUB_STEP_SUMMARY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:HostedRunner = 'ubuntu-latest'
$script:SetupHint = 'See .github/WORKFLOWS.md, "Runner selection".'

<#
    The job classes, in output order. Labels pick pool machines; a machine is never named. any-os jobs
    run the framework-dependent build (dotnet test --test-modules on the .dll) or build from source, so
    any OS and architecture serves them. compile is Linux X64 because its determinism manifest is
    compared byte for byte with the GitHub-hosted x64 rebuild; linux and linux-docker are kept to X64, the
    architecture their bash steps and service images are verified on. HostedOn lists the events whose
    jobs of the class skip the pool. Pool is false for analyze: its second half (Analyze · Quality) still
    waits up to hours for another run's coverage on a release PR, which on a pool machine would hold a
    machine the suites need, and both halves must check out at the same path (the scan's state names files absolutely),
    which GitHub-hosted runners guarantee and pool machines do not. Requires names what a machine's self-check
    must show beyond passing (#1880): 'upload' a measured upload speed of at least -MinUploadMBps (compile uploads
    the build, about 1 GB), 'docker' a working Docker (the container suites).
#>
function Get-CiRunnerClass {
  $linux = @('self-hosted', 'whizbang', 'Linux', 'X64')
  $classes = [ordered]@{}
  $classes['any-os'] = [pscustomobject]@{ Labels = @('self-hosted', 'whizbang'); HostedOn = @(); Pool = $true; Requires = @() }
  $classes['linux'] = [pscustomobject]@{ Labels = $linux; HostedOn = @(); Pool = $true; Requires = @() }
  $classes['compile'] = [pscustomobject]@{ Labels = $linux; HostedOn = @('push'); Pool = $true; Requires = @('upload') }
  $classes['analyze'] = [pscustomobject]@{ Labels = $linux; HostedOn = @(); Pool = $false; Requires = @() }
  $classes['linux-docker'] = [pscustomobject]@{ Labels = @($linux + 'docker'); HostedOn = @(); Pool = $true; Requires = @('docker') }
  return $classes
}

<#
    Whether the run may use the pool: Kind is 'trusted', 'external' (a fork or Dependabot pull request),
    or 'ineligible' (an event the picker does not know); only a trusted run may. Author association is not
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

<#
    Runner self-checks (#1880). .github/workflows/runner-self-check.yml runs .github/scripts/Test-CiRunnerCapability.ps1
    on each pool machine and records the result as an Actions cache entry: the key carries the runner's name, whether it
    passed, its measured upload speed and whether Docker works, and the entry's creation time says when. The key is
    the record so the picker reads it with one list call and no download. A machine with no recent passing record
    gets no job, so a machine that cannot do the work, or has not been checked lately, is skipped with no label
    changes by anyone.
#>
$script:CheckKeyPrefix = 'ci-runner-check/v1/'

function ConvertTo-RunnerCheckKey {
  param([Parameter(Mandatory)][AllowEmptyString()][string]$RunnerName, [bool]$Ok, [AllowNull()][object]$UploadMBps,
    [bool]$Docker, [Parameter(Mandatory)][string]$Nonce)
  if ($RunnerName -notmatch '^[A-Za-z0-9._-]+$') { throw "runner name '$RunnerName' cannot go in a self-check key" }
  $up = if ($null -eq $UploadMBps) { 'na' } else { [string][int]$UploadMBps }
  return "$script:CheckKeyPrefix$RunnerName/$(if ($Ok) { 'ok' } else { 'fail' })/up-$up/docker-$(if ($Docker) { 'yes' } else { 'no' })/$Nonce"
}

# The record a key holds, with CheckedAt from the entry's creation time (UTC); $null for a key of another shape.
function ConvertFrom-RunnerCheckKey {
  param([string]$Key, [string]$CreatedAt)
  $pattern = '^' + [regex]::Escape($script:CheckKeyPrefix) + '(?<name>[^/]+)/(?<result>ok|fail)/up-(?<up>\d+|na)/docker-(?<docker>yes|no)/'
  if ($Key -notmatch $pattern) { return $null }
  [pscustomobject]@{
    Runner     = $Matches['name']
    Ok         = $Matches['result'] -eq 'ok'
    UploadMBps = if ($Matches['up'] -eq 'na') { $null } else { [int]$Matches['up'] }
    Docker     = $Matches['docker'] -eq 'yes'
    CheckedAt  = [datetime]::Parse($CreatedAt, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]'AdjustToUniversal, AssumeUniversal')
  }
}

# Each runner's latest self-check (runner names compare without case), from the most recent 100 records.
function Get-RunnerCheck {
  param([Parameter(Mandatory)][scriptblock]$InvokeApi, [string]$Repository, [string]$RepoToken)
  $list = & $InvokeApi "/repos/$Repository/actions/caches?key=$script:CheckKeyPrefix&sort=created_at&direction=desc&per_page=100" $RepoToken
  $table = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
  foreach ($entry in @($list.actions_caches)) {
    $record = ConvertFrom-RunnerCheckKey -Key ([string]$entry.key) -CreatedAt ([string]$entry.created_at)
    if ($null -eq $record) { continue }
    if (-not $table.ContainsKey($record.Runner) -or $table[$record.Runner].CheckedAt -lt $record.CheckedAt) { $table[$record.Runner] = $record }
  }
  , $table
}

# Why a runner's latest self-check does not let it take a job of a class requiring -Requires; '' when it does.
function Test-RunnerCheck {
  param([AllowNull()]$Check, [string[]]$Requires = @(), [datetime]$Now, [int]$MaxAgeHours, [int]$MinUploadMBps)
  if ($null -eq $Check) { return 'no self-check recorded' }
  $age = ($Now - $Check.CheckedAt).TotalHours
  if ($age -gt $MaxAgeHours) { return "last self-check $([math]::Floor($age)) hours ago (limit $MaxAgeHours)" }
  if (-not $Check.Ok) { return 'failed its last self-check' }
  if ($Requires -contains 'upload') {
    if ($null -eq $Check.UploadMBps) { return 'upload speed not measured' }
    if ($Check.UploadMBps -lt $MinUploadMBps) { return "uploads at $($Check.UploadMBps) MB/s (needs $MinUploadMBps)" }
  }
  if ($Requires -contains 'docker' -and -not $Check.Docker) { return 'Docker not working' }
  return ''
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

# A runs-on value as JSON: a single label as a string, a label set as an array.
function ConvertTo-RunsOnJson {
  param([Parameter(Mandatory)][object]$Value)
  if ($Value -is [array]) { return (ConvertTo-Json -InputObject @($Value) -Compress) }
  return (ConvertTo-Json -InputObject ([string]$Value) -Compress)
}

<#
    The decision, with no I/O. Returns class -> RunsOn (JSON), Tier and Reason.

    PoolState: Group, SelectedRepositories, Runners; $null when it could not be read ($PoolUnavailable says
    why). QueuedJobs: the queued jobs of other runs; $null when they could not be read ($QueueUnavailable).
    Checks: runner name -> latest self-check (Get-RunnerCheck); $null when they could not be read
    ($ChecksUnavailable). Now is the time the checks' age is measured from.
#>
function Select-CiRunner {
  param(
      [Parameter(Mandatory)][hashtable]$Context,
      [AllowNull()]$PoolState,
      [string]$PoolUnavailable = '',
      [AllowNull()][object[]]$QueuedJobs,
      [string]$QueueUnavailable = '',
      [int]$QueueThreshold = 0,
      [AllowNull()]$Checks,
      [string]$ChecksUnavailable = '',
      [datetime]$Now = [datetime]::UtcNow,
      [int]$CheckMaxAgeHours = 24,
      [int]$MinUploadMBps = 10
  )
  $trust = Get-CiRunTrust -EventName $Context.EventName -Repository $Context.Repository -HeadRepository $Context.HeadRepository `
    -Actor $Context.Actor -PrAuthor $Context.PrAuthor

  # Why the pool cannot serve any class, before looking at labels.
  $poolSkip = ''
  if ($trust.Kind -ne 'trusted') { $poolSkip = $trust.Reason }
  elseif ($null -eq $PoolState) { $poolSkip = $PoolUnavailable }
  elseif ($null -eq $QueuedJobs) { $poolSkip = "the queue could not be read: $QueueUnavailable" }
  elseif ($null -eq $Checks) { $poolSkip = "the runner self-checks could not be read: $ChecksUnavailable" }
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
    if (-not $why -and -not $class.Pool) { $why = 'this job waits on other jobs of the run, and would hold a pool machine they need' }
    if (-not $why) {
      $labelled = @(Get-IdleRunner -Runners @($PoolState.Runners) -Labels $class.Labels)
      # Only a runner whose self-check shows it can do this class's job counts as idle (#1880).
      $idle = @(); $unfit = @()
      foreach ($runner in $labelled) {
        $check = if ($Checks.ContainsKey($runner.name)) { $Checks[$runner.name] } else { $null }
        $unfitWhy = Test-RunnerCheck -Check $check -Requires @($class.Requires) -Now $Now -MaxAgeHours $CheckMaxAgeHours -MinUploadMBps $MinUploadMBps
        if ($unfitWhy) { $unfit += "$($runner.name): $unfitWhy" } else { $idle += $runner }
      }
      $competing = Get-CompetingJobCount -QueuedJobs @($QueuedJobs) -IdleRunners $idle
      $counts = "$($idle.Count) idle, $competing queued ahead, threshold $QueueThreshold"
      if ($labelled.Count -eq 0) { $why = "no idle runner with labels $($class.Labels -join ', ')" }
      elseif ($idle.Count -eq 0) { $why = "no idle runner with labels $($class.Labels -join ', ') passed its self-check ($($unfit -join '; '))" }
      elseif ($idle.Count - $competing -le $QueueThreshold) { $why = "no spare runner ($counts)" }
      else {
        $decisions[$name] = [pscustomobject]@{
          RunsOn = ConvertTo-RunsOnJson $class.Labels; Tier = 'self-hosted'; Reason = "self-hosted pool: $counts"
        }
        continue
      }
    }

    # Tier 2: GitHub-hosted.
    $decisions[$name] = [pscustomobject]@{
      RunsOn = ConvertTo-RunsOnJson $script:HostedRunner; Tier = 'github-hosted'
      Reason = "GitHub-hosted ($script:HostedRunner); self-hosted pool skipped: $why"
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
      [int]$BudgetSeconds = 60, [string]$OutputFile = '', [string]$SummaryFile = '',
      [scriptblock]$InvokeApi, [datetime]$Now = [datetime]::UtcNow, [int]$CheckMaxAgeHours = 24, [int]$MinUploadMBps = 10
  )
  try {
    if (-not $InvokeApi) { $InvokeApi = New-GitHubApiInvoker -BudgetSeconds $BudgetSeconds }
    $context = @{
      EventName = $EventName; Repository = $Repository; HeadRepository = $HeadRepository; Actor = $Actor
      PrAuthor = $PrAuthor; RepositoryPrivate = ($RepositoryPrivate -eq 'true')
    }
    $trust = Get-CiRunTrust -EventName $EventName -Repository $Repository -HeadRepository $HeadRepository -Actor $Actor -PrAuthor $PrAuthor

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

    # The self-checks and the queue matter only when the pool could serve (its token checks above ensure $RepoToken is set).
    $checks = $null; $checksUnavailable = 'not read'
    $queuedJobs = $null; $queueUnavailable = 'not read'
    if ($poolState) {
      try { $checks = Get-RunnerCheck -InvokeApi $InvokeApi -Repository $Repository -RepoToken $RepoToken }
      catch { $checksUnavailable = $_.Exception.Message }
      try { $queuedJobs = @(Get-QueuedJob -InvokeApi $InvokeApi -Repository $Repository -RunId $RunId -RepoToken $RepoToken) }
      catch { $queueUnavailable = $_.Exception.Message }
    }

    $decisions = Select-CiRunner -Context $context -PoolState $poolState -PoolUnavailable $poolUnavailable -QueuedJobs $queuedJobs `
      -QueueUnavailable $queueUnavailable -QueueThreshold $QueueThreshold -Checks $checks -ChecksUnavailable $checksUnavailable `
      -Now $Now -CheckMaxAgeHours $CheckMaxAgeHours -MinUploadMBps $MinUploadMBps
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

  # pool: whether any class went to the pool, so the watcher (Watch-CiRunnerJob.ps1) runs only when there is a pool job to watch.
  $pool = @($decisions.Values | Where-Object { $_.Tier -eq 'self-hosted' }).Count -gt 0
  Write-RunnerFile -Path $OutputFile -Lines (@($decisions.Keys | ForEach-Object { "$_=$($decisions[$_].RunsOn)" }) + "pool=$($pool.ToString().ToLowerInvariant())")
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
  -MinUploadMBps $MinUploadMBps -CheckMaxAgeHours $CheckMaxAgeHours -OutputFile $OutputFile -SummaryFile $SummaryFile
exit 0
