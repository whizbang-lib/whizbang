# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# A test host process the runtime kills (an internal CLR error, an access violation, an Environment.FailFast) used to
# leave a suite with an exit code and nothing else: no result, no dump, nothing to say what the process was doing
# (#2446). The hang dump (#2402, TestSuiteHangDump.Tests.ps1) only fires on a module that stops moving, never on one
# that dies. Every suite now runs its modules with the testing platform's crash dump as well (Run-Tests.ps1
# -CrashDumpType): a module whose process crashes writes <module>_<pid>_crash.dmp, and the tests still running in
# <module>_<id>_crash.sequence.log, to TestResults under `dotnet test`'s working directory (the repository root), and
# the suite uploads both when it fails or is canceled. The dump is named for the test module's own process, not
# `dotnet` as the hang dump is.
#
# Heap is the type the suites take: the managed heap and every thread's stack, which is what a runtime crash needs,
# without a full dump's copy of every mapped image (about 200 MB for the one-test module below). The suites are derived from the files, so a new
# reusable-test-*.yml is checked as well.

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
}

Describe 'Every test suite takes a crash dump (#2446)' {
  It 'finds the suites' {
    $Suites.Count | Should -BeGreaterThan 0 -Because 'with no suite found every check below would pass on nothing'
  }

  Context '<Workflow>' -ForEach $Suites {
    BeforeAll {
      $script:Steps = Get-WorkflowStep $Path
      $script:Runs = @($Steps | Where-Object { $_.Text -match '^\s+run:.*Run-Tests\.ps1' })
    }

    It 'runs every module with a heap crash dump' {
      $Runs.Count | Should -BeGreaterThan 0
      foreach ($run in $Runs) {
        $line = ($run.Text | Where-Object { $_ -match '^\s+run:.*Run-Tests\.ps1' }) -join "`n"
        $line | Should -Match '-CrashDumpType Heap( |$)' -Because "every Run-Tests.ps1 call must pass it: $line"
      }
    }

    It 'uploads the crash dumps after the last test run, when the job fails or is canceled' {
      # By a path line, not any mention: the comment above a step is read as the end of the step before it.
      $upload = @($Steps | Where-Object { $_.Uses -like 'actions/upload-artifact@*' -and @($_.Text -match '^\s+\S+_crash\.dmp\s*$').Count -gt 0 })
      $upload.Count | Should -Be 1
      $upload[0].If | Should -Match 'failure\(\)'
      $upload[0].If | Should -Match 'cancelled\(\)' # review-checks:allow (GitHub expression function name)
      $text = $upload[0].Text -join "`n"
      foreach ($glob in 'TestResults/*_crash.dmp', 'TestResults/*_crash.sequence.log', 'tests/**/TestResults/*_crash.dmp',
        'tests/**/TestResults/*_crash.sequence.log', 'samples/**/TestResults/*_crash.dmp', 'samples/**/TestResults/*_crash.sequence.log') {
        $text | Should -Match ([regex]::Escape($glob))
      }
      $text | Should -Match 'if-no-files-found: ignore'
      $text | Should -Match 'retention-days: 7'
      $upload[0].Index | Should -BeGreaterThan ($Runs | Measure-Object -Property Index -Maximum).Maximum
    }
  }
}

# The checks above prove every suite asks for a crash dump; this one proves the platform writes it the way the suites
# ask, with the coverage collector and the hang dump on the same module, as every suite runs it. shapes/CrashRunner,
# built from the repository's package versions, runs a test that calls Environment.FailFast under `dotnet test` with
# the options Run-Tests.ps1 passes. The cap only bounds the failure: a crashed module ends in seconds.
Describe 'The crash dump is written under dotnet test (#2446)' {
  BeforeAll {
    $shapes = Join-Path $PSScriptRoot 'shapes'
    $script:Out = Join-Path ([System.IO.Path]::GetTempPath()) "whizbang-crash-$([guid]::NewGuid().ToString('N'))"
    $build = & dotnet build (Join-Path $shapes 'CrashRunner' 'CrashRunner.csproj') -c Release "-p:ShapesOut=$Out/" -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Building shapes/CrashRunner failed:`n$($build -join "`n")" }
    $module = Join-Path 'bin' 'CrashRunner' 'Release' 'net11.0' 'CrashRunner.dll'
    # As with the hang dump, `dotnet test` writes the dump to TestResults under its own working directory.
    $script:Results = Join-Path $shapes 'TestResults'
    if (Test-Path -LiteralPath $Results) { Remove-Item -LiteralPath $Results -Recurse -Force }

    # From the shapes directory, so the repository's global.json puts `dotnet test` on the testing platform.
    $psi = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($a in 'test', '--root-directory', $Out, '--test-modules', $module, '--coverage', '--hangdump', '--hangdump-timeout', '10m', '--crashdump', '--crashdump-type', 'Heap') { $psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $shapes
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $script:Ended = $process.WaitForExit(300000)
    if (-not $Ended) { $process.Kill($true); $process.WaitForExit() }
    $script:ExitCode = $process.ExitCode
    $script:Output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
  }

  AfterAll {
    if ($Out -and (Test-Path -LiteralPath $Out)) { Remove-Item -LiteralPath $Out -Recurse -Force }
    if ($Results -and (Test-Path -LiteralPath $Results)) { Remove-Item -LiteralPath $Results -Recurse -Force }
  }

  It 'fails the run when the module crashes' {
    $Ended | Should -BeTrue -Because "a crashed module must end the run; it was still running after 300 s:`n$Output"
    $ExitCode | Should -Not -Be 0
    $Output | Should -Not -Match 'Unknown option' -Because "the module must accept the options the suites pass:`n$Output"
  }

  It 'names the test that was running when the process crashed' {
    $Output | Should -Match 'crashed, a dump file was generated'
    $Output | Should -Match 'FailsFast'
  }

  It 'writes the crash dump and its test list to TestResults in the working directory, where the suites upload them from' {
    @(Get-ChildItem -Path $Results -Filter 'CrashRunner_*_crash.sequence.log' -ErrorAction SilentlyContinue).Count | Should -Be 1
    $dumps = @(Get-ChildItem -Path $Results -Recurse -Filter '*_crash.dmp' -ErrorAction SilentlyContinue)
    $dumps.Count | Should -Be 1 -Because "the run wrote:`n$((Get-ChildItem -Path $Results, $Out -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object FullName) -join "`n")`n$Output"
    $dumps[0].DirectoryName | Should -Be (Get-Item -LiteralPath $Results).FullName
    $dumps[0].Name | Should -Match '^CrashRunner_\d+_crash\.dmp$'
    $dumps[0].Length | Should -BeGreaterThan 0
  }
}
