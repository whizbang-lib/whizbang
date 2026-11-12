# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# scripts/Run-Tests.ps1 is what every CI test job runs, so its verdict is the job's verdict. These tests run
# the real script against a small repository with a fake `dotnet` (and a fake `docker`, so the container
# cleanup it does after every run never touches a real container) first on PATH. The fake records each
# call, builds a test DLL for the configuration it is asked for, and answers `dotnet test` with a recorded
# output and exit code. The fakes are POSIX shell scripts, so these tests skip themselves on Windows.

BeforeDiscovery {
  $script:NoPosixShell = $IsWindows
}

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path

  # A repository holding the real Run-Tests.ps1 and its modules, and one Unit test project in the solution.
  # Returns the repository path; the fakes live in <repo>/.fake.
  function New-FakeRepo {
    $repo = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $repo 'scripts'), (Join-Path $repo 'tests' 'A.Tests'), (Join-Path $repo '.fake' 'bin') | Out-Null
    Copy-Item -LiteralPath (Join-Path $Root 'scripts' 'Run-Tests.ps1') -Destination (Join-Path $repo 'scripts')
    Copy-Item -LiteralPath (Join-Path $Root 'scripts' 'lib') -Destination (Join-Path $repo 'scripts') -Recurse
    Set-Content -LiteralPath (Join-Path $repo 'Whizbang.slnx') -Value '<Solution><Project Path="tests/A.Tests/A.Tests.csproj" /></Solution>'
    Set-Content -LiteralPath (Join-Path $repo 'tests' 'A.Tests' 'A.Tests.csproj') -Value '<Project><PropertyGroup><IsTestProject>true</IsTestProject><WhizbangTestType>Unit</WhizbangTestType></PropertyGroup></Project>'

    $bin = Join-Path $repo '.fake' 'bin'
    # `dotnet build` writes A.Tests.dll under the configuration it was given (Debug when none, as the real
    # one does); `dotnet test` prints FAKE_TEST_OUTPUT and exits FAKE_TEST_EXIT; anything else succeeds.
    Set-Content -LiteralPath (Join-Path $bin 'dotnet') -Value @'
#!/bin/sh
echo "dotnet $*" >> "$FAKE_LOG"
case "$1" in
  build)
    cfg=Debug; prev=""
    for a in "$@"; do
      if [ "$prev" = "--configuration" ] || [ "$prev" = "-c" ]; then cfg="$a"; fi
      prev="$a"
    done
    mkdir -p "$FAKE_REPO/tests/A.Tests/bin/$cfg/net10.0"
    touch "$FAKE_REPO/tests/A.Tests/bin/$cfg/net10.0/A.Tests.dll"
    exit 0;;
  test)
    if [ -n "$FAKE_TEST_OUTPUT" ]; then cat "$FAKE_TEST_OUTPUT"; fi
    exit "${FAKE_TEST_EXIT:-0}";;
  *) exit 0;;
esac
'@
    Set-Content -LiteralPath (Join-Path $bin 'docker') -Value "#!/bin/sh`nexit 0"
    chmod +x (Join-Path $bin 'dotnet') (Join-Path $bin 'docker')
    $repo
  }

  # Marks the test project as built for a configuration, as a CI download of the build output does.
  function Set-Built([string]$Repo, [string]$Configuration) {
    $dir = Join-Path $Repo 'tests' 'A.Tests' 'bin' $Configuration 'net10.0'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $dir 'A.Tests.dll') -Force | Out-Null
  }

  # Runs the copied Run-Tests.ps1 in a child pwsh with the fakes first on PATH. `dotnet test` prints
  # $TestOutput and exits $TestExitCode. Returns the exit code, the combined output and the fake's call log.
  function Invoke-RunTests {
    param([string]$Repo, [string[]]$Arguments, [string]$TestOutput = '', [int]$TestExitCode = 0)
    $outputFile = Join-Path $Repo '.fake' 'test-output.txt'
    $log = Join-Path $Repo '.fake' 'calls.log'
    Set-Content -LiteralPath $outputFile -Value $TestOutput
    Set-Content -LiteralPath $log -Value ''
    $saved = @{ PATH = $env:PATH; FAKE_LOG = $env:FAKE_LOG; FAKE_REPO = $env:FAKE_REPO; FAKE_TEST_OUTPUT = $env:FAKE_TEST_OUTPUT; FAKE_TEST_EXIT = $env:FAKE_TEST_EXIT; GITHUB_ACTIONS = $env:GITHUB_ACTIONS }
    try {
      $env:PATH = "$(Join-Path $Repo '.fake' 'bin')$([IO.Path]::PathSeparator)$($saved.PATH)"
      $env:FAKE_LOG = $log
      $env:FAKE_REPO = $Repo
      $env:FAKE_TEST_OUTPUT = $outputFile
      $env:FAKE_TEST_EXIT = "$TestExitCode"
      $env:GITHUB_ACTIONS = $null
      Push-Location $Repo
      try {
        $out = & pwsh -NoProfile -File (Join-Path $Repo 'scripts' 'Run-Tests.ps1') @Arguments 2>&1 | Out-String
        $code = $LASTEXITCODE
      } finally {
        Pop-Location
      }
    } finally {
      foreach ($k in $saved.Keys) { Set-Item -Path "env:$k" -Value $saved[$k] }
    }
    [pscustomobject]@{ ExitCode = $code; Output = $out; Calls = @(Get-Content -LiteralPath $log | Where-Object { $_ }) }
  }

  # What `dotnet test` prints when a test module's process dies partway through (a stack overflow here):
  # the runtime's own report, the platform's "didn't exit gracefully" line, a module error counted as
  # `error: 1`, and the counts of only the tests that ran before it died. Exit code 7 is the platform's
  # "test host process exited non-gracefully".
  $script:CrashOutput = @'
Stack overflow.
Repeat 2000 times:
--------------------------------
   at A.Tests.Recursive.Next()
--------------------------------
[+10/x0/?0] A.Tests.dll (net10.0|arm64)
Test application process didn't exit gracefully, exit code is '7'
Test run summary: Aborted! - A.Tests.dll (net10.0|arm64)
  total: 10
  failed: 0
  succeeded: 10
  skipped: 0
  error: 1
'@

  # A crash the platform reports only by its exit code: no runtime or platform message reached the output.
  $script:SilentCrashOutput = @'
Test run summary: Aborted! - A.Tests.dll (net10.0|arm64)
  total: 10
  failed: 0
  succeeded: 10
  skipped: 0
  error: 1
'@

  # Every test ran and passed; the named pipe broke while the test host was being torn down. This is the
  # cleanup noise the script tolerates with a warning.
  $script:CleanupNoiseOutput = @'
Test run summary: Passed! - A.Tests.dll (net10.0|arm64)
  total: 10
  failed: 0
  succeeded: 10
  skipped: 0
  error: 1
System.IO.IOException: Broken pipe
   at System.IO.Pipes.PipeStream.WriteCore(ReadOnlySpan`1 buffer)
   at Microsoft.Testing.Platform.IPC.NamedPipeServer.SendMessageAsync()
'@
}

Describe 'Run-Tests.ps1 reports a crashed test process as a failure (#2231)' -Skip:$NoPosixShell {
  It 'fails a <Mode> run whose test process crashed, naming the crash' -ForEach @(
    @{ Mode = 'Unit' }, @{ Mode = 'AiUnit' }) {
    $repo = New-FakeRepo
    Set-Built $repo 'Release'
    $r = Invoke-RunTests -Repo $repo -Arguments @('-Mode', $Mode, '-Configuration', 'Release', '-NoBuild') -TestOutput $CrashOutput -TestExitCode 7
    ($r.Calls -join "`n") | Should -Match '(?m)^dotnet test .*--test-modules tests/A\.Tests/bin/Release/net10\.0/A\.Tests\.dll' -Because 'the test step must have run, or a failure here would prove nothing'
    $r.ExitCode | Should -Not -Be 0
    $r.Output | Should -Match 'TEST PROCESS CRASHED'
    $r.Output | Should -Match "didn't exit gracefully"
    $r.Output | Should -Not -Match 'cleanup noise'
  }

  It 'fails a <Mode> run whose test process exited with code 7 and printed nothing about it' -ForEach @(
    @{ Mode = 'Unit' }, @{ Mode = 'AiUnit' }) {
    $repo = New-FakeRepo
    Set-Built $repo 'Release'
    $r = Invoke-RunTests -Repo $repo -Arguments @('-Mode', $Mode, '-Configuration', 'Release', '-NoBuild') -TestOutput $SilentCrashOutput -TestExitCode 7
    $r.ExitCode | Should -Not -Be 0
    $r.Output | Should -Match 'TEST PROCESS CRASHED'
    $r.Output | Should -Match 'exit code 7'
  }

  It 'still passes a <Mode> run whose only problem is a broken pipe after every test passed' -ForEach @(
    @{ Mode = 'Unit' }, @{ Mode = 'AiUnit' }) {
    $repo = New-FakeRepo
    Set-Built $repo 'Release'
    $r = Invoke-RunTests -Repo $repo -Arguments @('-Mode', $Mode, '-Configuration', 'Release', '-NoBuild') -TestOutput $CleanupNoiseOutput -TestExitCode 1
    ($r.Calls -join "`n") | Should -Match '(?m)^dotnet test ' -Because 'the test step must have run'
    $r.ExitCode | Should -Be 0
    $r.Output | Should -Match 'cleanup noise'
    $r.Output | Should -Not -Match 'TEST PROCESS CRASHED'
  }
}

# The rules behind the verdict, one case each, without a child process.
Describe 'Get-TestRunVerdict' {
  BeforeAll {
    Import-Module (Join-Path $Root 'scripts' 'lib' 'TestRunVerdict.psm1') -Force
    function Get-Verdict([hashtable]$Overrides) {
      $a = @{ ExitCode = 0; Passed = 10; Failed = 0; InfrastructureErrors = 0; FailFastTriggered = $false; ProjectErrorCount = 0; BuildErrorCount = 0; CrashLines = @() }
      foreach ($k in $Overrides.Keys) { $a[$k] = $Overrides[$k] }
      Get-TestRunVerdict @a
    }
  }

  It 'is <Expected> for <Case>' -ForEach @(
    @{ Case = 'a clean run'; Overrides = @{}; Expected = $false }
    @{ Case = 'every test skipped (exit 8)'; Overrides = @{ ExitCode = 8 }; Expected = $false }
    @{ Case = 'a failed test'; Overrides = @{ Failed = 1; ExitCode = 2 }; Expected = $true }
    @{ Case = 'fail-fast'; Overrides = @{ FailFastTriggered = $true }; Expected = $true }
    @{ Case = 'a test project error'; Overrides = @{ ProjectErrorCount = 1 }; Expected = $true }
    @{ Case = 'a build error'; Overrides = @{ BuildErrorCount = 1 }; Expected = $true }
    @{ Case = 'a non-zero exit with no infrastructure error'; Overrides = @{ ExitCode = 1 }; Expected = $true }
    @{ Case = 'cleanup noise after passing tests'; Overrides = @{ ExitCode = 1; InfrastructureErrors = 1 }; Expected = $false }
    @{ Case = 'an infrastructure error with no passing test'; Overrides = @{ Passed = 0; ExitCode = 1; InfrastructureErrors = 1 }; Expected = $true }
    @{ Case = 'an infrastructure error on a zero exit with passing tests'; Overrides = @{ InfrastructureErrors = 1 }; Expected = $false }
    @{ Case = 'exit code 7'; Overrides = @{ ExitCode = 7; InfrastructureErrors = 1 }; Expected = $true }
    @{ Case = 'a crash line on a zero exit'; Overrides = @{ CrashLines = @('Stack overflow.') }; Expected = $true }
  ) {
    (Get-Verdict $Overrides).HasErrors | Should -Be $Expected
  }

  It 'never calls a crash cleanup noise, and marks its coverage partial' {
    $v = Get-Verdict @{ ExitCode = 7; InfrastructureErrors = 1; CrashLines = @("Test application process didn't exit gracefully, exit code is '7'") }
    $v.Crashed | Should -BeTrue
    $v.CleanupNoise | Should -BeFalse
    $v.HasTestFailures | Should -BeTrue
    $v.CrashReason | Should -Be "Test application process didn't exit gracefully, exit code is '7' (exit code 7)"
  }

  It 'names the crash lines alone when the exit code was zero' {
    (Get-Verdict @{ CrashLines = @('Stack overflow.', 'Test host process crashed') }).CrashReason | Should -Be 'Stack overflow.; Test host process crashed'
  }

  It 'names the exit code when no crash line was printed' {
    (Get-Verdict @{ ExitCode = 7 }).CrashReason | Should -Be 'the test process ended with exit code 7'
  }

  It 'gives no crash reason for a run that did not crash' {
    $v = Get-Verdict @{ ExitCode = 1; InfrastructureErrors = 1 }
    $v.Crashed | Should -BeFalse
    $v.CleanupNoise | Should -BeTrue
    $v.CrashReason | Should -Be ''
  }
}

Describe 'Select-TestProcessCrashLine' {
  BeforeAll {
    Import-Module (Join-Path $Root 'scripts' 'lib' 'TestRunVerdict.psm1') -Force
  }

  It 'selects <Line>' -ForEach @(
    @{ Line = "Test application process didn't exit gracefully, exit code is '7'" }
    @{ Line = 'Test host did not exit gracefully.' }
    @{ Line = 'The active run was aborted: Test host process crashed' }
    @{ Line = 'Stack overflow.' }
  ) {
    @(Select-TestProcessCrashLine -Line @("  $Line  ")) | Should -Be @($Line)
  }

  It 'selects nothing from ordinary output, blank lines or no lines' {
    @(Select-TestProcessCrashLine -Line @('  succeeded: 10', '', $null, 'System.IO.IOException: Broken pipe', 'a test named Stack overflow. guard')) | Should -HaveCount 0
    @(Select-TestProcessCrashLine -Line $null) | Should -HaveCount 0
    @(Select-TestProcessCrashLine -Line @()) | Should -HaveCount 0
  }
}

Describe 'Write-TestProcessCrash' {
  BeforeAll {
    Import-Module (Join-Path $Root 'scripts' 'lib' 'TestRunVerdict.psm1') -Force
  }

  It 'writes a workflow error annotation under GitHub Actions' {
    $saved = $env:GITHUB_ACTIONS
    try {
      $env:GITHUB_ACTIONS = 'true'
      $out = Write-TestProcessCrash -Verdict ([pscustomobject]@{ CrashReason = 'Stack overflow.' }) -Passed 3 6>&1 | Out-String
    } finally {
      $env:GITHUB_ACTIONS = $saved
    }
    $out | Should -Match '::error::TEST PROCESS CRASHED after 3 passing test\(s\); the rest of the suite never ran\. Cause: Stack overflow\.'
  }
}
