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
    Test execution mode (default: All)

    Verbose modes (full output):
    - All: ALL tests (default)
    - Unit: unit tests only
    - Integration: integration tests only

    AI modes (sparse output, token-efficient):
    - Ai: ALL tests
    - AiUnit: unit tests only (fast)
    - AiIntegrations: integration tests only

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

.PARAMETER Cleanup
    Clean up ALL test containers after tests complete, including shared containers
    (whizbang-test-postgres, whizbang-test-rabbitmq). Default: $true.
    Use -Cleanup:$false to preserve shared containers for faster subsequent runs.

.PARAMETER CleanupOnly
    Only clean up test containers without running any tests. Useful for freeing resources.

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
    ./Run-Tests.ps1
    Runs ALL tests with verbose output (default mode)

.EXAMPLE
    ./Run-Tests.ps1 -Mode Ai
    Runs ALL tests with AI-optimized sparse output

.EXAMPLE
    ./Run-Tests.ps1 -Mode AiUnit
    Runs unit tests only with AI-optimized output (fast)

.EXAMPLE
    ./Run-Tests.ps1 -Mode AiIntegrations
    Runs integration tests only with AI-optimized output

.EXAMPLE
    ./Run-Tests.ps1 -Mode Unit
    Runs unit tests only with full verbose output

.EXAMPLE
    ./Run-Tests.ps1 -Mode Integration
    Runs integration tests only with full verbose output

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
    ./Run-Tests.ps1 -Mode Ai -FailFast
    Runs all tests including integration tests, stops on first failure

.EXAMPLE
    ./Run-Tests.ps1 -Coverage
    Runs tests with code coverage collection (outputs Cobertura XML)

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

.EXAMPLE
    ./Run-Tests.ps1 -Tag AzureServiceBus
    Runs only test projects tagged with "AzureServiceBus"

.EXAMPLE
    ./Run-Tests.ps1 -Tag Docker
    Runs all test projects that require Docker (tagged with "Docker")

.EXAMPLE
    ./Run-Tests.ps1 -Tag Messaging -Mode AiIntegrations
    Runs all messaging transport integration tests with AI output

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

    [ValidateSet("All", "Ai", "AiUnit", "AiIntegrations", "Unit", "Integration")]
    [string]$Mode = "All",  # Test execution mode: All (verbose), Ai (sparse), AiUnit, AiIntegrations, Unit, Integration

    [int]$ProgressInterval = 60,  # Progress update interval in seconds (Ai modes only)
    [switch]$LiveUpdates,  # Show progress immediately when counts change (Ai modes only)
    [switch]$FailFast,  # Stop on first test failure
    [int]$HangTimeout = 180,  # Seconds of no output before hang warning (0 to disable)
    [switch]$Coverage,  # Collect code coverage (outputs Cobertura XML to TestResults/)

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",  # Build configuration (Debug or Release)

    [string]$ExcludeProjectFilter = "",  # Exclude projects matching this pattern (regex)

    [ArgumentCompleter({
        param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
        # Get all unique tags from test projects
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $tags = @()
        Get-ChildItem -Path "$repoRoot/tests", "$repoRoot/samples" -Recurse -Filter "*.csproj" -ErrorAction SilentlyContinue |
            ForEach-Object {
                $content = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
                if ($content -match '<WhizbangTestTags>([^<]+)</WhizbangTestTags>') {
                    $tags += $matches[1] -split ';'
                }
            }
        $tags | Sort-Object -Unique | Where-Object { $_ -like "*$wordToComplete*" } | ForEach-Object {
            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
        }
    })]
    [string]$Tag = "",  # Filter by WhizbangTestTags (e.g., "AzureServiceBus", "Docker", "Messaging")

    [bool]$Cleanup = $true,  # Clean up ALL containers after tests (default: true). Use -Cleanup:$false to preserve shared containers
    [switch]$CleanupOnly,  # Only clean up containers, don't run tests
    [switch]$NoBuild,  # Skip building, use existing build artifacts (for CI when artifacts are pre-built)

    [string]$LogFile = "",  # Tee output to file (verbose to file, sparse to console)

    [ValidateSet("All", "Ai", "AiUnit", "AiIntegrations", "Unit", "Integration")]
    [string]$LogMode = "",  # Log file verbosity (defaults to -Mode if not specified)

    [ValidateSet("Text", "Json")]
    [string]$OutputFormat = "Text",  # Output format: Text (human-readable) or Json (machine-readable)

    [switch]$NoHeader,  # Suppress the branded header (used when called from Run-PR.ps1)

    # Legacy parameters (deprecated, use -Mode instead)
    [bool]$ExcludeIntegration,
    [switch]$AiMode
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Import shared PR readiness module
Import-Module (Join-Path $PSScriptRoot "lib" "PR-Readiness-Common.psm1") -Force

# Handle legacy parameters (for backward compatibility)
if ($PSBoundParameters.ContainsKey('AiMode') -or $PSBoundParameters.ContainsKey('ExcludeIntegration')) {
    Write-Warning "Parameters -AiMode and -ExcludeIntegration are deprecated. Use -Mode instead."
    if ($AiMode -and $PSBoundParameters.ContainsKey('ExcludeIntegration') -and -not $ExcludeIntegration) {
        $Mode = "Ai"  # AI mode with all tests
    } elseif ($AiMode) {
        $Mode = "AiUnit"  # AI mode with unit tests only
    } elseif ($PSBoundParameters.ContainsKey('ExcludeIntegration') -and -not $ExcludeIntegration) {
        $Mode = "Ai"  # All tests (was Full)
    } else {
        $Mode = "Unit"  # Verbose unit tests only
    }
}

# Derive settings from Mode
$useAiOutput = $Mode -in @("Ai", "AiUnit", "AiIntegrations")
$useVerboseLogging = $Mode -in @("Unit", "Integration")  # Verbose log-format output for human-readable modes
$includeIntegrationTests = $Mode -in @("All", "Ai", "Integration", "AiIntegrations")
$onlyIntegrationTests = $Mode -in @("Integration", "AiIntegrations")

# FailFast defaults to true in AI modes (stop on first failure to save time)
if ($useAiOutput -and -not $PSBoundParameters.ContainsKey('FailFast')) {
    $FailFast = $true
}

# Initialize tee logging if -LogFile is specified
if ($LogFile) {
    $effectiveLogMode = if ($LogMode) { $LogMode } else { $Mode }
    Initialize-TeeLogging -LogFile $LogFile -ConsoleMode $Mode -LogMode $effectiveLogMode
}

# Track start time for history logging
$script:runStartTime = [DateTime]::UtcNow

# Show run estimation from history
$historyFile = Join-Path $PSScriptRoot ".." "logs" "test-runs.jsonl"
$estimate = Get-RunEstimate -FilePath $historyFile -FilterKey "mode" -FilterValue $Mode
$estimateStr = if ($estimate) { $estimate.Formatted } else { "" }

# Handle -CleanupOnly: just clean up containers and exit
if ($CleanupOnly) {
    Write-Host "Cleaning up ALL test containers..." -ForegroundColor Yellow

    # Stop and remove all test containers including shared ones
    $allTestContainers = @(
        "whizbang-test-postgres",
        "whizbang-test-rabbitmq"
    )

    foreach ($name in $allTestContainers) {
        $container = docker ps -a --filter "name=$name" --format "{{.ID}}" 2>$null
        if ($container) {
            Write-Host "  Stopping $name..." -ForegroundColor Gray
            docker stop $container 2>&1 | Out-Null
            docker rm $container 2>&1 | Out-Null
        }
    }

    # Clean up all whizbang test containers (using name prefix for safety)
    # This ensures we ONLY clean up containers created by Whizbang tests, not other projects
    $whizbangContainers = docker ps -a --filter "name=whizbang-test-" --format "{{.ID}}" 2>$null
    if ($whizbangContainers) {
        Write-Host "  Stopping Whizbang test containers..." -ForegroundColor Gray
        $whizbangContainers | ForEach-Object { docker stop $_ 2>&1 | Out-Null; docker rm $_ 2>&1 | Out-Null }
    }

    # Clean up Testcontainers ryuk (safe - this is Testcontainers' own reaper)
    $ryukContainers = docker ps -a --filter "ancestor=testcontainers/ryuk" --format "{{.ID}}" 2>$null
    if ($ryukContainers) {
        $ryukContainers | ForEach-Object { docker stop $_ 2>&1 | Out-Null; docker rm $_ 2>&1 | Out-Null }
    }

    # Prune unused networks and volumes
    docker network prune -f 2>&1 | Out-Null
    docker volume prune -f 2>&1 | Out-Null

    Write-Host "Container cleanup complete." -ForegroundColor Green
    exit 0
}

# Navigate to repo root
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

# Clean up any leftover test containers from previous runs
# This ensures tests start with a clean slate even if previous runs were interrupted
if ($includeIntegrationTests -or $onlyIntegrationTests) {
    if (-not $useAiOutput) {
        Write-Host "Cleaning up any leftover test containers..." -ForegroundColor Yellow
    }

    # Stop and remove Whizbang test containers EXCEPT shared containers which are designed to persist
    # We use name-based filtering with "whizbang-test-" prefix to avoid killing other projects' containers
    # The whizbang-test-postgres and whizbang-test-rabbitmq containers are intentionally kept running for reuse
    $whizbangContainers = docker ps -a --filter "name=whizbang-test-" --format "{{.ID}} {{.Names}}" | Where-Object { $_ -notmatch "whizbang-test-postgres" -and $_ -notmatch "whizbang-test-rabbitmq" } | ForEach-Object { ($_ -split " ")[0] }
    if ($whizbangContainers) {
        $whizbangContainers | ForEach-Object { docker stop $_ 2>&1 | Out-Null; docker rm $_ 2>&1 | Out-Null }
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

    # Prune stale Docker networks and volumes (non-interactive)
    docker network prune -f 2>&1 | Out-Null
    docker volume prune -f 2>&1 | Out-Null

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

    # Branded header (suppressed when called from Run-PR.ps1)
    if (-not $NoHeader) {
        $headerParams = @{ Mode = $Mode; Parallel = "$MaxParallel" }
        if ($Coverage) { $headerParams["Coverage"] = "On" }
        if ($FailFast) { $headerParams["FailFast"] = "On" }
        if ($ProjectFilter) { $headerParams["ProjectFilter"] = $ProjectFilter }
        if ($Tag) { $headerParams["Tag"] = $Tag }

        # Build detail lines for the config box
        $details = @()
        if ($onlyIntegrationTests) {
            $details += "Integration Tests: Only"
        } elseif (-not $includeIntegrationTests) {
            $details += "Integration Tests: Excluded"
        }
        if ($TestFilter) { $details += "Test Filter: $TestFilter" }
        if ($NoBuild) { $details += "Skipping build (using pre-built artifacts)" }

        Write-WhizbangHeader -ScriptName "Test Runner" -Params $headerParams -Estimate $estimateStr -Details $details
        [Console]::Out.Flush()
    }

    # Indentation: when called as child from Run-PR.ps1, indent all output to align
    # under the "▶" step indicators. Override Write-Host with an indented wrapper.
    $ind = if ($NoHeader) { "    " } else { "" }
    if ($NoHeader) {
        function Write-IndentedHost {
            param(
                [Parameter(Position = 0)] [string]$Object = "",
                [string]$ForegroundColor = "",
                [switch]$NoNewline
            )
            $params = @{}
            if ($ForegroundColor) { $params["ForegroundColor"] = $ForegroundColor }
            if ($NoNewline) { $params["NoNewline"] = $true }
            Microsoft.PowerShell.Utility\Write-Host "${Object}" @params
        }
        Set-Alias -Name Write-Host -Value Write-IndentedHost -Scope Local
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

    # Add no-build if requested (use pre-built artifacts)
    if ($NoBuild) {
        $testArgs += "--no-build"
    }

    # Coverage mode: Run projects in parallel with unique output paths per project
    # Each project writes coverage to its own directory to avoid collisions
    if ($Coverage) {
        if (-not $useAiOutput) {
            Write-Host "Coverage mode: Parallel execution with unique output paths" -ForegroundColor Yellow
        } else {
            Write-Host "Coverage mode: Parallel execution (max $MaxParallel)" -ForegroundColor Gray
        }

        # Discover test projects
        $testProjectPaths = @()

        # Get tests from main tests/ directory
        $testProjectPaths += Get-ChildItem -Path "$repoRoot/tests" -Recurse -Filter "*.csproj" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notmatch "AppHost" } |
            ForEach-Object { $_.FullName }

        # Get tests from samples directory
        $testProjectPaths += Get-ChildItem -Path "$repoRoot/samples" -Recurse -Filter "*.Tests.csproj" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notmatch "AppHost" } |
            ForEach-Object { $_.FullName }

        # Apply project filter if specified
        if ($ProjectFilter) {
            $testProjectPaths = @($testProjectPaths | Where-Object { $_ -match $ProjectFilter })
        }

        # Apply integration test filtering based on mode
        if ($onlyIntegrationTests) {
            $testProjectPaths = @($testProjectPaths | Where-Object { $_ -match "Integration\.Tests|IntegrationTests|Postgres\.Tests" })
        } elseif (-not $includeIntegrationTests) {
            $testProjectPaths = @($testProjectPaths | Where-Object { $_ -notmatch "Integration\.Tests|IntegrationTests|Postgres\.Tests" })
        }

        # Apply exclude filter if specified
        if ($ExcludeProjectFilter) {
            $testProjectPaths = @($testProjectPaths | Where-Object { $_ -notmatch $ExcludeProjectFilter })
        }

        # Apply Tag filter if specified (coverage mode)
        if ($Tag) {
            $testProjectPaths = @($testProjectPaths | Where-Object {
                $csprojPath = $_
                $tags = @()
                if (Test-Path $csprojPath) {
                    $content = Get-Content $csprojPath -Raw
                    if ($content -match '<WhizbangTestTags>([^<]+)</WhizbangTestTags>') {
                        $tags = $matches[1] -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
                    }
                }
                $tags -contains $Tag
            })
        }

        if ($testProjectPaths.Count -eq 0) {
            Write-Warning "No test projects found matching filters."
            exit 1
        }

        # Separate unit tests (parallel) from integration tests (sequential due to shared containers)
        $integrationPattern = "Integration\.Tests|IntegrationTests|Postgres\.Tests|Dapper\.Postgres"
        $unitTestProjects = @($testProjectPaths | Where-Object { $_ -notmatch $integrationPattern })
        $integrationTestProjects = @($testProjectPaths | Where-Object { $_ -match $integrationPattern })

        if (-not $useAiOutput) {
            Write-Host "Discovered $($unitTestProjects.Count) unit test projects (parallel)" -ForegroundColor Gray
            Write-Host "Discovered $($integrationTestProjects.Count) integration test projects (sequential)" -ForegroundColor Gray
        } else {
            Write-Host "Unit tests: $($unitTestProjects.Count) (parallel), Integration tests: $($integrationTestProjects.Count) (sequential)" -ForegroundColor Gray
        }

        # Helper function to run a single test project with coverage
        function Invoke-TestWithCoverage {
            param([string]$ProjectPath, [string]$Config, [string]$Filter, [bool]$FailFastEnabled)

            $projName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
            $projDir = [System.IO.Path]::GetDirectoryName($ProjectPath)

            # On Linux/macOS, set execute permission
            if ($IsLinux -or $IsMacOS) {
                $testExe = Join-Path $projDir "bin" $Config "net10.0" $projName
                if (Test-Path $testExe) { chmod +x $testExe 2>$null }
            }

            $coverageSettingsPath = Join-Path $repoRoot "codecoverage.config"
            $args = @("run", "--project", $ProjectPath, "--configuration", $Config, "--no-build", "--", "--coverage", "--coverage-output-format", "cobertura", "--coverage-settings", $coverageSettingsPath)
            if ($Filter) { $args += "--treenode-filter"; $args += "/*/*/*/*$Filter*" }
            if ($FailFastEnabled) { $args += "--fail-fast" }

            $output = & dotnet @args 2>&1
            return [PSCustomObject]@{
                ProjectName = $projName
                ExitCode = $LASTEXITCODE
                Output = ($output | Select-Object -Last 30) -join "`n"
            }
        }

        $results = [System.Collections.Concurrent.ConcurrentBag[PSCustomObject]]::new()
        $failFastTriggered = $false
        $startTime = Get-Date

        # Pre-build: Build all test projects first to avoid race conditions in parallel execution
        # This ensures all dependencies are built before running tests in parallel with --no-build
        # Skip if -NoBuild is specified (artifacts are pre-built, e.g., in CI)
        if (-not $NoBuild) {
            $allProjectsToBuild = $unitTestProjects + $integrationTestProjects
            Write-Host "Building $($allProjectsToBuild.Count) test projects..." -ForegroundColor Gray
            $buildFailed = $false
            foreach ($projectPath in $allProjectsToBuild) {
                $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
                $buildOutput = & dotnet build $projectPath --configuration $Configuration 2>&1
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "Build failed for $projectName`:" -ForegroundColor Red
                    $buildOutput | Select-Object -Last 20 | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
                    $buildFailed = $true
                    break
                }
            }
            if ($buildFailed) {
                exit 1
            }
            Write-Host "Build succeeded." -ForegroundColor Gray
        } else {
            Write-Host "Skipping build (-NoBuild specified, using pre-built artifacts)..." -ForegroundColor Gray
        }

        # Phase 1: Run unit tests in parallel
        if ($unitTestProjects.Count -gt 0) {
            Write-Host "Running $($unitTestProjects.Count) unit test projects in parallel (max $MaxParallel)..." -ForegroundColor Cyan

            $unitTestProjects | ForEach-Object -ThrottleLimit $MaxParallel -Parallel {
                $projectPath = $_
                $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
                $projectDir = [System.IO.Path]::GetDirectoryName($projectPath)
                $config = $using:Configuration
                $testFilter = $using:TestFilter
                $failFast = $using:FailFast
                $resultsBag = $using:results
                $coverageSettingsPath = Join-Path $using:repoRoot "codecoverage.config"

                if ($IsLinux -or $IsMacOS) {
                    $testExe = Join-Path $projectDir "bin" $config "net10.0" $projectName
                    if (Test-Path $testExe) { chmod +x $testExe 2>$null }
                }

                $projectArgs = @("run", "--project", $projectPath, "--configuration", $config, "--no-build", "--", "--coverage", "--coverage-output-format", "cobertura", "--coverage-settings", $coverageSettingsPath)
                if ($testFilter) { $projectArgs += "--treenode-filter"; $projectArgs += "/*/*/*/*$testFilter*" }
                if ($failFast) { $projectArgs += "--fail-fast" }

                $output = & dotnet @projectArgs 2>&1
                $resultsBag.Add([PSCustomObject]@{
                    ProjectName = $projectName
                    ExitCode = $LASTEXITCODE
                    Output = ($output | Select-Object -Last 30) -join "`n"
                })
            }
        }

        # Phase 2: Run integration tests sequentially (shared container resources)
        if ($integrationTestProjects.Count -gt 0 -and -not $failFastTriggered) {
            Write-Host "Running $($integrationTestProjects.Count) integration test projects sequentially..." -ForegroundColor Cyan

            foreach ($projectPath in $integrationTestProjects) {
                $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
                Write-Host "  Testing: $projectName" -ForegroundColor Gray

                $result = Invoke-TestWithCoverage -ProjectPath $projectPath -Config $Configuration -Filter $TestFilter -FailFastEnabled $FailFast
                $results.Add($result)

                if ($result.ExitCode -ne 0 -and $FailFast) {
                    $failFastTriggered = $true
                    Write-Host "  Stopping due to -FailFast" -ForegroundColor Red
                    break
                }
            }
        }

        # Complete sub-progress bar
        $totalTestProjects = $unitTestProjects.Count + $integrationTestProjects.Count
        if ($NoHeader) {
            Write-Progress -Id 1 -ParentId 0 -Activity "Running Tests" -Completed
        }

        # Aggregate results
        $totalProjectsPassed = 0
        $totalProjectsFailed = 0
        $failedProjects = @()

        $resultIndex = 0
        foreach ($result in $results) {
            $resultIndex++
            if ($NoHeader) {
                $pct = [math]::Round(($resultIndex / $totalTestProjects) * 100)
                Write-Progress -Id 1 -ParentId 0 -Activity "Test Results" -Status "$resultIndex/$totalTestProjects: $($result.ProjectName)" -PercentComplete $pct
            }

            if ($result.ExitCode -eq 0) {
                $totalProjectsPassed++
                if ($useAiOutput) {
                    Write-Host "  ✓ $($result.ProjectName) passed" -ForegroundColor Green
                }
            } else {
                $totalProjectsFailed++
                $failedProjects += $result.ProjectName
                if ($useAiOutput) {
                    Write-Host "  ✗ $($result.ProjectName) failed" -ForegroundColor Red
                } else {
                    Write-Host "  ✗ $($result.ProjectName) FAILED" -ForegroundColor Red
                }
                # Always show output for failed projects (both AI and verbose modes)
                Write-Host "    --- Output (last 30 lines) ---" -ForegroundColor DarkGray
                $result.Output -split "`n" | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
                Write-Host "    --- End output ---" -ForegroundColor DarkGray
            }
        }

        $allPassed = $totalProjectsFailed -eq 0

        $endTime = Get-Date
        $elapsed = $endTime - $startTime
        $elapsedString = if ($elapsed.TotalMinutes -ge 1) {
            "{0:F0}m {1:F0}s" -f [Math]::Floor($elapsed.TotalMinutes), $elapsed.Seconds
        } else {
            "{0:F1}s" -f $elapsed.TotalSeconds
        }

        Write-Host ""
        Write-Host "=== COVERAGE TEST RESULTS ===" -ForegroundColor Cyan
        Write-Host "Duration: $elapsedString" -ForegroundColor Cyan
        Write-Host "Projects Passed: $totalProjectsPassed" -ForegroundColor Green
        Write-Host "Projects Failed: $totalProjectsFailed" -ForegroundColor $(if ($totalProjectsFailed -gt 0) { "Red" } else { "Green" })

        # List failed projects for easy identification
        if ($failedProjects.Count -gt 0) {
            Write-Host ""
            Write-Host "Failed Projects:" -ForegroundColor Red
            foreach ($failedProject in $failedProjects) {
                Write-Host "  - $failedProject" -ForegroundColor Red
            }
        }
        Write-Host ""

        # Generate coverage report
        Write-Host ""
        Write-Host "Generating coverage report..." -ForegroundColor Cyan

        # Auto-install reportgenerator: try local manifest first, fall back to global
        $rgInstalled = dotnet tool list -g 2>$null | Select-String "dotnet-reportgenerator-globaltool"
        $rgLocal = dotnet tool list 2>$null | Select-String "dotnet-reportgenerator-globaltool"
        if (-not $rgInstalled -and -not $rgLocal) {
            Write-Host "Installing reportgenerator..." -ForegroundColor Yellow
            dotnet tool restore 2>&1 | Out-Null
            $rgLocal = dotnet tool list 2>$null | Select-String "dotnet-reportgenerator-globaltool"
            if (-not $rgLocal) {
                dotnet tool install -g dotnet-reportgenerator-globaltool 2>&1 | Out-Null
            }
        }

        # Find all cobertura XML files from test output
        $coberturaFiles = Get-ChildItem -Path (Join-Path $repoRoot "tests") -Filter "*.cobertura.xml" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "bin[/\\]Debug[/\\]net10\.0[/\\]TestResults" }

        if ($coberturaFiles.Count -gt 0) {
            $reportDir = Join-Path $repoRoot "coverage-report"
            $reports = ($coberturaFiles | ForEach-Object { $_.FullName }) -join ";"

            # Generate HTML and TextSummary reports
            reportgenerator "-reports:$reports" "-targetdir:$reportDir" "-reporttypes:Html;TextSummary" 2>&1 | Out-Null

            # Parse cobertura XML for detailed coverage info (single pass for totals + per-file)
            $totalLines = 0
            $totalCovered = 0
            $uncoveredByFile = @{}
            $fileCoverage = @{}
            $fileCount = 0

            Write-Host "Parsing coverage data..." -ForegroundColor Gray -NoNewline
            foreach ($cobFile in $coberturaFiles) {
                [xml]$cobXml = Get-Content $cobFile.FullName
                foreach ($package in $cobXml.coverage.packages.package) {
                    foreach ($class in $package.classes.class) {
                        $filename = $class.filename
                        # Only include src/ files, exclude test files
                        if ($filename -and $filename -match "[/\\]src[/\\]" -and $filename -notmatch "[/\\]tests?[/\\]") {
                            if (-not $fileCoverage.ContainsKey($filename)) {
                                $fileCoverage[$filename] = @{ Total = 0; Covered = 0 }
                                $fileCount++
                                if ($fileCount % 100 -eq 0) { Write-Host "." -NoNewline }
                            }
                            foreach ($line in $class.lines.line) {
                                $totalLines++
                                $fileCoverage[$filename].Total++
                                if ([int]$line.hits -gt 0) {
                                    $totalCovered++
                                    $fileCoverage[$filename].Covered++
                                } else {
                                    if (-not $uncoveredByFile.ContainsKey($filename)) {
                                        $uncoveredByFile[$filename] = [System.Collections.Generic.List[int]]::new()
                                    }
                                    $uncoveredByFile[$filename].Add([int]$line.number)
                                }
                            }
                        }
                    }
                }
            }
            Write-Host " done ($fileCount files)" -ForegroundColor Gray

            # Calculate and display overall coverage
            $coveragePct = if ($totalLines -gt 0) { [math]::Round(($totalCovered / $totalLines) * 100, 2) } else { 0 }
            Write-Host ""
            Write-Host "=====================================" -ForegroundColor Cyan
            Write-Host "  Code Coverage: $coveragePct% ($totalCovered / $totalLines lines)" -ForegroundColor $(if ($coveragePct -ge 100) { "Green" } elseif ($coveragePct -ge 80) { "Yellow" } else { "Red" })
            Write-Host "=====================================" -ForegroundColor Cyan

            # Show files below 100% coverage (data already collected in single pass above)
            if ($uncoveredByFile.Count -gt 0) {
                Write-Host ""
                Write-Host "Files below 100% coverage:" -ForegroundColor Yellow
                foreach ($file in ($uncoveredByFile.Keys | Sort-Object)) {
                    $fc = $fileCoverage[$file]
                    $filePct = if ($fc.Total -gt 0) { [math]::Round(($fc.Covered / $fc.Total) * 100, 2) } else { 0 }
                    $relPath = $file
                    if ($file.StartsWith($repoRoot)) {
                        $relPath = $file.Substring($repoRoot.Length).TrimStart("/", "\")
                    }
                    $lineNums = ($uncoveredByFile[$file] | Sort-Object -Unique) -join ", "
                    Write-Host "  $relPath - ${filePct}% coverage" -ForegroundColor Yellow
                    Write-Host "    Uncovered lines: $lineNums" -ForegroundColor DarkYellow
                }
            }

            $htmlReport = Join-Path $reportDir "index.html"
            Write-Host ""
            Write-Host "HTML report: $htmlReport" -ForegroundColor Cyan
        } else {
            Write-Host "No cobertura XML files found." -ForegroundColor Yellow
        }

        Write-Host ""
        if ($allPassed) {
            Write-Host "=== STATUS: ALL TESTS PASSED ===" -ForegroundColor Green
            exit 0
        } else {
            Write-Host "=== STATUS: SOME TESTS FAILED ===" -ForegroundColor Red
            exit 1
        }
    }

    # Test type discovery using WhizbangTestType MSBuild property
    # Projects set <WhizbangTestType>Unit</WhizbangTestType> or <WhizbangTestType>Integration</WhizbangTestType>
    # This replaces regex pattern matching for more explicit control
    $excludePattern = "AppHost|TestUtilities"

    # Cache for project test types (project path -> Unit|Integration)
    $script:projectTestTypeCache = @{}

    # Helper function to get WhizbangTestType from a .csproj file
    function Get-ProjectTestType {
        param([string]$CsprojPath)

        # Check cache first
        if ($script:projectTestTypeCache.ContainsKey($CsprojPath)) {
            return $script:projectTestTypeCache[$CsprojPath]
        }

        $testType = $null
        if (Test-Path $CsprojPath) {
            $content = Get-Content $CsprojPath -Raw
            if ($content -match '<WhizbangTestType>(\w+)</WhizbangTestType>') {
                $testType = $matches[1]
            }
        }

        $script:projectTestTypeCache[$CsprojPath] = $testType
        return $testType
    }

    # Cache for project tags (project path -> array of tags)
    $script:projectTagsCache = @{}

    # Helper function to get WhizbangTestTags from a .csproj file
    function Get-ProjectTags {
        param([string]$CsprojPath)

        # Check cache first
        if ($script:projectTagsCache.ContainsKey($CsprojPath)) {
            return $script:projectTagsCache[$CsprojPath]
        }

        $tags = @()
        if (Test-Path $CsprojPath) {
            $content = Get-Content $CsprojPath -Raw
            if ($content -match '<WhizbangTestTags>([^<]+)</WhizbangTestTags>') {
                $tags = $matches[1] -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
            }
        }

        $script:projectTagsCache[$CsprojPath] = $tags
        return $tags
    }

    # Helper function to test if a DLL has a specific tag
    function Test-HasTag {
        param(
            [System.IO.FileInfo]$DllFile,
            [string]$TagToMatch
        )

        $csprojPath = Get-CsprojForDll $DllFile
        if (-not $csprojPath) { return $false }

        $tags = Get-ProjectTags $csprojPath
        return $tags -contains $TagToMatch
    }

    # Helper function to find the .csproj for a DLL
    function Get-CsprojForDll {
        param([System.IO.FileInfo]$DllFile)

        $dllName = [System.IO.Path]::GetFileNameWithoutExtension($DllFile.Name)
        $projectDir = $DllFile.DirectoryName -replace "[/\\]bin[/\\]$Configuration[/\\]net10\.0$", ""
        $csprojPath = Join-Path $projectDir "$dllName.csproj"

        if (Test-Path $csprojPath) {
            return $csprojPath
        }
        return $null
    }

    # Helper function to test if a DLL is an integration test (based on WhizbangTestType property)
    function Test-IsIntegrationTest {
        param([System.IO.FileInfo]$DllFile)

        $csprojPath = Get-CsprojForDll $DllFile
        if (-not $csprojPath) { return $false }

        $testType = Get-ProjectTestType $csprojPath
        return $testType -eq "Integration"
    }

    # Helper function to test if a DLL is a unit test (based on WhizbangTestType property)
    function Test-IsUnitTest {
        param([System.IO.FileInfo]$DllFile)

        $csprojPath = Get-CsprojForDll $DllFile
        if (-not $csprojPath) { return $false }

        $testType = Get-ProjectTestType $csprojPath
        return $testType -eq "Unit"
    }

    # Helper function to test if a DLL has a valid test type defined
    function Test-HasTestType {
        param([System.IO.FileInfo]$DllFile)

        $csprojPath = Get-CsprojForDll $DllFile
        if (-not $csprojPath) { return $false }

        $testType = Get-ProjectTestType $csprojPath
        return $null -ne $testType
    }

    # Helper function to ensure build exists for dynamic DLL discovery
    function Ensure-BuildExists {
        # Pattern: bin/{Configuration}/net10.0/ - works for standard .NET output paths
        $anyDll = Get-ChildItem -Path $repoRoot -Recurse -Filter "*.Tests.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "bin[/\\]$Configuration[/\\]net10\.0[/\\]" } |
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

    # Helper function to check if a DLL is in its primary project location (not copied to another project's bin folder)
    # When project A references project B, B's DLL gets copied to A's bin folder. This causes duplicate test discovery.
    # We only want to run each test DLL from its primary location (where DLL name matches project folder name).
    function Test-IsPrimaryTestDll {
        param([System.IO.FileInfo]$DllFile)
        $dllName = [System.IO.Path]::GetFileNameWithoutExtension($DllFile.Name)
        $projectDir = $DllFile.DirectoryName -replace "[/\\]bin[/\\]$Configuration[/\\]net10\.0$", ""
        $projectName = [System.IO.Path]::GetFileName($projectDir)
        return $dllName -eq $projectName
    }

    # Use --test-modules with explicit DLL discovery for project filtering
    # This allows us to properly exclude AppHost projects which aren't test projects
    # Tag filtering applies to all discovery modes
    if ($Tag) {
        Ensure-BuildExists
        # Find all test DLLs and filter by tag
        $tagFilteredDlls = @(Get-ChildItem -Path $repoRoot -Recurse -Filter "*.Tests.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "bin[/\\]$Configuration[/\\]net10\.0[/\\]" } |
            Where-Object { $_.Name -notmatch $excludePattern } |
            Where-Object { -not $ExcludeProjectFilter -or $_.Name -notmatch $ExcludeProjectFilter } |
            Where-Object { Test-IsPrimaryTestDll $_ } |
            Where-Object { Test-HasTag $_ $Tag })

        # Apply project filter if also specified
        if ($ProjectFilter) {
            $tagFilteredDlls = @($tagFilteredDlls | Where-Object { $_.Name -match $ProjectFilter })
        }

        # Apply test type filtering based on mode
        if ($onlyIntegrationTests) {
            $tagFilteredDlls = @($tagFilteredDlls | Where-Object { Test-IsIntegrationTest $_ })
        } elseif (-not $includeIntegrationTests) {
            $tagFilteredDlls = @($tagFilteredDlls | Where-Object { Test-IsUnitTest $_ })
        } else {
            $tagFilteredDlls = @($tagFilteredDlls | Where-Object { (Test-IsUnitTest $_) -or (Test-IsIntegrationTest $_) })
        }

        if ($tagFilteredDlls.Count -gt 0) {
            $dllPaths = $tagFilteredDlls | ForEach-Object { [System.IO.Path]::GetRelativePath($repoRoot, $_.FullName) }
            $testArgs += "--test-modules"
            $testArgs += ($dllPaths -join ";")

            if (-not $useAiOutput) {
                Write-Host "Discovered $($tagFilteredDlls.Count) test projects with tag '$Tag'" -ForegroundColor Gray
            }
        } else {
            Write-Warning "No test projects found with tag '$Tag'. Check that projects have <WhizbangTestTags>$Tag</WhizbangTestTags>."
            exit 1
        }
    } elseif ($ProjectFilter) {
        Ensure-BuildExists
        # Find DLLs matching the filter, excluding AppHost and ensuring they're primary test DLLs
        # IMPORTANT: Only match *.Tests.dll to avoid picking up non-test DLLs like Whizbang.Data.EFCore.Postgres.dll
        $filteredDlls = @(Get-ChildItem -Path $repoRoot -Recurse -Filter "*$ProjectFilter*.Tests.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "bin[/\\]$Configuration[/\\]net10\.0[/\\]" } |
            Where-Object { $_.Name -notmatch "AppHost" } |
            Where-Object { -not $ExcludeProjectFilter -or $_.Name -notmatch $ExcludeProjectFilter } |
            Where-Object { Test-IsPrimaryTestDll $_ } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($repoRoot, $_.FullName) })

        if ($filteredDlls.Count -gt 0) {
            $testArgs += "--test-modules"
            $testArgs += ($filteredDlls -join ";")

            if (-not $useAiOutput) {
                Write-Host "Discovered $($filteredDlls.Count) test projects matching '$ProjectFilter'" -ForegroundColor Gray
            }
        } else {
            Write-Warning "No test DLLs found matching '$ProjectFilter'. Check the project name."
            exit 1
        }
    } elseif ($onlyIntegrationTests) {
        # Run ONLY integration tests (WhizbangTestType=Integration)
        Ensure-BuildExists
        # Filter by WhizbangTestType property in .csproj files
        $integrationDlls = @(Get-ChildItem -Path $repoRoot -Recurse -Filter "*.Tests.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "bin[/\\]$Configuration[/\\]net10\.0[/\\]" } |
            Where-Object { Test-IsPrimaryTestDll $_ } |
            Where-Object { Test-IsIntegrationTest $_ } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($repoRoot, $_.FullName) })

        if ($integrationDlls.Count -gt 0) {
            $testArgs += "--test-modules"
            $testArgs += ($integrationDlls -join ";")

            if (-not $useAiOutput) {
                Write-Host "Discovered $($integrationDlls.Count) integration test projects (WhizbangTestType=Integration)" -ForegroundColor Gray
            }
        } else {
            Write-Warning "No integration test projects found. Check that projects have <WhizbangTestType>Integration</WhizbangTestType>."
            exit 1
        }
    } elseif (-not $includeIntegrationTests) {
        # Run only unit tests (WhizbangTestType=Unit)
        Ensure-BuildExists
        # Filter by WhizbangTestType property - only include Unit tests
        $unitTestDlls = @(Get-ChildItem -Path $repoRoot -Recurse -Filter "*.Tests.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "bin[/\\]$Configuration[/\\]net10\.0[/\\]" } |
            Where-Object { Test-IsPrimaryTestDll $_ } |
            Where-Object { Test-IsUnitTest $_ } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($repoRoot, $_.FullName) })

        if ($unitTestDlls.Count -gt 0) {
            $testArgs += "--test-modules"
            $testArgs += ($unitTestDlls -join ";")

            if (-not $useAiOutput) {
                Write-Host "Discovered $($unitTestDlls.Count) unit test projects (WhizbangTestType=Unit)" -ForegroundColor Gray
            }
        } else {
            Write-Warning "No unit test projects found. Check that projects have <WhizbangTestType>Unit</WhizbangTestType>."
            exit 1
        }
    } else {
        # Include ALL test projects (Unit + Integration, excludes Benchmark)
        Ensure-BuildExists
        # Filter to projects with WhizbangTestType of Unit or Integration (not Benchmark)
        $allTestDlls = @(Get-ChildItem -Path $repoRoot -Recurse -Filter "*.Tests.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "bin[/\\]$Configuration[/\\]net10\.0[/\\]" } |
            Where-Object { Test-IsPrimaryTestDll $_ } |
            Where-Object { (Test-IsUnitTest $_) -or (Test-IsIntegrationTest $_) } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($repoRoot, $_.FullName) })

        if ($allTestDlls.Count -gt 0) {
            $testArgs += "--test-modules"
            $testArgs += ($allTestDlls -join ";")

            if (-not $useAiOutput) {
                Write-Host "Discovered $($allTestDlls.Count) test projects (Unit + Integration)" -ForegroundColor Gray
            }
        } else {
            Write-Warning "No test projects found. Check that projects have <WhizbangTestType>Unit|Integration</WhizbangTestType>."
            exit 1
        }
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
        # Flush output immediately so background processes show status right away
        [Console]::Out.Flush()
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
        $infrastructureErrors = 0  # Track MTP infrastructure error count from summary
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
            # Capture generic "error:" lines from test output (infrastructure errors)
            elseif ($lineStr -match "^\s*error:\s+(\d+)") {
                # This catches the final "error: X" summary line from MTP
                # Infrastructure errors are setup/teardown failures separate from test failures
                $infrastructureErrors += [int]$matches[1]
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

        # Capture any stderr content for display
        $stderrContent = $stderrBuilder.ToString().Trim()

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

        if ($infrastructureErrors -gt 0) {
            Write-Host ""
            Write-Host "=== INFRASTRUCTURE ERRORS ($infrastructureErrors) ===" -ForegroundColor Red
            Write-Host ""
            Write-Host "MTP reported $infrastructureErrors infrastructure error(s)." -ForegroundColor Red
            Write-Host "These are setup/teardown failures or test host issues, not test failures." -ForegroundColor Yellow
            Write-Host ""
            Write-Host "Possible causes:" -ForegroundColor Yellow
            Write-Host "  - Test fixture setup/teardown exceptions" -ForegroundColor Gray
            Write-Host "  - Container startup failures (Docker issues)" -ForegroundColor Gray
            Write-Host "  - Resource cleanup errors" -ForegroundColor Gray
            Write-Host "  - Assembly loading failures" -ForegroundColor Gray
            Write-Host ""
            Write-Host "Run the test project directly for detailed error messages:" -ForegroundColor Yellow
            Write-Host "  cd tests/YourProject.Tests && dotnet run" -ForegroundColor Gray
        }

        # Display stderr if any (may contain error details not in stdout)
        if ($stderrContent) {
            Write-Host ""
            Write-Host "=== STDERR OUTPUT ===" -ForegroundColor Yellow
            Write-Host ""
            # Show first 30 lines of stderr
            $stderrLines = $stderrContent -split "`n"
            $linesToShow = [Math]::Min($stderrLines.Count, 30)
            for ($i = 0; $i -lt $linesToShow; $i++) {
                Write-Host "  $($stderrLines[$i])" -ForegroundColor Gray
            }
            if ($stderrLines.Count -gt 30) {
                Write-Host "  ... ($($stderrLines.Count - 30) more lines)" -ForegroundColor DarkGray
            }
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
    } elseif ($useVerboseLogging) {
        # Verbose logging mode: Stream all output and capture errors for structured reporting at end
        # Similar to AI mode but shows all output in real-time instead of sparse progress
        $totalTests = 0
        $totalPassed = 0
        $totalFailed = 0
        $totalSkipped = 0
        $failedTests = @()
        $testDetails = @{}
        $buildErrors = @()
        $projectErrors = @()
        $infrastructureErrors = 0
        $currentFailedTest = $null
        $capturingStackTrace = $false
        $stackTraceLines = @()
        $startTime = Get-Date

        # Start process with redirected output
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

        $reader = $process.StandardOutput

        Write-Host ""
        Write-Host ">>> TEST OUTPUT >>>" -ForegroundColor Cyan
        Write-Host ""

        while (-not $reader.EndOfStream) {
            $lineStr = $reader.ReadLine()
            if ($null -eq $lineStr) { continue }

            # Stream ALL output in verbose mode (this is the key difference from AI mode)
            Write-Host $lineStr

            # Capture test counts from TUnit summary format
            if ($lineStr -match "^\s*succeeded:\s+(\d+)\s*$") {
                $totalPassed += [int]$matches[1]
            }
            elseif ($lineStr -match "^\s*failed:\s+(\d+)\s*$") {
                $totalFailed += [int]$matches[1]
            }
            elseif ($lineStr -match "^\s*skipped:\s+(\d+)\s*$") {
                $totalSkipped += [int]$matches[1]
            }

            # Capture failed test names
            if ($lineStr -match "^failed\s+([^\(]+)\s+\(") {
                if ($currentFailedTest -and $stackTraceLines.Count -gt 0) {
                    $testDetails[$currentFailedTest]["StackTrace"] = $stackTraceLines -join "`n"
                    $stackTraceLines = @()
                }

                $testName = $matches[1].Trim()
                if ($testName -notmatch "executing|DbCommand|Executed") {
                    $failedTests += $testName
                    $currentFailedTest = $testName
                    $testDetails[$testName] = @{
                        "ErrorMessage" = ""
                        "StackTrace" = ""
                        "Exception" = ""
                    }
                    $capturingStackTrace = $false

                    if ($FailFast -and -not $failFastTriggered) {
                        $failFastTriggered = $true
                        # Don't kill process in verbose mode - let it finish showing output
                    }
                }
            }
            elseif ($currentFailedTest) {
                if ($lineStr -match "^\s*(System\.\w+Exception|TUnit\.\w+Exception|.*Exception):\s*(.+)") {
                    $testDetails[$currentFailedTest]["Exception"] = $matches[1].Trim()
                    $testDetails[$currentFailedTest]["ErrorMessage"] = $matches[2].Trim()
                }
                elseif ($lineStr -match "^\s+at\s+[\w\.]+") {
                    $capturingStackTrace = $true
                    $stackTraceLines += $lineStr.Trim()
                }
                elseif ($capturingStackTrace) {
                    if ($lineStr -match "^\s+at\s+" -or $lineStr -match "^\s+in\s+.*:\s*line\s+\d+") {
                        $stackTraceLines += $lineStr.Trim()
                    }
                    else {
                        $capturingStackTrace = $false
                        if ($stackTraceLines.Count -gt 0) {
                            $testDetails[$currentFailedTest]["StackTrace"] = $stackTraceLines -join "`n"
                            $stackTraceLines = @()
                        }
                    }
                }
            }
            elseif ($lineStr -match "(\S+\.dll)\s+\(.*\)\s+failed with (\d+) error") {
                $projectName = $matches[1]
                $errorCount = $matches[2]
                $projectErrors += "$projectName failed with $errorCount error(s)"
            }
            elseif ($lineStr -match "^\s*error:\s+(\d+)") {
                $infrastructureErrors += [int]$matches[1]
            }
            elseif ($lineStr -match "error\s+(CS\d+|MSB\d+):") {
                $buildErrors += $lineStr.Trim()
            }
        }

        # Clean up
        $reader.Dispose()
        Unregister-Event -SourceIdentifier $stderrEvent.Name -ErrorAction SilentlyContinue
        Remove-Job -Id $stderrEvent.Id -Force -ErrorAction SilentlyContinue

        if (-not $process.HasExited) {
            $process.WaitForExit(5000) | Out-Null
        }
        $processExitCode = $process.ExitCode
        $process.Dispose()

        if ($currentFailedTest -and $stackTraceLines.Count -gt 0) {
            $testDetails[$currentFailedTest]["StackTrace"] = $stackTraceLines -join "`n"
        }

        $stderrContent = $stderrBuilder.ToString().Trim()

        Write-Host ""
        Write-Host "<<< END TEST OUTPUT <<<" -ForegroundColor Cyan

        # Calculate elapsed time
        $endTime = Get-Date
        $totalElapsed = $endTime - $startTime
        $elapsedString = if ($totalElapsed.TotalMinutes -ge 1) {
            "{0:F0}m {1:F0}s" -f [Math]::Floor($totalElapsed.TotalMinutes), $totalElapsed.Seconds
        } else {
            "{0:F1}s" -f $totalElapsed.TotalSeconds
        }

        $totalTests = $totalPassed + $totalFailed + $totalSkipped

        # Display structured summary
        Write-Host ""
        Write-Host "=====================================" -ForegroundColor Cyan
        Write-Host "  TEST RESULTS SUMMARY" -ForegroundColor Cyan
        Write-Host "=====================================" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Duration: $elapsedString" -ForegroundColor White
        Write-Host "Total Tests: $totalTests" -ForegroundColor White
        Write-Host "Passed: $totalPassed" -ForegroundColor Green
        Write-Host "Failed: $totalFailed" -ForegroundColor $(if ($totalFailed -gt 0) { "Red" } else { "Green" })
        Write-Host "Skipped: $totalSkipped" -ForegroundColor Yellow

        if ($buildErrors.Count -gt 0) {
            Write-Host ""
            Write-Host "BUILD ERRORS ($($buildErrors.Count)):" -ForegroundColor Red
            $buildErrors | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        }

        if ($projectErrors.Count -gt 0) {
            Write-Host ""
            Write-Host "PROJECT ERRORS ($($projectErrors.Count)):" -ForegroundColor Red
            $projectErrors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        }

        if ($failedTests.Count -gt 0) {
            Write-Host ""
            Write-Host "FAILED TESTS ($($failedTests.Count)):" -ForegroundColor Red
            foreach ($testName in $failedTests) {
                Write-Host ""
                Write-Host "  ✗ $testName" -ForegroundColor Red
                $details = $testDetails[$testName]
                if ($details["Exception"]) {
                    Write-Host "    Exception: $($details["Exception"])" -ForegroundColor Yellow
                }
                if ($details["ErrorMessage"]) {
                    Write-Host "    Message: $($details["ErrorMessage"])" -ForegroundColor Gray
                }
            }
        }

        if ($stderrContent) {
            Write-Host ""
            Write-Host "STDERR:" -ForegroundColor Yellow
            $stderrContent -split "`n" | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
        }

        # Emit AI instructions if there are failures
        if ($failedTests.Count -gt 0 -or $buildErrors.Count -gt 0) {
            Write-AiInstructions -Type TestFailure
        }

        Write-Host ""
    } else {
        # Normal mode: Pass through to native MTP output with built-in progress
        & dotnet @testArgs
    }

    # Check exit code (also consider projectErrors in AI mode since they may not affect LASTEXITCODE)
    if ($useAiOutput -or $useVerboseLogging) {
        # In AI/Verbose mode, use the process exit code and check captured errors
        # Note: dotnet test returns 0 on success, non-zero on failure
        # Don't count processExitCode alone - it can be non-zero due to skipped tests or cancellation
        #
        # Infrastructure errors (cleanup/teardown issues) are only treated as fatal if:
        # - Tests also failed (indicates a real problem)
        # - No tests passed (indicates setup failure)
        # If tests pass but cleanup throws, we log a warning but don't fail the build
        $hasTestFailures = $totalFailed -gt 0 -or $failFastTriggered -or $projectErrors.Count -gt 0 -or $buildErrors.Count -gt 0
        $hasInfraErrorsOnly = $infrastructureErrors -gt 0 -and -not $hasTestFailures

        if ($hasInfraErrorsOnly -and $totalPassed -gt 0) {
            # Infrastructure errors with passing tests - warn but don't fail
            Write-Host ""
            Write-Host "WARNING: $infrastructureErrors infrastructure error(s) during cleanup (all tests passed)" -ForegroundColor Yellow
            Write-Host "         This is typically Docker container cleanup noise and does not affect test results." -ForegroundColor DarkYellow
            $hasErrors = $false
        } else {
            # Real failures or infrastructure errors without passing tests
            $hasErrors = $hasTestFailures -or ($infrastructureErrors -gt 0 -and $totalPassed -eq 0)
        }
    } else {
        $hasErrors = $LASTEXITCODE -ne 0
    }

    # --- Coverage Report Generation ---
    if ($Coverage) {
        Write-Host ""
        Write-Host "Generating coverage report..." -ForegroundColor Cyan

        # Auto-install reportgenerator: try local manifest first, fall back to global
        $rgInstalled = dotnet tool list -g 2>$null | Select-String "dotnet-reportgenerator-globaltool"
        $rgLocal = dotnet tool list 2>$null | Select-String "dotnet-reportgenerator-globaltool"
        if (-not $rgInstalled -and -not $rgLocal) {
            Write-Host "Installing reportgenerator..." -ForegroundColor Yellow
            dotnet tool restore 2>&1 | Out-Null
            $rgLocal = dotnet tool list 2>$null | Select-String "dotnet-reportgenerator-globaltool"
            if (-not $rgLocal) {
                dotnet tool install -g dotnet-reportgenerator-globaltool 2>&1 | Out-Null
            }
        }

        # Find all cobertura XML files from test output
        $coberturaFiles = Get-ChildItem -Path (Join-Path $repoRoot "tests") -Filter "*.cobertura.xml" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "bin[/\\]Debug[/\\]net10\.0[/\\]TestResults" }

        if ($coberturaFiles.Count -gt 0) {
            $reportDir = Join-Path $repoRoot "coverage-report"
            $reports = ($coberturaFiles | ForEach-Object { $_.FullName }) -join ";"

            # Generate HTML and TextSummary reports
            reportgenerator "-reports:$reports" "-targetdir:$reportDir" "-reporttypes:Html;TextSummary" 2>&1 | Out-Null

            # Parse cobertura XML for detailed coverage info (single pass for totals + per-file)
            $totalLines = 0
            $totalCovered = 0
            $uncoveredByFile = @{}
            $fileCoverage = @{}
            $fileCount = 0

            Write-Host "Parsing coverage data..." -ForegroundColor Gray -NoNewline
            foreach ($cobFile in $coberturaFiles) {
                [xml]$cobXml = Get-Content $cobFile.FullName
                foreach ($package in $cobXml.coverage.packages.package) {
                    foreach ($class in $package.classes.class) {
                        $filename = $class.filename
                        # Only include src/ files, exclude test files
                        if ($filename -and $filename -match "[/\\]src[/\\]" -and $filename -notmatch "[/\\]tests?[/\\]") {
                            if (-not $fileCoverage.ContainsKey($filename)) {
                                $fileCoverage[$filename] = @{ Total = 0; Covered = 0 }
                                $fileCount++
                                if ($fileCount % 100 -eq 0) { Write-Host "." -NoNewline }
                            }
                            foreach ($line in $class.lines.line) {
                                $totalLines++
                                $fileCoverage[$filename].Total++
                                if ([int]$line.hits -gt 0) {
                                    $totalCovered++
                                    $fileCoverage[$filename].Covered++
                                } else {
                                    if (-not $uncoveredByFile.ContainsKey($filename)) {
                                        $uncoveredByFile[$filename] = [System.Collections.Generic.List[int]]::new()
                                    }
                                    $uncoveredByFile[$filename].Add([int]$line.number)
                                }
                            }
                        }
                    }
                }
            }
            Write-Host " done ($fileCount files)" -ForegroundColor Gray

            # Calculate and display overall coverage
            $coveragePct = if ($totalLines -gt 0) { [math]::Round(($totalCovered / $totalLines) * 100, 2) } else { 0 }
            Write-Host ""
            Write-Host "=====================================" -ForegroundColor Cyan
            Write-Host "  Code Coverage: $coveragePct% ($totalCovered / $totalLines lines)" -ForegroundColor $(if ($coveragePct -ge 100) { "Green" } elseif ($coveragePct -ge 80) { "Yellow" } else { "Red" })
            Write-Host "=====================================" -ForegroundColor Cyan

            # Show files below 100% coverage (data already collected in single pass above)
            if ($uncoveredByFile.Count -gt 0) {
                Write-Host ""
                Write-Host "Files below 100% coverage:" -ForegroundColor Yellow
                foreach ($file in ($uncoveredByFile.Keys | Sort-Object)) {
                    $fc = $fileCoverage[$file]
                    $filePct = if ($fc.Total -gt 0) { [math]::Round(($fc.Covered / $fc.Total) * 100, 2) } else { 0 }
                    $relPath = $file
                    if ($file.StartsWith($repoRoot)) {
                        $relPath = $file.Substring($repoRoot.Length).TrimStart("/", "\")
                    }
                    $lineNums = ($uncoveredByFile[$file] | Sort-Object -Unique) -join ", "
                    Write-Host "  $relPath - ${filePct}% coverage" -ForegroundColor Yellow
                    Write-Host "    Uncovered lines: $lineNums" -ForegroundColor DarkYellow
                }
            }

            $htmlReport = Join-Path $reportDir "index.html"
            Write-Host ""
            Write-Host "HTML report: $htmlReport" -ForegroundColor Cyan
        } else {
            Write-Host "No cobertura XML files found. Ensure tests ran with --coverage." -ForegroundColor Yellow
        }
    }

    # Calculate run duration
    $runDuration = ([DateTime]::UtcNow - $script:runStartTime).TotalSeconds

    # Calculate coverage percentage if available
    $coveragePct = $null
    if ($Coverage -and $totalLines -gt 0) {
        $coveragePct = [math]::Round(($totalCovered / $totalLines) * 100, 2)
    }

    # Write history entry
    $historyEntry = @{
        mode        = $Mode
        duration_s  = [math]::Round($runDuration, 1)
        total       = $totalPassed + $totalFailed + $totalSkipped
        passed      = $totalPassed
        failed      = $totalFailed
        skipped     = $totalSkipped
    }
    if ($null -ne $coveragePct) { $historyEntry["coverage_pct"] = $coveragePct }
    if ($failedTests.Count -gt 0) { $historyEntry["failed_tests"] = @($failedTests) }
    $histFile = Join-Path $PSScriptRoot ".." "logs" "test-runs.jsonl"
    Write-HistoryEntry -FilePath $histFile -Entry $historyEntry

    # Stop tee logging
    if ($LogFile) { Stop-TeeLogging }

    # JSON output mode
    if ($OutputFormat -eq "Json") {
        $jsonResult = @{
            status       = if (-not $hasErrors) { "passed" } else { "failed" }
            mode         = $Mode
            duration_s   = [math]::Round($runDuration, 1)
            total        = $totalPassed + $totalFailed + $totalSkipped
            passed       = $totalPassed
            failed       = $totalFailed
            skipped      = $totalSkipped
            failed_tests = @($failedTests)
        }
        if ($null -ne $coveragePct) { $jsonResult["coverage_pct"] = $coveragePct }
        if ($Coverage) {
            $htmlReport = Join-Path (Join-Path $repoRoot "coverage-report") "index.html"
            if (Test-Path $htmlReport) { $jsonResult["coverage_report"] = $htmlReport }
        }
        ConvertTo-JsonResult -Result $jsonResult
        exit $(if (-not $hasErrors) { 0 } else { 1 })
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
    if ($includeIntegrationTests -or $onlyIntegrationTests -or $failFastTriggered -or $Cleanup) {
        if (-not $useAiOutput) {
            Write-Host ""
            Write-Host "Cleaning up test containers..." -ForegroundColor Yellow
        }

        # Clean up Whizbang test containers using name-based filtering ONLY
        # This ensures we NEVER kill containers from other projects (postgres, rabbitmq, etc.)
        # All Whizbang test containers use the "whizbang-test-" prefix
        if ($Cleanup) {
            # Full cleanup: remove ALL whizbang-test-* containers including shared ones
            $whizbangContainers = docker ps -a --filter "name=whizbang-test-" --format "{{.ID}}" 2>$null
            if ($whizbangContainers) {
                $whizbangContainers | ForEach-Object {
                    docker stop $_ 2>&1 | Out-Null
                    docker rm $_ 2>&1 | Out-Null
                }
            }

            # Prune unused volumes (only when doing full cleanup)
            docker volume prune -f 2>&1 | Out-Null
        } else {
            # Partial cleanup: preserve shared containers (whizbang-test-postgres, whizbang-test-rabbitmq)
            $whizbangContainers = docker ps -a --filter "name=whizbang-test-" --format "{{.ID}} {{.Names}}" 2>$null | Where-Object { $_ -notmatch "whizbang-test-postgres" -and $_ -notmatch "whizbang-test-rabbitmq" } | ForEach-Object { ($_ -split " ")[0] }
            if ($whizbangContainers) {
                $whizbangContainers | ForEach-Object {
                    docker stop $_ 2>&1 | Out-Null
                    docker rm $_ 2>&1 | Out-Null
                }
            }
        }

        # Clean up Testcontainers ryuk (safe - this is Testcontainers' own reaper)
        $ryukContainers = docker ps -a --filter "ancestor=testcontainers/ryuk" --format "{{.ID}}" 2>$null
        if ($ryukContainers) {
            $ryukContainers | ForEach-Object {
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
