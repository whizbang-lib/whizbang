# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The runner picker chooses, per job class, the first of three tiers whose every check passes: a
# team-owned self-hosted machine in the W! pool, then Blacksmith when a repository variable names its
# label, then a GitHub-hosted runner. These tests cover each routing branch and each way a tier is
# skipped: an untrusted run, a missing token, a closed or missing runner group, no idle machine with the
# labels, a queue ahead of the run, an unset or malformed variable, and every API failure (an error, a
# timeout, an unexpected response). The GitHub API is replaced by a scriptblock, except in the tests of
# the real invoker, which use a local listener.

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Select-CiRunner.ps1'
  . $ScriptPath

  $script:Repo = 'whizbang-lib/whizbang'
  $script:Hosted = '"ubuntu-latest"'
  $script:AnyOs = '["self-hosted","whizbang"]'
  $script:Linux = '["self-hosted","whizbang","Linux","X64"]'
  $script:LinuxDocker = '["self-hosted","whizbang","Linux","X64","docker"]'
  $script:Bs = 'blacksmith-4vcpu-ubuntu-2404'
  $script:BsJson = '"blacksmith-4vcpu-ubuntu-2404"'

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

  # Select-CiRunner for a trusted push with an open pool, an empty queue and no Blacksmith, unless overridden.
  function Invoke-Select {
    param([hashtable]$Context = (New-Context), $PoolState = (New-PoolState), [string]$PoolUnavailable = '',
      $QueuedJobs = @(), [string]$QueueUnavailable = '', [int]$QueueThreshold = 0, [hashtable]$Variables = @{})
    Select-CiRunner -Context $Context -PoolState $PoolState -PoolUnavailable $PoolUnavailable -QueuedJobs $QueuedJobs `
      -QueueUnavailable $QueueUnavailable -QueueThreshold $QueueThreshold -Blacksmith (Get-BlacksmithConfig -Variables $Variables)
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
      [object[]]$RunningRuns = @(), [hashtable]$Jobs = @{}, [string[]]$Selected = @())
    $r = [ordered]@{
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
      [string]$Disabled = '', [string]$Organization = 'whizbang-lib', [string]$RunId = '1000', [int]$BudgetSeconds = 60,
      [hashtable]$Variables = @{})
    $script:OutPath = Join-Path $TestDrive "out-$([guid]::NewGuid().ToString('N')).txt"
    $script:SummaryPath = Join-Path $TestDrive "summary-$([guid]::NewGuid().ToString('N')).md"
    Invoke-SelectCiRunner -EventName $EventName -Repository $script:Repo -HeadRepository $HeadRepository -Actor $Actor `
      -PrAuthor $PrAuthor -RepositoryPrivate 'false' -Organization $Organization -GroupName 'Default' -RunId $RunId `
      -PoolToken $PoolToken -RepoToken $RepoToken -Disabled $Disabled -QueueThreshold 0 -BudgetSeconds $BudgetSeconds `
      -Variables $Variables -OutputFile $script:OutPath -SummaryFile $script:SummaryPath -InvokeApi $Api
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

Describe 'Get-LabelQueueCount' {
  It 'counts the queued jobs that ask for the label, whatever the case' {
    $jobs = @(
      (New-Job @($Bs))
      (New-Job @('Blacksmith-4vcpu-Ubuntu-2404'))
      (New-Job @($Bs) -Status 'in_progress')
      (New-Job @('ubuntu-latest'))
    )
    Get-LabelQueueCount -QueuedJobs $jobs -Label $Bs | Should -Be 2
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

  It 'keeps the pool from only the analysis, which waits hours on other jobs that may need the same machine' {
    $classes = Get-CiRunnerClass
    $classes['analyze'].Pool | Should -BeFalse
    foreach ($name in 'any-os', 'linux', 'compile', 'linux-docker') { $classes[$name].Pool | Should -BeTrue }
  }
}

Describe 'Get-BlacksmithConfig' {
  It 'is off when no label is set' {
    $c = Get-BlacksmithConfig -Variables @{}
    $c.Problem | Should -BeNullOrEmpty
    Get-BlacksmithLabel -Config $c -Class 'linux' | Should -BeNullOrEmpty
    $c.Forks | Should -BeFalse
    $c.MaxQueued | Should -Be 0
  }

  It 'uses the default label for every class and a per-class label over it' {
    $c = Get-BlacksmithConfig -Variables @{ CI_BLACKSMITH_RUNNER = " $Bs "; CI_BLACKSMITH_RUNNER_LINUX_DOCKER = 'blacksmith-8vcpu-ubuntu-2404' }
    Get-BlacksmithLabel -Config $c -Class 'linux' | Should -Be $Bs
    Get-BlacksmithLabel -Config $c -Class 'linux-docker' | Should -Be 'blacksmith-8vcpu-ubuntu-2404'
    Get-BlacksmithLabel -Config $c -Class 'any-os' | Should -Be $Bs
  }

  It 'reads the per-class variable for <Class>' -ForEach @(
    @{ Class = 'any-os'; Name = 'CI_BLACKSMITH_RUNNER_ANY_OS' }
    @{ Class = 'linux'; Name = 'CI_BLACKSMITH_RUNNER_LINUX' }
    @{ Class = 'compile'; Name = 'CI_BLACKSMITH_RUNNER_COMPILE' }
    @{ Class = 'analyze'; Name = 'CI_BLACKSMITH_RUNNER_ANALYZE' }
    @{ Class = 'linux-docker'; Name = 'CI_BLACKSMITH_RUNNER_LINUX_DOCKER' }
  ) {
    $c = Get-BlacksmithConfig -Variables @{ $Name = 'blacksmith-2vcpu-ubuntu-2404' }
    Get-BlacksmithLabel -Config $c -Class $Class | Should -Be 'blacksmith-2vcpu-ubuntu-2404'
  }

  It 'serves forks only when CI_BLACKSMITH_FORKS is exactly true (<Value>)' -ForEach @(
    @{ Value = 'true'; Expected = $true }
    @{ Value = 'TRUE'; Expected = $true }
    @{ Value = 'yes'; Expected = $false }
    @{ Value = ''; Expected = $false }
  ) {
    (Get-BlacksmithConfig -Variables @{ CI_BLACKSMITH_FORKS = $Value }).Forks | Should -Be $Expected
  }

  It 'reads CI_BLACKSMITH_MAX_QUEUED as a whole number' {
    (Get-BlacksmithConfig -Variables @{ CI_BLACKSMITH_MAX_QUEUED = '4' }).MaxQueued | Should -Be 4
    (Get-BlacksmithConfig -Variables @{ CI_BLACKSMITH_MAX_QUEUED = '' }).MaxQueued | Should -Be 0
  }

  It 'reports a problem for a malformed CI_BLACKSMITH_MAX_QUEUED (<Value>)' -ForEach @(
    @{ Value = 'many' }
    @{ Value = '-1' }
    @{ Value = '2.5' }
  ) {
    (Get-BlacksmithConfig -Variables @{ CI_BLACKSMITH_RUNNER = $Bs; CI_BLACKSMITH_MAX_QUEUED = $Value }).Problem |
      Should -BeLike "*CI_BLACKSMITH_MAX_QUEUED*$Value*"
  }
}

Describe 'Test-BlacksmithLabel' {
  It 'accepts <Label> for <Class>' -ForEach @(
    @{ Label = 'blacksmith-4vcpu-ubuntu-2404'; Class = 'linux' }
    @{ Label = 'blacksmith-4vcpu-ubuntu-2404'; Class = 'linux-docker' }
    @{ Label = 'blacksmith-4vcpu-ubuntu-2404-arm'; Class = 'any-os' }
    @{ Label = 'blacksmith-4vcpu-windows-2025'; Class = 'any-os' }
  ) {
    Test-BlacksmithLabel -Label $Label -Class $Class | Should -BeNullOrEmpty
  }

  It 'refuses <Label> for <Class>: <Why>' -ForEach @(
    @{ Label = 'blacksmith 4vcpu'; Class = 'linux'; Why = '*not a runner label*' }
    @{ Label = '"x"'; Class = 'any-os'; Why = '*not a runner label*' }
    @{ Label = 'blacksmith-4vcpu-ubuntu-2404-arm'; Class = 'linux'; Why = '*X64*' }
    @{ Label = 'blacksmith-4vcpu-windows-2025'; Class = 'linux-docker'; Why = '*Linux*' }
  ) {
    Test-BlacksmithLabel -Label $Label -Class $Class | Should -BeLike $Why
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

    It 'keeps the compile of a push run on GitHub-hosted runners, skipping Blacksmith too, while its tests use the pool' {
      $d = Invoke-Select -Context (New-Context -EventName 'push') -Variables @{ CI_BLACKSMITH_RUNNER = $Bs }
      $d['compile'].RunsOn | Should -Be $Hosted
      $d['compile'].Tier | Should -Be 'github-hosted'
      $d['compile'].Reason | Should -BeLike '*push*publish*'
      $d['linux'].RunsOn | Should -Be $Linux
    }

    It 'never sends the analysis to the pool, taking Blacksmith or GitHub-hosted instead' {
      $d = Invoke-Select
      $d['analyze'].RunsOn | Should -Be $Hosted
      $d['analyze'].Reason | Should -BeLike '*self-hosted pool skipped: this job waits on other jobs*'
      (Invoke-Select -Variables @{ CI_BLACKSMITH_RUNNER = $Bs })['analyze'].RunsOn | Should -Be $BsJson
    }

    It 'routes the compile of a pull request like any other class' {
      (Invoke-Select -Context (New-Context -EventName 'pull_request' -HeadRepository $Repo))['compile'].RunsOn | Should -Be $Linux
    }
  }

  Context 'tier 2: Blacksmith' {
    It 'takes Blacksmith when the pool is skipped and the label is set, naming why the pool was skipped' {
      $d = Invoke-Select -PoolState (New-PoolState -Runners @()) -Variables @{ CI_BLACKSMITH_RUNNER = $Bs }
      foreach ($class in 'any-os', 'linux', 'linux-docker') {
        $d[$class].RunsOn | Should -Be $BsJson
        $d[$class].Tier | Should -Be 'blacksmith'
        $d[$class].Reason | Should -BeLike "Blacksmith ($Bs)*self-hosted pool skipped: no idle*"
      }
    }

    It 'takes the per-class label' {
      $d = Invoke-Select -PoolState $null -PoolUnavailable 'off' -Variables @{ CI_BLACKSMITH_RUNNER = $Bs; CI_BLACKSMITH_RUNNER_LINUX_DOCKER = 'blacksmith-8vcpu-ubuntu-2404' }
      $d['linux-docker'].RunsOn | Should -Be '"blacksmith-8vcpu-ubuntu-2404"'
      $d['linux'].RunsOn | Should -Be $BsJson
    }

    It 'prefers an idle pool machine to Blacksmith' {
      (Invoke-Select -Variables @{ CI_BLACKSMITH_RUNNER = $Bs })['linux'].RunsOn | Should -Be $Linux
    }

    It 'serves a <Case> on Blacksmith when CI_BLACKSMITH_FORKS is true' -ForEach @(
      @{ Case = 'fork pull request'; Context = @{ EventName = 'pull_request'; Repository = 'whizbang-lib/whizbang'; HeadRepository = 'someone/whizbang'; Actor = 'someone'; PrAuthor = 'someone'; RepositoryPrivate = $false } }
      @{ Case = 'Dependabot pull request'; Context = @{ EventName = 'pull_request'; Repository = 'whizbang-lib/whizbang'; HeadRepository = 'whizbang-lib/whizbang'; Actor = 'dependabot[bot]'; PrAuthor = 'dependabot[bot]'; RepositoryPrivate = $false } }
    ) {
      $d = Invoke-Select -Context $Context -Variables @{ CI_BLACKSMITH_RUNNER = $Bs; CI_BLACKSMITH_FORKS = 'true' }
      $d['linux-docker'].RunsOn | Should -Be $BsJson
      $d['linux-docker'].Reason | Should -BeLike '*self-hosted pool skipped*'
    }

    It 'keeps a <Case> off Blacksmith unless CI_BLACKSMITH_FORKS is true' -ForEach @(
      @{ Case = 'fork pull request'; Context = @{ EventName = 'pull_request'; Repository = 'whizbang-lib/whizbang'; HeadRepository = 'someone/whizbang'; Actor = 'someone'; PrAuthor = 'someone'; RepositoryPrivate = $false } }
      @{ Case = 'Dependabot pull request'; Context = @{ EventName = 'pull_request'; Repository = 'whizbang-lib/whizbang'; HeadRepository = 'whizbang-lib/whizbang'; Actor = 'dependabot[bot]'; PrAuthor = 'dependabot[bot]'; RepositoryPrivate = $false } }
    ) {
      $d = Invoke-Select -Context $Context -Variables @{ CI_BLACKSMITH_RUNNER = $Bs }
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*Blacksmith skipped*CI_BLACKSMITH_FORKS*'
    }

    It 'never serves an ineligible event on Blacksmith, even with forks allowed' {
      $ctx = New-Context -EventName 'pull_request_target'
      $d = Invoke-Select -Context $ctx -Variables @{ CI_BLACKSMITH_RUNNER = $Bs; CI_BLACKSMITH_FORKS = 'true' }
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*Blacksmith skipped*not eligible*'
    }

    It 'takes Blacksmith while the queue for its label is under CI_BLACKSMITH_MAX_QUEUED' {
      $d = Invoke-Select -PoolState $null -PoolUnavailable 'off' -QueuedJobs @(New-Job @($Bs)) `
        -Variables @{ CI_BLACKSMITH_RUNNER = $Bs; CI_BLACKSMITH_MAX_QUEUED = '2' }
      $d['linux'].RunsOn | Should -Be $BsJson
      $d['linux'].Reason | Should -BeLike '*1 queued*'
    }

    It 'takes Blacksmith with no queue limit even when the queue is unreadable' {
      $d = Invoke-Select -PoolState $null -PoolUnavailable 'off' -QueuedJobs $null -QueueUnavailable 'boom' -Variables @{ CI_BLACKSMITH_RUNNER = $Bs }
      $d['linux'].RunsOn | Should -Be $BsJson
    }
  }

  Context 'tier 2 skipped: the final fallback' {
    It 'falls back when no label is set, naming both skipped tiers' {
      $d = Invoke-Select -PoolState (New-PoolState -Runners @())
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike 'GitHub-hosted*self-hosted pool skipped: no idle*Blacksmith skipped: CI_BLACKSMITH_RUNNER is not set*'
    }

    It 'falls back when the queue for the label is at CI_BLACKSMITH_MAX_QUEUED' {
      $d = Invoke-Select -PoolState $null -PoolUnavailable 'off' -QueuedJobs @((New-Job @($Bs)), (New-Job @($Bs))) `
        -Variables @{ CI_BLACKSMITH_RUNNER = $Bs; CI_BLACKSMITH_MAX_QUEUED = '2' }
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*Blacksmith skipped: 2 queued*CI_BLACKSMITH_MAX_QUEUED is 2*'
    }

    It 'falls back when a queue limit is set and the queue is unreadable' {
      $d = Invoke-Select -PoolState $null -PoolUnavailable 'off' -QueuedJobs $null -QueueUnavailable 'boom' `
        -Variables @{ CI_BLACKSMITH_RUNNER = $Bs; CI_BLACKSMITH_MAX_QUEUED = '2' }
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*Blacksmith skipped*queue*boom*'
    }

    It 'falls back on a malformed CI_BLACKSMITH_MAX_QUEUED' {
      $d = Invoke-Select -PoolState $null -PoolUnavailable 'off' -Variables @{ CI_BLACKSMITH_RUNNER = $Bs; CI_BLACKSMITH_MAX_QUEUED = 'lots' }
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*Blacksmith skipped*CI_BLACKSMITH_MAX_QUEUED*lots*'
    }

    It 'falls back on a label that does not fit the class' {
      $d = Invoke-Select -PoolState $null -PoolUnavailable 'off' -Variables @{ CI_BLACKSMITH_RUNNER = 'blacksmith-4vcpu-ubuntu-2404-arm' }
      $d['linux'].RunsOn | Should -Be $Hosted
      $d['linux'].Reason | Should -BeLike '*Blacksmith skipped*X64*'
      $d['any-os'].RunsOn | Should -Be '"blacksmith-4vcpu-ubuntu-2404-arm"'
    }

    It 'falls back on a malformed label' {
      $d = Invoke-Select -PoolState $null -PoolUnavailable 'off' -Variables @{ CI_BLACKSMITH_RUNNER = '["a","b"]' }
      foreach ($class in $d.Keys) { $d[$class].RunsOn | Should -Be $Hosted }
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
  ) {
    $responses = Get-PoolResponse -Group (New-Group -Visibility 'selected') -Selected @($Repo) `
      -QueuedRuns @([pscustomobject]@{ id = 11 }) -Jobs @{ 11 = @() }
    $decisions = Invoke-Entry -Api (New-FakeApi $responses -FailOn $Failing)
    $out = Read-Output
    foreach ($class in $decisions.Keys) { $out[$class] | Should -Be $Hosted }
    $decisions['linux'].Reason | Should -BeLike '*simulated failure*'
    Get-Content -Raw -LiteralPath $SummaryPath | Should -BeLike '*GitHub-hosted*simulated failure*'
  }

  It 'still takes Blacksmith when the pool API fails' {
    $decisions = Invoke-Entry -Api (New-FakeApi (Get-PoolResponse) -FailOn '/runners') -Variables @{ CI_BLACKSMITH_RUNNER = $Bs }
    (Read-Output)['linux-docker'] | Should -Be $BsJson
    $decisions['linux-docker'].Reason | Should -BeLike '*simulated failure*'
  }

  It 'reads the queue for Blacksmith on a fork pull request only when a queue limit is set' {
    $fork = @{ EventName = 'pull_request'; HeadRepository = 'someone/whizbang'; PrAuthor = 'someone' }
    $null = Invoke-Entry -Api (New-FakeApi (Get-PoolResponse)) @fork -Variables @{ CI_BLACKSMITH_RUNNER = $Bs; CI_BLACKSMITH_FORKS = 'true' }
    $ApiCalls.Count | Should -Be 0
    (Read-Output)['linux'] | Should -Be $BsJson
    $null = Invoke-Entry -Api (New-FakeApi (Get-PoolResponse)) @fork -Variables @{ CI_BLACKSMITH_RUNNER = $Bs; CI_BLACKSMITH_FORKS = 'true'; CI_BLACKSMITH_MAX_QUEUED = '3' }
    $ApiCalls | Should -Be @("repo-token /repos/$Repo/actions/runs?status=queued&per_page=20", "repo-token /repos/$Repo/actions/runs?status=in_progress&per_page=20")
    (Read-Output)['linux'] | Should -Be $BsJson
  }

  It 'skips Blacksmith under a queue limit when there is no repository token to read the queue' {
    $decisions = Invoke-Entry -Api (New-FakeApi @{}) -EventName 'pull_request' -HeadRepository 'someone/whizbang' -PrAuthor 'someone' `
      -RepoToken '' -Variables @{ CI_BLACKSMITH_RUNNER = $Bs; CI_BLACKSMITH_FORKS = 'true'; CI_BLACKSMITH_MAX_QUEUED = '3' }
    $ApiCalls.Count | Should -Be 0
    $decisions['linux'].RunsOn | Should -Be $Hosted
    $decisions['linux'].Reason | Should -BeLike '*Blacksmith skipped: the queue could not be read: no repository token*'
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

Describe 'Get-EnvironmentVariables' {
  It 'reads every CI_BLACKSMITH_ variable from the environment, and only those' {
    $saved = [Environment]::GetEnvironmentVariable('CI_BLACKSMITH_RUNNER_LINUX')
    try {
      [Environment]::SetEnvironmentVariable('CI_BLACKSMITH_RUNNER_LINUX', 'blacksmith-2vcpu-ubuntu-2404')
      $vars = Get-EnvironmentVariables
      @($vars.Keys | Sort-Object) | Should -Be @('CI_BLACKSMITH_FORKS', 'CI_BLACKSMITH_MAX_QUEUED', 'CI_BLACKSMITH_RUNNER',
        'CI_BLACKSMITH_RUNNER_ANALYZE', 'CI_BLACKSMITH_RUNNER_ANY_OS', 'CI_BLACKSMITH_RUNNER_COMPILE', 'CI_BLACKSMITH_RUNNER_LINUX', 'CI_BLACKSMITH_RUNNER_LINUX_DOCKER')
      $vars['CI_BLACKSMITH_RUNNER_LINUX'] | Should -Be 'blacksmith-2vcpu-ubuntu-2404'
    } finally {
      [Environment]::SetEnvironmentVariable('CI_BLACKSMITH_RUNNER_LINUX', $saved)
    }
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
  It 'falls back and exits 0 with no tokens or variables, writing every output' {
    $out = Join-Path $TestDrive 'entry-out.txt'
    $summary = Join-Path $TestDrive 'entry-summary.md'
    $names = 'RUNNER_POOL_TOKEN', 'GH_TOKEN', 'CI_BLACKSMITH_RUNNER'
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

  It 'takes Blacksmith from the environment variables CI passes' {
    $out = Join-Path $TestDrive 'entry-bs-out.txt'
    $saved = [Environment]::GetEnvironmentVariable('CI_BLACKSMITH_RUNNER')
    try {
      [Environment]::SetEnvironmentVariable('CI_BLACKSMITH_RUNNER', $Bs)
      pwsh -NoProfile -File $ScriptPath -EventName push -Repository $Repo -Organization 'whizbang-lib' -OutputFile $out | Out-Null
      $LASTEXITCODE | Should -Be 0
    } finally {
      [Environment]::SetEnvironmentVariable('CI_BLACKSMITH_RUNNER', $saved)
    }
    (Get-Content -LiteralPath $out) | Should -Contain "linux=$BsJson"
  }
}
