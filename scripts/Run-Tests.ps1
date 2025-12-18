#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Runs all test projects in the Whizbang solution with parallel execution and detailed reporting.

.DESCRIPTION
    This script executes all test projects using dotnet test with Microsoft.Testing.Platform (MTP)
    in .NET 10, leveraging parallel test module execution for maximum performance.

    The script uses `dotnet test --max-parallel-test-modules` which allows multiple test projects
    to run concurrently, similar to what you see in VS Code Test Explorer.

.PARAMETER MaxParallel
    Maximum number of test projects to run in parallel (default: CPU core count)

.PARAMETER ProjectFilter
    Filter to specific test projects using native .NET 10 --test-modules globbing (e.g., "Core", "EFCore.Postgres")
    Pattern: **/bin/**/Debug/net10.0/*{Filter}*.dll

.PARAMETER TestFilter
    Filter to specific tests by name pattern using --treenode-filter (e.g., "ProcessWorkBatchAsync")
    Pattern: /*/*/*/*{Filter}* (matches test names containing the filter string)
    Uses MTP tree filter syntax for TUnit compatibility

.PARAMETER Verbose
    Show detailed test output for each project

.PARAMETER AiMode
    Enable AI-optimized output with sparse progress updates and detailed error diagnostics

.PARAMETER ProgressInterval
    Progress update interval in seconds for AiMode (default: 60)
    Progress also updates when test count increases by 100+ or failures occur

.EXAMPLE
    ./Run-Tests.ps1
    Runs all tests with automatic parallel detection

.EXAMPLE
    ./Run-Tests.ps1 -MaxParallel 4
    Runs tests with maximum 4 projects in parallel

.EXAMPLE
    ./Run-Tests.ps1 -ProjectFilter "Core"
    Runs only test projects with "Core" in the name

.EXAMPLE
    ./Run-Tests.ps1 -ProjectFilter "EFCore.Postgres" -TestFilter "ProcessWorkBatchAsync"
    Runs only tests containing "ProcessWorkBatchAsync" in EFCore.Postgres test project

.EXAMPLE
    ./Run-Tests.ps1 -Verbose
    Runs all tests with detailed output

.EXAMPLE
    ./Run-Tests.ps1 -AiMode
    Runs all tests with AI-optimized output (sparse progress every 60s, detailed error diagnostics)

.EXAMPLE
    ./Run-Tests.ps1 -AiMode -ProgressInterval 30
    Runs tests in AI mode with progress updates every 30 seconds

.EXAMPLE
    ./Run-Tests.ps1 -AiMode -ProjectFilter "EFCore.Postgres"
    Runs EFCore.Postgres tests with AI-optimized output

.NOTES
    Technology Stack (as of December 2025):
    - .NET 10.0.1 (LTS release, November 2025)
    - TUnit 1.2.11+ (modern source-generated testing framework)
    - Rocks 9.3.0+ (Roslyn-based mocking for AOT compatibility)
    - Microsoft.Testing.Platform 2.0+ (native test runner)

    The global.json configures MTP as the test runner, enabling full integration
    with dotnet test including parallel execution and VS Code Test Explorer.

    This script wraps `dotnet test` to provide:
    - Automatic parallel detection (CPU core count)
    - Native .NET 10 filtering (no manual project loops)
    - Dual output modes:
      * Normal mode: Native MTP progress with animated bars and rich colors
      * AI mode: Sparse progress updates + detailed error diagnostics (75% token reduction)
    - Simplified parameter interface

    Native Filtering (.NET 10):
    - ProjectFilter uses `--test-modules` with globbing patterns (**/bin/**/Debug/net10.0/*{Filter}*.dll)
    - TestFilter uses `--treenode-filter` with wildcard patterns (/*/*/*/*{Filter}*)
    - Leverages Microsoft.Testing.Platform filtering for maximum performance
    - Maintains full parallelism across filtered tests

    For more information:
    - TUnit: https://tunit.dev
    - Rocks: https://github.com/JasonBock/Rocks
    - .NET 10: https://dotnet.microsoft.com/download/dotnet/10.0
    - MTP: https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro
#>

[CmdletBinding()]
param(
    [int]$MaxParallel = 0,  # 0 = use Environment.ProcessorCount
    [string]$ProjectFilter = "",
    [string]$TestFilter = "",
    [switch]$VerboseOutput,
    [switch]$AiMode,
    [int]$ProgressInterval = 60  # Progress update interval in seconds (AiMode only)
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Navigate to repo root
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    # Determine parallel level
    if ($MaxParallel -eq 0) {
        $MaxParallel = [Environment]::ProcessorCount
    }

    if (-not $AiMode) {
        Write-Host "=====================================" -ForegroundColor Cyan
        Write-Host "  Whizbang Test Suite Runner" -ForegroundColor Cyan
        Write-Host "  (Parallel Execution via dotnet test)" -ForegroundColor Cyan
        Write-Host "=====================================" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Parallel Test Execution: Up to $MaxParallel test modules concurrently" -ForegroundColor Yellow
        if ($ProjectFilter) {
            Write-Host "Project Filter: $ProjectFilter" -ForegroundColor Yellow
        }
        if ($TestFilter) {
            Write-Host "Test Filter: $TestFilter" -ForegroundColor Yellow
        }
        Write-Host ""
    } else {
        Write-Host "[WHIZBANG TEST SUITE - AI MODE]" -ForegroundColor Cyan
        Write-Host "Max Parallel: $MaxParallel" -ForegroundColor Gray
        if ($ProjectFilter) {
            Write-Host "Project Filter: $ProjectFilter" -ForegroundColor Gray
        }
        if ($TestFilter) {
            Write-Host "Test Filter: $TestFilter" -ForegroundColor Gray
        }
    }

    # Build the dotnet test command
    $testArgs = @("test")

    # Add parallel execution
    $testArgs += "--max-parallel-test-modules"
    $testArgs += $MaxParallel.ToString()

    # Add verbosity if requested
    if ($VerboseOutput) {
        $testArgs += "--verbosity"
        $testArgs += "normal"
    } else {
        $testArgs += "--verbosity"
        $testArgs += "minimal"
    }

    # Use --test-modules with globbing pattern for project filtering
    if ($ProjectFilter) {
        # Native .NET 10 globbing: **/bin/**/Debug/net10.0/*{Filter}*.dll
        $testArgs += "--test-modules"
        $testArgs += "**/bin/**/Debug/net10.0/*$ProjectFilter*.dll"
    } else {
        # Use the main solution file (already filtered to test projects via <IsTestProject>)
        $testArgs += "--solution"
        $testArgs += "Whizbang.slnx"
    }

    # Use --treenode-filter for test name filtering (MTP native filtering)
    # Pattern: /*/*/*/*{Filter}* matches any assembly/namespace/class/method containing the filter
    if ($TestFilter) {
        $testArgs += "--treenode-filter"
        $testArgs += "/*/*/*/*$TestFilter*"
    }

    # Run tests
    if (-not $AiMode) {
        $cmdDisplay = "dotnet " + ($testArgs -join " ")
        if ($cmdDisplay.Length -gt 100) {
            $cmdDisplay = $cmdDisplay.Substring(0, 97) + "..."
        }
        Write-Host "Executing: $cmdDisplay" -ForegroundColor Gray
        Write-Host ""
    } else {
        Write-Host "Starting test execution..." -ForegroundColor Gray
        Write-Host ""
    }

    # Process output based on mode
    if ($AiMode) {
        # AI mode: Stream and filter with smart progress updates
        $totalTests = 0
        $totalPassed = 0
        $totalFailed = 0
        $totalSkipped = 0
        $failedTests = @()
        $testDetails = @{}  # Dictionary to store detailed error info per test
        $buildErrors = @()
        $buildWarnings = @()
        $currentFailedTest = $null
        $capturingStackTrace = $false
        $stackTraceLines = @()

        # Progress tracking
        $startTime = Get-Date
        $lastProgressTime = $startTime
        $lastTotalTests = 0
        $lastTotalFailed = 0

        & dotnet @testArgs 2>&1 | ForEach-Object {
            $lineStr = $_.ToString()

            # Capture test summary lines (TUnit format: "total:", "failed:", "succeeded:", "skipped:")
            if ($lineStr -match "^\s*total:\s+(\d+)\s*$") {
                $totalTests = [int]$matches[1]
            }
            elseif ($lineStr -match "^\s*failed:\s+(\d+)\s*$") {
                $totalFailed = [int]$matches[1]
            }
            elseif ($lineStr -match "^\s*succeeded:\s+(\d+)\s*$") {
                $totalPassed = [int]$matches[1]
            }
            elseif ($lineStr -match "^\s*skipped:\s+(\d+)\s*$") {
                $totalSkipped = [int]$matches[1]
            }

            # Smart progress updates
            $now = Get-Date
            $elapsedSinceLastProgress = ($now - $lastProgressTime).TotalSeconds
            $testCountChange = $totalTests - $lastTotalTests
            $failureCountChange = $totalFailed - $lastTotalFailed

            # Show progress if:
            # - ProgressInterval seconds elapsed, OR
            # - Test count increased by 100+, OR
            # - Failure count changed
            if ($elapsedSinceLastProgress -ge $ProgressInterval -or
                $testCountChange -ge 100 -or
                $failureCountChange -gt 0) {

                if ($totalTests -gt 0) {
                    $elapsedMinutes = [Math]::Floor(($now - $startTime).TotalMinutes)
                    $failureIndicator = if ($totalFailed -gt 0) { " ⚠️" } else { "" }
                    Write-Host "[$($elapsedMinutes)m] Progress: $totalPassed passed, $totalFailed failed, $totalSkipped skipped$failureIndicator" -ForegroundColor Gray

                    $lastProgressTime = $now
                    $lastTotalTests = $totalTests
                    $lastTotalFailed = $totalFailed
                }
            }
            # Capture failed test names (lines starting with "failed " followed by test name)
            elseif ($lineStr -match "^failed\s+([^\(]+)\s+\(") {
                # Save previous test's stack trace if we were capturing
                if ($currentFailedTest -and $stackTraceLines.Count -gt 0) {
                    $testDetails[$currentFailedTest]["StackTrace"] = $stackTraceLines -join "`n"
                    $stackTraceLines = @()
                }

                $testName = $matches[1].Trim()
                # Exclude false positives (EF Core logging, etc.)
                if ($testName -notmatch "executing|DbCommand|Executed") {
                    $failedTests += $testName
                    $currentFailedTest = $testName
                    $testDetails[$testName] = @{
                        "ErrorMessage" = ""
                        "StackTrace" = ""
                        "Exception" = ""
                    }
                    $capturingStackTrace = $false
                }
            }
            # Capture error messages and exception details for current failed test
            elseif ($currentFailedTest) {
                # Detect exception type lines (e.g., "System.InvalidOperationException: Message")
                if ($lineStr -match "^\s*(System\.\w+Exception|TUnit\.\w+Exception|.*Exception):\s*(.+)") {
                    $testDetails[$currentFailedTest]["Exception"] = $matches[1].Trim()
                    $testDetails[$currentFailedTest]["ErrorMessage"] = $matches[2].Trim()
                }
                # Detect assertion failure messages (TUnit specific patterns)
                elseif ($lineStr -match "Expected:?\s*(.+)") {
                    $testDetails[$currentFailedTest]["ErrorMessage"] += "`nExpected: " + $matches[1].Trim()
                }
                elseif ($lineStr -match "Actual:?\s*(.+)") {
                    $testDetails[$currentFailedTest]["ErrorMessage"] += "`nActual: " + $matches[1].Trim()
                }
                elseif ($lineStr -match "But was:?\s*(.+)") {
                    $testDetails[$currentFailedTest]["ErrorMessage"] += "`nBut was: " + $matches[1].Trim()
                }
                # Detect start of stack trace (lines with "at " followed by namespace/method)
                elseif ($lineStr -match "^\s+at\s+[\w\.]+") {
                    $capturingStackTrace = $true
                    $stackTraceLines += $lineStr.Trim()
                }
                # Continue capturing stack trace lines
                elseif ($capturingStackTrace) {
                    # Stack trace continues if line starts with whitespace and contains "at " or file path
                    if ($lineStr -match "^\s+at\s+" -or $lineStr -match "^\s+in\s+.*:\s*line\s+\d+") {
                        $stackTraceLines += $lineStr.Trim()
                    }
                    else {
                        # Stack trace ended
                        $capturingStackTrace = $false
                        if ($stackTraceLines.Count -gt 0) {
                            $testDetails[$currentFailedTest]["StackTrace"] = $stackTraceLines -join "`n"
                            $stackTraceLines = @()
                        }
                    }
                }
                # Generic error message capture (non-empty lines that don't match other patterns)
                elseif ($lineStr.Trim() -ne "" -and
                        $lineStr -notmatch "^\s*(duration|total|failed|succeeded|skipped|passed):" -and
                        $lineStr -notmatch "^(Building|Determining|Restored)" -and
                        $testDetails[$currentFailedTest]["ErrorMessage"] -eq "") {
                    $testDetails[$currentFailedTest]["ErrorMessage"] = $lineStr.Trim()
                }
            }
            # Capture build errors
            elseif ($lineStr -match "error\s+(CS\d+|MSB\d+):") {
                $buildErrors += $lineStr.Trim()
            }
            # Capture critical warnings (skip common noise)
            elseif ($lineStr -match "warning\s+(CS\d+|MSB\d+|EFCORE\d+):" -and
                    $lineStr -notmatch "CS8019" -and  # Unnecessary using directive
                    $lineStr -notmatch "CS0105" -and  # Duplicate using directive
                    $lineStr -notmatch "CS8618" -and  # Non-nullable field
                    $lineStr -notmatch "CS8600" -and  # Converting null literal
                    $lineStr -notmatch "CS8601" -and  # Possible null reference assignment
                    $lineStr -notmatch "CS8602" -and  # Dereference of null reference
                    $lineStr -notmatch "CS8603" -and  # Possible null reference return
                    $lineStr -notmatch "CS8604" -and  # Possible null reference argument
                    $lineStr -notmatch "CS8619" -and  # Nullability mismatch
                    $lineStr -notmatch "CS8714" -and  # Type parameter nullability
                    $lineStr -notmatch "CS0414" -and  # Field assigned but never used
                    $lineStr -notmatch "EFCORE998" -and  # No DbContext classes found
                    $lineStr -notmatch "merge-message-registries") {
                $buildWarnings += $lineStr.Trim()
            }
        }

        # Save final test's stack trace if we were capturing
        if ($currentFailedTest -and $stackTraceLines.Count -gt 0) {
            $testDetails[$currentFailedTest]["StackTrace"] = $stackTraceLines -join "`n"
        }

        # Display summary
        Write-Host ""
        Write-Host "=== TEST RESULTS SUMMARY ===" -ForegroundColor Cyan

        if ($totalTests -gt 0) {
            Write-Host "Total Tests: $totalTests" -ForegroundColor White
            Write-Host "Passed: $totalPassed" -ForegroundColor Green
            Write-Host "Failed: $totalFailed" -ForegroundColor $(if ($totalFailed -gt 0) { "Red" } else { "Green" })
            Write-Host "Skipped: $totalSkipped" -ForegroundColor Yellow

            if ($totalPassed + $totalFailed + $totalSkipped -ne $totalTests) {
                Write-Host "Warning: Test counts don't add up (${totalPassed} + ${totalFailed} + ${totalSkipped} != ${totalTests})" -ForegroundColor Yellow
            }
        } else {
            Write-Host "No test results parsed" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "Possible reasons:" -ForegroundColor Yellow
            Write-Host "  1. Build failed (check BUILD ERRORS section above)" -ForegroundColor Gray
            Write-Host "  2. Test filter matched zero tests (try broader pattern)" -ForegroundColor Gray
            Write-Host "  3. Project filter matched zero projects (check project name)" -ForegroundColor Gray
            Write-Host ""
            Write-Host "Try running without filters to see all tests:" -ForegroundColor Yellow
            Write-Host "  pwsh scripts/Run-Tests.ps1 -AiMode" -ForegroundColor Gray
        }

        if ($buildErrors.Count -gt 0) {
            Write-Host ""
            Write-Host "=== BUILD ERRORS ($($buildErrors.Count)) ===" -ForegroundColor Red
            $buildErrors | Select-Object -First 10 | ForEach-Object { Write-Host $_ -ForegroundColor Red }
            if ($buildErrors.Count -gt 10) {
                Write-Host "... and $($buildErrors.Count - 10) more errors" -ForegroundColor Red
            }
        }

        if ($buildWarnings.Count -gt 0) {
            Write-Host ""
            Write-Host "=== BUILD WARNINGS ($($buildWarnings.Count)) ===" -ForegroundColor Yellow
            Write-Host "(Showing first 5, filtered for relevance)" -ForegroundColor Gray
            $buildWarnings | Select-Object -First 5 | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
        }

        if ($failedTests.Count -gt 0) {
            Write-Host ""
            Write-Host "=== FAILED TESTS ($($failedTests.Count)) ===" -ForegroundColor Red
            Write-Host ""

            foreach ($testName in $failedTests) {
                Write-Host "TEST: $testName" -ForegroundColor Red
                Write-Host "----------------------------------------" -ForegroundColor DarkGray

                $details = $testDetails[$testName]

                # Show exception type if captured
                if ($details["Exception"] -ne "") {
                    Write-Host "Exception: $($details["Exception"])" -ForegroundColor Yellow
                }

                # Show error message
                if ($details["ErrorMessage"] -ne "") {
                    Write-Host "Error Message:" -ForegroundColor Yellow
                    Write-Host $details["ErrorMessage"] -ForegroundColor Gray
                }

                # Show stack trace (limit to most relevant lines)
                if ($details["StackTrace"] -ne "") {
                    Write-Host ""
                    Write-Host "Stack Trace:" -ForegroundColor Yellow
                    $stackLines = $details["StackTrace"] -split "`n"
                    # Show first 15 lines of stack trace (usually the most relevant)
                    $linesToShow = [Math]::Min($stackLines.Count, 15)
                    for ($i = 0; $i -lt $linesToShow; $i++) {
                        Write-Host "  $($stackLines[$i])" -ForegroundColor Gray
                    }
                    if ($stackLines.Count -gt 15) {
                        Write-Host "  ... ($($stackLines.Count - 15) more lines)" -ForegroundColor DarkGray
                    }
                }

                Write-Host ""
            }
        }

        Write-Host ""
    } else {
        # Normal mode: Pass through to native MTP output with built-in progress
        & dotnet @testArgs
    }

    # Check exit code
    if ($LASTEXITCODE -eq 0) {
        if ($AiMode) {
            Write-Host "=== STATUS: ALL TESTS PASSED ===" -ForegroundColor Green
        } else {
            Write-Host ""
            Write-Host "=====================================" -ForegroundColor Green
            Write-Host "  All tests passed!" -ForegroundColor Green
            Write-Host "=====================================" -ForegroundColor Green
        }
        exit 0
    } else {
        if ($AiMode) {
            Write-Host "=== STATUS: TESTS FAILED OR BUILD ERRORS ===" -ForegroundColor Red
        } else {
            Write-Host ""
            Write-Host "=====================================" -ForegroundColor Red
            Write-Host "  Some tests failed or build errors occurred" -ForegroundColor Red
            Write-Host "=====================================" -ForegroundColor Red
        }
        exit 1
    }

} finally {
    Pop-Location
}
