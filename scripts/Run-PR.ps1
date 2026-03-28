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
    [switch]$CleanAll,      # Remove all logs, metrics, and reports before running

    [switch]$PersistContainer,  # Keep SonarQube Docker container running after script ends

    [switch]$KeepScanFolder,   # Keep the temp scan folder after script ends (for debugging)
    [switch]$DirectScan        # Skip temp folder — scan real repo directly (requires Developer Edition for branch support)
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
$originalRepoRoot = $repoRoot  # Preserve for metrics/history that must survive cleanup
$script:scanFolder = $null

# Handle cleanup flags before anything else
if ($CleanAll) {
    Write-Host "Cleaning all logs, metrics, and reports..." -ForegroundColor Yellow
    Invoke-CleanAll -RepoRoot $originalRepoRoot
    Write-Host ""
} elseif ($CleanLogs -or $CleanMetrics) {
    if ($CleanLogs) {
        Write-Host "Cleaning logs and reports..." -ForegroundColor Yellow
        Invoke-CleanLogs -RepoRoot $originalRepoRoot
    }
    if ($CleanMetrics) {
        Write-Host "Cleaning metrics..." -ForegroundColor Yellow
        Invoke-CleanMetrics -RepoRoot $originalRepoRoot
    }
    Write-Host ""
}

# Auto-generate a timestamped log file for this run (captures ALL output including child scripts)
$logsDir = Join-Path $originalRepoRoot "logs"
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

# Get current branch (from original repo, before scan folder replaces $repoRoot)
$currentBranch = (git -C $repoRoot rev-parse --abbrev-ref HEAD 2>$null).Trim()

# Show estimation
$historyFile = Join-Path $originalRepoRoot "logs" "pr-checks.jsonl"
$estimate = Get-RunEstimate -FilePath $historyFile
$estimateStr = if ($estimate) { $estimate.Formatted } else { "" }

# Branded header (suppressed with -NoHeader) — show immediately so user sees output
if (-not $NoHeader) {
    $headerParams = @{ Action = $Action; Mode = $Mode; Branch = $currentBranch }
    if ($CoverageThreshold -ne 80) { $headerParams["CoverageThreshold"] = "${CoverageThreshold}%" }
    Write-WhizbangHeader -ScriptName "PR Runner" -Params $headerParams -Estimate $estimateStr
    Write-AiLine "Lines marked with 🤖 require AI attention. Filter with: grep '🤖'" -ForegroundColor DarkGray
    Write-AiLine "Log: $effectiveLogFile" -ForegroundColor DarkGray
    if (-not $DirectScan -and -not $SkipSonar) {
        $expectedScanFolder = Join-Path ([System.IO.Path]::GetTempPath()) "whizbang-sonar-scan-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Write-Host "Scan folder: $expectedScanFolder" -ForegroundColor DarkGray
    }
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
    $script:failureTypes = @()

    # Total steps always includes all steps (skipped ones still show in output)
    $script:totalSteps = 10  # Scan Folder, Sonar Container, Format, Sonar Ready, Build, Unit Tests, Integration, Coverage Report, Sonar Analysis, Coverage Threshold

    # Load per-step estimates from history (full stats: avg, stddev, p85)
    $stepHistoryFile = Join-Path $originalRepoRoot "logs" "pr-steps.jsonl"
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
        $pct = [math]::Min(100, [math]::Round(($script:stepNumber / $script:totalSteps) * 100))

        $timingStr = Format-StepTiming -StepName $Name

        # Calculate total estimated time from all step P85 estimates
        $script:totalEstSec = 0
        foreach ($s in $script:stepStats.Values) { $script:totalEstSec += $s.P85 }
        $totalEstSec = $script:totalEstSec
        $elapsed = ([DateTime]::UtcNow - $startTime).TotalSeconds

        # Update step-based progress bar
        Write-Progress -Id 0 -Activity "Preparing PR" -Status "Step $($script:stepNumber)/$($script:totalSteps): $Name" -PercentComplete $pct

        $stepStart = [DateTime]::UtcNow
        $stepLabel = "  ▶ [$($script:stepNumber)/$($script:totalSteps)] $Name... $timingStr"
        if ($ShowOutput) {
            Write-Host $stepLabel -ForegroundColor Cyan
        } else {
            Write-Host $stepLabel -ForegroundColor Cyan -NoNewline
        }

        # Step estimate for per-step progress
        $script:stepEstSec = if ($script:stepStats.ContainsKey($Name)) { $script:stepStats[$Name].P85 } else { 0 }
        $stepEstSec = $script:stepEstSec
        $script:currentStepName = $Name
        $script:stepStart = $stepStart

        try {
            $result = & $Action
            $stepDuration = ([DateTime]::UtcNow - $stepStart).TotalSeconds

            Update-StepProgress
            Write-Progress -Id 2 -Activity "x" -Completed -ErrorAction SilentlyContinue

            if ($result.ExitCode -ne 0) {
                $prefix = if ($ShowOutput) { "  ▶ $Name..." } else { "" }
                Write-AiLine "$prefix ❌ Failed ($(Format-Duration -Seconds $stepDuration))" -ForegroundColor Red
                $script:steps += @{ name = $Name; status = "failed"; duration_s = [math]::Round($stepDuration, 1); details = $result.Details }
                $script:overallPassed = $false
                if (-not ($script:failureTypes -contains $FailureType)) { $script:failureTypes += $FailureType }
                return $false
            }
            else {
                $prefix = if ($ShowOutput) { "  ▶ $Name..." } else { "" }
                Write-AiLine "$prefix ✅ Passed ($(Format-Duration -Seconds $stepDuration))" -ForegroundColor Green
                $script:steps += @{ name = $Name; status = "passed"; duration_s = [math]::Round($stepDuration, 1); details = $result.Details }
                return $true
            }
        }
        catch {
            $stepDuration = ([DateTime]::UtcNow - $stepStart).TotalSeconds
            $prefix = if ($ShowOutput) { "  ▶ $Name..." } else { "" }
            Write-AiLine "$prefix ❌ Error ($(Format-Duration -Seconds $stepDuration))" -ForegroundColor Red
            Write-AiLine "    $_" -ForegroundColor Red
            $script:steps += @{ name = $Name; status = "error"; duration_s = [math]::Round($stepDuration, 1); details = $_.ToString() }
            $script:overallPassed = $false
            return $false
        }
        finally {
            Write-Progress -Id 2 -Activity "x" -Completed -ErrorAction SilentlyContinue
            Update-StepProgress
        }
    }

    # Update all time-based progress bars (called from polling loops)
    function script:Update-StepProgress {
        if (-not $script:stepStart) { return }
        $elapsed = ([DateTime]::UtcNow - $startTime).TotalSeconds
        $stepElapsed = ([DateTime]::UtcNow - $script:stepStart).TotalSeconds

        # Bar 1: Total estimated time (only when we have estimates)
        if ($script:totalEstSec -gt 0) {
            $timePct = [math]::Min(100, [math]::Round(($elapsed / $script:totalEstSec) * 100))
            $remaining = [math]::Max(0, $script:totalEstSec - $elapsed)
            Write-Progress -Id 1 -ParentId 0 -Activity "Total: $(Format-Duration -Seconds $elapsed) elapsed" -Status "~$(Format-Duration -Seconds $remaining) remaining" -PercentComplete $timePct
        } else {
            # No total estimate — show elapsed only (PercentComplete 0 ensures bar renders)
            Write-Progress -Id 1 -ParentId 0 -Activity "Total: $(Format-Duration -Seconds $elapsed) elapsed" -Status "(no overall estimate)" -PercentComplete 0
        }

        # Bar 2: Per-step progress (ALWAYS rendered — PercentComplete 0 when no estimate)
        if ($script:stepEstSec -gt 0) {
            $stepPct = [math]::Min(100, [math]::Round(($stepElapsed / $script:stepEstSec) * 100))
            $stepRemain = [math]::Max(0, $script:stepEstSec - $stepElapsed)
            Write-Progress -Id 2 -ParentId 0 -Activity "$($script:currentStepName): $(Format-Duration -Seconds $stepElapsed) elapsed" -Status "~$(Format-Duration -Seconds $stepRemain) remaining" -PercentComplete $stepPct
        } else {
            # No step estimate — show elapsed with 0% bar (guarantees visibility)
            Write-Progress -Id 2 -ParentId 0 -Activity "$($script:currentStepName): $(Format-Duration -Seconds $stepElapsed) elapsed" -Status "(no estimate — first run)" -PercentComplete 0
        }
    }

    # Run a dotnet command as a background process, polling with progress bar ticks
    function Invoke-DotnetWithProgress {
        param([string]$Arguments, [string]$WorkingDir)
        return Invoke-ProcessWithProgress -FilePath "dotnet" -Arguments $Arguments -WorkingDir $WorkingDir
    }

    # Append captured process output to the run log file (not shown on console)
    function Write-ToRunLog {
        param([string]$Label, [string]$Content)
        if ($Content -and $effectiveLogFile) {
            $separator = "`n--- [$Label] $(Get-Date -Format 'HH:mm:ss') ---`n"
            Add-Content -Path $effectiveLogFile -Value "$separator$Content" -ErrorAction SilentlyContinue
        }
    }

    # Run any process as background with progress bar polling; returns exit code
    function Invoke-ProcessWithProgress {
        param([string]$FilePath, [string]$Arguments, [string]$WorkingDir)
        $outFile = [System.IO.Path]::GetTempFileName()
        $errFile = [System.IO.Path]::GetTempFileName()
        $proc = Start-Process -FilePath $FilePath -ArgumentList $Arguments -WorkingDirectory $WorkingDir -NoNewWindow -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        while (-not $proc.HasExited) {
            Update-StepProgress
            Start-Sleep -Milliseconds 2000
        }
        $proc.WaitForExit()
        Write-ToRunLog -Label "$FilePath $Arguments" -Content (Get-Content $outFile -Raw -ErrorAction SilentlyContinue)
        Write-ToRunLog -Label "$FilePath $Arguments (stderr)" -Content (Get-Content $errFile -Raw -ErrorAction SilentlyContinue)
        Remove-Item $outFile, $errFile -ErrorAction SilentlyContinue
        return $proc.ExitCode
    }

    # Run any process as background with progress bar polling; returns exit code + captured output
    function Invoke-ProcessWithProgressAndOutput {
        param([string]$FilePath, [string]$Arguments, [string]$WorkingDir)
        $outFile = [System.IO.Path]::GetTempFileName()
        $errFile = [System.IO.Path]::GetTempFileName()
        $proc = Start-Process -FilePath $FilePath -ArgumentList $Arguments -WorkingDirectory $WorkingDir -NoNewWindow -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        while (-not $proc.HasExited) {
            Update-StepProgress
            Start-Sleep -Milliseconds 2000
        }
        $proc.WaitForExit()
        $output = (Get-Content $outFile -Raw -ErrorAction SilentlyContinue)
        $errOutput = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue)
        Write-ToRunLog -Label "$FilePath $Arguments" -Content $output
        Write-ToRunLog -Label "$FilePath $Arguments (stderr)" -Content $errOutput
        $combined = "$output$errOutput"
        Remove-Item $outFile, $errFile -ErrorAction SilentlyContinue
        return @{ ExitCode = $proc.ExitCode; Output = $combined }
    }

    Write-Host ""
    Write-Host "  Preparing PR..." -ForegroundColor Cyan
    Write-Host ""

    # ── Sonar variables (shared across steps) ──
    $script:sonarStarted = $false
    ${script:sonarToken} = $null
    $sonarUrl = "http://localhost:9000"
    $sonarProjectKey = "whizbang-local"
    $script:sonarStartTime = [DateTime]::UtcNow
    $script:dockerAvailable = $null -ne (Get-Command docker -ErrorAction SilentlyContinue)
    $script:dockerOk = $false
    $script:sonarContainerStarted = $false

    # Step 1: Create scan folder for SonarQube Community Edition (does not support branch analysis)
    if ($DirectScan -or $SkipSonar) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Create Scan Folder... ⏭️ Skipped" -ForegroundColor DarkGray
        $script:steps += @{ name = "Create Scan Folder"; status = "skipped"; duration_s = 0 }
    } else {
        $null = Run-Step -Name "Create Scan Folder" -ShowOutput -Action {
            $script:scanFolder = New-SonarScanFolder -RepoRoot $repoRoot -BranchName $currentBranch
            $script:repoRoot = $script:scanFolder
            Write-Host "    Scan folder: $($script:scanFolder)" -ForegroundColor DarkGray

            return @{ ExitCode = 0; Details = $script:scanFolder }
        }
    }

    # Step 2: Start SonarQube container (non-blocking — boots while format runs)
    if ($SkipSonar) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Start SonarQube Container... ⏭️ Skipped" -ForegroundColor DarkGray
        $script:steps += @{ name = "Start SonarQube Container"; status = "skipped"; duration_s = 0 }
    } else {
        $null = Run-Step -Name "Start SonarQube Container" -Action {
            if ($script:dockerAvailable) {
                $dockerRunning = docker info 2>&1; $script:dockerOk = $LASTEXITCODE -eq 0
                if ($script:dockerOk) {
                    $composeFile = Join-Path $repoRoot "docker-compose.sonarqube.yml"
                    if (Test-Path $composeFile) {
                        Invoke-ProcessWithProgress -FilePath "docker" -Arguments "compose -f $composeFile up -d" -WorkingDir $repoRoot | Out-Null
                        $script:sonarContainerStarted = $true
                        $script:sonarStartTime = [DateTime]::UtcNow
                    }
                }
            }
            @{ ExitCode = 0; Details = $null }
        } | Out-Null
    }

    # Step 2: Format check (with AutoFix support) — runs while SonarQube boots
    if ($SkipFormat) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Format Check... ⏭️ Skipped $(Format-StepTiming 'Format Check')" -ForegroundColor DarkGray
        $script:steps += @{ name = "Format Check"; status = "skipped"; duration_s = 0 }
    } else {
        $continue = Run-Step -Name "Format Check" -FailureType "FormatFailure" -ShowOutput -Action {
            # Run format check with diagnostic verbosity to get file:line:rule details
            $fmtResult = Invoke-ProcessWithProgressAndOutput -FilePath "dotnet" -Arguments "format --verify-no-changes --verbosity diagnostic $repoRoot" -WorkingDir $repoRoot
            $exitCode = $fmtResult.ExitCode
            $formatOutput = $fmtResult.Output

            if ($exitCode -ne 0) {
                # Extract formatting violations: lines matching "path(line,col): severity ruleId: message"
                $violations = @($formatOutput -split "`n" |
                    Where-Object { $_ -match ":\s*(warning|error)\s+\w+\s*:" -or $_ -match "Formatted code file" } |
                    ForEach-Object { $_.Trim() } |
                    Where-Object { $_ })

                if ($violations.Count -gt 0) {
                    Write-AiLine "    Formatting issues:" -ForegroundColor Yellow
                    foreach ($v in $violations | Select-Object -First 25) {
                        Write-AiLine "      $v" -ForegroundColor Gray
                    }
                    if ($violations.Count -gt 25) {
                        Write-Host "      ... and $($violations.Count - 25) more" -ForegroundColor DarkGray
                    }
                } else {
                    # Fallback: show which files would be changed
                    $changedFiles = @($formatOutput -split "`n" | Where-Object { $_ -match "\.cs" } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -First 20)
                    if ($changedFiles.Count -gt 0) {
                        Write-AiLine "    Files with formatting issues:" -ForegroundColor Yellow
                        foreach ($f in $changedFiles) {
                            Write-AiLine "      - $f" -ForegroundColor Gray
                        }
                    }
                }

                if ($AutoFix) {
                    Write-Host ""
                    Write-Host "    AutoFix: Running dotnet format..." -ForegroundColor Yellow
                    $exitCode = Invoke-DotnetWithProgress -Arguments "format" -WorkingDir $repoRoot
                    Write-Host "    AutoFix: Re-checking..." -ForegroundColor Yellow
                    $exitCode = Invoke-DotnetWithProgress -Arguments "format --verify-no-changes $repoRoot" -WorkingDir $repoRoot
                    if ($exitCode -eq 0) {
                        Write-AiLine "    AutoFix: ✅ All formatting issues resolved" -ForegroundColor Green
                    }
                }
            }
            @{ ExitCode = $exitCode; Details = $null }
        }
        if (-not $continue -and $FailFast) { return @{ Passed = $false; Steps = $script:steps } }
    }

    # Step 3: Wait for SonarQube ready + sonarscanner begin (before Build so scanner captures it)
    if ($SkipSonar -or -not $script:sonarContainerStarted) {
        if (-not $SkipSonar) {
            $script:stepNumber++
            Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] SonarQube Ready... ⏭️ Skipped (container not started)" -ForegroundColor DarkGray
            $script:steps += @{ name = "SonarQube Ready"; status = "skipped"; duration_s = 0 }
        } else {
            $script:stepNumber++
            Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] SonarQube Ready... ⏭️ Skipped" -ForegroundColor DarkGray
            $script:steps += @{ name = "SonarQube Ready"; status = "skipped"; duration_s = 0 }
        }
    } else {
        $continue = Run-Step -Name "SonarQube Ready" -Action {
            $sonarTimeout = 300  # 5 minutes from container start
            $sonarReady = $false
            while (([DateTime]::UtcNow - $script:sonarStartTime).TotalSeconds -lt $sonarTimeout) {
                try {
                    $resp = Invoke-RestMethod -Uri "$sonarUrl/api/system/status" -TimeoutSec 3 -ErrorAction SilentlyContinue
                    if ($resp.status -eq "UP") { $sonarReady = $true; break }
                } catch { }
                Update-StepProgress
                Start-Sleep -Seconds 5
            }

            if (-not $sonarReady) {
                Write-AiLine "    SonarQube did not become ready within timeout" -ForegroundColor Red
                @{ ExitCode = 1; Details = "SonarQube timeout" }
                return
            }

            # Ensure token exists
            $tokenFile = Join-Path $repoRoot ".sonarqube-local.token"
            if (-not (Test-Path $tokenFile)) {
                try {
                    $authHeader = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("admin:admin"))
                    $tokenResponse = Invoke-RestMethod -Uri "$sonarUrl/api/user_tokens/generate" -Method Post -Headers @{ Authorization = "Basic $authHeader" } -Body @{ name = "whizbang-local-$(Get-Date -Format 'yyyyMMdd-HHmmss')" } -ErrorAction Stop
                    $tokenResponse.token | Out-File -FilePath $tokenFile -NoNewline -Encoding utf8
                } catch {
                    Write-AiLine "    ⚠️ Could not generate token: $_" -ForegroundColor Yellow
                }
            }

            if (Test-Path $tokenFile) {
                ${script:sonarToken} = (Get-Content $tokenFile -Raw).Trim()

                # Load exclusions
                # Load ALL sonar.config properties (exclusions, issue ignores, coverage, CPD, etc.)
                $sonarConfigArgs = @()
                $exclusionConfig = Join-Path $repoRoot "sonar.config"
                if (Test-Path $exclusionConfig) {
                    Get-Content $exclusionConfig | Where-Object { $_ -match "^[^#].*=" } | ForEach-Object {
                        $parts = $_ -split "=", 2
                        $key = $parts[0].Trim()
                        $value = $parts[1].Trim()
                        if ($key -and $value) {
                            $sonarConfigArgs += "/d:${key}=${value}"
                        }
                    }
                }

                # Run sonarscanner begin
                Push-Location $repoRoot
                $sonarCoverageReportPath = Join-Path $repoRoot "coverage" "sonarqube" "SonarQube.xml"
                $beginArgs = @("begin", "/k:$sonarProjectKey", "/d:sonar.login=${script:sonarToken}", "/d:sonar.host.url=$sonarUrl", "/d:sonar.coverageReportPaths=$sonarCoverageReportPath")
                # sonar.branch.name requires Developer Edition — only set in DirectScan mode
                if ($DirectScan) {
                    $directBranch = git rev-parse --abbrev-ref HEAD 2>$null
                    if ($directBranch) { $beginArgs += "/d:sonar.branch.name=$directBranch" }
                }
                $beginArgs += $sonarConfigArgs
                # Join args with space, quoting values that contain commas or wildcards
                $joinedArgs = ($beginArgs | ForEach-Object {
                    if ($_ -match '[,\*]' -and $_ -match '^/d:') {
                        $eqIdx = $_.IndexOf('=')
                        if ($eqIdx -gt 0) {
                            $_.Substring(0, $eqIdx + 1) + '"' + $_.Substring($eqIdx + 1) + '"'
                        } else { $_ }
                    } else { $_ }
                }) -join " "
                $beginResult = Invoke-ProcessWithProgressAndOutput -FilePath "dotnet-sonarscanner" -Arguments $joinedArgs -WorkingDir $repoRoot
                if ($beginResult.ExitCode -eq 0) {
                    $script:sonarStarted = $true
                } else {
                    Write-AiLine "    SonarScanner begin failed:" -ForegroundColor Red
                    $beginResult.Output -split "`n" | Select-Object -Last 5 | ForEach-Object {
                        Write-Host "      $_" -ForegroundColor DarkGray
                    }
                }
                Pop-Location
            }

            @{ ExitCode = $(if ($script:sonarStarted) { 0 } else { 0 }); Details = $null }
        }
        if (-not $continue -and $FailFast) { return @{ Passed = $false; Steps = $script:steps } }
    }

    # Step 4: Build (captured by SonarScanner if started)
    if ($SkipBuild) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Build... ⏭️ Skipped $(Format-StepTiming 'Build')" -ForegroundColor DarkGray
        $script:steps += @{ name = "Build"; status = "skipped"; duration_s = 0 }
    } else {
        $continue = Run-Step -Name "Build" -FailureType "BuildFailure" -ShowOutput -Action {
            $buildResult = Invoke-ProcessWithProgressAndOutput -FilePath "dotnet" -Arguments "build --verbosity minimal" -WorkingDir $repoRoot
            $exitCode = $buildResult.ExitCode
            if ($exitCode -ne 0) {
                # Extract error lines from build output
                $errors = @($buildResult.Output -split "`n" |
                    Where-Object { $_ -match ":\s*error\s+" } |
                    ForEach-Object { $_.Trim() } |
                    Where-Object { $_ })
                if ($errors.Count -gt 0) {
                    Write-AiLine "    Build errors:" -ForegroundColor Red
                    foreach ($e in $errors | Select-Object -First 30) {
                        Write-AiLine "      $e" -ForegroundColor Gray
                    }
                    if ($errors.Count -gt 30) {
                        Write-Host "      ... and $($errors.Count - 30) more" -ForegroundColor DarkGray
                    }
                } else {
                    # No structured errors found — show last lines of output
                    $tail = @($buildResult.Output -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 20)
                    foreach ($line in $tail) {
                        Write-Host "      $($line.Trim())" -ForegroundColor Gray
                    }
                }
            }
            @{ ExitCode = $exitCode; Details = $null }
        }
        if (-not $continue -and $FailFast) { return @{ Passed = $false; Steps = $script:steps } }
    }

    # Step 5: Unit tests (with coverage collection)
    $script:coveragePct = $null
    if ($SkipUnitTests) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Unit Tests... ⏭️ Skipped $(Format-StepTiming 'Unit Tests')" -ForegroundColor DarkGray
        $script:steps += @{ name = "Unit Tests"; status = "skipped"; duration_s = 0 }
    } else {
        $unitTestLogFile = Join-Path $originalRepoRoot "logs" "pr-unit-tests.log"
        $continue = Run-Step -Name "Unit Tests" -FailureType "TestFailure" -ShowOutput -Action {
            $testScript = Join-Path $PSScriptRoot "Run-Tests.ps1"
            $parentEpoch = [DateTimeOffset]::new($startTime, [TimeSpan]::Zero).ToUnixTimeSeconds()
            & $testScript -Mode AiUnit -Coverage -FailFast -NoBuild -NoHeader -NoReport -LogFile $unitTestLogFile -LogMode All -ParentStartTime $parentEpoch -ParentTotalEstSec $script:totalEstSec -StepEstSec $script:stepEstSec -ParentStepNumber $script:stepNumber -ParentTotalSteps $script:totalSteps
            $exitCode = $LASTEXITCODE
            if ($exitCode -ne 0) {
                Write-AiLine "    Full output: $unitTestLogFile" -ForegroundColor DarkYellow
            }
            @{ ExitCode = $exitCode; Details = $null }
        }
        if (-not $continue -and $FailFast) { return @{ Passed = $false; Steps = $script:steps } }
    }

    # Step 6: Integration tests (with coverage collection)
    if ($SkipIntegration) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Integration Tests... ⏭️ Skipped $(Format-StepTiming 'Integration Tests')" -ForegroundColor DarkGray
        $script:steps += @{ name = "Integration Tests"; status = "skipped"; duration_s = 0 }
    } else {
        $integrationTestLogFile = Join-Path $originalRepoRoot "logs" "pr-integration-tests.log"
        $continue = Run-Step -Name "Integration Tests" -FailureType "TestFailure" -ShowOutput -Action {
            $testScript = Join-Path $PSScriptRoot "Run-Tests.ps1"
            $parentEpoch = [DateTimeOffset]::new($startTime, [TimeSpan]::Zero).ToUnixTimeSeconds()
            & $testScript -Mode AiIntegrations -Coverage -FailFast -NoBuild -NoHeader -NoReport -LogFile $integrationTestLogFile -LogMode All -ParentStartTime $parentEpoch -ParentTotalEstSec $script:totalEstSec -StepEstSec $script:stepEstSec -ParentStepNumber $script:stepNumber -ParentTotalSteps $script:totalSteps
            $exitCode = $LASTEXITCODE
            if ($exitCode -ne 0) {
                Write-AiLine "    Full output: $integrationTestLogFile" -ForegroundColor DarkYellow
            }
            @{ ExitCode = $exitCode; Details = $null }
        }
        if (-not $continue -and $FailFast) { return @{ Passed = $false; Steps = $script:steps } }
    }

    # Step 7: Coverage report (combined from unit + integration test coverage)
    # Matches CI pipeline: merge all cobertura XMLs, generate HTML + TextSummary + SonarQube format
    # MUST run before SonarQube Analysis so sonarscanner end can pick up the coverage data
    if (-not $SkipCoverage -and (-not $SkipUnitTests -or -not $SkipIntegration)) {
        $continue = Run-Step -Name "Coverage Report" -FailureType "BuildFailure" -Action {
            # Coverage files are in the original repo (tests run there, not in the scan folder)
            $coberturaFiles = @(Get-ChildItem -Path (Join-Path $originalRepoRoot "tests") -Filter "*.cobertura.xml" -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match "bin[/\\]Debug[/\\]net10\.0[/\\]TestResults" })

            if ($coberturaFiles.Count -gt 0) {
                $reportDir = Join-Path $originalRepoRoot "coverage-report"
                $sonarDir = Join-Path $originalRepoRoot "coverage" "sonarqube"
                $reports = ($coberturaFiles | ForEach-Object { $_.FullName }) -join ";"
                $fileFilters = "-*.g.cs;-**/.whizbang-generated/*"

                # Generate HTML + TextSummary + JsonSummary for human consumption
                Invoke-ProcessWithProgress -FilePath "reportgenerator" -Arguments "`"-reports:$reports`" `"-targetdir:$reportDir`" `"-reporttypes:Html;TextSummary;JsonSummary`" `"-filefilters:$fileFilters`"" -WorkingDir $originalRepoRoot | Out-Null

                # Generate SonarQube format for local SonarQube ingestion (matches CI pipeline)
                Invoke-ProcessWithProgress -FilePath "reportgenerator" -Arguments "`"-reports:$reports`" `"-targetdir:$sonarDir`" `"-reporttypes:SonarQube`" `"-filefilters:$fileFilters`"" -WorkingDir $originalRepoRoot | Out-Null

                # Copy SonarQube coverage report to scan folder so sonarscanner end can pick it up
                if ($script:scanFolder -and (Test-Path $sonarDir)) {
                    $scanSonarDir = Join-Path $script:scanFolder "coverage" "sonarqube"
                    New-Item -ItemType Directory -Path $scanSonarDir -Force | Out-Null
                    Copy-Item -Path (Join-Path $sonarDir "*") -Destination $scanSonarDir -Force
                }

                $summaryFile = Join-Path $reportDir "Summary.txt"
                if (Test-Path $summaryFile) {
                    $summaryText = Get-Content $summaryFile -Raw
                    if ($summaryText -match "Line coverage:\s+([\d.]+)%") { $script:coveragePct = [double]$Matches[1] }
                    $totalCovered = 0; $totalLines = 0
                    if ($summaryText -match "Covered lines:\s+(\d+)") { $totalCovered = [int]$Matches[1] }
                    if ($summaryText -match "Coverable lines:\s+(\d+)") { $totalLines = [int]$Matches[1] }
                    Write-AiLine "    Coverage: $($script:coveragePct)% ($totalCovered / $totalLines lines)" -ForegroundColor Cyan
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

    # Step 8: Sonar end (finish analysis started before build)
    # Runs AFTER Coverage Report so sonarscanner end picks up coverage/sonarqube/SonarQube.xml
    if ($SkipSonar) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] SonarQube Analysis... ⏭️ Skipped $(Format-StepTiming 'SonarQube Analysis')" -ForegroundColor DarkGray
        $script:steps += @{ name = "SonarQube Analysis"; status = "skipped"; duration_s = 0 }
    } elseif ($script:sonarStarted) {
        $continue = Run-Step -Name "SonarQube Analysis" -FailureType "SonarFailure" -Action {
            Push-Location $repoRoot
            try {
                $sonarResult = Invoke-ProcessWithProgressAndOutput -FilePath "dotnet-sonarscanner" -Arguments "end /d:sonar.login=`"${script:sonarToken}`"" -WorkingDir $repoRoot
                $exitCode = $sonarResult.ExitCode
                $sonarEndOutput = $sonarResult.Output
                if ($exitCode -ne 0) {
                    Write-AiLine "    sonarscanner end failed:" -ForegroundColor Red
                    $sonarEndOutput -split "`n" | Select-Object -Last 10 | ForEach-Object {
                        Write-Host "      $_" -ForegroundColor DarkGray
                    }
                }

                # Query results from local SonarQube
                if ($exitCode -eq 0) {
                    Start-Sleep -Seconds 5  # Wait for SonarQube to process
                    try {
                        # SonarQube 9.9 LTS uses token as username with empty password for Basic auth
                        $authBytes = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${script:sonarToken}:"))
                        $authHeader = @{ Authorization = "Basic $authBytes" }

                        # Quality gate
                        $qg = Invoke-RestMethod -Uri "$sonarUrl/api/qualitygates/project_status?projectKey=$sonarProjectKey" -Headers $authHeader -ErrorAction Stop
                        if ($qg.projectStatus.status -eq "OK") {
                            Write-AiLine "    Quality Gate: ✅ Passed" -ForegroundColor Green
                        } else {
                            Write-AiLine "    Quality Gate: ❌ $($qg.projectStatus.status)" -ForegroundColor Red
                            $exitCode = 1
                        }

                        # Measures: coverage, duplication, issues
                        $metrics = "coverage,duplicated_lines_density,bugs,vulnerabilities,code_smells,ncloc"
                        $measures = Invoke-RestMethod -Uri "$sonarUrl/api/measures/component?component=$sonarProjectKey&metricKeys=$metrics" -Headers $authHeader -ErrorAction Stop
                        $mValues = @{}
                        foreach ($m in $measures.component.measures) { $mValues[$m.metric] = $m.value }

                        if ($mValues.ContainsKey("coverage")) {
                            Write-AiLine "    Coverage: $($mValues['coverage'])%" -ForegroundColor Cyan
                        }
                        if ($mValues.ContainsKey("duplicated_lines_density")) {
                            Write-AiLine "    Duplication: $($mValues['duplicated_lines_density'])%" -ForegroundColor $(if ([double]$mValues['duplicated_lines_density'] -gt 3) { "Yellow" } else { "Green" })
                        }
                        $bugCount = if ($mValues.ContainsKey("bugs")) { $mValues['bugs'] } else { "0" }
                        $vulnCount = if ($mValues.ContainsKey("vulnerabilities")) { $mValues['vulnerabilities'] } else { "0" }
                        $smellCount = if ($mValues.ContainsKey("code_smells")) { $mValues['code_smells'] } else { "0" }
                        Write-AiLine "    Issues: $bugCount bugs, $vulnCount vulnerabilities, $smellCount code smells" -ForegroundColor $(if ([int]$bugCount -gt 0 -or [int]$vulnCount -gt 0) { "Red" } else { "Gray" })

                        # Top issues with file + line numbers
                        $issues = Invoke-RestMethod -Uri "$sonarUrl/api/issues/search?projectKeys=$sonarProjectKey&ps=20&statuses=OPEN,CONFIRMED&s=SEVERITY&asc=false" -Headers $authHeader -ErrorAction Stop
                        if (@($issues.issues).Count -gt 0) {
                            Write-Host ""
                            Write-AiLine "    Top Issues:" -ForegroundColor Yellow
                            foreach ($issue in $issues.issues | Select-Object -First 15) {
                                $severityValue = if ($issue.PSObject.Properties['severity']) { $issue.severity } else { $null }
                                $severity = switch ($severityValue) {
                                    "BLOCKER" { "🔴" }
                                    "CRITICAL" { "🟠" }
                                    "MAJOR" { "🟡" }
                                    "MINOR" { "🔵" }
                                    default { "⚪" }
                                }
                                $component = if ($issue.PSObject.Properties['component']) { $issue.component -replace "^${sonarProjectKey}:", "" } else { "(unknown)" }
                                $line = if ($issue.PSObject.Properties['line']) { ":$($issue.line)" } else { "" }
                                $rule = if ($issue.PSObject.Properties['rule']) { $issue.rule } else { "unknown" }
                                $message = if ($issue.PSObject.Properties['message']) { $issue.message } else { "(no message)" }
                                Write-AiLine "      $severity [$rule] $message" -ForegroundColor Gray
                                Write-AiLine "         $component$line" -ForegroundColor DarkGray
                            }
                            if ($issues.total -gt 15) {
                                Write-Host "      ... and $($issues.total - 15) more issues" -ForegroundColor DarkGray
                            }
                        }

                        Write-Host ""
                        Write-Host "    Dashboard: $sonarUrl/dashboard?id=$sonarProjectKey" -ForegroundColor DarkCyan
                    } catch {
                        Write-Host "    Warning: Could not fetch SonarQube results: $_" -ForegroundColor Yellow
                    }
                }
                @{ ExitCode = $exitCode; Details = $null }
            }
            finally { Pop-Location }
        }
        if (-not $continue -and $FailFast) { return @{ Passed = $false; Steps = $script:steps } }
    } else {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] SonarQube Analysis... ⏭️ Skipped (no token configured)" -ForegroundColor DarkGray
        $script:steps += @{ name = "SonarQube Analysis"; status = "skipped"; duration_s = 0 }
    }

    # Step 9: Coverage threshold
    if ($SkipCoverage) {
        $script:stepNumber++
        Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Coverage Threshold... ⏭️ Skipped $(Format-StepTiming 'Coverage Threshold')" -ForegroundColor DarkGray
        $script:steps += @{ name = "Coverage Threshold"; status = "skipped"; duration_s = 0 }
    } else {
        $script:stepNumber++
        $covTimingStr = Format-StepTiming -StepName "Coverage Threshold"
        Write-Progress -Id 0 -Activity "Preparing PR" -Status "Step $($script:stepNumber)/$($script:totalSteps): Coverage Threshold" -PercentComplete 100
        if ($null -ne $script:coveragePct) {
            if ($script:coveragePct -lt $CoverageThreshold) {
                Write-AiLine "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Coverage Threshold... ❌ $($script:coveragePct)% < ${CoverageThreshold}% $covTimingStr" -ForegroundColor Red
                $script:steps += @{ name = "Coverage Threshold"; status = "failed"; duration_s = 0; details = "Coverage $($script:coveragePct)% below threshold ${CoverageThreshold}%" }
                $script:overallPassed = $false
            }
            else {
                Write-AiLine "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Coverage Threshold... ✅ $($script:coveragePct)% >= ${CoverageThreshold}% $covTimingStr" -ForegroundColor Green
                $script:steps += @{ name = "Coverage Threshold"; status = "passed"; duration_s = 0; details = "Coverage $($script:coveragePct)%" }
            }
        } else {
            Write-Host "  ▶ [$($script:stepNumber)/$($script:totalSteps)] Coverage Threshold... ⏭️ Skipped (no coverage data) $covTimingStr" -ForegroundColor DarkGray
            $script:steps += @{ name = "Coverage Threshold"; status = "skipped"; duration_s = 0 }
        }
    }

    # Complete the progress bars
    Write-Progress -Id 1 -Activity "Time" -Completed
    Write-Progress -Id 0 -Activity "Preparing PR" -Completed

    # Save per-step history for future estimation (always to original repo)
    $stepHistoryFile = Join-Path $originalRepoRoot "logs" "pr-steps.jsonl"
    Write-HistoryEntry -FilePath $stepHistoryFile -Entry @{
        steps = $script:steps
        total_duration_s = [math]::Round(([DateTime]::UtcNow - $startTime).TotalSeconds, 1)
        passed = $script:overallPassed
    }

    # Show AI instructions once at the end (deduplicated by failure type)
    if ($script:failureTypes.Count -gt 0) {
        Write-Host ""
        foreach ($ft in ($script:failureTypes | Select-Object -Unique)) {
            Write-AiInstructions -Type $ft
        }
    }

    return @{ Passed = $script:overallPassed; Steps = $script:steps; CoveragePct = $script:coveragePct }
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

        $checks = @($prData.statusCheckRollup)

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

    # Stop SonarQube container (unless -PersistContainer)
    if (-not $SkipSonar -and -not $PersistContainer) {
        $composeFile = Join-Path $originalRepoRoot "docker-compose.sonarqube.yml"
        if (Test-Path $composeFile) {
            Write-Host "  Stopping SonarQube container..." -ForegroundColor DarkGray
            docker compose -f $composeFile down 2>&1 | Out-Null
        }
    }

    # Clean up scan folder
    if ($script:scanFolder -and -not $KeepScanFolder -and (Test-Path $script:scanFolder)) {
        Write-Host "  Cleaning up scan folder..." -ForegroundColor DarkGray
        Remove-SonarScanFolder -ScanFolder $script:scanFolder
    } elseif ($script:scanFolder -and $KeepScanFolder) {
        Write-Host "  Scan folder preserved: $($script:scanFolder)" -ForegroundColor DarkGray
    }

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

Write-Host "Log: $effectiveLogFile" -ForegroundColor DarkGray
Write-Host ""
exit $(if ($result.Passed) { 0 } else { 1 })
