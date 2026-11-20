# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The pool's fallback (#1880). A machine went away mid-job (dropped after ten minutes with no log, or timed a job
# out), and a job assigned to labels no machine carried any more queued for good. GitHub cannot move a job to another
# runner once it is queued, so the fallback works at the run's edges: the watcher (Mode Watch, a job of the run) ends
# a run whose only remaining work is pool jobs no machine will start, and the re-runner (Mode Rerun, its own workflow)
# re-runs the failed jobs of a run that failed only because a pool machine was lost. A re-run job takes ubuntu-latest
# (the attempt guard, Select-CiRunner.Tests.ps1 "Re-runs re-plan"). The API, the clock and the sleep are passed in.

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Invoke-CiRunnerFallback.ps1'
  . $ScriptPath

  $script:Repo = 'whizbang-lib/whizbang'
  $script:Now = [datetime]::new(2026, 11, 20, 12, 0, 0, [DateTimeKind]::Utc)
  $script:Pool = @('self-hosted', 'whizbang', 'Linux', 'X64')
  $script:Watcher = 'Plan · Watch self-hosted jobs'

  function New-Job {
    param([long]$Id = 1, [string]$Name = 'Test · Unit', [string]$Status = 'queued', [string]$Conclusion = '',
      [string[]]$Labels = $script:Pool, [double]$MinutesAgo = 0, [string]$RunnerName = '')
    [pscustomobject]@{
      id = $Id; name = $Name; status = $Status; conclusion = $(if ($Conclusion) { $Conclusion } else { $null })
      labels = $Labels; created_at = $script:Now.AddMinutes(-$MinutesAgo).ToString('o'); runner_name = $RunnerName
    }
  }

  function New-Runner {
    param([string]$Name = 'r1', [string]$Status = 'online', [string[]]$Labels = $script:Pool)
    [pscustomobject]@{ name = $Name; status = $Status; busy = $true; labels = @($Labels | ForEach-Object { [pscustomobject]@{ name = $_ } }) }
  }

  function New-Run {
    param([string]$Status = 'completed', [string]$Conclusion = 'failure', [string]$EventName = 'pull_request', [int]$Attempt = 1)
    [pscustomobject]@{ id = 500; status = $Status; conclusion = $Conclusion; event = $EventName; run_attempt = $Attempt }
  }

  # A fake GitHub API for GET and POST: answers from $Responses, records every call, throws for a path it was not given.
  function New-FakeApi {
    param([System.Collections.IDictionary]$Responses = @{}, [string]$FailOn = '')
    $script:Calls = [System.Collections.Generic.List[string]]::new()
    $responses = $Responses; $calls = $script:Calls; $failOn = $FailOn
    {
      param([string]$Method, [string]$Path, $Body)
      $calls.Add("$Method $Path$(if ($null -ne $Body) { ' ' + (ConvertTo-Json $Body -Compress -Depth 5) })")
      if ($failOn -and $Path -like "*$failOn*") { throw "simulated failure for $Path" }
      $key = "$Method $Path"
      if ($responses.Contains($key)) {
        $answer = $responses[$key]
        # A list of answers is served one per call, the last repeating.
        if ($answer -is [System.Collections.Generic.Queue[object]]) { if ($answer.Count -gt 1) { return $answer.Dequeue() } return $answer.Peek() }
        return $answer
      }
      if ($Method -eq 'POST') { return $null }
      throw "unexpected API call: $key"
    }.GetNewClosure()
  }

  function New-Queue([object[]]$Items) {
    $q = [System.Collections.Generic.Queue[object]]::new()
    foreach ($i in $Items) { $q.Enqueue($i) }
    , $q
  }

  $script:JobsPath = "/repos/$script:Repo/actions/runs/500/jobs?filter=latest&per_page=100"
}

Describe 'Test-PoolJob' {
  It 'is a pool job when it asked for self-hosted, whatever the case' {
    Test-PoolJob -Job (New-Job -Labels @('Self-Hosted', 'whizbang')) | Should -BeTrue
    Test-PoolJob -Job (New-Job -Labels @('ubuntu-latest')) | Should -BeFalse
    Test-PoolJob -Job (New-Job -Labels @()) | Should -BeFalse
  }
}

Describe 'Get-StuckPoolJob' {
  It 'finds a pool job queued past the wait that no online machine carries the labels for' {
    $jobs = @(New-Job -MinutesAgo 11)
    $stuck = @(Get-StuckPoolJob -Jobs $jobs -Runners @(New-Runner -Status 'offline') -Now $Now -QueuedMinutes 10 -CeilingMinutes 45)
    $stuck.Name | Should -Be @('Test · Unit')
    $stuck[0].Reason | Should -Be 'queued 11 minutes; no online runner carries self-hosted, whizbang, Linux, X64'
  }

  It 'leaves a queued pool job alone while a machine with its labels is online (busy and alive), until the ceiling' {
    $runners = @(New-Runner)
    @(Get-StuckPoolJob -Jobs @(New-Job -MinutesAgo 30) -Runners $runners -Now $Now -QueuedMinutes 10 -CeilingMinutes 45).Count | Should -Be 0
    $stuck = @(Get-StuckPoolJob -Jobs @(New-Job -MinutesAgo 45) -Runners $runners -Now $Now -QueuedMinutes 10 -CeilingMinutes 45)
    $stuck[0].Reason | Should -Be 'queued 45 minutes, the most a pool job waits'
  }

  It 'applies only the ceiling when the machines could not be read' {
    @(Get-StuckPoolJob -Jobs @(New-Job -MinutesAgo 30) -Runners $null -Now $Now -QueuedMinutes 10 -CeilingMinutes 45).Count | Should -Be 0
    @(Get-StuckPoolJob -Jobs @(New-Job -MinutesAgo 50) -Runners $null -Now $Now -QueuedMinutes 10 -CeilingMinutes 45).Count | Should -Be 1
  }

  It 'never counts <Case>' -ForEach @(
    @{ Case = 'a job queued a short time'; Job = @{ MinutesAgo = 9 } }
    @{ Case = 'a running pool job, however slow'; Job = @{ Status = 'in_progress'; MinutesAgo = 300 } }
    @{ Case = 'a GitHub-hosted job'; Job = @{ Labels = @('ubuntu-latest'); MinutesAgo = 300 } }
    @{ Case = 'a finished job'; Job = @{ Status = 'completed'; Conclusion = 'success'; MinutesAgo = 300 } }
  ) {
    @(Get-StuckPoolJob -Jobs @(New-Job @Job) -Runners @() -Now $Now -QueuedMinutes 10 -CeilingMinutes 45).Count | Should -Be 0
  }
}

Describe 'Get-WatchAction' {
  It 'cancels when a pool job is stuck and nothing else is running, so no work is thrown away' {
    $jobs = @((New-Job -Name $Watcher -Status 'in_progress' -Labels @('ubuntu-latest')), (New-Job -MinutesAgo 20), (New-Job -Name 'Build · Compile' -Status 'completed' -Conclusion 'success'))
    Get-WatchAction -Jobs $jobs -Stuck @([pscustomobject]@{ Name = 'Test · Unit'; Reason = 'x' }) -SelfName $Watcher | Should -Be 'cancel'
  }

  It 'waits while another job is still running, even with a stuck pool job' {
    $jobs = @((New-Job -Name $Watcher -Status 'in_progress'), (New-Job -MinutesAgo 20), (New-Job -Name 'Test · PostgreSQL' -Status 'in_progress'))
    Get-WatchAction -Jobs $jobs -Stuck @([pscustomobject]@{ Name = 'Test · Unit'; Reason = 'x' }) -SelfName $Watcher | Should -Be 'wait'
  }

  It 'waits while jobs are queued and none is stuck' {
    Get-WatchAction -Jobs @((New-Job -Name $Watcher -Status 'in_progress'), (New-Job)) -Stuck @() -SelfName $Watcher | Should -Be 'wait'
  }

  It 'is done when every other job has finished' {
    $jobs = @((New-Job -Name $Watcher -Status 'in_progress'), (New-Job -Status 'completed' -Conclusion 'failure'))
    Get-WatchAction -Jobs $jobs -Stuck @() -SelfName $Watcher | Should -Be 'done'
  }
}

Describe 'Invoke-RunnerWatch' {
  BeforeAll {
    function script:Invoke-Watch {
      param($Api, [object[]]$Runners = @(), [int]$MaxPolls = 20)
      $script:Slept = 0
      $script:Said = Invoke-RunnerWatch -InvokeApi $Api -Repository $Repo -RunId '500' -SelfName $Watcher -DefaultBranch 'develop' `
        -GetRunners { $Runners }.GetNewClosure() -Now { $script:Now } -Sleep { $script:Slept++ } -PollSeconds 30 -QueuedMinutes 10 -CeilingMinutes 45 `
        -MaxPolls $MaxPolls 6>&1
    }
  }

  It 'hands a run whose only work left is a stuck pool job to the re-runner, then cancels it' {
    $jobs = [pscustomobject]@{ jobs = @((New-Job -Name $Watcher -Status 'in_progress' -Labels @('ubuntu-latest')), (New-Job -Id 7 -MinutesAgo 20)) }
    $api = New-FakeApi @{ "GET $JobsPath" = $jobs }
    Invoke-Watch -Api $api
    $Calls | Should -Contain ("POST /repos/$Repo/actions/workflows/ci-runner-fallback.yml/dispatches " + '{"ref":"develop","inputs":{"run-id":"500"}}')
    $Calls[-1] | Should -Be "POST /repos/$Repo/actions/runs/500/cancel"
    ($Said -join "`n") | Should -BeLike '*Test · Unit*queued 20 minutes; no online runner*'
  }

  It 'stops when every other job has finished on two polls in a row, without canceling anything' {
    $running = [pscustomobject]@{ jobs = @((New-Job -Name $Watcher -Status 'in_progress'), (New-Job -Status 'in_progress')) }
    $done = [pscustomobject]@{ jobs = @((New-Job -Name $Watcher -Status 'in_progress'), (New-Job -Status 'completed' -Conclusion 'success')) }
    $api = New-FakeApi @{ "GET $JobsPath" = (New-Queue @($running, $done, $running, $done, $done)) }
    Invoke-Watch -Api $api
    @($Calls | Where-Object { $_ -like 'POST*' }).Count | Should -Be 0
    @($Calls | Where-Object { $_ -like 'GET*' }).Count | Should -Be 5
    $Slept | Should -Be 4
  }

  It 'keeps watching through an API error, and gives up quietly after MaxPolls' {
    $api = New-FakeApi @{} -FailOn '/jobs'
    Invoke-Watch -Api $api -MaxPolls 3
    @($Calls | Where-Object { $_ -like 'POST*' }).Count | Should -Be 0
    ($Said -join "`n") | Should -BeLike '*simulated failure*'
    ($Said -join "`n") | Should -BeLike '*stopped watching after 3 polls*'
  }

  It 'still cancels when the re-runner cannot be started, saying so' {
    $jobs = [pscustomobject]@{ jobs = @((New-Job -Name $Watcher -Status 'in_progress'), (New-Job -MinutesAgo 50)) }
    $api = New-FakeApi @{ "GET $JobsPath" = $jobs } -FailOn '/dispatches'
    Invoke-Watch -Api $api -Runners @(New-Runner)
    $Calls[-1] | Should -Be "POST /repos/$Repo/actions/runs/500/cancel"
    ($Said -join "`n") | Should -BeLike '*re-run failed jobs by hand*'
  }

  It 'applies only the ceiling when the machines cannot be read' {
    $jobs = [pscustomobject]@{ jobs = @((New-Job -Name $Watcher -Status 'in_progress'), (New-Job -MinutesAgo 20)) }
    $api = New-FakeApi @{ "GET $JobsPath" = $jobs }
    $script:Slept = 0
    $null = Invoke-RunnerWatch -InvokeApi $api -Repository $Repo -RunId '500' -SelfName $Watcher -DefaultBranch 'develop' `
      -GetRunners { throw 'no token' } -Now { $script:Now } -Sleep { $script:Slept++ } -PollSeconds 30 -QueuedMinutes 10 -CeilingMinutes 45 -MaxPolls 2 6>$null
    @($Calls | Where-Object { $_ -like 'POST*' }).Count | Should -Be 0
  }
}

Describe 'Test-RunnerLost' {
  It 'reads <Message> as a lost machine' -ForEach @(
    @{ Message = 'The self-hosted runner: wpc lost communication with the server. Verify the machine is running.' }
    @{ Message = 'The runner has received a shutdown signal. This can happen when the runner service is stopped.' }
    @{ Message = 'The job running on runner wpc has exceeded the maximum execution time of 15 minutes.' }
    @{ Message = 'The job was not acquired by Runner of type self-hosted even after multiple attempts' }
  ) {
    Test-RunnerLost -Messages @('Process completed with exit code 1.', $Message) | Should -BeTrue
  }

  It 'reads an ordinary failure as not lost' {
    Test-RunnerLost -Messages @('Process completed with exit code 1.') | Should -BeFalse
    Test-RunnerLost -Messages @() | Should -BeFalse
  }
}

Describe 'Get-FallbackDecision' {
  BeforeAll {
    function script:Get-Decision {
      param($Run = (New-Run), [object[]]$Jobs, [hashtable]$Messages = @{}, [string]$Trigger = 'workflow_run')
      Get-FallbackDecision -Run $Run -Jobs $Jobs -Messages $Messages -Trigger $Trigger -MaxAttempts 3 -Aggregators @('Gate · CI result')
    }
    $script:Lost = 'The self-hosted runner: wpc lost communication with the server.'
  }

  It 're-runs a run that failed only because a pool machine was lost (the gate failing after it)' {
    $jobs = @(
      (New-Job -Id 1 -Status 'completed' -Conclusion 'failure')
      (New-Job -Id 2 -Name 'Gate · CI result' -Status 'completed' -Conclusion 'failure' -Labels @('ubuntu-latest'))
      (New-Job -Id 3 -Name 'Build · Compile' -Status 'completed' -Conclusion 'success')
      (New-Job -Id 4 -Name 'Test · Unit (net10.0)' -Status 'completed' -Conclusion 'cancelled')
    )
    $d = Get-Decision -Jobs $jobs -Messages @{ 1 = @($Lost) }
    $d.Rerun | Should -BeTrue
    $d.Reason | Should -BeLike '*Test · Unit*lost*'
  }

  It 'does not re-run when <Case>' -ForEach @(
    @{ Case = 'a job failed for another reason too'; Jobs = @(
        @{ Id = 1; Status = 'completed'; Conclusion = 'failure' }
        @{ Id = 5; Name = 'Test · PostgreSQL'; Status = 'completed'; Conclusion = 'failure' }); Messages = @{ 1 = @('lost communication with the server') }; Why = '*Test · PostgreSQL failed for another reason*' }
    @{ Case = 'a GitHub-hosted job reports a lost runner'; Jobs = @(
        @{ Id = 1; Status = 'completed'; Conclusion = 'failure'; Labels = @('ubuntu-latest') }); Messages = @{ 1 = @('lost communication with the server') }; Why = '*Test · Unit failed for another reason*' }
    @{ Case = 'no pool machine was lost'; Jobs = @(
        @{ Id = 1; Status = 'completed'; Conclusion = 'failure' }); Messages = @{ 1 = @('Process completed with exit code 1.') }; Why = '*Test · Unit failed for another reason*' }
    @{ Case = 'nothing failed'; Jobs = @(@{ Id = 1; Status = 'completed'; Conclusion = 'cancelled' }); Messages = @{}; Why = '*no pool machine was lost*' }
  ) {
    $built = @($Jobs | ForEach-Object { $j = $_; New-Job @j })
    $d = Get-Decision -Jobs $built -Messages $Messages
    $d.Rerun | Should -BeFalse
    $d.Reason | Should -BeLike $Why
  }

  It 'does not re-run a run that <Case>' -ForEach @(
    @{ Case = 'has not finished'; Run = @{ Status = 'in_progress'; Conclusion = '' }; Why = '*not finished*' }
    @{ Case = 'succeeded'; Run = @{ Conclusion = 'success' }; Why = '*concluded success*' }
    @{ Case = 'was in the merge queue'; Run = @{ EventName = 'merge_group' }; Why = '*merge queue*already removed*' }
    @{ Case = 'was already re-run enough'; Run = @{ Attempt = 3 }; Why = '*attempt 3*' }
  ) {
    $d = Get-Decision -Run (New-Run @Run) -Jobs @(New-Job -Status 'completed' -Conclusion 'failure') -Messages @{ 1 = @($Lost) }
    $d.Rerun | Should -BeFalse
    $d.Reason | Should -BeLike $Why
  }

  It 're-runs a run the watcher canceled, whatever its jobs say' {
    $d = Get-Decision -Run (New-Run -Conclusion 'cancelled') -Jobs @(New-Job -Status 'completed' -Conclusion 'cancelled') -Trigger 'watcher'
    $d.Rerun | Should -BeTrue
    $d.Reason | Should -BeLike '*watcher*'
  }

  It 'leaves a canceled run alone when the watcher did not ask (someone canceled it by hand)' {
    $d = Get-Decision -Run (New-Run -Conclusion 'cancelled') -Jobs @(New-Job -Status 'completed' -Conclusion 'cancelled')
    $d.Rerun | Should -BeFalse
    $d.Reason | Should -BeLike '*concluded cancelled*'  # review-checks:allow: 'cancelled' is the GitHub API conclusion value
  }

  It 'leaves a run the watcher asked about alone when it did not end canceled' {
    $d = Get-Decision -Run (New-Run -Conclusion 'failure') -Jobs @() -Trigger 'watcher'
    $d.Rerun | Should -BeFalse
  }
}

Describe 'Invoke-RunnerRerun' {
  BeforeAll {
    $script:RunPath = "/repos/$script:Repo/actions/runs/500"
    function script:Invoke-Rerun {
      param($Api, [string]$Trigger = 'workflow_run', [int]$MaxPolls = 5)
      $script:Slept = 0
      $script:Summary = Join-Path $TestDrive "summary-$([guid]::NewGuid().ToString('N')).md"
      $script:Decision = Invoke-RunnerRerun -InvokeApi $Api -Repository $Repo -RunId '500' -Trigger $Trigger -SummaryFile $Summary `
        -Sleep { $script:Slept++ } -PollSeconds 30 -MaxPolls $MaxPolls 6>$null
    }
  }

  It 're-runs the failed jobs when a pool machine was lost, reading the annotations of the failed pool jobs only' {
    $api = New-FakeApi @{
      "GET $RunPath"                                                   = (New-Run)
      "GET $JobsPath"                                                  = [pscustomobject]@{ jobs = @(
          (New-Job -Id 1 -Status 'completed' -Conclusion 'failure')
          (New-Job -Id 2 -Name 'Gate · CI result' -Status 'completed' -Conclusion 'failure' -Labels @('ubuntu-latest'))
          (New-Job -Id 3 -Status 'completed' -Conclusion 'success')) }
      "GET /repos/$Repo/check-runs/1/annotations?per_page=50"        = @([pscustomobject]@{ message = 'The self-hosted runner: wpc lost communication with the server.' })
    }
    Invoke-Rerun -Api $api
    $Decision.Rerun | Should -BeTrue
    $Calls[-1] | Should -Be "POST $RunPath/rerun-failed-jobs"
    @($Calls | Where-Object { $_ -like '*annotations*' }).Count | Should -Be 1
    Get-Content -Raw $Summary | Should -BeLike '*Re-ran the failed jobs on GitHub-hosted runners*'
  }

  It 'waits for a run the watcher canceled to finish, then re-runs it' {
    $api = New-FakeApi @{
      "GET $RunPath"  = (New-Queue @((New-Run -Status 'in_progress' -Conclusion ''), (New-Run -Conclusion 'cancelled')))
      "GET $JobsPath" = [pscustomobject]@{ jobs = @(New-Job -Status 'completed' -Conclusion 'cancelled') }
    }
    Invoke-Rerun -Api $api -Trigger 'watcher'
    $Slept | Should -Be 1
    $Calls[-1] | Should -Be "POST $RunPath/rerun-failed-jobs"
  }

  It 'gives up on a run that does not finish, re-running nothing' {
    $api = New-FakeApi @{ "GET $RunPath" = (New-Run -Status 'in_progress' -Conclusion '') }
    Invoke-Rerun -Api $api -Trigger 'watcher' -MaxPolls 3
    $Decision.Rerun | Should -BeFalse
    @($Calls | Where-Object { $_ -like 'POST*' }).Count | Should -Be 0
  }

  It 'counts a job whose annotations cannot be read as not lost' {
    $api = New-FakeApi @{
      "GET $RunPath"  = (New-Run)
      "GET $JobsPath" = [pscustomobject]@{ jobs = @(New-Job -Id 1 -Status 'completed' -Conclusion 'failure') }
    }
    Invoke-Rerun -Api $api
    $Decision.Rerun | Should -BeFalse
  }

  It 'reports, and never throws, when the re-run itself fails' {
    $api = New-FakeApi @{
      "GET $RunPath"  = (New-Run -Conclusion 'cancelled')
      "GET $JobsPath" = [pscustomobject]@{ jobs = @() }
    } -FailOn 'rerun-failed-jobs'
    { Invoke-Rerun -Api $api -Trigger 'watcher' } | Should -Not -Throw
    Get-Content -Raw $Summary | Should -BeLike '*could not re-run*simulated failure*'
  }

  It 'reports, and never throws, when the run cannot be read' {
    $api = New-FakeApi @{} -FailOn $RunPath
    { Invoke-Rerun -Api $api } | Should -Not -Throw
    $Decision.Rerun | Should -BeFalse
    Get-Content -Raw $Summary | Should -BeLike '*simulated failure*'
  }
}

Describe 'Invoke-CiRunnerFallback' {
  BeforeAll {
    $script:EntryArgs = @{
      Mode = 'Watch'; Repository = $Repo; RunId = '500'; SelfName = $Watcher; DefaultBranch = 'develop'; Organization = 'whizbang-lib'
      GroupName = 'Default'; Trigger = 'watcher'; QueuedMinutes = 10; CeilingMinutes = 45; SummaryFile = ''
    }
    $script:Clock = { $script:Now }
    $script:NoSleep = { }
  }

  It 'does nothing, and says so, without a token' {
    $said = Invoke-CiRunnerFallback -Arguments $EntryArgs -Token '' -Now $Clock -Sleep $NoSleep 6>&1
    $said[-1] | Should -Be 'skipped'
    ($said -join "`n") | Should -BeLike '*No GH_TOKEN*'
  }

  It 'watches with the runner list read through the pool token' {
    Mock Invoke-RunnerWatch { $script:GotRunners = & $GetRunners; 'done' }
    $pool = New-FakeApi @{}
    $poolApi = { param($Path, $Token) [pscustomobject]@{ runner_groups = @([pscustomobject]@{ id = 7; name = 'Default'; visibility = 'all' }); runners = @(New-Runner) } }
    Invoke-CiRunnerFallback -Arguments $EntryArgs -Token 't' -PoolToken 'p' -Api $pool -PoolApi $poolApi -Now $Clock -Sleep $NoSleep | Should -Be 'done'
    @($GotRunners).name | Should -Be @('r1')
    Should -Invoke Invoke-RunnerWatch -Times 1 -ParameterFilter { $RunId -eq '500' -and $DefaultBranch -eq 'develop' -and $SelfName -eq $Watcher }
  }

  It 'watches without the machines when there is no pool token' {
    Mock Invoke-RunnerWatch { try { & $GetRunners; 'read' } catch { $_.Exception.Message } }
    Invoke-CiRunnerFallback -Arguments $EntryArgs -Token 't' -Api (New-FakeApi @{}) -PoolApi { } -Now $Clock -Sleep $NoSleep | Should -Be 'no runner-pool token'
  }

  It 'builds its own clients from the tokens when given none' {
    Mock Invoke-RunnerWatch { 'done' }
    Invoke-CiRunnerFallback -Arguments $EntryArgs -Token 't' -Now $Clock -Sleep $NoSleep | Should -Be 'done'
    Should -Invoke Invoke-RunnerWatch -Times 1 -ParameterFilter { $null -ne $InvokeApi }
  }

  It 're-runs in Rerun mode' {
    Mock Invoke-RunnerRerun { 'reran' }
    $rerun = $EntryArgs.Clone(); $rerun.Mode = 'Rerun'
    Invoke-CiRunnerFallback -Arguments $rerun -Token 't' -Api (New-FakeApi @{}) -Now $Clock -Sleep $NoSleep | Should -Be 'reran'
    Should -Invoke Invoke-RunnerRerun -Times 1 -ParameterFilter { $Trigger -eq 'watcher' -and $RunId -eq '500' }
  }

  It 'warns, and never throws, when anything inside throws' {
    Mock Invoke-RunnerRerun { throw 'boom' }
    $rerun = $EntryArgs.Clone(); $rerun.Mode = 'Rerun'
    $said = Invoke-CiRunnerFallback -Arguments $rerun -Token 't' -Api (New-FakeApi @{}) -Now $Clock -Sleep $NoSleep 6>&1
    $said[-1] | Should -Be 'failed'
    ($said -join "`n") | Should -BeLike '*boom*'
  }
}

Describe 'New-GitHubApiClient' {
  It 'sends the method, the token and a JSON body, and returns the parsed JSON' {
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
        $client = New-GitHubApiClient -Token 'secret-token' -BaseUrl "http://127.0.0.1:$p" -TimeoutSec 10
        & $client 'POST' '/repos/x/dispatches' ([ordered]@{ ref = 'develop' })
        & $client 'GET' '/repos/x/runs/1' $null
      }
      $context = $serve.GetAwaiter().GetResult()
      $context.Request.HttpMethod | Should -Be 'POST'
      $context.Request.Headers['Authorization'] | Should -Be 'Bearer secret-token'
      $context.Request.Url.PathAndQuery | Should -Be '/repos/x/dispatches'
      [IO.StreamReader]::new($context.Request.InputStream).ReadToEnd() | Should -Be '{"ref":"develop"}'
      $bytes = [Text.Encoding]::UTF8.GetBytes('{"ok":true}')
      $context.Response.ContentType = 'application/json'
      $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
      $context.Response.Close()
      $second = $listener.GetContextAsync().GetAwaiter().GetResult()
      $second.Request.HttpMethod | Should -Be 'GET'
      $second.Request.HasEntityBody | Should -BeFalse
      $bytes = [Text.Encoding]::UTF8.GetBytes('{"id":1}')
      $second.Response.ContentType = 'application/json'
      $second.Response.OutputStream.Write($bytes, 0, $bytes.Length)
      $second.Response.Close()
      $results = @($job | Wait-Job | Receive-Job)
      $results[0].ok | Should -BeTrue
      $results[1].id | Should -Be 1
    } finally {
      $listener.Stop()
      $job | Remove-Job -Force -ErrorAction SilentlyContinue
    }
  }
}

Describe 'The workflows' {
  BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
    $script:Ci = Get-Content -Raw (Join-Path $Root '.github' 'workflows' 'ci.yml')
    $script:Fallback = Get-Content -Raw (Join-Path $Root '.github' 'workflows' 'ci-runner-fallback.yml')
  }

  It 'watches only a run that sent a job to the pool, in the attempt the plan was made for, off the gate' {
    $Ci | Should -Match "(?ms)^  runner-watch:\s*\n    name: $([regex]::Escape($Watcher))\s*$"
    $Ci | Should -Match "needs\.pick-runner\.outputs\.pool == 'true' && needs\.pick-runner\.outputs\.attempt == github\.run_attempt"
    $Ci | Should -Match '-Mode Watch'
    ($Ci -split "`n" | Where-Object { $_ -match '^    needs: \[changes, queue-prs' }) | Should -Not -Match 'runner-watch'
  }

  It 're-runs from the default branch on the CI run finishing or on the watcher asking, with only the rights it needs' {
    $Fallback | Should -Match '(?m)^  workflow_run:\s*$'
    $Fallback | Should -Match '(?m)^    workflows: \[CI\]\s*$'
    $Fallback | Should -Match '(?m)^  workflow_dispatch:\s*$'
    $Fallback | Should -Match '(?m)^      actions: write\b'
    $Fallback | Should -Match '(?m)^      checks: read\b'
    $Fallback | Should -Match '-Mode Rerun'
    # Never checks out the code of the run it judges.
    $Fallback | Should -Not -Match 'head_sha|head_branch'
  }
}
