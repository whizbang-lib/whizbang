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

.PARAMETER Mode
    Test execution mode (default: Ai)
    - Ai: AI-optimized sparse output + exclude integration tests (fast, token-efficient)
    - Ci: Full output + exclude integration tests (for CI/CD pipelines)
    - Full: Full output + include all tests (comprehensive validation)
    - AiFull: AI-optimized output + include all tests (comprehensive but token-efficient)
    - IntegrationsOnly: Full output + only integration tests
    - AiIntegrations: AI-optimized output + only integration tests

.PARAMETER ProgressInterval
    Progress update interval in seconds for AI modes (default: 60)

.PARAMETER LiveUpdates
    Show progress immediately when test counts change (AI modes only)
    Without this flag, progress respects ProgressInterval for sparse updates

.PARAMETER ExcludeIntegration
    DEPRECATED: Use -Mode instead. Exclude integration tests from the run.

.PARAMETER AiMode
    DEPRECATED: Use -Mode instead. Enable AI-optimized output.

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
    ./Run-Tests.ps1 -Mode Ai
    Runs tests with AI-optimized output, excluding integration tests (default mode)

.EXAMPLE
    ./Run-Tests.ps1 -Mode Ci
    Runs tests with full output, excluding integration tests (for CI/CD)

.EXAMPLE
    ./Run-Tests.ps1 -Mode Full
    Runs ALL tests including integration tests with full output (5-10+ minutes)

.EXAMPLE
    ./Run-Tests.ps1 -Mode AiFull
    Runs ALL tests including integration tests with AI-optimized output

.EXAMPLE
    ./Run-Tests.ps1 -Mode IntegrationsOnly
    Runs ONLY integration tests with full output

.EXAMPLE
    ./Run-Tests.ps1 -Mode AiIntegrations
    Runs ONLY integration tests with AI-optimized output

.EXAMPLE
    ./Run-Tests.ps1 -Mode Ai -ProgressInterval 30
    Runs tests in AI mode with progress updates every 30 seconds

.EXAMPLE
    ./Run-Tests.ps1 -Mode Ai -ProjectFilter "EFCore.Postgres"
    Runs EFCore.Postgres tests with AI-optimized output

.EXAMPLE
    ./Run-Tests.ps1 -TestFilter "Lifecycle"
    Runs all tests with "Lifecycle" in the class or test name
    Pattern: /*/*/*/*Lifecycle*

.EXAMPLE
    ./Run-Tests.ps1 -ProjectFilter "Integration.Tests" -TestFilter "/*/*/*LifecycleTests/*"
    Runs all tests in classes ending with "LifecycleTests" in Integration.Tests project
    Pattern: /Assembly/Namespace/ClassName/TestName
    Example: /*/ECommerce.Integration.Tests.Lifecycle.*/OutboxLifecycleTests/*

.EXAMPLE
    ./Run-Tests.ps1 -TestFilter "/*/*/*/PostPerspective*"
    Runs all tests starting with "PostPerspective" across all projects

.EXAMPLE
    ./Run-Tests.ps1 -TestFilter "/*/Whizbang.Core.Tests/*/*"
    Runs all tests in the Whizbang.Core.Tests assembly

.NOTES
    TUnit TreeNode Filter Syntax:
    - Format: /Assembly/Namespace/ClassName/TestName
    - Wildcards: Use * to match any segment
    - Examples:
      * /*/*/*/*SomeTest* - Tests with "SomeTest" anywhere in name
      * /*/*/*Tests/* - All tests in classes ending with "Tests"
      * /*/MyNamespace.*/*/* - All tests in namespace starting with "MyNamespace."
      * /*/*/*/SpecificTest - Only tests named exactly "SpecificTest"

    Common Patterns:
    - Lifecycle tests: -TestFilter "/*/*/*LifecycleTests/*"
    - Integration tests: -ProjectFilter "Integration.Tests"
    - Single class: -TestFilter "/*/YourNamespace/YourClass/*"
    - Single test: -TestFilter "/*/*/*/YourTestName"

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
    [ValidateSet("Ai", "Ci", "Full", "AiFull", "IntegrationsOnly", "AiIntegrations")]
    [string]$Mode = "Ai",  # Test execution mode: Ai (default), Ci, Full, AiFull, IntegrationsOnly, AiIntegrations
    [int]$ProgressInterval = 60,  # Progress update interval in seconds (Ai modes only)
    [switch]$LiveUpdates,  # Show progress immediately when counts change (Ai modes only)

    # Legacy parameters (deprecated, use -Mode instead)
    [bool]$ExcludeIntegration,
    [switch]$AiMode
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Handle legacy parameters (for backward compatibility)
if ($PSBoundParameters.ContainsKey('AiMode') -or $PSBoundParameters.ContainsKey('ExcludeIntegration')) {
    Write-Warning "Parameters -AiMode and -ExcludeIntegration are deprecated. Use -Mode instead."
    if ($AiMode -and $PSBoundParameters.ContainsKey('ExcludeIntegration') -and -not $ExcludeIntegration) {
        $Mode = "AiFull"
    } elseif ($useAiOutput) {
        $Mode = "Ai"
    } elseif ($PSBoundParameters.ContainsKey('ExcludeIntegration') -and -not $ExcludeIntegration) {
        $Mode = "Full"
    } else {
        $Mode = "Ci"
    }
}

# Derive settings from Mode
$useAiOutput = $Mode -in @("Ai", "AiFull", "AiIntegrations")
$includeIntegrationTests = $Mode -in @("Full", "AiFull", "IntegrationsOnly", "AiIntegrations")
$onlyIntegrationTests = $Mode -in @("IntegrationsOnly", "AiIntegrations")

# Navigate to repo root
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    # Determine parallel level
    if ($MaxParallel -eq 0) {
        $MaxParallel = [Environment]::ProcessorCount
    }

    if (-not $useAiOutput) {
        Write-Host "=====================================" -ForegroundColor Cyan
        Write-Host "  Whizbang Test Suite Runner" -ForegroundColor Cyan
        Write-Host "  (Parallel Execution via dotnet test)" -ForegroundColor Cyan
        Write-Host "=====================================" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Parallel Test Execution: Up to $MaxParallel test modules concurrently" -ForegroundColor Yellow
        Write-Host "Mode: $Mode" -ForegroundColor Yellow
        if ($onlyIntegrationTests) {
            Write-Host "Integration Tests: Only (other tests excluded)" -ForegroundColor Yellow
        } elseif (-not $includeIntegrationTests) {
            Write-Host "Integration Tests: Excluded (use -Mode Full or -Mode AiFull to include)" -ForegroundColor Yellow
        }
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
        Write-Host "Mode: $Mode" -ForegroundColor Gray
        if ($onlyIntegrationTests) {
            Write-Host "Integration Tests: Only" -ForegroundColor Gray
        } elseif (-not $includeIntegrationTests) {
            Write-Host "Integration Tests: Excluded" -ForegroundColor Gray
        }
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
    } elseif ($onlyIntegrationTests) {
        # Run ONLY integration tests
        $testArgs += "--test-modules"
        $testArgs += "**/bin/**/Debug/net10.0/*Integration.Tests.dll"
    } elseif (-not $includeIntegrationTests) {
        # Exclude integration tests (they take 5-10+ minutes)
        # Pattern matches: Whizbang.*.Tests.dll but NOT *Integration.Tests.dll
        $testArgs += "--test-modules"
        $testArgs += "**/bin/**/Debug/net10.0/Whizbang.*.Tests.dll"
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
    if (-not $useAiOutput) {
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
    if ($useAiOutput) {
        # AI mode: Stream and filter with smart progress updates
        $totalTests = 0
        $totalPassed = 0
        $totalFailed = 0
        $totalSkipped = 0
        $failedTests = @()
        $testDetails = @{}  # Dictionary to store detailed error info per test
        $buildErrors = @()
        $buildWarnings = @()
        $projectErrors = @()  # Track test project-level errors (not individual test failures)
        $currentFailedTest = $null
        $capturingStackTrace = $false
        $stackTraceLines = @()

        # Progress tracking
        $startTime = Get-Date
        $lastProgressTime = $startTime
        $lastTotalTests = 0
        $lastTotalFailed = 0
        $lineCounter = 0

        & dotnet @testArgs 2>&1 | ForEach-Object {
            $lineStr = $_.ToString()
            $lineCounter++

            # Capture test counts from TUnit progress format: [+passed/xfailed/?skipped]
            # Note: Multiple test projects run in parallel, we track the highest seen for approximate progress
            if ($lineStr -match '\[\+(\d+)/x(\d+)/\?(\d+)\]') {
                $passed = [int]$matches[1]
                $failed = [int]$matches[2]
                $skipped = [int]$matches[3]

                # Track highest values seen across parallel projects (approximate total)
                if ($passed -gt $totalPassed) { $totalPassed = $passed }
                if ($failed -gt $totalFailed) { $totalFailed = $failed }
                if ($skipped -gt $totalSkipped) { $totalSkipped = $skipped }
                $totalTests = $totalPassed + $totalFailed + $totalSkipped
            }
            # Capture final summary lines and accumulate totals across test projects
            elseif ($lineStr -match "^\s*succeeded:\s+(\d+)\s*$") {
                # When a project completes, add its results to cumulative total
                $totalPassed += [int]$matches[1]
                $totalTests = $totalPassed + $totalFailed + $totalSkipped
            }
            elseif ($lineStr -match "^\s*failed:\s+(\d+)\s*$") {
                $totalFailed += [int]$matches[1]
                $totalTests = $totalPassed + $totalFailed + $totalSkipped
            }
            elseif ($lineStr -match "^\s*skipped:\s+(\d+)\s*$") {
                $totalSkipped += [int]$matches[1]
                $totalTests = $totalPassed + $totalFailed + $totalSkipped
            }

            # Smart progress updates
            # Check time every 100 lines OR when counts change (if LiveUpdates enabled)
            $shouldCheckTime = $lineCounter % 100 -eq 0
            $countsChanged = $LiveUpdates -and ($totalTests -ne $lastTotalTests -or $totalFailed -ne $lastTotalFailed)

            if ($shouldCheckTime -or $countsChanged) {
                $now = Get-Date
                $elapsedSinceLastProgress = ($now - $lastProgressTime).TotalSeconds

                # Show progress if:
                # - ProgressInterval elapsed (always), OR
                # - LiveUpdates mode and counts changed
                $shouldShow = $elapsedSinceLastProgress -ge $ProgressInterval -or $countsChanged

                if ($shouldShow) {
                    $elapsedMinutes = [Math]::Floor(($now - $startTime).TotalMinutes)

                    if ($totalTests -gt 0) {
                        # Show test progress
                        $failureIndicator = if ($totalFailed -gt 0) { " ⚠️" } else { "" }
                        Write-Host "[$($elapsedMinutes)m] Progress: $totalPassed passed, $totalFailed failed, $totalSkipped skipped$failureIndicator" -ForegroundColor Gray
                    } else {
                        # Show heartbeat (building/not yet testing)
                        Write-Host "[$($elapsedMinutes)m] Running... (building or preparing tests)" -ForegroundColor DarkGray
                    }

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
            # Capture test project errors (e.g., "Whizbang.Data.Postgres.Tests.dll failed with 1 error(s)")
            elseif ($lineStr -match "(\S+\.dll)\s+\(.*\)\s+failed with (\d+) error") {
                $projectName = $matches[1]
                $errorCount = $matches[2]
                $projectErrors += "$projectName failed with $errorCount error(s)"
            }
            # Capture generic "error:" lines from test output
            elseif ($lineStr -match "^\s*error:\s+(\d+)") {
                # This catches the final "error: 1" summary line
                # Don't add to projectErrors here as it's already captured above
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

        # Calculate elapsed time
        $endTime = Get-Date
        $totalElapsed = $endTime - $startTime
        $elapsedString = if ($totalElapsed.TotalMinutes -ge 1) {
            "{0:F0}m {1:F0}s" -f [Math]::Floor($totalElapsed.TotalMinutes), $totalElapsed.Seconds
        } else {
            "{0:F1}s" -f $totalElapsed.TotalSeconds
        }

        # Display summary
        Write-Host ""
        Write-Host "=== TEST RESULTS SUMMARY ===" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Total Duration: $elapsedString" -ForegroundColor Cyan

        if ($totalTests -gt 0) {
            Write-Host ""
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

        if ($projectErrors.Count -gt 0) {
            Write-Host ""
            Write-Host "=== TEST PROJECT ERRORS ($($projectErrors.Count)) ===" -ForegroundColor Red
            Write-Host ""
            $projectErrors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            Write-Host ""
            Write-Host "Note: These are test project-level errors (setup/teardown failures, resource issues)" -ForegroundColor Yellow
            Write-Host "      Run the specific project individually for more details" -ForegroundColor Yellow
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

    # Check exit code (also consider projectErrors in AI mode since they may not affect LASTEXITCODE)
    $hasErrors = $LASTEXITCODE -ne 0
    if ($useAiOutput) {
        # In AI mode, also check if we captured project errors (intermittent race conditions)
        $hasErrors = $hasErrors -or $projectErrors.Count -gt 0 -or $totalFailed -gt 0
    }

    if (-not $hasErrors) {
        if ($useAiOutput) {
            Write-Host "=== STATUS: ALL TESTS PASSED ===" -ForegroundColor Green
        } else {
            Write-Host ""
            Write-Host "=====================================" -ForegroundColor Green
            Write-Host "  All tests passed!" -ForegroundColor Green
            Write-Host "=====================================" -ForegroundColor Green
        }
        exit 0
    } else {
        if ($useAiOutput) {
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
