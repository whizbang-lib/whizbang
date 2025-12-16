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
    Filter to specific test projects (e.g., "Core", "Schema", "EFCore.Postgres")

.PARAMETER TestFilter
    Filter to specific tests by name pattern (TUnit filter syntax)

.PARAMETER Verbose
    Show detailed test output for each project

.EXAMPLE
    ./Test-All.ps1
    Runs all tests with automatic parallel detection

.EXAMPLE
    ./Test-All.ps1 -MaxParallel 4
    Runs tests with maximum 4 projects in parallel

.EXAMPLE
    ./Test-All.ps1 -ProjectFilter "Core"
    Runs only test projects with "Core" in the name

.EXAMPLE
    ./Test-All.ps1 -ProjectFilter "EFCore.Postgres" -TestFilter "ProcessWorkBatchAsync"
    Runs only tests containing "ProcessWorkBatchAsync" in EFCore.Postgres test project

.EXAMPLE
    ./Test-All.ps1 -Verbose
    Runs all tests with detailed output

.EXAMPLE
    ./Test-All.ps1 -AiMode
    Runs all tests with compact, parseable output optimized for AI analysis

.EXAMPLE
    ./Test-All.ps1 -AiMode -ProjectFilter "EFCore.Postgres"
    Runs EFCore.Postgres tests with AI-optimized compact output

.NOTES
    Technology Stack (as of December 2025):
    - .NET 10.0.1 (LTS release, November 2025)
    - TUnit 1.2.11+ (modern source-generated testing framework)
    - Rocks 9.3.0+ (Roslyn-based mocking for AOT compatibility)
    - Microsoft.Testing.Platform 2.0+ (native test runner)

    The global.json configures MTP as the test runner, enabling full integration
    with dotnet test including parallel execution and VS Code Test Explorer.

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
    [switch]$AiMode
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

    # If we have a project filter, find matching projects instead of using solution
    if ($ProjectFilter) {
        $testProjects = Get-ChildItem -Path "tests" -Filter "*.csproj" -Recurse |
            Where-Object { $_.Name -match $ProjectFilter } |
            Select-Object -ExpandProperty FullName

        if ($testProjects.Count -eq 0) {
            Write-Host "No test projects found matching filter: $ProjectFilter" -ForegroundColor Red
            exit 1
        }

        if (-not $AiMode) {
            Write-Host "Found $($testProjects.Count) matching project(s):" -ForegroundColor Gray
            $testProjects | ForEach-Object { Write-Host "  - $_" -ForegroundColor Gray }
            Write-Host ""
        }

        # Add each matching project
        foreach ($project in $testProjects) {
            $testArgs += $project
        }
    } else {
        # Use the main solution file (already filtered to test projects via <IsTestProject>)
        $testArgs += "--solution"
        $testArgs += "Whizbang.slnx"
    }

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

    # Add test name filter if specified
    if ($TestFilter) {
        $testArgs += "--filter"
        $testArgs += $TestFilter
    }

    # Run tests
    if (-not $AiMode) {
        $cmdDisplay = "dotnet " + ($testArgs -join " ")
        if ($cmdDisplay.Length -gt 100) {
            $cmdDisplay = $cmdDisplay.Substring(0, 97) + "..."
        }
        Write-Host "Executing: $cmdDisplay" -ForegroundColor Gray
        Write-Host ""
    }

    $output = & dotnet @testArgs 2>&1

    # Process output based on mode
    if ($AiMode) {
        # AI mode: Filter and summarize
        $totalTests = 0
        $totalPassed = 0
        $totalFailed = 0
        $totalSkipped = 0
        $failedTests = @()
        $buildErrors = @()
        $buildWarnings = @()

        foreach ($line in $output) {
            $lineStr = $line.ToString()

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
            # Capture failed test names (lines starting with "failed " followed by test name)
            elseif ($lineStr -match "^failed\s+([^\(]+)\s+\(") {
                $testName = $matches[1].Trim()
                # Exclude false positives (EF Core logging, etc.)
                if ($testName -notmatch "executing|DbCommand|Executed") {
                    $failedTests += $testName
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
            Write-Host "No test results parsed (build may have failed)" -ForegroundColor Yellow
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
            $failedTests | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        }

        Write-Host ""
    } else {
        # Normal mode: Display all output
        $output | ForEach-Object { Write-Host $_ }
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
