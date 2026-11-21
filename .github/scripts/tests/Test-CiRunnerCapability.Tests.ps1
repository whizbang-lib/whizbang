# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The pool's self-check (#1880). A machine that was online and idle got jobs it could not do: pve01 had no
# PowerShell, so the .NET setup failed, and uploaded so slowly that the compile hit its job timeout. The self-check
# workflow runs this script on each pool machine and records what it found as an Actions cache entry the runner
# picker reads (Select-CiRunner.ps1, Get-RunnerCheck); a machine with no recent passing record gets no job. These
# tests cover each finding, the record, and the workflow that runs it. Every clock and probe is passed in.

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-CiRunnerCapability.ps1'
  . $ScriptPath

  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:Workflow = Join-Path $Root '.github' 'workflows' 'runner-self-check.yml'

  function New-Drive([string]$Root, [double]$FreeGB) {
    [pscustomobject]@{ RootDirectory = [pscustomobject]@{ FullName = $Root }; AvailableFreeSpace = [long]($FreeGB * 1GB) }
  }

  function New-Runner {
    param([string]$Name = 'r1', [string]$Status = 'online', [bool]$Busy = $false, [string[]]$Labels = @('self-hosted', 'whizbang', 'Linux', 'X64'))
    [pscustomobject]@{ name = $Name; status = $Status; busy = $Busy; labels = @($Labels | ForEach-Object { [pscustomobject]@{ name = $_ } }) }
  }

  function Read-Output([string]$Path) {
    $table = @{}
    foreach ($line in (Get-Content -LiteralPath $Path)) { $name, $value = $line -split '=', 2; $table[$name] = $value }
    $table
  }
}

Describe 'Get-RunnerCapabilityResult' {
  It 'passes a machine with PowerShell 7 and enough free disk' {
    $r = Get-RunnerCapabilityResult -PwshVersion ([version]'7.4.6') -FreeDiskGB 80 -MinDiskGB 20
    $r.Ok | Should -BeTrue
    $r.Problems | Should -BeNullOrEmpty
  }

  It 'fails a machine <Case>' -ForEach @(
    @{ Case = 'with PowerShell older than 7'; Pwsh = '6.2.7'; Disk = 80; Problems = @('PowerShell 6.2.7 (needs 7.0 or later)') }
    @{ Case = 'low on disk'; Pwsh = '7.4.6'; Disk = 12.4; Problems = @('12 GB free (needs 20)') }
    @{ Case = 'whose free disk is unknown'; Pwsh = '7.4.6'; Disk = $null; Problems = @('free disk space unknown') }
    @{ Case = 'with both'; Pwsh = '5.1'; Disk = 3; Problems = @('PowerShell 5.1 (needs 7.0 or later)', '3 GB free (needs 20)') }
  ) {
    $r = Get-RunnerCapabilityResult -PwshVersion ([version]$Pwsh) -FreeDiskGB $Disk -MinDiskGB 20
    $r.Ok | Should -BeFalse
    $r.Problems | Should -Be $Problems
  }

  It 'holds exactly the minimum free disk as enough' {
    (Get-RunnerCapabilityResult -PwshVersion ([version]'7.0') -FreeDiskGB 20 -MinDiskGB 20).Ok | Should -BeTrue
  }
}

Describe 'Measure-UploadSpeed' {
  It 'is the megabytes uploaded over the seconds the upload step took, rounded down' {
    Measure-UploadSpeed -UploadMB 200 -StartedMs 1000 -FinishedMs 11000 -Uploaded $true | Should -Be 20
    Measure-UploadSpeed -UploadMB 200 -StartedMs 0 -FinishedMs 30000 -Uploaded $true | Should -Be 6
  }

  It 'measures nothing when <Case>' -ForEach @(
    @{ Case = 'the upload failed'; Started = 0; Finished = 1000; Uploaded = $false; NoStart = $false }
    @{ Case = 'no start time was recorded'; Started = 0L; Finished = 1000; Uploaded = $true; NoStart = $true }
    @{ Case = 'the clock went backwards'; Started = 5000; Finished = 1000; Uploaded = $true; NoStart = $false }
  ) {
    $started = if ($NoStart) { $null } else { $Started }
    Measure-UploadSpeed -UploadMB 200 -StartedMs $started -FinishedMs $Finished -Uploaded $Uploaded | Should -BeNullOrEmpty
  }
}

Describe 'Get-FreeSpaceGB' {
  It 'reads the drive the path is on, the longest root that holds it' {
    $drives = @((New-Drive '/' 5), (New-Drive '/home' 90), (New-Drive '/home/runner/work' 300), (New-Drive '/home/other' 1))
    Get-FreeSpaceGB -Path '/home/runner/work/whizbang' -Drives $drives | Should -Be 300
    Get-FreeSpaceGB -Path '/home/runner' -Drives $drives | Should -Be 90
  }

  It 'compares Windows roots without case' {
    Get-FreeSpaceGB -Path 'd:\a\whizbang' -Drives @((New-Drive 'C:\' 10), (New-Drive 'D:\' 70)) | Should -Be 70
  }

  It 'returns nothing when no drive holds the path' {
    Get-FreeSpaceGB -Path '/nowhere' -Drives @(New-Drive 'C:\' 10) | Should -BeNullOrEmpty
  }
}

Describe 'Test-DockerWork' {
  It 'is true when the probe exits 0' {
    Test-DockerWork -Probe { $global:LASTEXITCODE = 0 } | Should -BeTrue
  }

  It 'is false when the probe <Case>' -ForEach @(
    @{ Case = 'exits non-zero'; Probe = { $global:LASTEXITCODE = 1 } }
    @{ Case = 'throws (no docker on the path)'; Probe = { throw 'docker: command not found' } }
  ) {
    Test-DockerWork -Probe $Probe | Should -BeFalse
  }
}

Describe 'Get-SelfCheckSlot' {
  It 'gives one slot per online pool runner, busy or idle' {
    $runners = @(
      (New-Runner -Name 'a')
      (New-Runner -Name 'b' -Busy $true)
      (New-Runner -Name 'c' -Status 'offline')
      (New-Runner -Name 'd' -Labels @('self-hosted', 'Linux'))
    )
    Get-SelfCheckSlot -Runners $runners | Should -Be @(1, 2)
  }

  It 'gives no slots for no runners' {
    @(Get-SelfCheckSlot -Runners @()).Count | Should -Be 0
  }
}

Describe 'Invoke-RunnerSelfCheck' {
  Context 'Plan' {
    It 'writes the slots as a JSON array' {
      $out = Join-Path $TestDrive 'plan.txt'
      $state = { [pscustomobject]@{ Group = [pscustomobject]@{ name = 'Default' }; Runners = @((New-Runner -Name 'a'), (New-Runner -Name 'b')) } }
      Invoke-RunnerSelfCheck -Mode Plan -OutputFile $out -GetPoolState $state
      (Read-Output $out)['slots'] | Should -Be '[1,2]'
    }

    It 'writes no slots, and warns, when <Case>' -ForEach @(
      @{ Case = 'the pool cannot be read'; State = { throw 'no token' }; Why = '*no token*' }
      @{ Case = 'the group does not exist'; State = { [pscustomobject]@{ Group = $null; Runners = @() } }; Why = '*not found*' }
      @{ Case = 'no pool runner is online'; State = { [pscustomobject]@{ Group = [pscustomobject]@{ name = 'Default' }; Runners = @() } }; Why = '*no pool runner is online*' }
    ) {
      $out = Join-Path $TestDrive "plan-$([guid]::NewGuid().ToString('N')).txt"
      $said = Invoke-RunnerSelfCheck -Mode Plan -OutputFile $out -GetPoolState $State 6>&1
      (Read-Output $out)['slots'] | Should -Be '[]'
      ($said -join "`n") | Should -BeLike $Why
    }
  }

  Context 'Prepare' {
    It 'writes a file of the size to upload and the time it finished' {
      $out = Join-Path $TestDrive 'prepare.txt'
      $file = Join-Path $TestDrive 'upload' 'probe.bin'
      Invoke-RunnerSelfCheck -Mode Prepare -OutputFile $out -UploadFile $file -UploadMB 2 -NowMs { 1234 }
      (Get-Item $file).Length | Should -Be 2MB
      (Read-Output $out)['started-ms'] | Should -Be '1234'
    }
  }

  Context 'Record' {
    BeforeAll {
      function script:Invoke-Record {
        param([version]$Pwsh = '7.4.6', $FreeGB = 80, [bool]$Docker = $true, [string]$Outcome = 'success', [string]$Runner = 'wpc')
        $script:Out = Join-Path $TestDrive "record-$([guid]::NewGuid().ToString('N')).txt"
        $script:Summary = Join-Path $TestDrive "summary-$([guid]::NewGuid().ToString('N')).md"
        $script:RecordPath = Join-Path $TestDrive "record-$([guid]::NewGuid().ToString('N'))" 'runner-check.txt'
        $free = $FreeGB; $dockerOk = $Docker
        Invoke-RunnerSelfCheck -Mode Record -OutputFile $Out -SummaryFile $Summary -RecordFile $RecordPath -RunnerName $Runner `
          -UploadMB 200 -StartedMs 0 -UploadOutcome $Outcome -MinDiskGB 20 -Nonce '9-1-1' -NowMs { 10000 } `
          -PwshVersion $Pwsh -GetFreeSpaceGB { $free }.GetNewClosure() -DockerProbe { $dockerOk }.GetNewClosure()
      }
    }

    It 'records a passing machine with its upload speed and Docker' {
      Invoke-Record
      $o = Read-Output $Out
      $o['ok'] | Should -Be 'true'
      $o['key'] | Should -Be 'ci-runner-check/v1/wpc/ok/up-20/docker-yes/9-1-1'
      Get-Content -Raw $RecordPath | Should -BeLike '*ci-runner-check/v1/wpc/ok/*'
      Get-Content -Raw $Summary | Should -BeLike '*wpc*passed*20 MB/s*'
    }

    It 'records a failing machine, saying why, and that its upload was not measured when it failed' {
      Invoke-Record -Pwsh '6.2' -Docker $false -Outcome 'failure'
      $o = Read-Output $Out
      $o['ok'] | Should -Be 'false'
      $o['key'] | Should -Be 'ci-runner-check/v1/wpc/fail/up-na/docker-no/9-1-1'
      Get-Content -Raw $Summary | Should -BeLike '*failed*PowerShell 6.2 (needs 7.0 or later)*'
    }

    It 'writes ok=false and no key for a runner name the key cannot hold' {
      Invoke-Record -Runner 'bad/name'
      $o = Read-Output $Out
      $o['ok'] | Should -Be 'false'
      $o.ContainsKey('key') | Should -BeFalse
      Get-Content -Raw $Summary | Should -BeLike "*bad/name*cannot go in a self-check key*"
    }
  }
}

Describe 'The script run as the workflow runs it' {
  It 'plans no check, and exits 0, without a runner-pool token' {
    $out = Join-Path $TestDrive 'entry-plan.txt'
    $saved = [Environment]::GetEnvironmentVariable('RUNNER_POOL_TOKEN')
    try {
      [Environment]::SetEnvironmentVariable('RUNNER_POOL_TOKEN', $null)
      $said = pwsh -NoProfile -File $ScriptPath -Mode Plan -Organization 'whizbang-lib' -OutputFile $out -SummaryFile ''
      $LASTEXITCODE | Should -Be 0
    } finally { [Environment]::SetEnvironmentVariable('RUNNER_POOL_TOKEN', $saved) }
    (Read-Output $out)['slots'] | Should -Be '[]'
    ($said -join "`n") | Should -BeLike '*no runner-pool token*'
  }

  It 'records this machine, measuring its disk under the work path' {
    $out = Join-Path $TestDrive 'entry-record.txt'
    $record = Join-Path $TestDrive 'entry' 'record.txt'
    pwsh -NoProfile -File $ScriptPath -Mode Record -RunnerName 'probe-test' -UploadMB 200 -StartedMs '' -UploadOutcome 'failure' `
      -Nonce '1-1-1' -MinDiskGB 0 -RecordFile $record -WorkPath $TestDrive -OutputFile $out -SummaryFile '' | Out-Null
    $LASTEXITCODE | Should -Be 0
    $o = Read-Output $out
    $o['ok'] | Should -Be 'true'
    $o['key'] | Should -BeLike 'ci-runner-check/v1/probe-test/ok/up-na/docker-*/1-1-1'
  }
}

Describe 'The self-check workflow' {
  BeforeAll { $script:Text = Get-Content -Raw -LiteralPath $Workflow }

  It 'runs on a schedule and by hand, and a new run replaces one still waiting for machines' {
    $Text | Should -Match '(?m)^  schedule:\s*$'
    $Text | Should -Match '(?m)^  workflow_dispatch:'
    $Text | Should -Match '(?m)^  cancel-in-progress: true\s*$'
  }

  It 'checks on the pool machines, one job per online machine' {
    $Text | Should -Match "(?m)^    runs-on: \[self-hosted, whizbang\]\s*$"
    $Text | Should -Match 'slot: \$\{\{ fromJSON\(needs\.plan\.outputs\.slots\) \}\}'
  }

  It 'times the upload around the artifact upload, keeping it a day, uncompressed' {
    $Text | Should -Match '-Mode Prepare'
    $Text | Should -Match 'retention-days: 1'
    $Text | Should -Match 'compression-level: 0'
    $Text | Should -Match 'UPLOAD_OUTCOME: \$\{\{ steps\.upload\.outcome \}\}'
  }

  It 'records the result under the key the picker reads, and fails the job of a machine that failed' {
    $Text | Should -Match 'uses: actions/cache/save@[0-9a-f]{40}'
    $Text | Should -Match 'key: \$\{\{ steps\.record\.outputs\.key \}\}'
    $Text | Should -Match "steps\.record\.outputs\.ok != 'true'"
  }

  It 'is kept out of the weekly cache cleanup, which would otherwise take the pool off every Sunday' {
    Get-Content -Raw (Join-Path $Root '.github' 'workflows' 'cache-cleanup-scheduled.yml') | Should -Match 'startswith\("ci-runner-check/"\) \| not'
  }
}
