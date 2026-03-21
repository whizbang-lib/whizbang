#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Full PR lifecycle manager: prepare, create, and monitor pull requests.

.DESCRIPTION
    Manages the complete PR lifecycle with four operational modes:
    - Prepare: Run all local checks (format, build, tests, sonar)
    - Create:  Create a PR via gh CLI following gitflow rules
    - Monitor: Watch CI checks on an existing PR with live status table
    - Full:    Prepare + Create + Monitor (the "full send")

    Parameters follow the same conventions as Run-Tests.ps1 and Run-Sonar.ps1.

.PARAMETER Action
    Operational mode: Prepare, Create, Monitor, or Full.

.PARAMETER Mode
    Output verbosity. All = verbose, Ai = sparse/token-efficient.

.PARAMETER OutputFormat
    Output format: Text (human-readable) or Json (machine-readable).

.PARAMETER LogFile
    Path to write log output. If empty, no file logging.

.PARAMETER LogMode
    Log file verbosity. Defaults to -Mode if not specified.

.PARAMETER FailFast
    Stop on first step failure.

.PARAMETER CoverageThreshold
    Minimum coverage percentage required (default 80).

.PARAMETER SkipSonar
    Skip SonarCloud analysis in Prepare action.

.PARAMETER SkipIntegration
    Skip integration tests in Prepare action.

.PARAMETER PrNumber
    Attach to existing PR by number (for Monitor action). Auto-detects if 0.

.PARAMETER Title
    PR title. Auto-generated from branch name if empty.

.PARAMETER BaseBranch
    Target branch for PR. Auto-detected from gitflow rules if empty.

.PARAMETER Draft
    Create PR as draft.

.PARAMETER PollInterval
    Seconds between CI check polls (default 30).

.EXAMPLE
    .\Run-PR.ps1
    Full send: prepare + create PR + monitor CI checks

.EXAMPLE
    .\Run-PR.ps1 -Action Prepare -Mode Ai
    Run all local checks with sparse output

.EXAMPLE
    .\Run-PR.ps1 -Action Monitor -PrNumber 142
    Monitor CI checks on PR #142

.EXAMPLE
    .\Run-PR.ps1 -Action Create -Draft -Title "feat: add lifecycle coordinator"
    Create a draft PR with custom title
#>

[CmdletBinding()]
param(
    [ValidateSet("Prepare", "Create", "Monitor", "Full")]
    [string]$Action = "Full",

    [ValidateSet("All", "Ai")]
    [string]$Mode = "All",

    [ValidateSet("Text", "Json")]
    [string]$OutputFormat = "Text",

    [string]$LogFile = "",

    [ValidateSet("All", "Ai")]
    [string]$LogMode = "",

    [switch]$FailFast,

    [bool]$AutoFix = $true,  # Automatically fix issues (e.g., run dotnet format) instead of failing

    [int]$CoverageThreshold = 80,

    [switch]$SkipFormat,       # Skip format check
    [switch]$SkipBuild,        # Skip build step
    [switch]$SkipUnitTests,    # Skip unit tests + coverage
    [switch]$SkipIntegration,  # Skip integration tests
    [switch]$SkipSonar,        # Skip SonarQube analysis
    [switch]$SkipCoverage,     # Skip coverage threshold check

    [int]$PrNumber = 0,

    [string]$Title = "",

    [string]$BaseBranch = "",

    [switch]$Draft,

    [int]$PollInterval = 30,

    [switch]$NoHeader,  # Suppress the branded header

    [switch]$CleanLogs,     # Remove log files and coverage reports before running
    [switch]$CleanMetrics,  # Remove JSONL history/metrics files before running
    [switch]$CleanAll       # Remove all logs, metrics, and reports before running
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Suppress progress bars in AI mode (cleaner for token-based consumers and CI)
if ($Mode -eq "Ai") {
    $ProgressPreference = 'SilentlyContinue'
}

# Import shared module
Import-Module (Join-Path $PSScriptRoot "lib" "PR-Readiness-Common.psm1") -Force

$useAiOutput = $Mode -eq "Ai"
$repoRoot = Split-Path -Parent $PSScriptRoot

# Handle cleanup flags before anything else
if ($CleanAll) {
    Write-Host "Cleaning all logs, metrics, and reports..." -ForegroundColor Yellow
    Invoke-CleanAll -RepoRoot $repoRoot
    Write-Host ""
} elseif ($CleanLogs -or $CleanMetrics) {
    if ($CleanLogs) {
        Write-Host "Cleaning logs and reports..." -ForegroundColor Yellow
        Invoke-CleanLogs -RepoRoot $repoRoot
    }
    if ($CleanMetrics) {
        Write-Host "Cleaning metrics..." -ForegroundColor Yellow
        Invoke-CleanMetrics -RepoRoot $repoRoot
    }
    Write-Host ""
}

# Auto-generate a timestamped log file for this run (captures ALL output including child scripts)
$logsDir = Join-Path $repoRoot "logs"
if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir -Force | Out-Null }
$runTimestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
$autoLogFile = Join-Path $logsDir "pr-run-${runTimestamp}.log"
$effectiveLogFile = if ($LogFile) { $LogFile } else { $autoLogFile }

# Start transcript to capture all console output (including child scripts)
Start-Transcript -Path $effectiveLogFile -Force | Out-Null

# Initialize tee logging for structured dual-output if needed
if ($LogFile) {
    $effectiveLogMode = if ($LogMode) { $LogMode } else { $Mode }
    Initialize-TeeLogging -LogFile $LogFile -ConsoleMode $Mode -LogMode $effectiveLogMode
}

# Track timing
$startTime = [DateTime]::UtcNow

# Get current branch
$currentBranch = (git -C $repoRoot rev-parse --abbrev-ref HEAD 2>$null).Trim()

# Show estimation
$historyFile = Join-Path $repoRoot "logs" "pr-checks.jsonl"
$estimate = Get-RunEstimate -FilePath $historyFile
$estimateStr = if ($estimate) { $estimate.Formatted } else { "" }

# Branded header (suppressed with -NoHeader)
if (-not $NoHeader) {
    $headerParams = @{ Action = $Action; Mode = $Mode; Branch = $currentBranch }
    if ($CoverageThreshold -ne 80) { $headerParams["CoverageThreshold"] = "${CoverageThreshold}%" }
    Write-WhizbangHeader -ScriptName "PR Runner" -Params $headerParams -Estimate $estimateStr
    Write-Host "  Log: $effectiveLogFile" -ForegroundColor DarkGray
    Write-Host ""
}

# ============================================================================
# Gitflow Detection
# ============================================================================

function Get-GitflowBaseBranch {
    param([string]$BranchName)

    switch -Regex ($BranchName) {
        "^(feature|feat)/"    { return "develop" }
        "^(fix|chore|docs|refactor|test|perf|ci|build|style)/" { return "develop" }
        "^(release|releases)/" { return "main" }
        "^hotfix/"            { return "main" }
        "^bugfix/"            { return "develop" }
        "^dependabot/"        { return "develop" }
        default               { return "develop" }
    }
}

function Get-PrTitleFromBranch {
    param([string]$BranchName)

    # Extract the meaningful part after the prefix
    $parts = $BranchName -split "/", 2
    if ($parts.Count -eq 2) {
        $prefix = $parts[0]
        $name = $parts[1] -replace "-", " " -replace "_", " "
        return "${prefix}: $name"
    }
    # Fallback: use latest commit message
    return (git log -1 --format=%s 2>$null).Trim()
}

# ============================================================================
# Prepare Action
# ============================================================================

function Invoke-Prepare {
    $script:steps = @()
    $script:overallPassed = $true
    $script:stepNumber = 0

    # Calculate total steps (dynamic based on flags)
    # Steps: Format, Build, Unit Tests, Integration Tests, Coverage Report, Sonar, Coverage Threshold
    $script:totalSteps = 0
    if (-not $SkipFormat) { $script:totalSteps++ }
    if (-not $SkipBuild) { $script:totalSteps++ }
    if (-not $SkipUnitTests) { $script:totalSteps++ }
    if (-not $SkipIntegration) { $script:totalSteps++ }
    if (-not $SkipCoverage) { $script:totalSteps += 2 }  # Coverage Report + Coverage Threshold
    if (-not $SkipSonar) { $script:totalSteps++ }
    if ($script:totalSteps -eq 0) { $script:totalSteps = 1 }  # avoid div by zero

    # Load per-step estimates from history (full stats: avg, stddev, p85)
    $stepHistoryFile = Join-Path $repoRoot "logs" "pr-steps.jsonl"
    $script:stepStats = @{}  # name -> { Avg, StdDev, P85, Count }
    if (Test-Path $stepHistoryFile) {
        $entries = @(Get-Content $stepHistoryFile -ErrorAction SilentlyContinue |
            Where-Object { $_.Trim() } |
            ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } |
            Where-Object { $_ -ne $null -and $_.steps -and ($_.v -eq 1 -or -not $_.v) })
        if ($entries.Count -ge 1) {
            $stepDurations = @{}
            foreach ($entry in $entries) {
                foreach ($step in $entry.steps) {
                    if ($step.duration_s -gt 0) {
                        if (-not $stepDurations.ContainsKey($step.name)) { $stepDurations[$step.name] = @() }
                        $stepDurations[$step.name] += [double]$step.duration_s
                    }
                }
            }
            foreach ($name in $stepDurations.Keys) {
                $durations = @($stepDurations[$name] | Sort-Object)
                $avg = ($durations | Measure-Object -Average).Average
                $sumSq = ($durations | ForEach-Object { [Math]::Pow($_ - $avg, 2) } | Measure-Object -Sum).Sum
                $stddev = if ($durations.Count -gt 0) { [Math]::Sqrt($sumSq / $durations.Count) } else { 0 }
                $p85Index = [Math]::Max(0, [Math]::Ceiling($durations.Count * 0.85) - 1)
                $p85 = $durations[[Math]::Min($p85Index, $durations.Count - 1)]
                $script:stepStats[$name] = @{
                    Avg    = [math]::Round($avg, 1)
                    StdDev = [math]::Round($stddev, 1)
                    P85    = [math]::Round($p85, 1)
                    Count  = $durations.Count
                }
            }
        }
    }

    function Format-StepTiming {
        param([string]$StepName)
        if ($script:stepStats.ContainsKey($StepName)) {
            $s = $script:stepStats[$StepName]
            return "(Timing | est:$(Format-Duration -Seconds $s.P85) | avg:$(Format-Duration -Seconds $s.Avg) | std dev:$(Format-Duration -Seconds $s.StdDev) | 85th%:$(Format-Duration -Seconds $s.P85))"
        } else {
            return "(Timing | no historical data yet)"
        }
    }

    function Run-Step {
        param(
            [string]$Name,
            [scriptblock]$Action,
            [string]$FailureType = "BuildFailure",
            [switch]$ShowOutput  # When set, child output is shown (newline after name, result on separate line)
        )

        $script:stepNumber++
        $pct = [math]::Round(($script:stepNumber / $script:totalSteps) * 100)

        $timingStr = Format-StepTiming -StepName $Name

        # Calculate total estimated time from all step P85 estimates
        $totalEstSec = 0
        foreach ($s in $script:stepStats.Values) { $totalEstSec += $s.P85 }
        $elapsed = ([DateTime]::UtcNow - $startTime).TotalSeconds

        # Update step-based progress bar
        Write-Progress -Id 0 -Activity "Preparing PR" -Status "Step $($script:stepNumber)/$($script:totalSteps): $Name" -PercentComplete $pct

        # Update time-based progress bar (only if we have estimates)
        if ($totalEstSec -gt 0) {
            $timePct = [math]::Min(100, [math]::Round(($elapsed / $totalEstSec) * 100))
            $remaining = [math]::Max(0, $totalEstSec - $elapsed)
            Write-Progress -Id 1 -ParentId 0 -Activity "Elapsed: $(Format-Duration -Seconds $elapsed)" -Status "Remaining: ~$(Format-Duration -Seconds $remaining)" -PercentComplete $timePct
        }

        $stepStart = [DateTime]::UtcNow
        $stepLabel = "  ▶ [$($script:stepNumber)/$($script:totalSteps)] $Name... $timingStr"
        if ($ShowOutput) {
            Write-Host $stepLabel -ForegroundColor Cyan
        } else {
            Write-Host $stepLabel -ForegroundColor Cyan -NoNewline
        }

        try {
            $result = & $Action
            $stepDuration = ([DateTime]::UtcNow - $stepStart).TotalSeconds

            if ($result.ExitCode -ne 0) {
                $prefix = if ($ShowOutput) { "  ▶ $Name..." } else { "" }
                Write-Host "$prefix ❌ Failed ($(Format-Duration -Seconds $stepDuration))" -ForegroundColor Red
                $script:steps += @{ name = $Name; status = "failed"; duration_s = [math]::Round($stepDuration, 1); details = $result.Details }
                $script:overallPassed = $false
                Write-AiInstructions -Type $FailureType
                return $false
            }
            else {
                $prefix = if ($ShowOutput) { "  ▶ $Name..." } else { "" }
                Write-Host "$prefix ✅ Passed ($(Format-Duration -Seconds $stepDuration))" -ForegroundColor Green
                $script:steps += @{ name = $Name; status = "passed"; duration_s = [math]::Round($stepDuration, 1); details = $result.Details }
                return $true
            }
        }
        catch {
            $stepDuration = ([DateTime]::UtcNow - $stepStart).TotalSeconds
            $prefix = if ($ShowOutput) { "  ▶ $Name..." } else { "" }
            Write-Host "$prefix ❌ Error ($(Format-Duration -Seconds $stepDuration))" -ForegroundColor Red
            Write-Host "    $_" -ForegroundColor Red
            $script:steps += @{ name = $Name; status = "error"; duration_s = [math]::Round($stepDuration, 1); details = $_.ToString() }
            $script:overallPassed = $false
            return $false
        }
    }

    Write-Host ""
    Write-Host "  Preparing PR..." -ForegroundColor Cyan
    Write-Host ""

    # Step 1: Format check (with AutoFix support)
    if ($SkipFormat) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Format Check... ⏭️ Skipped $(Format-StepTiming 'Format Check')" -ForegroundColor DarkGray
        $script:steps += @{ name = "Format Check"; status = "skipped"; duration_s = 0 }
    } else {
        $continue = Run-Step -Name "Format Check" -FailureType "FormatFailure" -Action {
            Push-Location $repoRoot
            try {
                dotnet format --verify-no-changes 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0 -and $AutoFix) {
                    Write-Host ""
                    Write-Host "    AutoFix: Running dotnet format..." -ForegroundColor Yellow
                    dotnet format 2>&1 | Out-Null
                    Write-Host "    AutoFix: Re-checking..." -ForegroundColor Yellow
                    dotnet format --verify-no-changes 2>&1 | Out-Null
                }
                @{ ExitCode = $LASTEXITCODE; Details = $null }
            }
            finally { Pop-Location }
        }
        if (-not $continue -and $FailFast) { return @{ Passed = $false; Steps = $script:steps } }
    }

    # Step 2: Build
    if ($SkipBuild) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Build... ⏭️ Skipped $(Format-StepTiming 'Build')" -ForegroundColor DarkGray
        $script:steps += @{ name = "Build"; status = "skipped"; duration_s = 0 }
    } else {
        $continue = Run-Step -Name "Build" -FailureType "BuildFailure" -Action {
            Push-Location $repoRoot
            try {
                dotnet build --verbosity minimal 2>&1 | Out-Null
                @{ ExitCode = $LASTEXITCODE; Details = $null }
            }
            finally { Pop-Location }
        }
        if (-not $continue -and $FailFast) { return @{ Passed = $false; Steps = $script:steps } }
    }

    # Step 3: Unit tests (with coverage collection)
    $coveragePct = $null
    if ($SkipUnitTests) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Unit Tests... ⏭️ Skipped $(Format-StepTiming 'Unit Tests')" -ForegroundColor DarkGray
        $script:steps += @{ name = "Unit Tests"; status = "skipped"; duration_s = 0 }
    } else {
        $unitTestLogFile = Join-Path $repoRoot "logs" "pr-unit-tests.log"
        $continue = Run-Step -Name "Unit Tests" -FailureType "TestFailure" -ShowOutput -Action {
            $testScript = Join-Path $PSScriptRoot "Run-Tests.ps1"
            & $testScript -Mode AiUnit -Coverage -FailFast -NoBuild -NoHeader -NoReport -LogFile $unitTestLogFile -LogMode All 2>&1 | Out-Null
            $exitCode = $LASTEXITCODE
            if ($exitCode -ne 0) {
                Write-Host "    Full output: $unitTestLogFile" -ForegroundColor DarkYellow
            }
            @{ ExitCode = $exitCode; Details = $null }
        }
        if (-not $continue -and $FailFast) { return @{ Passed = $false; Steps = $script:steps } }
    }

    # Step 4: Integration tests (with coverage collection)
    if ($SkipIntegration) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Integration Tests... ⏭️ Skipped $(Format-StepTiming 'Integration Tests')" -ForegroundColor DarkGray
        $script:steps += @{ name = "Integration Tests"; status = "skipped"; duration_s = 0 }
    } else {
        $integrationTestLogFile = Join-Path $repoRoot "logs" "pr-integration-tests.log"
        $continue = Run-Step -Name "Integration Tests" -FailureType "TestFailure" -ShowOutput -Action {
            $testScript = Join-Path $PSScriptRoot "Run-Tests.ps1"
            & $testScript -Mode AiIntegrations -Coverage -FailFast -NoBuild -NoHeader -NoReport -LogFile $integrationTestLogFile -LogMode All 2>&1 | Out-Null
            $exitCode = $LASTEXITCODE
            if ($exitCode -ne 0) {
                Write-Host "    Full output: $integrationTestLogFile" -ForegroundColor DarkYellow
            }
            @{ ExitCode = $exitCode; Details = $null }
        }
        if (-not $continue -and $FailFast) { return @{ Passed = $false; Steps = $script:steps } }
    }

    # Step 5: Sonar (unless skipped)
    if ($SkipSonar) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] SonarQube Analysis... ⏭️ Skipped $(Format-StepTiming 'SonarQube Analysis')" -ForegroundColor DarkGray
        $script:steps += @{ name = "SonarQube Analysis"; status = "skipped"; duration_s = 0 }
    } else {
        $continue = Run-Step -Name "SonarCloud Analysis" -FailureType "SonarFailure" -Action {
            $sonarScript = Join-Path $PSScriptRoot "Run-Sonar.ps1"
            & $sonarScript -Mode Ai -OutputFormat Json -SkipBuild -NoHeader 2>&1 | Out-Null
            @{ ExitCode = $LASTEXITCODE; Details = $null }
        }
        if (-not $continue -and $FailFast) { return @{ Passed = $false; Steps = $script:steps } }
    }

    # Step 5b: Coverage report (combined from unit + integration test coverage)
    # Matches CI pipeline: merge all cobertura XMLs, generate HTML + TextSummary + SonarQube format
    if (-not $SkipCoverage -and (-not $SkipUnitTests -or -not $SkipIntegration)) {
        $continue = Run-Step -Name "Coverage Report" -FailureType "BuildFailure" -Action {
            $coberturaFiles = Get-ChildItem -Path (Join-Path $repoRoot "tests") -Filter "*.cobertura.xml" -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match "bin[/\\]Debug[/\\]net10\.0[/\\]TestResults" }

            if ($coberturaFiles.Count -gt 0) {
                $reportDir = Join-Path $repoRoot "coverage-report"
                $sonarDir = Join-Path $repoRoot "coverage" "sonarqube"
                $reports = ($coberturaFiles | ForEach-Object { $_.FullName }) -join ";"
                $fileFilters = "-*.g.cs;-**/.whizbang-generated/*"

                # Generate HTML + TextSummary + JsonSummary for human consumption
                reportgenerator "-reports:$reports" "-targetdir:$reportDir" "-reporttypes:Html;TextSummary;JsonSummary" "-filefilters:$fileFilters" 2>&1 | Out-Null

                # Generate SonarQube format for local SonarQube ingestion (matches CI pipeline)
                reportgenerator "-reports:$reports" "-targetdir:$sonarDir" "-reporttypes:SonarQube" "-filefilters:$fileFilters" 2>&1 | Out-Null

                $summaryFile = Join-Path $reportDir "Summary.txt"
                if (Test-Path $summaryFile) {
                    $summaryText = Get-Content $summaryFile -Raw
                    if ($summaryText -match "Line coverage:\s+([\d.]+)%") { $script:coveragePct = [double]$Matches[1] }
                    $totalCovered = 0; $totalLines = 0
                    if ($summaryText -match "Covered lines:\s+(\d+)") { $totalCovered = [int]$Matches[1] }
                    if ($summaryText -match "Coverable lines:\s+(\d+)") { $totalLines = [int]$Matches[1] }
                    Write-Host "    Coverage: $($script:coveragePct)% ($totalCovered / $totalLines lines)" -ForegroundColor Cyan
                    Write-Host "    HTML report: $(Join-Path $reportDir 'index.html')" -ForegroundColor DarkCyan
                    Write-Host "    SonarQube report: $(Join-Path $sonarDir 'SonarQube.xml')" -ForegroundColor DarkGray
                }
                @{ ExitCode = 0; Details = $null }
            } else {
                Write-Host "    No coverage data collected" -ForegroundColor Yellow
                @{ ExitCode = 0; Details = $null }
            }
        }
    }

    # Step 6: Coverage threshold
    if ($SkipCoverage) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Coverage Threshold... ⏭️ Skipped $(Format-StepTiming 'Coverage Threshold')" -ForegroundColor DarkGray
        $script:steps += @{ name = "Coverage Threshold"; status = "skipped"; duration_s = 0 }
    } else {
        $script:stepNumber++
        $covTimingStr = Format-StepTiming -StepName "Coverage Threshold"
        Write-Progress -Id 0 -Activity "Preparing PR" -Status "Step $($script:stepNumber)/$($script:totalSteps): Coverage Threshold" -PercentComplete 100
        if ($null -ne $coveragePct) {
            if ($coveragePct -lt $CoverageThreshold) {
                Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Coverage Threshold... ❌ ${coveragePct}% < ${CoverageThreshold}% $covTimingStr" -ForegroundColor Red
                $script:steps += @{ name = "Coverage Threshold"; status = "failed"; duration_s = 0; details = "Coverage ${coveragePct}% below threshold ${CoverageThreshold}%" }
                $script:overallPassed = $false
            }
            else {
                Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Coverage Threshold... ✅ ${coveragePct}% >= ${CoverageThreshold}% $covTimingStr" -ForegroundColor Green
                $script:steps += @{ name = "Coverage Threshold"; status = "passed"; duration_s = 0; details = "Coverage ${coveragePct}%" }
            }
        }
    }

    # Complete the progress bars
    Write-Progress -Id 1 -Activity "Time" -Completed
    Write-Progress -Id 0 -Activity "Preparing PR" -Completed

    # Save per-step history for future estimation
    $stepHistoryFile = Join-Path $repoRoot "logs" "pr-steps.jsonl"
    Write-HistoryEntry -FilePath $stepHistoryFile -Entry @{
        steps = $script:steps
        total_duration_s = [math]::Round(([DateTime]::UtcNow - $startTime).TotalSeconds, 1)
        passed = $script:overallPassed
    }

    return @{ Passed = $script:overallPassed; Steps = $script:steps; CoveragePct = $coveragePct }
}

# ============================================================================
# Create Action
# ============================================================================

function Invoke-Create {
    Write-Host ""
    Write-Host "  Creating PR..." -ForegroundColor Cyan
    Write-Host ""

    # Detect base branch from gitflow
    $base = if ($BaseBranch) { $BaseBranch } else { Get-GitflowBaseBranch -BranchName $currentBranch }
    Write-Host "  Branch: $currentBranch → $base (gitflow)" -ForegroundColor Gray

    # Auto-generate title
    $prTitle = if ($Title) { $Title } else { Get-PrTitleFromBranch -BranchName $currentBranch }
    Write-Host "  Title: $prTitle" -ForegroundColor Gray

    # Check if branch is pushed
    $remoteBranch = git -C $repoRoot rev-parse --verify "origin/$currentBranch" 2>$null
    if (-not $remoteBranch) {
        Write-Host "  Pushing branch to origin..." -ForegroundColor Yellow
        git -C $repoRoot push -u origin HEAD 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ❌ Failed to push branch" -ForegroundColor Red
            return @{ Success = $false; PrNumber = 0; PrUrl = "" }
        }
    }

    # Check if PR already exists
    $existingPr = gh pr view --json number,url 2>$null | ConvertFrom-Json -ErrorAction SilentlyContinue
    if ($existingPr) {
        Write-Host "  PR already exists: #$($existingPr.number) - $($existingPr.url)" -ForegroundColor Yellow
        return @{ Success = $true; PrNumber = $existingPr.number; PrUrl = $existingPr.url }
    }

    # Create PR
    $ghArgs = @("pr", "create", "--base", $base, "--title", $prTitle, "--body", "Created by Whizbang PR Runner")
    if ($Draft) { $ghArgs += "--draft" }

    $createOutput = & gh @ghArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ❌ Failed to create PR: $createOutput" -ForegroundColor Red
        return @{ Success = $false; PrNumber = 0; PrUrl = "" }
    }

    $prUrl = ($createOutput | Select-Object -Last 1).Trim()
    Write-Host "  ✅ PR created: $prUrl" -ForegroundColor Green

    # Extract PR number from URL
    $prNum = if ($prUrl -match "/pull/(\d+)") { [int]$Matches[1] } else { 0 }

    # Hotfix reminder
    if ($currentBranch -match "^hotfix/") {
        Write-Host ""
        Write-Host "  ⚠️  HOTFIX: Remember to also merge to develop after main merge" -ForegroundColor Yellow
    }

    return @{ Success = $true; PrNumber = $prNum; PrUrl = $prUrl }
}

# ============================================================================
# Monitor Action
# ============================================================================

# CI check display name mapping
$script:CheckDisplayNames = @{
    "build"                  = "Build & Format"
    "test-unit"              = "Unit Tests"
    "test-inmemory"          = "InMemory Integration"
    "test-postgres"          = "PostgreSQL Integration"
    "test-rabbitmq"          = "RabbitMQ Integration"
    "test-servicebus"        = "Service Bus Integration"
    "codeql"                 = "CodeQL (csharp)"
    "quality"                = "SonarCloud Quality Gate"
    "security-secrets"       = "Security (Secrets)"
    "security-scorecard"     = "Security (Scorecard)"
    "security-supply-chain"  = "Security (Supply Chain)"
    "git-flow-check"         = "Git Flow Check"
    "version-comment"        = "Version Comment"
}

function Get-CheckDisplayName {
    param([string]$Name)
    $lower = $Name.ToLower()
    foreach ($key in $script:CheckDisplayNames.Keys) {
        if ($lower -match $key) { return $script:CheckDisplayNames[$key] }
    }
    return $Name
}

function Get-StatusIcon {
    param([string]$State, [string]$Conclusion)
    # statusCheckRollup uses UPPERCASE: COMPLETED, IN_PROGRESS, QUEUED, SUCCESS, FAILURE, etc.
    if ($State -eq "COMPLETED") {
        switch ($Conclusion) {
            "SUCCESS"   { return "✅" }
            "FAILURE"   { return "❌" }
            "SKIPPED"   { return "⏭️" }
            "NEUTRAL"   { return "⏭️" }
            "CANCELLED" { return "⏹️" }
            default     { return "❓" }
        }
    }
    elseif ($State -eq "IN_PROGRESS") { return "🔄" }
    else { return "⏳" }
}

function Invoke-Monitor {
    param([int]$PrNum)

    # Auto-detect PR number if not provided
    if ($PrNum -eq 0) {
        $prJson = gh pr view --json number,url,headRefName,baseRefName 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ❌ No PR found for current branch. Use -PrNumber to specify." -ForegroundColor Red
            return @{ Success = $false }
        }
        $prData = $prJson | ConvertFrom-Json
        $PrNum = $prData.number
    }

    # Get PR details (single call gets everything — see plans/pr-monitoring-api-reference.md)
    $prInfo = gh pr view $PrNum --json number,url,headRefName,baseRefName,title,state,mergeable,mergeStateStatus,reviewDecision,statusCheckRollup 2>$null | ConvertFrom-Json
    if (-not $prInfo) {
        Write-Host "  ❌ Could not fetch PR #$PrNum" -ForegroundColor Red
        return @{ Success = $false }
    }

    Write-Host ""
    Write-Host "  PR #$($prInfo.number): $($prInfo.headRefName) → $($prInfo.baseRefName)" -ForegroundColor Cyan
    Write-Host "  $($prInfo.url)" -ForegroundColor DarkCyan
    Write-Host ""

    # Load check estimates from history
    $checkEstimates = Get-CheckEstimate -FilePath $historyFile

    $timeout = [DateTime]::UtcNow.AddMinutes(60)
    $monitorStart = [DateTime]::UtcNow
    $allComplete = $false
    $anyFailed = $false
    $renderCount = 0
    $tableRows = @()
    $checks = @()

    while ([DateTime]::UtcNow -lt $timeout) {
        # Fetch current check status via statusCheckRollup (single API call)
        $prData = gh pr view $PrNum --json statusCheckRollup,mergeable,mergeStateStatus 2>$null | ConvertFrom-Json
        if (-not $prData -or -not $prData.statusCheckRollup) {
            Start-Sleep -Seconds $PollInterval
            continue
        }

        $checks = $prData.statusCheckRollup

        # Build status table
        $tableRows = @()
        $completed = 0
        $total = $checks.Count
        $anyFailed = $false

        foreach ($check in ($checks | Sort-Object name)) {
            $displayName = Get-CheckDisplayName -Name $check.name
            $icon = Get-StatusIcon -State $check.status -Conclusion $check.conclusion

            $statusStr = ""
            if ($check.status -eq "COMPLETED") {
                $completed++
                $duration = ""
                if ($check.startedAt -and $check.completedAt) {
                    $dur = ([DateTime]$check.completedAt - [DateTime]$check.startedAt).TotalSeconds
                    $duration = " ($(Format-Duration -Seconds $dur))"
                }
                $conclusionText = switch ($check.conclusion) {
                    "SUCCESS" { "Passed" }
                    "FAILURE" { "Failed"; $anyFailed = $true }
                    "SKIPPED" { "Skipped" }
                    "NEUTRAL" { "Neutral" }
                    default   { $check.conclusion }
                }
                $statusStr = "$icon $conclusionText$duration"
            }
            elseif ($check.status -eq "IN_PROGRESS") {
                $elapsed = ""
                if ($check.startedAt) {
                    $elapsedSec = ([DateTime]::UtcNow - [DateTime]$check.startedAt).TotalSeconds
                    $elapsed = Format-Duration -Seconds $elapsedSec
                }
                $estStr = ""
                if ($checkEstimates -and $checkEstimates.ContainsKey($displayName)) {
                    $estStr = " / ~$(Format-Duration -Seconds $checkEstimates[$displayName]) est."
                }
                $statusStr = "$icon Running ($elapsed$estStr)"
            }
            else {
                # QUEUED or other pending states
                $estStr = ""
                if ($checkEstimates -and $checkEstimates.ContainsKey($displayName)) {
                    $estStr = " (~$(Format-Duration -Seconds $checkEstimates[$displayName]) est.)"
                }
                $statusStr = "$icon Pending$estStr"
            }

            $tableRows += @{ Name = $displayName; Status = $statusStr }
        }

        # Render table
        if ($total -gt 0) {
            $maxNameLen = ($tableRows | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum
            $maxStatusLen = ($tableRows | ForEach-Object { $_.Status.Length } | Measure-Object -Maximum).Maximum
            $nameWidth = [Math]::Max($maxNameLen + 2, 30)
            $statusWidth = [Math]::Max($maxStatusLen + 2, 30)

            # Clear previous render using ANSI escape codes (move cursor up)
            if ($renderCount -gt 0) {
                $linesToClear = $total + 6  # header + rows + footer
                for ($i = 0; $i -lt $linesToClear; $i++) {
                    Write-Host "`e[A`e[2K" -NoNewline
                }
            }

            Write-Host "  ┌$("─" * $nameWidth)┬$("─" * $statusWidth)┐" -ForegroundColor DarkGray
            Write-Host "  │$("Check".PadRight($nameWidth))│$("Status".PadRight($statusWidth))│" -ForegroundColor DarkGray
            Write-Host "  ├$("─" * $nameWidth)┼$("─" * $statusWidth)┤" -ForegroundColor DarkGray

            foreach ($row in $tableRows) {
                $nameColor = "White"
                if ($row.Status -match "Failed") { $nameColor = "Red" }
                elseif ($row.Status -match "Passed") { $nameColor = "Green" }

                Write-Host "  │" -ForegroundColor DarkGray -NoNewline
                Write-Host "$($row.Name.PadRight($nameWidth))" -ForegroundColor $nameColor -NoNewline
                Write-Host "│" -ForegroundColor DarkGray -NoNewline
                Write-Host "$($row.Status.PadRight($statusWidth))" -ForegroundColor White -NoNewline
                Write-Host "│" -ForegroundColor DarkGray
            }

            Write-Host "  └$("─" * $nameWidth)┴$("─" * $statusWidth)┘" -ForegroundColor DarkGray

            # Progress bar
            $barWidth = 20
            $filledWidth = [math]::Round(($completed / [math]::Max($total, 1)) * $barWidth)
            $bar = "█" * $filledWidth + "░" * ($barWidth - $filledWidth)
            $elapsed = Format-Duration -Seconds ([DateTime]::UtcNow - $monitorStart).TotalSeconds
            Write-Host ""
            Write-Host "  Progress: $bar $completed/$total checks complete" -ForegroundColor Cyan
            Write-Host "  Elapsed: $elapsed" -ForegroundColor Gray

            $renderCount++
        }

        # Check if all complete
        if ($completed -eq $total -and $total -gt 0) {
            $allComplete = $true
            break
        }

        Start-Sleep -Seconds $PollInterval
    }

    # Final summary
    Write-Host ""
    if ($allComplete -and -not $anyFailed) {
        Write-Host "  ✅ All CI checks passed!" -ForegroundColor Green
        # Show merge readiness
        if ($prData.mergeable -eq "MERGEABLE" -and $prData.mergeStateStatus -eq "CLEAN") {
            Write-Host "  🟢 PR is ready to merge" -ForegroundColor Green
        }
        elseif ($prData.mergeStateStatus -eq "BEHIND") {
            Write-Host "  ⚠️  PR is behind base branch — rebase or merge required" -ForegroundColor Yellow
        }
    }
    elseif ($anyFailed) {
        Write-Host "  ❌ Some CI checks failed" -ForegroundColor Red
        foreach ($row in $tableRows) {
            if ($row.Status -match "Failed") {
                Write-Host "    - $($row.Name)" -ForegroundColor Red
            }
        }
        # Fetch failed job logs for the first failure
        $failedCheck = $checks | Where-Object { $_.status -eq "COMPLETED" -and $_.conclusion -eq "FAILURE" } | Select-Object -First 1
        if ($failedCheck -and $failedCheck.detailsUrl -match "actions/runs/(\d+)/job/(\d+)") {
            $runId = $Matches[1]
            $jobId = $Matches[2]
            Write-Host ""
            Write-Host "  Failed job logs (first failure):" -ForegroundColor Yellow
            $failedLogs = gh run view $runId --job $jobId --log-failed 2>$null
            if ($failedLogs) {
                $failedLogs -split "`n" | Select-Object -Last 30 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            }
        }
        Write-AiInstructions -Type TestFailure
    }
    elseif (-not $allComplete) {
        Write-Host "  ⏰ Monitoring timed out after 60 minutes" -ForegroundColor Yellow
    }

    # Save check history
    $checkData = @{}
    foreach ($check in $checks) {
        $displayName = Get-CheckDisplayName -Name $check.name
        $dur = 0
        if ($check.startedAt -and $check.completedAt) {
            $dur = ([DateTime]$check.completedAt - [DateTime]$check.startedAt).TotalSeconds
        }
        $checkData[$displayName] = @{ duration_s = [math]::Round($dur, 1); conclusion = $check.conclusion }
    }

    $totalDuration = ([DateTime]::UtcNow - $monitorStart).TotalSeconds
    Write-HistoryEntry -FilePath $historyFile -Entry @{
        pr              = $PrNum
        branch          = $currentBranch
        checks          = $checkData
        total_duration_s = [math]::Round($totalDuration, 1)
        all_passed      = ($allComplete -and -not $anyFailed)
    }

    return @{ Success = ($allComplete -and -not $anyFailed); PrNumber = $PrNum }
}

# ============================================================================
# Main Execution
# ============================================================================

$result = @{ Passed = $true }
$prNumber = $PrNumber
$prUrl = ""

try {
    # Prepare
    if ($Action -in @("Prepare", "Full")) {
        $prepResult = Invoke-Prepare
        $result.Passed = $prepResult.Passed

        if (-not $prepResult.Passed -and $Action -eq "Full") {
            Write-Host ""
            Write-Host "  Prepare failed — skipping PR creation and monitoring." -ForegroundColor Red
        }
    }

    # Create
    if ($Action -in @("Create", "Full") -and $result.Passed) {
        $createResult = Invoke-Create
        if ($createResult.Success) {
            $prNumber = $createResult.PrNumber
            $prUrl = $createResult.PrUrl
        }
        else {
            $result.Passed = $false
        }
    }

    # Monitor
    if ($Action -in @("Monitor", "Full") -and ($result.Passed -or $Action -eq "Monitor")) {
        $monResult = Invoke-Monitor -PrNum $prNumber
        if (-not $monResult.Success) {
            $result.Passed = $false
        }
    }
}
catch {
    Write-Host ""
    Write-Host "  ⚠️  Unexpected error: $_" -ForegroundColor Yellow
    Write-Host "  $($_.ScriptStackTrace)" -ForegroundColor DarkGray
    $result.Passed = $false
}
finally {
    $duration = ([DateTime]::UtcNow - $startTime).TotalSeconds

    # Clean up progress bars
    Write-Progress -Id 2 -Activity "x" -Completed -ErrorAction SilentlyContinue
    Write-Progress -Id 1 -Activity "x" -Completed -ErrorAction SilentlyContinue
    Write-Progress -Id 0 -Activity "x" -Completed -ErrorAction SilentlyContinue

    # Stop tee logging
    if ($LogFile) { Stop-TeeLogging }

    # Stop transcript
    Stop-Transcript | Out-Null
}

# JSON output
if ($OutputFormat -eq "Json") {
    $jsonResult = @{
        status     = if ($result.Passed) { "passed" } else { "failed" }
        action     = $Action
        duration_s = [math]::Round($duration, 1)
        branch     = $currentBranch
    }
    if ($prNumber -gt 0) { $jsonResult["pr_number"] = $prNumber }
    if ($prUrl) { $jsonResult["pr_url"] = $prUrl }
    if ($result.Steps) { $jsonResult["steps"] = $result.Steps }
    ConvertTo-JsonResult -Result $jsonResult
}

Write-Host ""
exit $(if ($result.Passed) { 0 } else { 1 })
