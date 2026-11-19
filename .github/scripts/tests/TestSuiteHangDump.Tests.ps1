# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# A test module that hangs used to hold its suite until the job's own limit canceled it (#2402): no result, no
# dump, nothing naming the test, and the canceled suite then aborted the coverage and Sonar jobs after it. Every
# suite now runs its modules with the testing platform's hang dump (Run-Tests.ps1 -HangDumpTimeout): after that
# long with no test activity a module prints the tests still running, writes <process>_<pid>_hang.dmp and the
# test list <process>_<pid>_hang.log to its TestResults (the process is `dotnet` under `dotnet test`), and is
# killed, so the suite fails with the dump still to upload.
#
# The timeout is a time with no test activity, not the module's whole run (a module that finished 3591 tests in
# 88 s was dumped by a 45 s timeout only when one test then ran 55 s with nothing else moving). So it is checked
# against the job, not the suites' durations: at least 5 minutes, far above any wait a passing test makes, and at
# most half the job's limit, so a hang in a module that starts late still dumps, and uploads, before the job is
# canceled. The suites are derived from the files, so a new reusable-test-*.yml is checked as well.

BeforeDiscovery {
  $root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:Suites = @(Get-ChildItem -Path (Join-Path $root '.github' 'workflows') -Filter 'reusable-test-*.yml' |
      Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'Run-Tests\.ps1' } |
      ForEach-Object { @{ Workflow = $_.Name; Path = $_.FullName } })
}

BeforeAll {
  # The steps of a workflow in order: each one's if, uses and full text, split at the "      - " step markers.
  function Get-WorkflowStep([string]$Path) {
    $steps = [System.Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($line in Get-Content -LiteralPath $Path) {
      if ($line -match '^      - (.*)$') {
        if ($current) { $steps.Add($current) }
        $current = [pscustomobject]@{ Index = $steps.Count; If = ''; Uses = ''; Text = [System.Collections.Generic.List[string]]::new() }
        $line = "        $($Matches[1])"
      }
      elseif ($line -match '^\s{0,5}\S') {
        if ($current) { $steps.Add($current); $current = $null }
        continue
      }
      if (-not $current) { continue }
      $current.Text.Add($line)
      if ($line -match '^        if:\s*(.+?)\s*$') { $current.If = $Matches[1] }
      if ($line -match '^        uses:\s*(\S+)') { $current.Uses = $Matches[1] }
    }
    if ($current) { $steps.Add($current) }
    , $steps
  }

  # A platform timeout as Run-Tests.ps1 accepts it (a whole number of s, m or h), in minutes.
  function ConvertTo-Minutes([string]$Timeout) {
    if ($Timeout -notmatch '^([1-9][0-9]*)([smh])$') { throw "Not a Run-Tests.ps1 hang dump timeout: '$Timeout'." }
    [double]$Matches[1] * @{ s = 1 / 60; m = 1; h = 60 }[$Matches[2]]
  }
}

Describe 'Every test suite takes a hang dump (#2402)' {
  It 'finds the suites' {
    $Suites.Count | Should -BeGreaterThan 0 -Because 'with no suite found every check below would pass on nothing'
  }

  Context '<Workflow>' -ForEach $Suites {
    BeforeAll {
      $script:Steps = Get-WorkflowStep $Path
      $script:Runs = @($Steps | Where-Object { $_.Text -match '^\s+run:.*Run-Tests\.ps1' })
      $script:JobLimit = [int](@(Get-Content -LiteralPath $Path | Where-Object { $_ -match '^    timeout-minutes:\s*(\d+)\s*$' } |
            ForEach-Object { [int]($_ -replace '\D', '') })[0])
    }

    It 'runs every module with a hang dump timeout of at least 5 minutes and at most half the job limit' {
      $Runs.Count | Should -BeGreaterThan 0
      $JobLimit | Should -BeGreaterThan 0 -Because 'the job states its limit'
      foreach ($run in $Runs) {
        $line = ($run.Text | Where-Object { $_ -match '^\s+run:.*Run-Tests\.ps1' }) -join "`n"
        $line | Should -Match '-HangDumpTimeout (\S+)' -Because "every Run-Tests.ps1 call must pass it: $line"
        $minutes = ConvertTo-Minutes ([regex]::Match($line, '-HangDumpTimeout (\S+)').Groups[1].Value)
        $minutes | Should -BeGreaterOrEqual 5
        $minutes | Should -BeLessOrEqual ($JobLimit / 2)
      }
    }

    It 'uploads the hang dumps and their test lists after the last test run, when the job fails or is canceled' {
      $upload = @($Steps | Where-Object { $_.Uses -like 'actions/upload-artifact@*' -and ($_.Text -join "`n") -match '_hang\.dmp' })
      $upload.Count | Should -Be 1
      $upload[0].If | Should -Match 'failure\(\)'
      $upload[0].If | Should -Match 'cancelled\(\)' # review-checks:allow (GitHub expression function name)
      $text = $upload[0].Text -join "`n"
      foreach ($glob in 'tests/**/TestResults/*_hang.dmp', 'tests/**/TestResults/*_hang.log', 'samples/**/TestResults/*_hang.dmp', 'samples/**/TestResults/*_hang.log') {
        $text | Should -Match ([regex]::Escape($glob))
      }
      $upload[0].Index | Should -BeGreaterThan ($Runs | Measure-Object -Property Index -Maximum).Maximum
    }
  }
}

# The checks above prove every suite asks for a hang dump; this one proves the platform honors the request the way
# the suites make it. It is not a given: with the extension and platform at 2.0.2, a module run under `dotnet test`
# (server mode, as Run-Tests.ps1 runs every suite) accepted --hangdump and never dumped, while the same module run
# directly did. So shapes/HangRunner, built from the repository's package versions, runs a test that never completes
# under `dotnet test` with the options Run-Tests.ps1 passes. The cap only bounds the failure: a dump that fires ends
# the module in seconds.
Describe 'The hang dump fires under dotnet test (#2402)' {
  BeforeAll {
    $shapes = Join-Path $PSScriptRoot 'shapes'
    $script:Out = Join-Path ([System.IO.Path]::GetTempPath()) "whizbang-hang-$([guid]::NewGuid().ToString('N'))"
    $build = & dotnet build (Join-Path $shapes 'HangRunner' 'HangRunner.csproj') -c Release "-p:ShapesOut=$Out/" -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Building shapes/HangRunner failed:`n$($build -join "`n")" }
    $module = Join-Path 'bin' 'HangRunner' 'Release' 'net11.0' 'HangRunner.dll'
    $script:Results = Join-Path $Out 'bin' 'HangRunner' 'Release' 'net11.0' 'TestResults'

    # From the shapes directory, so the repository's global.json puts `dotnet test` on the testing platform.
    $psi = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($a in 'test', '--root-directory', $Out, '--test-modules', $module, '--hangdump', '--hangdump-timeout', '5s') { $psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $shapes
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $script:Ended = $process.WaitForExit(180000)
    if (-not $Ended) { $process.Kill($true); $process.WaitForExit() }
    $script:ExitCode = $process.ExitCode
    $script:Output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
  }

  AfterAll {
    if ($Out -and (Test-Path -LiteralPath $Out)) { Remove-Item -LiteralPath $Out -Recurse -Force }
  }

  It 'ends the hung module and fails the run' {
    $Ended | Should -BeTrue -Because "the hang dump must end the module; it was still running after 180 s:`n$Output"
    $ExitCode | Should -Not -Be 0
  }

  It 'names the test that was still running' {
    $Output | Should -Match 'Hang dump timeout'
    $Output | Should -Match 'NeverCompletesAsync'
  }

  It 'writes the dump and its test list to the module''s TestResults, where the suites upload them from' {
    @(Get-ChildItem -Path $Results -Filter '*_hang.dmp' -ErrorAction SilentlyContinue).Count | Should -Be 1
    @(Get-ChildItem -Path $Results -Filter '*_hang.log' -ErrorAction SilentlyContinue).Count | Should -Be 1
  }
}
