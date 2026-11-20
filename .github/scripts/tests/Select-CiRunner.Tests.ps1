# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The runner picker chooses, per job class, a team-owned self-hosted machine in the W! pool when every
# check passes, and a GitHub-hosted runner otherwise. These tests cover each routing branch and each way
# the pool is skipped: an untrusted run, a missing token, a closed or missing runner group, no idle
# machine with the labels, a queue ahead of the run, and every API failure (an error, a timeout, an
# unexpected response). The GitHub API is replaced by a scriptblock, except in the tests of
# the real invoker, which use a local listener.

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Select-CiRunner.ps1'
  . $ScriptPath

  $script:Repo = 'whizbang-lib/whizbang'
  $script:Hosted = '"ubuntu-latest"'
  $script:AnyOs = '["self-hosted","whizbang"]'
  $script:Linux = '["self-hosted","whizbang","Linux","X64"]'
  $script:LinuxDocker = '["self-hosted","whizbang","Linux","X64","docker"]'
  $script:ChecksPath = "/repos/$script:Repo/actions/caches?key=ci-runner-check/v1/&sort=created_at&direction=desc&per_page=100"

  function New-Runner {
    param([string]$Name = 'r1', [string]$Status = 'online', [bool]$Busy = $false,
      [string[]]$Labels = @('self-hosted', 'whizbang', 'Linux', 'X64', 'docker'))
    [pscustomobject]@{
      id     = 1
      name   = $Name
      status = $Status
      busy   = $Busy
      labels = @($Labels | ForEach-Object { [pscustomobject]@{ name = $_; type = 'custom' } })
    }
  }

  function New-Group {
    param([string]$Visibility = 'all', [bool]$AllowsPublic = $true, [bool]$Restricted = $false, [string]$Name = 'Default')
    [pscustomobject]@{
      id                         = 7
      name                       = $Name
      visibility                 = $Visibility
      allows_public_repositories = $AllowsPublic
      restricted_to_workflows    = $Restricted
    }
  }

  function New-Job {
    param([string[]]$Labels, [string]$Status = 'queued')
    [pscustomobject]@{ status = $Status; labels = $Labels }
  }

  function New-Context {
    param([string]$EventName = 'push', [string]$HeadRepository = '', [string]$Actor = 'someone',
      [string]$PrAuthor = '', [bool]$RepositoryPrivate = $false)
    @{
      EventName = $EventName; Repository = $script:Repo; HeadRepository = $HeadRepository
      Actor = $Actor; PrAuthor = $PrAuthor; RepositoryPrivate = $RepositoryPrivate
    }
  }

  # A pool state: an open group and one idle machine carrying every pool label, unless overridden.
  function New-PoolState {
    param($Group = (New-Group), [string[]]$Selected = @(), [object[]]$Runners = @(New-Runner))
    [pscustomobject]@{ Group = $Group; SelectedRepositories = $Selected; Runners = $Runners }
  }

  # The fixed "now" of every capability decision: no test reads the clock (#1880).
  $script:Now = [datetime]::new(2026, 11, 20, 12, 0, 0, [DateTimeKind]::Utc)

  # A self-check record, as Get-RunnerCheck returns it: passed an hour ago, uploading at 100 MB/s with Docker working.
  function New-Check {
    param([string]$Runner = 'r1', [bool]$Ok = $true, [AllowNull()][object]$Up = 100, [bool]$Docker = $true, [double]$HoursAgo = 1)
    [pscustomobject]@{ Runner = $Runner; Ok = $Ok; UploadMBps = $Up; Docker = $Docker; CheckedAt = $script:Now.AddHours(-$HoursAgo) }
  }

  # A runner name -> latest self-check table, as Get-RunnerCheck returns it.
  function New-CheckTable {
    param([object[]]$Checks = @())
    $table = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($c in $Checks) { $table[$c.Runner] = $c }
    , $table
  }

  # Select-CiRunner for a trusted push with an open pool and an empty queue, unless overridden. Unless -Checks is
  # given, every runner of the pool state passed its self-check an hour ago.
  function Invoke-Select {
    param([hashtable]$Context = (New-Context), $PoolState = (New-PoolState), [string]$PoolUnavailable = '',
      $QueuedJobs = @(), [string]$QueueUnavailable = '', [int]$QueueThreshold = 0, [switch]$NoChecks,
      $Checks = $null, [string]$ChecksUnavailable = '', [int]$CheckMaxAgeHours = 24, [int]$MinUploadMBps = 10)
    if (-not $NoChecks -and $null -eq $Checks) {
      $Checks = New-CheckTable @($(if ($PoolState) { @($PoolState.Runners) | ForEach-Object { New-Check -Runner $_.name } }))
    }
    Select-CiRunner -Context $Context -PoolState $PoolState -PoolUnavailable $PoolUnavailable -QueuedJobs $QueuedJobs `
      -QueueUnavailable $QueueUnavailable -QueueThreshold $QueueThreshold -Checks $Checks -ChecksUnavailable $ChecksUnavailable `
      -Now $script:Now -CheckMaxAgeHours $CheckMaxAgeHours -MinUploadMBps $MinUploadMBps
  }

  # A fake GitHub API: answers each path from $Responses, records the calls, throws for a path it was
  # told to fail on, and throws for a path nobody expected.
  function New-FakeApi {
    param([System.Collections.IDictionary]$Responses, [string]$FailOn = '')
    $script:ApiCalls = [System.Collections.Generic.List[string]]::new()
    $responses = $Responses; $failOn = $FailOn; $calls = $script:ApiCalls
    {
      param([string]$Path, [string]$Token)
      $calls.Add("$Token $Path")
      if ($failOn -and $Path -like "*$failOn*") { throw "simulated failure for $Path" }
      if ($responses.Contains($Path)) { return $responses[$Path] }
      throw "unexpected API call: $Path"
    }.GetNewClosure()
  }

  # The answers of an org with one open group, one idle machine and an empty queue.
  function Get-PoolResponse {
    param($Group = (New-Group), [object[]]$Runners = @(New-Runner), [object[]]$QueuedRuns = @(),
      [object[]]$RunningRuns = @(), [hashtable]$Jobs = @{}, [string[]]$Selected = @(), [object[]]$Caches = $null)
    # Unless -Caches is given, every runner passed its self-check an hour ago.
    if ($null -eq $Caches) {
      $Caches = @($Runners | ForEach-Object {
          [pscustomobject]@{ key = (ConvertTo-RunnerCheckKey -RunnerName $_.name -Ok $true -UploadMBps 100 -Docker $true -Nonce '1-1'); created_at = $script:Now.AddHours(-1).ToString('o') }
        })
    }
    $r = [ordered]@{
      $script:ChecksPath = [pscustomobject]@{ total_count = $Caches.Count; actions_caches = $Caches }
      '/orgs/whizbang-lib/actions/runner-groups?per_page=100'                 = [pscustomobject]@{ total_count = 1; runner_groups = @($Group) }
      '/orgs/whizbang-lib/actions/runner-groups/7/runners?per_page=100'       = [pscustomobject]@{ total_count = $Runners.Count; runners = $Runners }
      '/orgs/whizbang-lib/actions/runner-groups/7/repositories?per_page=100'  = [pscustomobject]@{
        total_count = $Selected.Count; repositories = @($Selected | ForEach-Object { [pscustomobject]@{ full_name = $_ } })
      }
      "/repos/$script:Repo/actions/runs?status=queued&per_page=20"      = [pscustomobject]@{ total_count = $QueuedRuns.Count; workflow_runs = $QueuedRuns }
      "/repos/$script:Repo/actions/runs?status=in_progress&per_page=20" = [pscustomobject]@{ total_count = $RunningRuns.Count; workflow_runs = $RunningRuns }
    }
    foreach ($id in $Jobs.Keys) {
      $r["/repos/$script:Repo/actions/runs/$id/jobs?filter=latest&per_page=100"] = [pscustomobject]@{ jobs = $Jobs[$id] }
    }
    $r
  }

  # Invoke-SelectCiRunner for a trusted merge-queue run with both tokens, writing to TestDrive files.
  function Invoke-Entry {
    param([scriptblock]$Api, [string]$EventName = 'merge_group', [string]$HeadRepository = '', [string]$Actor = 'someone',
      [string]$PrAuthor = '', [string]$PoolToken = 'pool-token', [string]$RepoToken = 'repo-token',
      [string]$Disabled = '', [string]$Organization = 'whizbang-lib', [string]$RunId = '1000', [int]$BudgetSeconds = 60)
    $script:OutPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N')).txt"
    $script:SummaryPath = Join-Path $TestDrive "summary-$([guid]::NewGuid().ToString('N')).md"
    Invoke-SelectCiRunner -EventName $EventName -Repository $script:Repo -HeadRepository $HeadRepository -Actor $Actor `
      -PrAuthor $PrAuthor -RepositoryPrivate 'false' -Organization $Organization -GroupName 'Default' -RunId $RunId `
      -PoolToken $PoolToken -RepoToken $RepoToken -Disabled $Disabled -QueueThreshold 0 -BudgetSeconds $BudgetSeconds `
      -OutputFile $script:OutPath -SummaryFile $script:SummaryPath -InvokeApi $Api -Now $script:Now
  }

  # The outputs the entry point wrote, as a name -> value table.
  function Read-Output {
    $table = @{}
    foreach ($line in (Get-Content -LiteralPath $script:OutPath)) {
      $name, $value = $line -split '=', 2
      $table[$name] = $value
    }
    $table
  }
}

Describe 'Get-CiRunTrust' {
  It 'trusts a <EventName> run' -ForEach @(
    @{ EventName = 'push' }
    @{ EventName = 'merge_group' }
    @{ EventName = 'workflow_dispatch' }
    @{ EventName = 'schedule' }
  ) {
    $t = Get-CiRunTrust -EventName $EventName -Repository $Repo -HeadRepository '' -Actor 'someone' -PrAuthor ''
    $t.Kind | Should -Be 'trusted'
    $t.Reason | Should -BeLike "*$EventName*"
  }

  It 'trusts a pull request from a branch of this repository, whatever the case of the name' {
    $t = Get-CiRunTrust -EventName 'pull_request' -Repository $Repo -HeadRepository 'Whizbang-Lib/Whizbang' -Actor 'someone' -PrAuthor 'someone'
    $t.Kind | Should -Be 'trusted'
  }

  It 'treats a pull request from a fork as external' {
    $t = Get-CiRunTrust -EventName 'pull_request' -Repository $Repo -HeadRepository 'someone/whizbang' -Actor 'someone' -PrAuthor 'someone'
    $t.Kind | Should -Be 'external'
    $t.Reason | Should -BeLike '*fork*'
  }

  It 'treats a pull request whose head repository is unknown (a deleted fork) as external' {
    (Get-CiRunTrust -EventName 'pull_request' -Repository $Repo -HeadRepository '' -Actor 'someone' -PrAuthor 'someone').Kind |
      Should -Be 'external'
  }

  It 'treats a Dependabot pull request as external, by <Who>' -ForEach @(
    @{ Who = 'author'; Actor = 'someone'; Author = 'dependabot[bot]' }
    @{ Who = 'actor'; Actor = 'dependabot[bot]'; Author = 'someone' }
    @{ Who = 'actor, any case'; Actor = 'Dependabot[bot]'; Author = 'someone' }
  ) {
    $t = Get-CiRunTrust -EventName 'pull_request' -Repository $Repo -HeadRepository $Repo -Actor $Actor -PrAuthor $Author
    $t.Kind | Should -Be 'external'
    $t.Reason | Should -BeLike '*Dependabot*'
  }

  It 'treats a Dependabot push as external' {
    (Get-CiRunTrust -EventName 'push' -Repository $Repo -HeadRepository '' -Actor 'dependabot[bot]' -PrAuthor '').Kind |
      Should -Be 'external'
  }

  It 'makes an event it does not know ineligible (<EventName>)' -ForEach @(
    @{ EventName = 'pull_request_target' }
    @{ EventName = 'issue_comment' }
    @{ EventName = '' }
  ) {
    $t = Get-CiRunTrust -EventName $EventName -Repository $Repo -HeadRepository $Repo -Actor 'someone' -PrAuthor 'someone'
    $t.Kind | Should -Be 'ineligible'
    $t.Reason | Should -BeLike '*not eligible*'
  }
}

Describe 'Get-RunnerGroupAccess' {
  It 'is open to a public repository when the group is visible to all and allows public repositories' {
    (Get-RunnerGroupAccess -Group (New-Group) -Repository $Repo -RepositoryPrivate $false -SelectedRepositories @()).Open |
      Should -BeTrue
  }

  It 'is closed when there is no group' {
    $a = Get-RunnerGroupAccess -Group $null -Repository $Repo -RepositoryPrivate $false -SelectedRepositories @()
    $a.Open | Should -BeFalse
    $a.Reason | Should -BeLike '*not found*'
  }

  It 'is closed to a public repository when the group does not allow public repositories' {
    $a = Get-RunnerGroupAccess -Group (New-Group -AllowsPublic $false) -Repository $Repo -RepositoryPrivate $false -SelectedRepositories @()
    $a.Open | Should -BeFalse
    $a.Reason | Should -BeLike '*public repositories*'
  }

  It 'ignores the public-repository setting for a private repository' {
    (Get-RunnerGroupAccess -Group (New-Group -AllowsPublic $false) -Repository $Repo -RepositoryPrivate $true -SelectedRepositories @()).Open |
      Should -BeTrue
  }

  It 'is open when the group selects this repository, whatever the case' {
    (Get-RunnerGroupAccess -Group (New-Group -Visibility 'selected') -Repository $Repo -RepositoryPrivate $false `
        -SelectedRepositories @('whizbang-lib/other', 'Whizbang-Lib/Whizbang')).Open | Should -BeTrue
  }

  It 'is closed when the group selects only other repositories' {
    $a = Get-RunnerGroupAccess -Group (New-Group -Visibility 'selected') -Repository $Repo -RepositoryPrivate $false `
      -SelectedRepositories @('whizbang-lib/other')
    $a.Open | Should -BeFalse
    $a.Reason | Should -BeLike '*not one of the repositories*'
  }

  It 'is closed to a public repository when the group is for private repositories only' {
    $a = Get-RunnerGroupAccess -Group (New-Group -Visibility 'private') -Repository $Repo -RepositoryPrivate $false -SelectedRepositories @()
    $a.Open | Should -BeFalse
    $a.Reason | Should -BeLike '*private repositories*'
  }

  It 'is open to a private repository when the group is for private repositories' {
    (Get-RunnerGroupAccess -Group (New-Group -Visibility 'private') -Repository $Repo -RepositoryPrivate $true -SelectedRepositories @()).Open |
      Should -BeTrue
  }

  It 'is closed when the group is restricted to selected workflows' {
    $a = Get-RunnerGroupAccess -Group (New-Group -Restricted $true) -Repository $Repo -RepositoryPrivate $false -SelectedRepositories @()
    $a.Open | Should -BeFalse
    $a.Reason | Should -BeLike '*selected workflows*'
  }

  It 'is closed when the group visibility is one it does not know' {
    $a = Get-RunnerGroupAccess -Group (New-Group -Visibility 'enterprise') -Repository $Repo -RepositoryPrivate $false -SelectedRepositories @()
    $a.Open | Should -BeFalse
    $a.Reason | Should -BeLike "*visibility 'enterprise'*"
  }
}

Describe 'Get-IdleRunner' {
  It 'returns the online, idle runners that carry every label, whatever the case' {
    $runners = @(
      (New-Runner -Name 'match' -Labels @('self-hosted', 'WHIZBANG', 'linux', 'X64'))
      (New-Runner -Name 'busy' -Busy $true)
      (New-Runner -Name 'offline' -Status 'offline')
      (New-Runner -Name 'windows' -Labels @('self-hosted', 'whizbang', 'Windows', 'X64'))
      (New-Runner -Name 'not-pool' -Labels @('self-hosted', 'Linux', 'X64'))
    )
    $idle = @(Get-IdleRunner -Runners $runners -Labels @('self-hosted', 'whizbang', 'Linux', 'X64'))
    $idle.name | Should -Be @('match')
  }

  It 'returns nothing for no runners' {
    @(Get-IdleRunner -Runners @() -Labels @('self-hosted')).Count | Should -Be 0
  }
}

Describe 'Get-CompetingJobCount' {
  It 'counts the queued self-hosted jobs some idle runner could take' {
    $idle = @(New-Runner -Labels @('self-hosted', 'whizbang', 'Linux', 'X64'))
    $jobs = @(
      (New-Job @('self-hosted', 'whizbang'))                     # competes
      (New-Job @('self-hosted', 'whizbang', 'linux'))            # competes, any case
      (New-Job @('self-hosted', 'whizbang', 'Windows'))          # no idle runner fits
      (New-Job @('ubuntu-latest'))                               # GitHub-hosted
      (New-Job @('self-hosted', 'whizbang') -Status 'in_progress') # already running
    )
    Get-CompetingJobCount -QueuedJobs $jobs -IdleRunners $idle | Should -Be 2
  }

  It 'is zero with no idle runners or no jobs' {
    Get-CompetingJobCount -QueuedJobs @(New-Job @('self-hosted')) -IdleRunners @() | Should -Be 0
    Get-CompetingJobCount -QueuedJobs @() -IdleRunners @(New-Runner) | Should -Be 0
  }
}

Describe 'ConvertTo-RunsOnJson' {
  It 'renders a single label as a JSON string and a label set as a JSON array' {
    ConvertTo-RunsOnJson -Value 'ubuntu-latest' | Should -Be $Hosted
    ConvertTo-RunsOnJson -Value @('self-hosted', 'whizbang') | Should -Be $AnyOs
  }
}

Describe 'Get-CiRunnerClass' {
  It 'defines the job classes by labels, never machine names' {
    $classes = Get-CiRunnerClass
    @($classes.Keys) | Should -Be @('any-os', 'linux', 'compile', 'analyze', 'linux-docker')
    $classes['any-os'].Labels | Should -Be @('self-hosted', 'whizbang')
    $classes['linux'].Labels | Should -Be @('self-hosted', 'whizbang', 'Linux', 'X64')
    $classes['compile'].Labels | Should -Be @('self-hosted', 'whizbang', 'Linux', 'X64')
    $classes['analyze'].Labels | Should -Be @('self-hosted', 'whizbang', 'Linux', 'X64')
    $classes['linux-docker'].Labels | Should -Be @('self-hosted', 'whizbang', 'Linux', 'X64', 'docker')
  }

  It 'keeps only the compile of a push run, the one that publishes, on GitHub-hosted runners' {
    $classes = Get-CiRunnerClass
    $classes['compile'].HostedOn | Should -Be @('push')
    foreach ($name in 'any-os', 'linux', 'analyze', 'linux-docker') { $classes[$name].HostedOn | Should -BeNullOrEmpty }
  }

  It 'asks upload speed of the compile, which uploads the build, and Docker of the container suites (#1880)' {
    $classes = Get-CiRunnerClass
    $classes['compile'].Requires | Should -Be @('upload')
    $classes['linux-docker'].Requires | Should -Be @('docker')
    foreach ($name in 'any-os', 'linux', 'analyze') { $classes[$name].Requires | Should -BeNullOrEmpty }
  }

  It 'keeps the pool from only the analysis, which waits hours on other jobs that may need the same machine' {
    $classes = Get-CiRunnerClass
    $classes['analyze'].Pool | Should -BeFalse
    foreach ($name in 'any-os', 'linux', 'compile', 'linux-docker') { $classes[$name].Pool | Should -BeTrue }
  }
}

Describe 'Select-CiRunner' {
  Context 'tier 1: the self-hosted pool' {
    It 'routes every class to the pool when the run is trusted, the group open and a machine idle' {
      $d = Invoke-Select -Context (New-Context -EventName 'pull_request' -HeadRepository $Repo -PrAuthor 'someone')
      $d['any-os'].RunsOn | Should -Be $AnyOs
      $d['linux'].RunsOn | Should -Be $Linux
      $d['compile'].RunsOn | Should -Be $Linux
      $d['linux-docker'].RunsOn | Should -Be $LinuxDocker
      $d['analyze'].RunsOn | Should -Be $Hosted
      $d['linux'].Tier | Should -Be 'self-hosted'
      $d['linux'].Reason | Should -BeLike 'self-hosted pool: 1 idle*0 queued*'
    }

    It 'routes each trusted event to the pool (<EventName>)' -ForEach @(
      @{ EventName = 'push' }
      @{ EventName = 'merge_group' }
      @{ EventName = 'workflow_dispatch' }
      @{ EventName = 'schedule' }
    ) {
      (Invoke-Select -Context (New-Context -EventName $EventName))['linux'].RunsOn | Should -Be $Linux
    }

    It 'routes past a queue that leaves a machine spare' {
      $d = Invoke-Select -QueuedJobs @(New-Job @('self-hosted', 'whizbang')) -PoolState (New-PoolState -Runners @((New-Runner -Name 'a'), (New-Runner -Name 'b')))
      $d['linux'].RunsOn | Should -Be $Linux
    }

    It 'needs more spare machines than the threshold' {
      $two = New-PoolState -Runners @((New-Runner -Name 'a'), (New-Runner -Name 'b'))
      (Invoke-Select -PoolState $two -QueueThreshold 2)['linux'].RunsOn | Should -Be $Hosted
      (Invoke-Select -PoolState $two -QueueThreshold 1)['linux'].RunsOn | Should -Be $Linux
    }
  }

  Context 'tier 1 skipped' {
    It 'skips the pool for every class on <Case>' -ForEach @(
      @{ Case = 'a fork pull request'; Context = @{ EventName = 'pull_request'; Repository = 'whizbang-lib/whizbang'; HeadRepository = 'someone/whizbang'; Actor = 'someone'; PrAuthor = 'someone'; RepositoryPrivate = $false }; Why = '*fork*' }
      @{ Case = 'a Dependabot pull request'; Context = @{ EventName = 'pull_request'; Repository = 'whizbang-lib/whizbang'; HeadRepository = 'whizbang-lib/whizbang'; Actor = 'dependabot[bot]'; PrAuthor = 'dependabot[bot]'; RepositoryPrivate = $false }; Why = '*Dependabot*' }
      @{ Case = 'an ineligible event'; Context = @{ EventName = 'pull_request_target'; Repository = 'whizbang-lib/whizbang'; HeadRepository = ''; Actor = 'someone'; PrAuthor = ''; RepositoryPrivate = $false }; Why = '*not eligible*' }
    ) {
      $d = Invoke-Select -Context $Context
      foreach ($class in $d.Keys) {
        $d[$class].RunsOn | Should -Be $Hosted
        $d[$class].Tier | Should -Be 'github-hosted'
        $d[$class].Reason | Should -BeLike $Why
      }
    }

    It 'skips the pool when its state could not be read' {
      $d = Invoke-Select -PoolState $null -PoolUnavailable 'no runner-pool token'
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*self-hosted pool skipped: no runner-pool token*'
    }

    It 'skips the pool when the queue could not be read' {
      $d = Invoke-Select -QueuedJobs $null -QueueUnavailable 'boom'
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*queue*boom*'
    }

    It 'skips the pool when the group is closed to the repository' {
      $d = Invoke-Select -PoolState (New-PoolState -Group (New-Group -AllowsPublic $false))
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*public repositories*'
    }

    It 'skips the pool when the group selects other repositories' {
      (Invoke-Select -PoolState (New-PoolState -Group (New-Group -Visibility 'selected') -Selected @('whizbang-lib/other')))['any-os'].RunsOn |
        Should -Be $Hosted
    }

    It 'skips the pool when it has no machines' {
      $d = Invoke-Select -PoolState (New-PoolState -Runners @())
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*no idle*'
    }

    It 'skips the pool when every machine is <Case>' -ForEach @(
      @{ Case = 'busy'; Runner = @{ Busy = $true } }
      @{ Case = 'offline'; Runner = @{ Status = 'offline' } }
    ) {
      $d = Invoke-Select -PoolState (New-PoolState -Runners @(New-Runner @Runner))
      foreach ($class in $d.Keys) { $d[$class].RunsOn | Should -Be $Hosted }
    }

    It 'skips the pool only for the classes whose labels no idle machine carries' {
      $d = Invoke-Select -PoolState (New-PoolState -Runners @(New-Runner -Labels @('self-hosted', 'whizbang', 'Windows', 'ARM64')))
      $d['any-os'].RunsOn | Should -Be $AnyOs
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux-docker'].RunsOn | Should -Be $Hosted
      $d['linux-docker'].Reason | Should -BeLike '*no idle*Linux*X64*docker*'
    }

    It 'skips the pool when the queue ahead takes every idle machine' {
      $d = Invoke-Select -QueuedJobs @(New-Job @('self-hosted', 'whizbang'))
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*1 idle*1 queued*'
    }

    It 'keeps the compile of a push run on GitHub-hosted runners while its tests use the pool' {
      $d = Invoke-Select -Context (New-Context -EventName 'push')
      $d['compile'].RunsOn | Should -Be $Hosted
      $d['compile'].Tier | Should -Be 'github-hosted'
      $d['compile'].Reason | Should -BeLike '*push*publish*'
      $d['linux'].RunsOn | Should -Be $Linux
    }

    It 'never sends the analysis to the pool, taking GitHub-hosted instead' {
      $d = Invoke-Select
      $d['analyze'].RunsOn | Should -Be $Hosted
      $d['analyze'].Tier | Should -Be 'github-hosted'
      $d['analyze'].Reason | Should -BeLike '*self-hosted pool skipped: this job waits on other jobs*'
    }

    It 'routes the compile of a pull request like any other class' {
      (Invoke-Select -Context (New-Context -EventName 'pull_request' -HeadRepository $Repo))['compile'].RunsOn | Should -Be $Linux
    }
  }

  Context 'tier 2: GitHub-hosted' {
    It 'falls back to GitHub-hosted, naming why the pool was skipped and no other tier' {
      $d = Invoke-Select -PoolState (New-PoolState -Runners @())
      foreach ($class in $d.Keys) {
        $d[$class].RunsOn | Should -Be $Hosted
        $d[$class].Tier | Should -Be 'github-hosted'
      }
      $d['linux'].Reason | Should -Be 'GitHub-hosted (ubuntu-latest); self-hosted pool skipped: no idle runner with labels self-hosted, whizbang, Linux, X64'
    }
  }
}

Describe 'Get-RunnerPoolState' {
  It 'reads the group and its runners with the pool token' {
    $api = New-FakeApi (Get-PoolResponse)
    $state = Get-RunnerPoolState -InvokeApi $api -Organization 'whizbang-lib' -GroupName 'default' -PoolToken 'pool'
    $state.Group.name | Should -Be 'Default'
    @($state.Runners).Count | Should -Be 1
    @($state.SelectedRepositories).Count | Should -Be 0
    $ApiCalls | Should -Be @('pool /orgs/whizbang-lib/actions/runner-groups?per_page=100', 'pool /orgs/whizbang-lib/actions/runner-groups/7/runners?per_page=100')
  }

  It 'reads the selected repositories only for a group with selected visibility' {
    $api = New-FakeApi (Get-PoolResponse -Group (New-Group -Visibility 'selected') -Selected @($Repo))
    $state = Get-RunnerPoolState -InvokeApi $api -Organization 'whizbang-lib' -GroupName 'Default' -PoolToken 'pool'
    $state.SelectedRepositories | Should -Be @($Repo)
  }

  It 'returns no group and reads nothing more when the group does not exist' {
    $api = New-FakeApi (Get-PoolResponse -Group (New-Group -Name 'Other'))
    $state = Get-RunnerPoolState -InvokeApi $api -Organization 'whizbang-lib' -GroupName 'Default' -PoolToken 'pool'
    $state.Group | Should -BeNullOrEmpty
    $ApiCalls.Count | Should -Be 1
  }
}

Describe 'Get-QueuedJob' {
  It 'reads the queued jobs of the other queued and running runs with the repository token' {
    $api = New-FakeApi (Get-PoolResponse -QueuedRuns @([pscustomobject]@{ id = 11 }) -RunningRuns @([pscustomobject]@{ id = 12 }, [pscustomobject]@{ id = 1000 }) -Jobs @{
        11 = @(New-Job @('self-hosted', 'whizbang'))
        12 = @((New-Job @('ubuntu-latest') -Status 'completed'), (New-Job @('ubuntu-latest')))
      })
    $jobs = @(Get-QueuedJob -InvokeApi $api -Repository $Repo -RunId '1000' -RepoToken 'repo')
    $jobs.Count | Should -Be 2
    @($jobs | Where-Object { $_.status -ne 'queued' }).Count | Should -Be 0
    $ApiCalls | Should -Contain "repo /repos/$Repo/actions/runs/11/jobs?filter=latest&per_page=100"
    $ApiCalls | Where-Object { $_ -like '*runs/1000/*' } | Should -BeNullOrEmpty
  }

  It 'looks at no more than MaxRuns runs' {
    $runs = @(1..5 | ForEach-Object { [pscustomobject]@{ id = $_ } })
    $jobs = @{}; foreach ($i in 1..5) { $jobs[$i] = @() }
    $api = New-FakeApi (Get-PoolResponse -QueuedRuns $runs -Jobs $jobs)
    $null = Get-QueuedJob -InvokeApi $api -Repository $Repo -RunId '1000' -RepoToken 'repo' -MaxRuns 3
    @($ApiCalls | Where-Object { $_ -like '*/jobs?*' }).Count | Should -Be 3
  }
}

Describe 'Invoke-SelectCiRunner' {
  It 'writes the pool label sets and a summary when everything checks out' {
    $decisions = Invoke-Entry -Api (New-FakeApi (Get-PoolResponse)) -EventName 'pull_request' -HeadRepository $Repo -PrAuthor 'someone'
    $out = Read-Output
    $out['linux'] | Should -Be $Linux
    $out['compile'] | Should -Be $Linux
    $out['any-os'] | Should -Be $AnyOs
    $out['linux-docker'] | Should -Be $LinuxDocker
    $out['analyze'] | Should -Be $Hosted
    $decisions['linux'].Tier | Should -Be 'self-hosted'
    Get-Content -Raw -LiteralPath $SummaryPath | Should -BeLike '*linux*self-hosted pool*'
  }

  It 'skips the pool on <Case> without calling the API, and falls back' -ForEach @(
    @{ Case = 'a fork pull request'; Arguments = @{ EventName = 'pull_request'; HeadRepository = 'someone/whizbang'; PrAuthor = 'someone' }; Why = '*fork*' }
    @{ Case = 'a Dependabot pull request'; Arguments = @{ EventName = 'pull_request'; HeadRepository = 'whizbang-lib/whizbang'; Actor = 'dependabot[bot]'; PrAuthor = 'dependabot[bot]' }; Why = '*Dependabot*' }
    @{ Case = 'no runner-pool token (the app secrets are not set)'; Arguments = @{ PoolToken = '' }; Why = '*RUNNER_POOL_APP*' }
    @{ Case = 'no repository token'; Arguments = @{ RepoToken = '' }; Why = '*repository token*' }
    @{ Case = 'the pool switched off'; Arguments = @{ Disabled = 'true' }; Why = '*RUNNER_POOL_DISABLED*' }
    @{ Case = 'no organization'; Arguments = @{ Organization = '' }; Why = '*organization*' }
  ) {
    $api = New-FakeApi @{}
    $decisions = Invoke-Entry -Api $api @Arguments
    $ApiCalls.Count | Should -Be 0
    $out = Read-Output
    foreach ($class in $decisions.Keys) {
      $out[$class] | Should -Be $Hosted
      $decisions[$class].Reason | Should -BeLike $Why
    }
    Get-Content -Raw -LiteralPath $SummaryPath | Should -BeLike $Why
  }

  It 'falls back when the API call for <Failing> fails' -ForEach @(
    @{ Failing = 'runner-groups?' }
    @{ Failing = '/runners' }
    @{ Failing = '/repositories' }
    @{ Failing = 'status=queued' }
    @{ Failing = 'status=in_progress' }
    @{ Failing = '/jobs' }
    @{ Failing = 'actions/caches' }
  ) {
    $responses = Get-PoolResponse -Group (New-Group -Visibility 'selected') -Selected @($Repo) `
      -QueuedRuns @([pscustomobject]@{ id = 11 }) -Jobs @{ 11 = @() }
    $decisions = Invoke-Entry -Api (New-FakeApi $responses -FailOn $Failing)
    $out = Read-Output
    foreach ($class in $decisions.Keys) { $out[$class] | Should -Be $Hosted }
    $decisions['linux'].Reason | Should -BeLike '*simulated failure*'
    Get-Content -Raw -LiteralPath $SummaryPath | Should -BeLike '*GitHub-hosted*simulated failure*'
  }

  It 'falls back on a response it cannot read (<Case>)' -ForEach @(
    @{ Case = 'a string, not JSON'; Body = 'not json' }
    @{ Case = 'JSON without runner_groups'; Body = [pscustomobject]@{ message = 'Not Found' } }
    @{ Case = 'nothing'; Body = $null }
  ) {
    $responses = Get-PoolResponse
    $responses['/orgs/whizbang-lib/actions/runner-groups?per_page=100'] = $Body
    $decisions = Invoke-Entry -Api (New-FakeApi $responses)
    foreach ($class in $decisions.Keys) { (Read-Output)[$class] | Should -Be $Hosted }
  }

  It 'falls back on a runner list it cannot read' {
    $responses = Get-PoolResponse
    $responses['/orgs/whizbang-lib/actions/runner-groups/7/runners?per_page=100'] = [pscustomobject]@{ runners = @([pscustomobject]@{ name = 'x' }) }
    $decisions = Invoke-Entry -Api (New-FakeApi $responses)
    $decisions['any-os'].RunsOn | Should -Be $Hosted
    (Read-Output)['any-os'] | Should -Be $Hosted
  }

  It 'falls back on a job list it cannot read' {
    $responses = Get-PoolResponse -QueuedRuns @([pscustomobject]@{ id = 11 }) -Jobs @{ 11 = @([pscustomobject]@{ name = 'x' }) }
    $decisions = Invoke-Entry -Api (New-FakeApi $responses)
    $decisions['linux'].RunsOn | Should -Be $Hosted
  }

  It 'falls back when the group is missing' {
    $decisions = Invoke-Entry -Api (New-FakeApi (Get-PoolResponse -Group (New-Group -Name 'Other')))
    $decisions['linux'].Reason | Should -BeLike "*'Default'*not found*"
    (Read-Output)['linux'] | Should -Be $Hosted
  }

  It 'falls back when the time budget is spent before a call' {
    $decisions = Invoke-Entry -Api $null -BudgetSeconds 0
    $decisions['linux'].Reason | Should -BeLike '*budget*'
    (Read-Output)['linux'] | Should -Be $Hosted
  }

  It 'falls back for every class when the decision itself throws' {
    Mock Select-CiRunner { throw 'unexpected' }
    $decisions = Invoke-Entry -Api (New-FakeApi (Get-PoolResponse))
    foreach ($class in (Get-CiRunnerClass).Keys) {
      $decisions[$class].RunsOn | Should -Be $Hosted
      $decisions[$class].Reason | Should -BeLike '*picker failed: unexpected*'
      (Read-Output)[$class] | Should -Be $Hosted
    }
  }

  It 'still returns its decisions when the output and summary files cannot be written' {
    $bad = Join-Path $TestDrive 'missing-dir' 'nested' 'out.txt'
    $d = Invoke-SelectCiRunner -EventName 'push' -Repository $Repo -Organization 'whizbang-lib' -PoolToken '' `
      -OutputFile $bad -SummaryFile $bad -InvokeApi (New-FakeApi @{})
    $d['linux'].RunsOn | Should -Be $Hosted
  }

  It 'writes nothing to files it was not given' {
    { Invoke-SelectCiRunner -EventName 'push' -Repository $Repo -Organization 'whizbang-lib' -PoolToken '' -InvokeApi (New-FakeApi @{}) } |
      Should -Not -Throw
  }
}

Describe 'New-GitHubApiInvoker' {
  It 'throws when the time budget is spent, before any request' {
    $invoke = New-GitHubApiInvoker -BaseUrl 'http://127.0.0.1:9' -TimeoutSec 5 -BudgetSeconds 0
    { & $invoke '/x' 'token' } | Should -Throw '*budget*'
  }

  It 'throws when the server refuses the connection' {
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
    $invoke = New-GitHubApiInvoker -BaseUrl "http://127.0.0.1:$port" -TimeoutSec 5 -BudgetSeconds 60
    { & $invoke '/x' 'token' } | Should -Throw
  }

  It 'throws when the server accepts the connection and never answers (timeout)' {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
      $invoke = New-GitHubApiInvoker -BaseUrl "http://127.0.0.1:$($listener.LocalEndpoint.Port)" -TimeoutSec 1 -BudgetSeconds 60
      { & $invoke '/x' 'token' } | Should -Throw
    } finally {
      $listener.Stop()
    }
  }

  It 'sends the token and returns the parsed JSON of a successful request' {
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://127.0.0.1:$port/")
    $listener.Start()
    try {
      $serve = $listener.GetContextAsync()
      $job = Start-ThreadJob -ArgumentList $ScriptPath, $port -ScriptBlock {
        param($path, $p)
        . $path
        $invoke = New-GitHubApiInvoker -BaseUrl "http://127.0.0.1:$p" -TimeoutSec 10 -BudgetSeconds 60
        & $invoke '/orgs/x' 'secret-token'
      }
      $context = $serve.GetAwaiter().GetResult()
      $context.Request.Headers['Authorization'] | Should -Be 'Bearer secret-token'
      $context.Request.Url.PathAndQuery | Should -Be '/orgs/x'
      $bytes = [Text.Encoding]::UTF8.GetBytes('{"total_count":0,"runner_groups":[]}')
      $context.Response.ContentType = 'application/json'
      $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
      $context.Response.Close()
      $result = $job | Wait-Job | Receive-Job
      $result.total_count | Should -Be 0
    } finally {
      $listener.Stop()
      $job | Remove-Job -Force -ErrorAction SilentlyContinue
    }
  }
}

Describe 'The script run as CI runs it' {
  It 'falls back and exits 0 with no tokens, writing every output' {
    $out = Join-Path $TestDrive 'entry-out.txt'
    $summary = Join-Path $TestDrive 'entry-summary.md'
    $names = 'RUNNER_POOL_TOKEN', 'GH_TOKEN'
    $saved = @{}; foreach ($n in $names) { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
    try {
      foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $null) }
      pwsh -NoProfile -File $ScriptPath -EventName push -Repository $Repo -Organization 'whizbang-lib' `
        -OutputFile $out -SummaryFile $summary | Out-Null
      $LASTEXITCODE | Should -Be 0
    } finally {
      foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
    }
    $lines = Get-Content -LiteralPath $out
    foreach ($class in 'any-os', 'linux', 'compile', 'analyze', 'linux-docker') { $lines | Should -Contain "$class=$Hosted" }
  }
}

Describe 'Which class each ci.yml job takes' {
  BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path

    # Each job of a workflow: its name and its text, split at the two-space job keys under jobs:.
    function Get-WorkflowJobText([string]$Path) {
      $jobs = [ordered]@{}
      $inJobs = $false; $current = $null
      foreach ($line in (Get-Content -LiteralPath $Path)) {
        if ($line -match '^jobs:\s*$') { $inJobs = $true; continue }
        if (-not $inJobs) { continue }
        if ($line -match '^\S') { break }
        if ($line -match '^  ([A-Za-z0-9_-]+):\s*$') { $current = $Matches[1]; $jobs[$current] = [System.Text.StringBuilder]::new(); continue }
        # LF, not AppendLine: the tests anchor on line ends, and AppendLine writes CRLF on Windows.
        if ($current) { $null = $jobs[$current].Append($line).Append("`n") }
      }
      $table = [ordered]@{}
      foreach ($k in $jobs.Keys) { $table[$k] = $jobs[$k].ToString() }
      $table
    }

    $script:CiJobs = Get-WorkflowJobText (Join-Path $Root '.github' 'workflows' 'ci.yml')
    $script:Expected = [ordered]@{
      'build'                  = 'compile'
      'analyzers'              = 'compile'
      'compile-net10'          = 'compile'
      'format'                 = 'any-os'
      'noop-rebuild'           = 'any-os'
      'unit-tests'             = 'any-os'
      'component-tests'        = 'any-os'
      'script-tests'           = 'any-os'
      'quality-build'          = 'analyze'
      'quality'                = 'analyze'
      'queue-quality'          = 'analyze'
      'postgres-integration'   = 'linux-docker'
      'inmemory-integration'   = 'linux-docker'
      'rabbitmq-integration'   = 'linux-docker'
      'servicebus-integration' = 'linux-docker'
      'azureblob-integration'  = 'linux-docker'
      'general-integration'    = 'linux-docker'
    }
  }

  It 'routes <Job> through the <Class> class' -ForEach @(
    @{ Job = 'build'; Class = 'compile' }
    @{ Job = 'analyzers'; Class = 'compile' }
    @{ Job = 'compile-net10'; Class = 'compile' }
    @{ Job = 'format'; Class = 'any-os' }
    @{ Job = 'noop-rebuild'; Class = 'any-os' }
    @{ Job = 'unit-tests'; Class = 'any-os' }
    @{ Job = 'component-tests'; Class = 'any-os' }
    @{ Job = 'script-tests'; Class = 'any-os' }
    @{ Job = 'quality-build'; Class = 'analyze' }
    @{ Job = 'quality'; Class = 'analyze' }
    @{ Job = 'queue-quality'; Class = 'analyze' }
    @{ Job = 'postgres-integration'; Class = 'linux-docker' }
    @{ Job = 'inmemory-integration'; Class = 'linux-docker' }
    @{ Job = 'rabbitmq-integration'; Class = 'linux-docker' }
    @{ Job = 'servicebus-integration'; Class = 'linux-docker' }
    @{ Job = 'azureblob-integration'; Class = 'linux-docker' }
    @{ Job = 'general-integration'; Class = 'linux-docker' }
  ) {
    $text = $CiJobs[$Job]
    $text | Should -Not -BeNullOrEmpty
    # attempt is not a class: every routed job reads it to take the plan only in its own attempt (#1880).
    $used = @([regex]::Matches($text, 'needs\.pick-runner\.outputs\.([a-z-]+)') | ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $_ -ne 'attempt' } | Select-Object -Unique)
    $used | Should -Be @($Class)
    $text | Should -Match '(?m)^    needs: \[[^\]]*\bpick-runner\b'
  }

  It 'uses a class the picker writes, for every routed job' {
    $classes = @((Get-CiRunnerClass).Keys)
    foreach ($class in $Expected.Values) { $classes | Should -Contain $class }
  }

  It 'routes no other job, so pack, publish, release, gate and planning jobs stay on GitHub-hosted runners' {
    # A class output, not attempt or pool: the watcher reads those to decide whether to run, and runs on ubuntu-latest.
    $classOutput = 'pick-runner\.outputs\.(' + ((@((Get-CiRunnerClass).Keys) | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'
    $routed = @($CiJobs.Keys | Where-Object { $CiJobs[$_] -match $classOutput })
    @($routed | Sort-Object) | Should -Be @($Expected.Keys | Sort-Object)
  }

  It 'runs the watcher on ubuntu-latest, whatever the plan (#1880)' {
    $CiJobs['runner-watch'] | Should -Match '(?m)^    runs-on: ubuntu-latest\s*$'
  }

  It 'never makes the gate wait on the picker' {
    $CiJobs['ci-result'] | Should -Not -Match 'pick-runner'
  }

  It 'defaults every picker output to ubuntu-latest' {
    $outputs = @([regex]::Matches($CiJobs['pick-runner'], "(?m)^      ([a-z-]+): \$\{\{ steps\.pick\.outputs\.\1 \|\| '`"ubuntu-latest`"' \}\}$") | ForEach-Object { $_.Groups[1].Value })
    @($outputs | Sort-Object) | Should -Be @((Get-CiRunnerClass).Keys | Sort-Object)
  }

  It 'never runs a service-container suite on the any-os class (GitHub runs service containers only on Linux)' {
    foreach ($job in $Expected.Keys | Where-Object { $_ -like '*-integration' }) {
      $CiJobs[$job] | Should -Not -Match 'outputs\.any-os'
    }
  }
}

Describe 'Every step an any-os job reaches runs on Windows' {
  # The first run on the pool's Windows machines failed in .github/actions/setup-dotnet-retry: its bash step
  # found no bash (or the WSL stub) and nothing after it ran. Every step an any-os job reaches, through a
  # reusable workflow or a local composite action, must name its shell, and a bash or sh step must be
  # guarded off Windows. A run step without a shell is refused too: on Windows it runs under pwsh, and
  # bash syntax there fails the same way.
  BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path

    function Get-JobBlocks([string]$Path) {
      $jobs = [ordered]@{}
      $inJobs = $false; $current = $null
      foreach ($line in (Get-Content -LiteralPath $Path)) {
        if ($line -match '^jobs:\s*$') { $inJobs = $true; continue }
        if (-not $inJobs) { continue }
        if ($line -match '^\S') { break }
        if ($line -match '^  ([A-Za-z0-9_-]+):\s*$') { $current = $Matches[1]; $jobs[$current] = [System.Collections.Generic.List[string]]::new(); continue }
        if ($current) { $jobs[$current].Add($line) }
      }
      $jobs
    }

    # The steps of a job or a composite action: each a list of lines, split at the list items under steps:.
    function Get-StepBlocks([string[]]$Lines) {
      $steps = [System.Collections.Generic.List[object]]::new()
      $indent = -1; $inSteps = $false; $current = $null
      foreach ($line in $Lines) {
        if (-not $inSteps) { if ($line -match '^(\s*)steps:\s*$') { $inSteps = $true; $stepsIndent = $Matches[1].Length }; continue }
        if ($line -match '^(\s*)\S' -and $Matches[1].Length -le $stepsIndent -and $line -notmatch '^\s*-') { break }
        if ($line -match '^(\s*)- ' -and ($indent -lt 0 -or $Matches[1].Length -eq $indent)) {
          $indent = $Matches[1].Length
          $current = [System.Collections.Generic.List[string]]::new(); $steps.Add($current)
        }
        if ($null -ne $current) { $current.Add($line) }
      }
      $steps
    }

    # Problems with one step; follows a local composite action into its own steps.
    function Get-StepProblem([System.Collections.Generic.List[string]]$Step, [string]$Where) {
      $text = $Step -join "`n"
      $name = if ($text -match '(?m)^[ \t]*-?[ \t]*name:\s*(.+)$') { $Matches[1].Trim() } else { "$($Step[0])".Trim() }
      $guarded = $text -match "(?m)^[ \t]*-?[ \t]*if:.*runner\.os\s*!=\s*'Windows'"
      $shell = if ($text -match '(?m)^[ \t]*-?[ \t]*shell:\s*(\S+)') { $Matches[1] } else { '' }
      $isRun = $text -match '(?m)^[ \t]*-?[ \t]*run:'
      if ($isRun -and -not $shell) { "$Where / ${name}: a run step with no shell" }
      if ($shell -in @('bash', 'sh') -and -not $guarded) { "$Where / ${name}: shell $shell without an if: runner.os != 'Windows' guard" }
      # A guarded step is unreachable on Windows, so neither is the composite action it uses.
      if (-not $guarded -and $text -match '(?m)^[ \t]*-?[ \t]*uses:\s*\./(\.github/actions/[^\s#]+)') {
        $relative = $Matches[1]
        $action = Join-Path $script:Root $relative 'action.yml'
        foreach ($inner in (Get-StepBlocks (Get-Content -LiteralPath $action))) {
          Get-StepProblem -Step $inner -Where "$Where -> $relative"
        }
      }
    }

    $script:CiPath = Join-Path $Root '.github' 'workflows' 'ci.yml'
    $script:CiJobBlocks = Get-JobBlocks $CiPath
    $script:AnyOsJobs = @($CiJobBlocks.Keys | Where-Object { ($CiJobBlocks[$_] -join "`n") -match 'pick-runner\.outputs\.any-os' })
  }

  It 'finds the any-os jobs' {
    $AnyOsJobs.Count | Should -BeGreaterOrEqual 5
  }

  It 'has no bash step without a Windows guard and no run step without a shell, in <_>' -ForEach @('format', 'noop-rebuild', 'script-tests', 'unit-tests', 'component-tests') {
    $job = $_
    $lines = $CiJobBlocks[$job]
    $problems = [System.Collections.Generic.List[string]]::new()
    $reusable = ($lines -join "`n") -match '(?m)^\s*uses:\s*\./(\.github/workflows/[^\s#]+)'
    if ($reusable) {
      $path = Join-Path $Root $Matches[1]
      $inner = Get-JobBlocks $path
      foreach ($name in $inner.Keys) {
        foreach ($step in (Get-StepBlocks $inner[$name])) { foreach ($p in (Get-StepProblem -Step $step -Where "$job ($name)")) { $problems.Add($p) } }
      }
    } else {
      foreach ($step in (Get-StepBlocks $lines)) { foreach ($p in (Get-StepProblem -Step $step -Where $job)) { $problems.Add($p) } }
    }
    $problems | Should -BeNullOrEmpty
  }

  It 'covers every any-os job' {
    @($AnyOsJobs | Sort-Object) | Should -Be @('component-tests', 'format', 'noop-rebuild', 'script-tests', 'unit-tests')
  }

  It 'catches the regression it was written for' {
    $step = [System.Collections.Generic.List[string]]@('      - name: Setup', "        uses: ./.github/actions/setup-dotnet-retry")
    $bad = [System.Collections.Generic.List[string]]@('    - name: x', '      uses: nick-fields/retry@abc', '      with:', '        shell: bash')
    @(Get-StepProblem -Step $bad -Where 'probe') | Should -BeLike "*shell bash without*"
    $plain = [System.Collections.Generic.List[string]]@('      - name: y', '        run: echo hi')
    @(Get-StepProblem -Step $plain -Where 'probe') | Should -BeLike '*no shell*'
    $ok = [System.Collections.Generic.List[string]]@('    - name: z', "      if: runner.os != 'Windows'", '      shell: bash', '      run: echo hi')
    @(Get-StepProblem -Step $ok -Where 'probe') | Should -BeNullOrEmpty
  }

  It 'never sets a bash default shell in ci.yml or a reusable workflow an any-os job calls' {
    foreach ($file in @($CiPath) + @(Get-ChildItem (Join-Path $Root '.github' 'workflows') -Filter 'reusable-test-*.yml' | ForEach-Object FullName)) {
      (Get-Content -Raw -LiteralPath $file) | Should -Not -Match '(?ms)^\s*defaults:\s*\n\s*run:\s*\n\s*shell:\s*(bash|sh)'
    }
  }
}

Describe 'Re-runs re-plan (#1880)' {
  # "Re-run failed jobs" re-runs the failed jobs and their dependents but not the picker, so they kept the labels the
  # picker chose in the first attempt; after a machine left the pool the re-run queued for good on a label no runner
  # had. The picker now says which attempt it planned for, and every routed job takes the plan only in that attempt:
  # a job re-run in a later attempt without the picker goes to ubuntu-latest, and "re-run all jobs" re-runs the picker,
  # so its plan is fresh again.
  BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
    $script:Workflows = Join-Path $Root '.github' 'workflows'
    $script:HostedLiteral = [regex]::Escape(" || '`"ubuntu-latest`"') }}")

    # The violations of the rule in one workflow's text: each runs-on that reads the picker's plan without checking the
    # attempt, each reusable-workflow call that passes the plan without the attempt, and each reusable workflow whose
    # runs-on input is used without its runner-plan-attempt input.
    function script:Get-ReplanViolation([string]$Text, [string]$Name) {
      $violations = [System.Collections.Generic.List[string]]::new()
      $guardedNeeds = '^\s+runs-on: \$\{\{ fromJSON\(needs\.pick-runner\.outputs\.attempt == github\.run_attempt && needs\.pick-runner\.outputs\.[a-z-]+' + $HostedLiteral + '\s*$'
      $guardedInput = '^\s+runs-on: \$\{\{ fromJSON\(inputs\.runner-plan-attempt == github\.run_attempt && inputs\.runs-on' + $HostedLiteral + '\s*$'
      $lines = $Text -split "`n"
      foreach ($line in $lines) {
        if ($line -match '^    runs-on: .*pick-runner\.outputs' -and $line -notmatch $guardedNeeds) { $violations.Add("${Name}: $($line.Trim())") }
        if ($line -match '^    runs-on: .*inputs\.runs-on' -and $line -notmatch $guardedInput) { $violations.Add("${Name}: $($line.Trim())") }
      }
      # A call that passes the plan as an input passes the attempt it was planned for too.
      $callers = [regex]::Split($Text, '(?m)^(?=  [A-Za-z0-9_-]+:\s*$)')
      foreach ($job in $callers) {
        if ($job -match '(?m)^      runs-on: \$\{\{ needs\.pick-runner\.outputs\.[a-z-]+ \}\}' -and
          $job -notmatch '(?m)^      runner-plan-attempt: \$\{\{ needs\.pick-runner\.outputs\.attempt \}\}') {
          $violations.Add("${Name}: a call passes the picker's plan without runner-plan-attempt")
        }
      }
      if ($Text -match '(?m)^      runs-on:\s*$' -and $Text -match 'workflow_call:' -and $Text -notmatch '(?m)^      runner-plan-attempt:\s*$') {
        $violations.Add("${Name}: takes a runs-on input without a runner-plan-attempt input")
      }
      return @($violations)
    }
  }

  It 'says which attempt the plan is for' {
    (Get-Content -Raw (Join-Path $Workflows 'ci.yml')) | Should -Match '(?m)^      attempt: \$\{\{ github\.run_attempt \}\}\s*$'
  }

  It 'takes the plan only in the attempt it was made for, in <_>' -ForEach @(
    'ci.yml', 'reusable-build.yml', 'reusable-quality-build.yml', 'reusable-quality.yml', 'reusable-test-unit.yml',
    'reusable-test-component.yml', 'reusable-test-postgres.yml', 'reusable-test-inmemory.yml', 'reusable-test-rabbitmq.yml',
    'reusable-test-servicebus.yml', 'reusable-test-azureblob.yml', 'reusable-test-integration.yml'
  ) {
    Get-ReplanViolation -Text (Get-Content -Raw (Join-Path $Workflows $_)) -Name $_ | Should -BeNullOrEmpty
  }

  It 'covers every workflow that runs where the picker says' {
    $routed = @(Get-ChildItem $Workflows -Filter '*.yml' | Where-Object { (Get-Content -Raw $_.FullName) -match 'pick-runner\.outputs|inputs\.runs-on' } |
        ForEach-Object Name | Sort-Object)
    $routed | Should -Be @('ci.yml', 'reusable-build.yml', 'reusable-quality-build.yml', 'reusable-quality.yml', 'reusable-test-azureblob.yml',
      'reusable-test-component.yml', 'reusable-test-inmemory.yml', 'reusable-test-integration.yml', 'reusable-test-postgres.yml',
      'reusable-test-rabbitmq.yml', 'reusable-test-servicebus.yml', 'reusable-test-unit.yml')
  }

  It 'catches the regression it was written for' {
    $old = @"
jobs:
  format:
    needs: [pick-runner]
    runs-on: `${{ fromJSON(needs.pick-runner.outputs.any-os || '"ubuntu-latest"') }}
  unit-tests:
    uses: ./.github/workflows/reusable-test-unit.yml
    with:
      runs-on: `${{ needs.pick-runner.outputs.any-os }}
"@
    @(Get-ReplanViolation -Text $old -Name 'old').Count | Should -Be 2
    $reusable = @"
on:
  workflow_call:
    inputs:
      runs-on:
        type: string
jobs:
  test:
    runs-on: `${{ fromJSON(inputs.runs-on || '"ubuntu-latest"') }}
"@
    @(Get-ReplanViolation -Text $reusable -Name 'reusable').Count | Should -Be 2
  }
}

Describe 'Runner self-check records (#1880)' {
  # A machine can be online and idle and still unable to do the job: pve01 had no PowerShell, so the .NET setup
  # failed, and its upload speed made the compile's artifact uploads hit the job timeout. A pool machine is now
  # eligible only while it has a recent passing self-check (.github/workflows/runner-self-check.yml), recorded as
  # an Actions cache entry whose key carries the result and whose creation time says when.
  Context 'ConvertTo-RunnerCheckKey and ConvertFrom-RunnerCheckKey' {
    It 'round-trips a passing check with its upload speed and Docker' {
      $key = ConvertTo-RunnerCheckKey -RunnerName 'wpc' -Ok $true -UploadMBps 42 -Docker $true -Nonce '99-1'
      $key | Should -Be 'ci-runner-check/v1/wpc/ok/up-42/docker-yes/99-1'
      $r = ConvertFrom-RunnerCheckKey -Key $key -CreatedAt '2026-11-20T11:00:00Z'
      $r.Runner | Should -Be 'wpc'
      $r.Ok | Should -BeTrue
      $r.UploadMBps | Should -Be 42
      $r.Docker | Should -BeTrue
      $r.CheckedAt | Should -Be $Now.AddHours(-1)
      $r.CheckedAt.Kind | Should -Be 'Utc'
    }

    It 'round-trips a failed check whose upload was not measured' {
      $key = ConvertTo-RunnerCheckKey -RunnerName 'pve01' -Ok $false -UploadMBps $null -Docker $false -Nonce '7-2'
      $key | Should -Be 'ci-runner-check/v1/pve01/fail/up-na/docker-no/7-2'
      $r = ConvertFrom-RunnerCheckKey -Key $key -CreatedAt '2026-11-20T11:00:00Z'
      $r.Ok | Should -BeFalse
      $r.UploadMBps | Should -BeNullOrEmpty
      $r.Docker | Should -BeFalse
    }

    It 'reads no record from a key of another shape (<Key>)' -ForEach @(
      @{ Key = 'ci-runner-check/v2/wpc/ok/up-42/docker-yes/1-1' }
      @{ Key = 'ci-runner-check/v1/wpc/maybe/up-42/docker-yes/1-1' }
      @{ Key = 'ci-runner-check/v1/wpc/ok/up-fast/docker-yes/1-1' }
      @{ Key = 'nuget-Linux-abc' }
    ) {
      ConvertFrom-RunnerCheckKey -Key $Key -CreatedAt '2026-11-20T11:00:00Z' | Should -BeNullOrEmpty
    }

    It 'refuses a runner name that would break the key (<Name>)' -ForEach @(
      @{ Name = 'a/b' }
      @{ Name = '' }
      @{ Name = 'a,b' }
    ) {
      { ConvertTo-RunnerCheckKey -RunnerName $Name -Ok $true -UploadMBps 1 -Docker $true -Nonce '1' } | Should -Throw
    }
  }

  Context 'Get-RunnerCheck' {
    It 'reads the records with the repository token and keeps the latest per runner, whatever the case' {
      $caches = @(
        [pscustomobject]@{ key = 'ci-runner-check/v1/wpc/fail/up-na/docker-no/1-1'; created_at = '2026-11-20T08:00:00Z' }
        [pscustomobject]@{ key = 'ci-runner-check/v1/WPC/ok/up-50/docker-yes/2-1'; created_at = '2026-11-20T10:00:00Z' }
        [pscustomobject]@{ key = 'ci-runner-check/v1/wpc/fail/up-na/docker-no/0-1'; created_at = '2026-11-19T10:00:00Z' }
        [pscustomobject]@{ key = 'ci-runner-check/v1/ultra/ok/up-90/docker-yes/3-1'; created_at = '2026-11-20T09:00:00Z' }
        [pscustomobject]@{ key = 'ci-runner-check/v9/other/ok/up-90/docker-yes/3-1'; created_at = '2026-11-20T09:00:00Z' }
      )
      $api = New-FakeApi @{ $ChecksPath = [pscustomobject]@{ total_count = 5; actions_caches = $caches } }
      $checks = Get-RunnerCheck -InvokeApi $api -Repository $Repo -RepoToken 'repo'
      $checks.Count | Should -Be 2
      $checks['wpc'].Ok | Should -BeTrue
      $checks['wpc'].UploadMBps | Should -Be 50
      $checks['ultra'].UploadMBps | Should -Be 90
      $ApiCalls | Should -Be @("repo $ChecksPath")
    }

    It 'returns an empty table when nothing was recorded' {
      $api = New-FakeApi @{ $ChecksPath = [pscustomobject]@{ total_count = 0; actions_caches = @() } }
      (Get-RunnerCheck -InvokeApi $api -Repository $Repo -RepoToken 'repo').Count | Should -Be 0
    }
  }

  Context 'Test-RunnerCheck' {
    It 'passes a runner with a recent passing check' {
      Test-RunnerCheck -Check (New-Check) -Requires @('upload', 'docker') -Now $Now -MaxAgeHours 24 -MinUploadMBps 10 | Should -Be ''
    }

    It 'fails a runner <Case>' -ForEach @(
      @{ Case = 'with no check'; Check = $null; Requires = @(); Why = 'no self-check recorded' }
      @{ Case = 'whose check is stale'; Check = @{ HoursAgo = 30 }; Requires = @(); Why = 'last self-check 30 hours ago (limit 24)' }
      @{ Case = 'whose last check failed'; Check = @{ Ok = $false }; Requires = @(); Why = 'failed its last self-check' }
      @{ Case = 'too slow to upload the build'; Check = @{ Up = 5 }; Requires = @('upload'); Why = 'uploads at 5 MB/s (needs 10)' }
      @{ Case = 'whose upload speed was not measured'; Check = @{ Up = $null }; Requires = @('upload'); Why = 'upload speed not measured' }
      @{ Case = 'without a working Docker'; Check = @{ Docker = $false }; Requires = @('docker'); Why = 'Docker not working' }
    ) {
      $c = if ($null -eq $Check) { $null } else { New-Check @Check }
      Test-RunnerCheck -Check $c -Requires $Requires -Now $Now -MaxAgeHours 24 -MinUploadMBps 10 | Should -Be $Why
    }

    It 'asks only what the class requires: a slow, Docker-less runner still serves a class requiring neither' {
      Test-RunnerCheck -Check (New-Check -Up 1 -Docker $false) -Requires @() -Now $Now -MaxAgeHours 24 -MinUploadMBps 10 | Should -Be ''
    }

    It 'holds a check exactly MaxAgeHours old as fresh, and one a minute older as stale' {
      Test-RunnerCheck -Check (New-Check -HoursAgo 24) -Requires @() -Now $Now -MaxAgeHours 24 -MinUploadMBps 10 | Should -Be ''
      Test-RunnerCheck -Check (New-Check -HoursAgo (24 + 1 / 60)) -Requires @() -Now $Now -MaxAgeHours 24 -MinUploadMBps 10 |
        Should -BeLike 'last self-check 24 hours ago*'
    }
  }

  Context 'Select-CiRunner' {
    It 'routes only to runners that passed their self-check' {
      $state = New-PoolState -Runners @((New-Runner -Name 'a'), (New-Runner -Name 'b'))
      $d = Invoke-Select -PoolState $state -Checks (New-CheckTable @(New-Check -Runner 'a'))
      $d['linux'].RunsOn | Should -Be $Linux
      $d['linux'].Reason | Should -BeLike 'self-hosted pool: 1 idle*'
    }

    It 'skips the pool for every class when no idle runner passed, naming each runner and why' {
      $state = New-PoolState -Runners @((New-Runner -Name 'pve01'), (New-Runner -Name 'wpc'))
      $d = Invoke-Select -PoolState $state -Checks (New-CheckTable @(New-Check -Runner 'wpc' -HoursAgo 48))
      foreach ($class in $d.Keys) { $d[$class].RunsOn | Should -Be $Hosted }
      $d['linux'].Reason | Should -Be ('GitHub-hosted (ubuntu-latest); self-hosted pool skipped: no idle runner with labels self-hosted, whizbang, Linux, X64 ' +
        'passed its self-check (pve01: no self-check recorded; wpc: last self-check 48 hours ago (limit 24))')
    }

    It 'keeps the compile off a runner too slow to upload the build, while the other classes use it' {
      $d = Invoke-Select -Context (New-Context -EventName 'merge_group') -Checks (New-CheckTable @(New-Check -Up 5)) -MinUploadMBps 10
      $d['compile'].RunsOn | Should -Be $Hosted
      $d['compile'].Reason | Should -BeLike '*r1: uploads at 5 MB/s (needs 10)*'
      $d['linux'].RunsOn | Should -Be $Linux
      $d['linux-docker'].RunsOn | Should -Be $LinuxDocker
    }

    It 'keeps the container suites off a runner whose Docker failed its check' {
      $d = Invoke-Select -Context (New-Context -EventName 'merge_group') -Checks (New-CheckTable @(New-Check -Docker $false))
      $d['linux-docker'].RunsOn | Should -Be $Hosted
      $d['linux-docker'].Reason | Should -BeLike '*r1: Docker not working*'
      $d['compile'].RunsOn | Should -Be $Linux
    }

    It 'skips the pool for every class when the self-checks could not be read' {
      $d = Invoke-Select -NoChecks -Checks $null -ChecksUnavailable 'boom'
      foreach ($class in $d.Keys) { $d[$class].RunsOn | Should -Be $Hosted }
      $d['linux'].Reason | Should -BeLike '*the runner self-checks could not be read: boom'
    }

    It 'counts the queue against the runners that passed, not every idle one' {
      $state = New-PoolState -Runners @((New-Runner -Name 'a'), (New-Runner -Name 'b'))
      $d = Invoke-Select -PoolState $state -Checks (New-CheckTable @(New-Check -Runner 'a')) -QueuedJobs @(New-Job @('self-hosted', 'whizbang'))
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*no spare runner (1 idle, 1 queued ahead*'
    }
  }

  Context 'Invoke-SelectCiRunner' {
    It 'reads the self-checks and routes by them' {
      $caches = @([pscustomobject]@{ key = 'ci-runner-check/v1/r1/fail/up-na/docker-no/1-1'; created_at = $Now.AddHours(-1).ToString('o') })
      $decisions = Invoke-Entry -Api (New-FakeApi (Get-PoolResponse -Caches $caches))
      $decisions['linux'].Reason | Should -BeLike '*r1: failed its last self-check*'
      (Read-Output)['linux'] | Should -Be $Hosted
      $ApiCalls | Should -Contain "repo-token $ChecksPath"
    }

    It 'writes pool=true when any class goes to the pool, and pool=false when none does' {
      $null = Invoke-Entry -Api (New-FakeApi (Get-PoolResponse))
      (Read-Output)['pool'] | Should -Be 'true'
      $null = Invoke-Entry -Api (New-FakeApi @{}) -PoolToken ''
      (Read-Output)['pool'] | Should -Be 'false'
    }

    It 'writes pool=false when the decision itself throws' {
      Mock Select-CiRunner { throw 'unexpected' }
      $null = Invoke-Entry -Api (New-FakeApi (Get-PoolResponse))
      (Read-Output)['pool'] | Should -Be 'false'
    }
  }

  Context 'ci.yml' {
    It 'passes the self-check limits and exposes whether the pool was used' {
      $ci = Get-Content -Raw (Join-Path $PSScriptRoot '..' '..' 'workflows' 'ci.yml')
      $ci | Should -Match "(?m)^      pool: \`$\{\{ steps\.pick\.outputs\.pool \|\| 'false' \}\}\s*$"
      $ci | Should -Match "MIN_UPLOAD_MBPS: \`$\{\{ vars\.RUNNER_POOL_MIN_UPLOAD_MBPS \|\| '10' \}\}"
      $ci | Should -Match "CHECK_MAX_AGE_HOURS: \`$\{\{ vars\.RUNNER_POOL_CHECK_MAX_AGE_HOURS \|\| '24' \}\}"
      $ci | Should -Match '-MinUploadMBps "\$env:MIN_UPLOAD_MBPS" -CheckMaxAgeHours "\$env:CHECK_MAX_AGE_HOURS"'
    }
  }
}
