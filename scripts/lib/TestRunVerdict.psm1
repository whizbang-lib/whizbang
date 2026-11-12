# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

# The verdict on one `dotnet test` run, as scripts/Run-Tests.ps1 (and so every CI test job) reports it.
# Tested by .github/scripts/tests/Run-Tests.Tests.ps1, which runs the real script against a fake `dotnet`.

# Lines that mean a test process died partway through its suite (#2231). The tests after that point never
# ran, so the counts printed afterwards are a fraction of the suite, and the platform counts the dead module
# as one `error:`, the same shape as the broken-pipe noise tolerated after a clean run.
$script:TestProcessCrashPatterns = @(
  # Microsoft.Testing.Platform, when the test application's process ends without finishing its session.
  "Test application process didn't exit gracefully"
  'Test host did not exit gracefully'
  # VSTest's equivalent.
  'Test host process crashed'
  # The runtime's own report of a stack overflow, printed before it kills the process.
  '^\s*Stack overflow\.'
)

# Microsoft.Testing.Platform's exit code for "the test host process exited non-gracefully".
$script:TestProcessCrashExitCode = 7

# Exit code 8: every selected test was skipped (TUnit). Not a failure.
$script:ZeroTestsExitCode = 8

function Select-TestProcessCrashLine {
  <#
  .SYNOPSIS
    Returns the lines that report a crashed test process, trimmed; nothing when there are none.
  #>
  [CmdletBinding()]
  [OutputType([string])]
  param(
    [AllowNull()][AllowEmptyCollection()][AllowEmptyString()]
    [string[]]$Line
  )
  foreach ($l in @($Line)) {
    if ([string]::IsNullOrWhiteSpace($l)) { continue }
    foreach ($pattern in $script:TestProcessCrashPatterns) {
      if ($l -match $pattern) {
        $l.Trim()
        break
      }
    }
  }
}

function Get-TestRunVerdict {
  <#
  .SYNOPSIS
    Decides whether a test run failed.
  .DESCRIPTION
    A run fails when a test failed, fail-fast stopped it, a test project or the build reported errors, the
    process exited non-zero for a reason other than cleanup noise, or a test process crashed. Cleanup noise
    is a non-zero exit with no failed test and an infrastructure error (the platform's named pipe breaking
    while the test host is torn down); it only warns, and only when some test passed. A crash is never
    cleanup noise, whatever the counts say: the tests after it never ran.
  .OUTPUTS
    HasErrors (the run failed), HasTestFailures (it failed for a reason other than infrastructure errors with
    no passing test, so its coverage is partial), CleanupNoise (passed, with a warning to print), Crashed, and
    CrashReason (the crash lines, or the exit code when the output carried none).
  #>
  [CmdletBinding()]
  [OutputType([pscustomobject])]
  param(
    [int]$ExitCode,
    [int]$Passed,
    [int]$Failed,
    [int]$InfrastructureErrors,
    [bool]$FailFastTriggered,
    [int]$ProjectErrorCount,
    [int]$BuildErrorCount,
    [AllowNull()][AllowEmptyCollection()]
    [string[]]$CrashLines
  )
  $crashLineList = @($CrashLines | Where-Object { $_ })
  $crashed = $crashLineList.Count -gt 0 -or $ExitCode -eq $script:TestProcessCrashExitCode
  $exitFailed = $ExitCode -ne 0 -and $ExitCode -ne $script:ZeroTestsExitCode
  $exitIsCleanupNoise = -not $crashed -and $exitFailed -and $Failed -eq 0 -and $InfrastructureErrors -gt 0
  $hasTestFailures = $crashed -or $Failed -gt 0 -or $FailFastTriggered -or $ProjectErrorCount -gt 0 -or
    $BuildErrorCount -gt 0 -or ($exitFailed -and -not $exitIsCleanupNoise)
  $infraOnly = ($InfrastructureErrors -gt 0 -or $exitIsCleanupNoise) -and -not $hasTestFailures
  $cleanupNoise = $infraOnly -and $Passed -gt 0
  $hasErrors = if ($cleanupNoise) { $false } else { $hasTestFailures -or ($InfrastructureErrors -gt 0 -and $Passed -eq 0) }

  $reason = ''
  if ($crashed) {
    $reason = if ($crashLineList.Count -gt 0) { $crashLineList -join '; ' } else { "the test process ended with exit code $ExitCode" }
    if ($crashLineList.Count -gt 0 -and $ExitCode -ne 0) { $reason += " (exit code $ExitCode)" }
  }

  [pscustomobject]@{
    HasErrors       = $hasErrors
    HasTestFailures = $hasTestFailures
    CleanupNoise    = $cleanupNoise
    Crashed         = $crashed
    CrashReason     = $reason
  }
}

function Write-TestProcessCrash {
  <#
  .SYNOPSIS
    Names a crashed test process: a workflow error annotation under GitHub Actions, red text otherwise.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][pscustomobject]$Verdict,
    [int]$Passed
  )
  $message = "TEST PROCESS CRASHED after $Passed passing test(s); the rest of the suite never ran. Cause: $($Verdict.CrashReason)"
  Write-Host ''
  if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::error::$message" } else { Write-Host $message -ForegroundColor Red }
}

Export-ModuleMember -Function Select-TestProcessCrashLine, Get-TestRunVerdict, Write-TestProcessCrash
