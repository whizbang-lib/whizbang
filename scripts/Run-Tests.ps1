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

.PARAMETER FailFast
    Stop test execution on first failure. Useful for quickly identifying and fixing issues.
    When enabled, adds --fail-fast flag to dotnet test command.

.PARAMETER HangTimeout
    Timeout in seconds to detect hung tests (default: 180). If no output is received for this
    duration, a warning is displayed. After 2x this timeout, the test run is terminated.
    Set to 0 to disable hang detection.

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
    ./Run-Tests.ps1 -FailFast
    Runs tests and stops immediately on first failure

.EXAMPLE
    ./Run-Tests.ps1 -Mode AiFull -FailFast
    Runs all tests including integration tests, stops on first failure

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

    [ArgumentCompleter({
        param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
        # Get all test project names from the tests/ directory
        $testProjects = Get-ChildItem -Path "$PSScriptRoot/../tests" -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path "$($_.FullName)/*.csproj" } |
            ForEach-Object { $_.Name -replace '\.Tests$', '' }

        # Also get sample test projects
        $sampleProjects = Get-ChildItem -Path "$PSScriptRoot/../samples" -Recurse -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "*.Tests" -and (Test-Path "$($_.FullName)/*.csproj") } |
            ForEach-Object { $_.Name -replace '\.Tests$', '' }

        # Combine and filter based on what user has typed
        $allProjects = $testProjects + $sampleProjects | Sort-Object -Unique
        $allProjects | Where-Object { $_ -like "*$wordToComplete*" } | ForEach-Object {
            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
        }
    })]
    [string]$ProjectFilter = "",

    [string]$TestFilter = "",
    [switch]$VerboseOutput,

    [ValidateSet("Ai", "Ci", "Full", "AiFull", "IntegrationsOnly", "AiIntegrations")]
    [string]$Mode = "Ai",  # Test execution mode: Ai (default), Ci, Full, AiFull, IntegrationsOnly, AiIntegrations

    [int]$ProgressInterval = 60,  # Progress update interval in seconds (Ai modes only)
    [switch]$LiveUpdates,  # Show progress immediately when counts change (Ai modes only)
    [switch]$FailFast,  # Stop on first test failure
    [int]$HangTimeout = 180,  # Seconds of no output before hang warning (0 to disable)

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

# Clean up any leftover test containers from previous runs
# This ensures tests start with a clean slate even if previous runs were interrupted
if ($includeIntegrationTests -or $onlyIntegrationTests) {
    if (-not $useAiOutput) {
        Write-Host "Cleaning up any leftover test containers..." -ForegroundColor Yellow
    }

    # Stop and remove ServiceBus emulator containers
    $serviceBusContainers = docker ps -a --filter "name=servicebus-emulator" --format "{{.ID}}"
    if ($serviceBusContainers) {
        $serviceBusContainers | ForEach-Object { docker stop $_ 2>&1 | Out-Null; docker rm $_ 2>&1 | Out-Null }
    }

    # Stop and remove SQL Server containers for ServiceBus
    $mssqlContainers = docker ps -a --filter "name=mssql-servicebus" --format "{{.ID}}"
    if ($mssqlContainers) {
        $mssqlContainers | ForEach-Object { docker stop $_ 2>&1 | Out-Null; docker rm $_ 2>&1 | Out-Null }
    }

    # Stop and remove PostgreSQL test containers
    $postgresContainers = docker ps -a --filter "name=postgres" --format "{{.ID}}"
    if ($postgresContainers) {
        $postgresContainers | ForEach-Object { docker stop $_ 2>&1 | Out-Null; docker rm $_ 2>&1 | Out-Null }
    }

    # Stop and remove RabbitMQ test containers (EXCEPT shared container which is designed to persist)
    # The whizbang-test-rabbitmq container is intentionally kept running for reuse
    $rabbitContainers = docker ps -a --filter "name=rabbitmq" --format "{{.ID}} {{.Names}}" | Where-Object { $_ -notmatch "whizbang-test-rabbitmq" } | ForEach-Object { ($_ -split " ")[0] }
    if ($rabbitContainers) {
        $rabbitContainers | ForEach-Object { docker stop $_ 2>&1 | Out-Null; docker rm $_ 2>&1 | Out-Null }
    }

    # Ensure shared RabbitMQ container is running (required for RabbitMQ integration tests)
    $sharedRabbitState = docker inspect --format="{{.State.Status}}" whizbang-test-rabbitmq 2>$null
    if (-not $sharedRabbitState) {
        # Container doesn't exist - create it
        if (-not $useAiOutput) {
            Write-Host "Starting shared RabbitMQ container..." -ForegroundColor Yellow
        }
        docker run --detach --name whizbang-test-rabbitmq `
            -e RABBITMQ_DEFAULT_USER=guest `
            -e RABBITMQ_DEFAULT_PASS=guest `
            --publish 0:5672 `
            --publish 0:15672 `
            --restart no `
            rabbitmq:3.13-management-alpine 2>&1 | Out-Null
        # Wait for RabbitMQ to be ready
        Start-Sleep -Seconds 10
    } elseif ($sharedRabbitState -ne "running") {
        # Container exists but not running - start it
        if (-not $useAiOutput) {
            Write-Host "Starting stopped RabbitMQ container..." -ForegroundColor Yellow
        }
        docker start whizbang-test-rabbitmq 2>&1 | Out-Null
        Start-Sleep -Seconds 5
    }

    # Verify RabbitMQ is responding
    $mgmtPort = ((docker port whizbang-test-rabbitmq 15672 2>$null) -split "`n")[0] -replace '.*:', ''
    if ($mgmtPort) {
        $maxAttempts = 15
        for ($i = 1; $i -le $maxAttempts; $i++) {
            try {
                # Use .NET HttpClient for reliable cross-platform HTTP with basic auth
                $authBytes = [System.Text.Encoding]::ASCII.GetBytes("guest:guest")
                $authHeader = [Convert]::ToBase64String($authBytes)
                $headers = @{ "Authorization" = "Basic $authHeader" }
                $response = Invoke-RestMethod -Uri "http://localhost:$mgmtPort/api/overview" -Headers $headers -TimeoutSec 5 -ErrorAction Stop
                if ($response.management_version) {
                    if (-not $useAiOutput) {
                        Write-Host "RabbitMQ container ready on port $mgmtPort" -ForegroundColor Green
                    }
                    break
                }
            } catch {
                if ($i -eq $maxAttempts) {
                    Write-Warning "RabbitMQ container may not be fully ready after $maxAttempts attempts - tests will retry"
                }
                Start-Sleep -Seconds 2
            }
        }
    }

    # Stop and remove Testcontainers ryuk (reaper) containers
    $ryukContainers = docker ps -a --filter "ancestor=testcontainers/ryuk" --format "{{.ID}}"
    if ($ryukContainers) {
        $ryukContainers | ForEach-Object { docker stop $_ 2>&1 | Out-Null; docker rm $_ 2>&1 | Out-Null }
    }

    # Prune stale Docker networks (non-interactive)
    docker network prune -f 2>&1 | Out-Null

    if (-not $useAiOutput) {
        Write-Host "Container cleanup complete." -ForegroundColor Green
        Write-Host ""
    }
}

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
        if ($FailFast) {
            Write-Host "Fail Fast: Enabled (stops on first failure)" -ForegroundColor Yellow
        }
        Write-Host ""
    } else {
        Write-Host "[WHIZBANG TEST SUITE - AI MODE]" -ForegroundColor Cyan

        # Display actual command line that was used
        $cmdLine = $MyInvocation.Line
        if ([string]::IsNullOrWhiteSpace($cmdLine)) {
            $cmdLine = "$($MyInvocation.MyCommand.Path) -Mode $Mode"
            if ($FailFast) { $cmdLine += " -FailFast" }
            if ($ProjectFilter) { $cmdLine += " -ProjectFilter '$ProjectFilter'" }
            if ($TestFilter) { $cmdLine += " -TestFilter '$TestFilter'" }
        }
        Write-Host "Command: $cmdLine" -ForegroundColor DarkGray

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
        if ($FailFast) {
            Write-Host "Fail Fast: Enabled" -ForegroundColor Gray
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

    # Add fail-fast if requested (stops on first failure)
    if ($FailFast) {
        $testArgs += "--fail-fast"
    }

    # Pattern for identifying integration test DLLs: *Integration.Tests.dll or *IntegrationTests.dll
    # Excludes AppHost projects which are Aspire hosts, not test projects
    $integrationTestPattern = "Integration\.Tests\.dll$|IntegrationTests\.dll$"
    $excludePattern = "AppHost"

    # Helper function to test if a DLL name is an integration test
    function Test-IsIntegrationTest {
        param([string]$DllName)
        return ($DllName -match $integrationTestPattern) -and ($DllName -notmatch $excludePattern)
    }

    # Helper function to ensure build exists for dynamic DLL discovery
    function Ensure-BuildExists {
        # Pattern: bin/Debug/net10.0/ - works for standard .NET output paths
        $anyDll = Get-ChildItem -Path $repoRoot -Recurse -Filter "*.Tests.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "bin[/\\]Debug[/\\]net10\.0[/\\]" } |
            Select-Object -First 1

        if (-not $anyDll) {
            if (-not $useAiOutput) {
                Write-Host "No test DLLs found. Building solution first..." -ForegroundColor Yellow
            } else {
                Write-Host "Building solution..." -ForegroundColor Gray
            }
            & dotnet build --verbosity quiet
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Build failed. Cannot discover test projects."
                exit 1
            }
        }
    }

    # Use --test-modules with globbing pattern for project filtering
    if ($ProjectFilter) {
        # Native .NET 10 globbing: **/bin/**/Debug/net10.0/*{Filter}*.dll
        $testArgs += "--test-modules"
        $testArgs += "**/bin/**/Debug/net10.0/*$ProjectFilter*.dll"
    } elseif ($onlyIntegrationTests) {
        # Run ONLY integration tests
        Ensure-BuildExists
        # Wrap in @() to ensure array even when empty (prevents null.Count error with StrictMode)
        # Pattern: bin/Debug/net10.0/ - works for standard .NET output paths
        $integrationDlls = @(Get-ChildItem -Path $repoRoot -Recurse -Filter "*.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "bin[/\\]Debug[/\\]net10\.0[/\\]" } |
            Where-Object { Test-IsIntegrationTest $_.Name } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($repoRoot, $_.FullName) })

        if ($integrationDlls.Count -gt 0) {
            $testArgs += "--test-modules"
            # Use relative paths - dotnet test has issues with absolute paths containing semicolons
            $testArgs += ($integrationDlls -join ";")

            if (-not $useAiOutput) {
                Write-Host "Discovered $($integrationDlls.Count) integration test projects" -ForegroundColor Gray
            }
        } else {
            Write-Warning "No integration test DLLs found after build. Check that integration test projects exist."
            exit 1
        }
    } elseif (-not $includeIntegrationTests) {
        # Exclude integration tests - find all *.Tests.dll and filter out integration tests
        # This ensures all test projects are discovered while excluding slow integration tests
        Ensure-BuildExists
        # Wrap in @() to ensure array even when empty (prevents null.Count error with StrictMode)
        # Pattern: bin/Debug/net10.0/ - works for standard .NET output paths
        $allTestDlls = @(Get-ChildItem -Path $repoRoot -Recurse -Filter "*.Tests.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "bin[/\\]Debug[/\\]net10\.0[/\\]" } |
            Where-Object { -not (Test-IsIntegrationTest $_.Name) } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($repoRoot, $_.FullName) })

        if ($allTestDlls.Count -gt 0) {
            $testArgs += "--test-modules"
            # Use relative paths - dotnet test has issues with absolute paths containing semicolons
            $testArgs += ($allTestDlls -join ";")

            if (-not $useAiOutput) {
                Write-Host "Discovered $($allTestDlls.Count) test projects (excluding integration tests)" -ForegroundColor Gray
            }
        } else {
            Write-Warning "No test DLLs found after build. Check that test projects exist."
            exit 1
        }
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

    # FailFast tracking (declared at outer scope for finally block access)
    $failFastTriggered = $false

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
        # Two separate tracking systems:
        # 1. In-progress counts (highest seen from progress lines) - approximate, for display during execution
        # 2. Completed counts (from summary lines) - accurate, for final reporting
        $startTime = Get-Date
        $lastProgressTime = $startTime
        $lastOutputTime = $startTime
        $lastHangCheckTime = $startTime
        $lastTotalTests = 0
        $lastTotalFailed = 0
        $lineCounter = 0
        $hangWarningShown = $false

        # Track completed project results separately (from summary lines)
        $completedPassed = 0
        $completedFailed = 0
        $completedSkipped = 0

        # Track in-progress estimates (highest seen from progress lines)
        $inProgressPassed = 0
        $inProgressFailed = 0
        $inProgressSkipped = 0

        # FailFast tracking (set at outer scope, used here)
        $firstFailureDetails = $null

        # Start process with redirected output so we can kill it on failure
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = "dotnet"
        $psi.Arguments = $testArgs -join " "
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.WorkingDirectory = $repoRoot

        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $psi

        # Collect stderr asynchronously
        $stderrBuilder = [System.Text.StringBuilder]::new()
        $stderrEvent = Register-ObjectEvent -InputObject $process -EventName ErrorDataReceived -Action {
            if ($EventArgs.Data) {
                $stderrBuilder.AppendLine($EventArgs.Data) | Out-Null
            }
        }

        $process.Start() | Out-Null
        $process.BeginErrorReadLine()

        # Read stdout line by line with hang detection
        # Use async read with periodic timeout checks
        $reader = $process.StandardOutput
        $readTask = $null

        while (-not $process.HasExited -or -not $reader.EndOfStream) {
            # Start async read if not already pending
            if ($null -eq $readTask -or $readTask.IsCompleted) {
                if ($reader.EndOfStream) { break }
                $readTask = $reader.ReadLineAsync()
            }

            # Wait for read with timeout (500ms) to allow hang detection
            $completed = $readTask.Wait(500)

            if ($completed) {
                $lineStr = $readTask.Result
                $readTask = $null  # Reset for next read

                if ($null -eq $lineStr) { continue }

                $lineCounter++
                $lastOutputTime = Get-Date
                $hangWarningShown = $false  # Reset warning on new output
            } else {
                # No output received - check for hang condition AND time-based progress
                $now = Get-Date
                $silentSeconds = ($now - $lastOutputTime).TotalSeconds
                $elapsedSinceLastProgress = ($now - $lastProgressTime).TotalSeconds

                # Time-based progress update (even when no output)
                if ($elapsedSinceLastProgress -ge $ProgressInterval) {
                    $elapsedMinutes = [Math]::Floor(($now - $startTime).TotalMinutes)
                    $displayPassed = if ($completedPassed -gt 0) { $completedPassed } else { $inProgressPassed }
                    $displayFailed = if ($completedFailed -gt 0) { $completedFailed } else { $inProgressFailed }
                    $displaySkipped = if ($completedSkipped -gt 0) { $completedSkipped } else { $inProgressSkipped }
                    $displayTotal = $displayPassed + $displayFailed + $displaySkipped

                    if ($displayTotal -gt 0) {
                        $failureIndicator = if ($displayFailed -gt 0) { " ⚠️" } else { "" }
                        Write-Host "[$($elapsedMinutes)m] Progress: ~$displayPassed passed, ~$displayFailed failed, ~$displaySkipped skipped$failureIndicator (in progress)" -ForegroundColor Gray
                    } else {
                        Write-Host "[$($elapsedMinutes)m] Running... (building or preparing tests)" -ForegroundColor DarkGray
                    }
                    $lastProgressTime = $now
                }

                # Hang detection
                if ($HangTimeout -gt 0 -and ($now - $lastHangCheckTime).TotalSeconds -ge 10) {
                    $lastHangCheckTime = $now

                    if ($silentSeconds -ge ($HangTimeout * 2)) {
                        # Hard hang - terminate
                        Write-Host ""
                        Write-Host "=== HANG DETECTED ===" -ForegroundColor Red
                        Write-Host "No output for $([Math]::Floor($silentSeconds)) seconds. Terminating test run." -ForegroundColor Red

                        # Show any stderr that was captured (may contain error info)
                        $stderrContent = $stderrBuilder.ToString().Trim()
                        if ($stderrContent) {
                            Write-Host ""
                            Write-Host "Error output:" -ForegroundColor Yellow
                            $stderrContent -split "`n" | Select-Object -First 20 | ForEach-Object {
                                Write-Host "  $_" -ForegroundColor Gray
                            }
                        }

                        Write-Host ""
                        try {
                            $process.Kill($true)
                        } catch { }
                        $failFastTriggered = $true
                        break
                    } elseif ($silentSeconds -ge $HangTimeout -and -not $hangWarningShown) {
                        # Soft hang - warning
                        $elapsedMinutes = [Math]::Floor(($now - $startTime).TotalMinutes)
                        Write-Host "[$($elapsedMinutes)m] ⚠️ No output for $([Math]::Floor($silentSeconds))s - possible hang (will terminate at $($HangTimeout * 2)s)" -ForegroundColor Yellow
                        $hangWarningShown = $true
                    }
                }
                continue  # Continue loop without processing (no line received)
            }

            # Capture test counts from TUnit progress format: [+passed/xfailed/?skipped]
            # Note: Multiple test projects run in parallel, we track the highest seen for approximate progress
            if ($lineStr -match '\[\+(\d+)/x(\d+)/\?(\d+)\]') {
                $passed = [int]$matches[1]
                $failed = [int]$matches[2]
                $skipped = [int]$matches[3]

                # Track highest values seen across parallel projects (approximate in-progress total)
                if ($passed -gt $inProgressPassed) { $inProgressPassed = $passed }
                if ($failed -gt $inProgressFailed) { $inProgressFailed = $failed }
                if ($skipped -gt $inProgressSkipped) { $inProgressSkipped = $skipped }
            }
            # Capture final summary lines - these are the accurate completed counts
            # Reset in-progress tracking when a project completes to avoid double-counting
            elseif ($lineStr -match "^\s*succeeded:\s+(\d+)\s*$") {
                $completedPassed += [int]$matches[1]
                # Reset in-progress since this project's tests moved to completed
                $inProgressPassed = 0
            }
            elseif ($lineStr -match "^\s*failed:\s+(\d+)\s*$") {
                $completedFailed += [int]$matches[1]
                $inProgressFailed = 0
            }
            elseif ($lineStr -match "^\s*skipped:\s+(\d+)\s*$") {
                $completedSkipped += [int]$matches[1]
                $inProgressSkipped = 0
            }

            # Calculate display totals
            # Use completed counts as authoritative; in-progress is just an activity indicator
            # This avoids double-counting (a project's progress lines overlap with its summary)
            $completedTotal = $completedPassed + $completedFailed + $completedSkipped
            $inProgressTotal = $inProgressPassed + $inProgressFailed + $inProgressSkipped

            # For display, prefer completed if available, otherwise show in-progress
            if ($completedTotal -gt 0) {
                $totalPassed = $completedPassed
                $totalFailed = $completedFailed
                $totalSkipped = $completedSkipped
            } else {
                $totalPassed = $inProgressPassed
                $totalFailed = $inProgressFailed
                $totalSkipped = $inProgressSkipped
            }
            $totalTests = $totalPassed + $totalFailed + $totalSkipped

            # Smart progress updates
            # Check time on every line (cheap operation) to ensure consistent interval reporting
            $now = Get-Date
            $elapsedSinceLastProgress = ($now - $lastProgressTime).TotalSeconds
            $countsChanged = $LiveUpdates -and ($totalTests -ne $lastTotalTests -or $totalFailed -ne $lastTotalFailed)

            # Show progress if:
            # - ProgressInterval elapsed (always), OR
            # - LiveUpdates mode and counts changed
            $shouldShow = $elapsedSinceLastProgress -ge $ProgressInterval -or $countsChanged

            if ($shouldShow) {
                $elapsedMinutes = [Math]::Floor(($now - $startTime).TotalMinutes)

                if ($totalTests -gt 0 -or $inProgressTotal -gt 0) {
                    # Show test progress
                    $failureIndicator = if ($totalFailed -gt 0) { " ⚠️" } else { "" }
                    # Show running indicator if there's in-progress activity beyond completed
                    if ($completedTotal -gt 0 -and $inProgressTotal -gt 0) {
                        Write-Host "[$($elapsedMinutes)m] Progress: $totalPassed passed, $totalFailed failed, $totalSkipped skipped$failureIndicator (+$inProgressTotal running)" -ForegroundColor Gray
                    } elseif ($completedTotal -eq 0 -and $inProgressTotal -gt 0) {
                        Write-Host "[$($elapsedMinutes)m] Progress: ~$totalPassed passed, ~$totalFailed failed, ~$totalSkipped skipped$failureIndicator (in progress)" -ForegroundColor Gray
                    } else {
                        Write-Host "[$($elapsedMinutes)m] Progress: $totalPassed passed, $totalFailed failed, $totalSkipped skipped$failureIndicator" -ForegroundColor Gray
                    }
                } else {
                    # Show heartbeat (building/not yet testing)
                    Write-Host "[$($elapsedMinutes)m] Running... (building or preparing tests)" -ForegroundColor DarkGray
                }

                $lastProgressTime = $now
                $lastTotalTests = $totalTests
                $lastTotalFailed = $totalFailed
            }

            # Capture failed test names (lines starting with "failed " followed by test name)
            # Note: This is an independent if block, not chained to progress display
            if ($lineStr -match "^failed\s+([^\(]+)\s+\(") {
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

                    # FailFast: If this is the first failure and FailFast is enabled,
                    # continue reading to capture error details, then stop
                    if ($FailFast -and -not $failFastTriggered) {
                        $failFastTriggered = $true
                        $firstFailureDetails = $testName

                        # Read up to 50 more lines to capture exception and stack trace
                        $extraLines = 0
                        $maxExtraLines = 50
                        while (-not $reader.EndOfStream -and $extraLines -lt $maxExtraLines) {
                            $extraLine = $reader.ReadLine()
                            if ($null -eq $extraLine) { continue }
                            $extraLines++

                            # Capture exception type
                            if ($extraLine -match "^\s*(System\.\w+Exception|TUnit\.\w+Exception|.*Exception):\s*(.+)") {
                                $testDetails[$currentFailedTest]["Exception"] = $matches[1].Trim()
                                $testDetails[$currentFailedTest]["ErrorMessage"] = $matches[2].Trim()
                            }
                            # Capture stack trace
                            elseif ($extraLine -match "^\s+at\s+[\w\.]+") {
                                $stackTraceLines += $extraLine.Trim()
                            }
                            elseif ($extraLine -match "^\s+in\s+.*:\s*line\s+\d+") {
                                $stackTraceLines += $extraLine.Trim()
                            }
                            # Stop when we hit the next test or summary
                            elseif ($extraLine -match "^(failed|passed|skipped|Test run summary|succeeded:)") {
                                break
                            }
                        }

                        # Save captured stack trace
                        if ($stackTraceLines.Count -gt 0) {
                            $testDetails[$currentFailedTest]["StackTrace"] = $stackTraceLines -join "`n"
                        }

                        # Kill the process immediately
                        Write-Host ""
                        Write-Host "=== FAIL-FAST TRIGGERED ===" -ForegroundColor Red
                        Write-Host "Stopping test run due to first failure." -ForegroundColor Red
                        Write-Host ""

                        try {
                            $process.Kill($true)  # Kill process tree
                        } catch {
                            # Process may have already exited
                        }
                        break  # Exit the while loop
                    }
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

        # Clean up process resources
        $reader.Dispose()
        Unregister-Event -SourceIdentifier $stderrEvent.Name -ErrorAction SilentlyContinue
        Remove-Job -Id $stderrEvent.Id -Force -ErrorAction SilentlyContinue

        # Wait for process to exit (if not already killed)
        if (-not $process.HasExited) {
            $process.WaitForExit(5000) | Out-Null
            if (-not $process.HasExited) {
                try { $process.Kill($true) } catch { }
            }
        }
        $processExitCode = $process.ExitCode
        $process.Dispose()

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

        # Use actual captured failed tests count (more accurate than parsed summary when fail-fast kills process)
        $actualFailedCount = $failedTests.Count
        if ($actualFailedCount -gt $totalFailed) {
            $totalFailed = $actualFailedCount
        }

        if ($failFastTriggered) {
            Write-Host ""
            Write-Host "Note: Test run was stopped early due to -FailFast" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "Tests Run Before Stop: ~$totalTests" -ForegroundColor White
            Write-Host "Passed: ~$totalPassed" -ForegroundColor Green
            Write-Host "Failed: $actualFailedCount (stopped on first failure)" -ForegroundColor Red
            Write-Host "Skipped: ~$totalSkipped" -ForegroundColor Yellow
        } elseif ($totalTests -gt 0) {
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
    if ($useAiOutput) {
        # In AI mode, use the process exit code and check captured errors
        # Note: dotnet test returns 0 on success, non-zero on failure
        # Don't count processExitCode alone - it can be non-zero due to skipped tests or cancellation
        $hasErrors = $totalFailed -gt 0 -or $failFastTriggered -or $projectErrors.Count -gt 0 -or $buildErrors.Count -gt 0
    } else {
        $hasErrors = $LASTEXITCODE -ne 0
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
    # Clean up test containers after test run completes (especially important after fail-fast)
    # This ensures containers don't hang around after abrupt test termination
    if ($includeIntegrationTests -or $onlyIntegrationTests -or $failFastTriggered) {
        if (-not $useAiOutput) {
            Write-Host ""
            Write-Host "Cleaning up test containers..." -ForegroundColor Yellow
        }

        # Stop and remove all testcontainers (postgres, servicebus, etc.)
        # NOTE: We preserve whizbang-test-rabbitmq as it's designed to persist across runs
        # Use image-based filtering to catch containers that may have random names
        $testImages = @(
            "postgres:*",
            "mcr.microsoft.com/azure-messaging/servicebus-emulator:*",
            "mcr.microsoft.com/mssql/server:*",
            "testcontainers/ryuk:*"
        )

        foreach ($image in $testImages) {
            $containers = docker ps -a --filter "ancestor=$image" --format "{{.ID}}" 2>$null
            if ($containers) {
                $containers | ForEach-Object {
                    docker stop $_ 2>&1 | Out-Null
                    docker rm $_ 2>&1 | Out-Null
                }
            }
        }

        # Clean up RabbitMQ containers by image, but preserve the shared one
        $rabbitContainers = docker ps -a --filter "ancestor=rabbitmq:3.13-management-alpine" --format "{{.ID}} {{.Names}}" 2>$null | Where-Object { $_ -notmatch "whizbang-test-rabbitmq" } | ForEach-Object { ($_ -split " ")[0] }
        if ($rabbitContainers) {
            $rabbitContainers | ForEach-Object {
                docker stop $_ 2>&1 | Out-Null
                docker rm $_ 2>&1 | Out-Null
            }
        }

        # Also clean up by name pattern for any containers that might have escaped image filtering
        # NOTE: Exclude whizbang-test-rabbitmq from rabbitmq cleanup
        $namePatterns = @("postgres", "servicebus-emulator", "mssql-servicebus")
        foreach ($pattern in $namePatterns) {
            $containers = docker ps -a --filter "name=$pattern" --format "{{.ID}}" 2>$null
            if ($containers) {
                $containers | ForEach-Object {
                    docker stop $_ 2>&1 | Out-Null
                    docker rm $_ 2>&1 | Out-Null
                }
            }
        }

        # Clean up other rabbitmq containers but preserve the shared one
        $otherRabbitContainers = docker ps -a --filter "name=rabbitmq" --format "{{.ID}} {{.Names}}" 2>$null | Where-Object { $_ -notmatch "whizbang-test-rabbitmq" } | ForEach-Object { ($_ -split " ")[0] }
        if ($otherRabbitContainers) {
            $otherRabbitContainers | ForEach-Object {
                docker stop $_ 2>&1 | Out-Null
                docker rm $_ 2>&1 | Out-Null
            }
        }

        if (-not $useAiOutput) {
            Write-Host "Container cleanup complete." -ForegroundColor Green
        }
    }

    Pop-Location
}
