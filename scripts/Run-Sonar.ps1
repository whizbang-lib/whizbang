#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Runs SonarCloud analysis with AI-optimized output, JSON mode, and history tracking.

.DESCRIPTION
    Wrapper around Run-SonarAnalysis.ps1 that adds PR-readiness features:
    - Branded Whizbang header
    - AI-optimized sparse output (-Mode Ai)
    - JSON output for machine consumption (-OutputFormat Json)
    - Tee logging with independent console/file verbosity
    - Run history and estimation (logs/sonar-runs.jsonl)
    - SonarCloud API calls for quality gate status, issues, coverage delta
    - AI instructions on failure

    Parameters follow the same conventions as Run-Tests.ps1 and Run-PR.ps1.

.PARAMETER Token
    SonarCloud authentication token. If not provided, delegates to Run-SonarAnalysis.ps1
    which checks SONAR_TOKEN env var or .sonarcloud.token file.

.PARAMETER SkipBuild
    Skip the build step and only run the SonarCloud end analysis.

.PARAMETER Mode
    Output verbosity. All = verbose, Ai = sparse/token-efficient.

.PARAMETER OutputFormat
    Output format: Text (human-readable) or Json (machine-readable).

.PARAMETER LogFile
    Path to write log output. If empty, no file logging.

.PARAMETER LogMode
    Log file verbosity. Defaults to -Mode if not specified.

.PARAMETER FailFast
    Exit immediately on failure.

.PARAMETER PersistContainer
    Keep SonarQube Docker container running after script ends. By default the container
    is stopped when the script completes.

.EXAMPLE
    .\Run-Sonar.ps1
    Run analysis with verbose output

.EXAMPLE
    .\Run-Sonar.ps1 -Mode Ai -OutputFormat Json
    Run analysis with sparse output and JSON result

.EXAMPLE
    .\Run-Sonar.ps1 -Mode Ai -LogFile logs/sonar.log -LogMode All
    Sparse console output, verbose log file
#>

[CmdletBinding()]
param(
    [string]$Token = "",

    [switch]$SkipBuild,

    [ValidateSet("All", "Ai")]
    [string]$Mode = "All",

    [ValidateSet("Text", "Json")]
    [string]$OutputFormat = "Text",

    [string]$LogFile = "",

    [ValidateSet("All", "Ai")]
    [string]$LogMode = "",

    [switch]$FailFast,

    [switch]$NoHeader,  # Suppress the branded header (used when called from Run-PR.ps1)

    [switch]$CleanLogs,     # Remove log files and coverage reports before running
    [switch]$CleanMetrics,  # Remove JSONL history/metrics files before running
    [switch]$CleanAll,      # Remove all logs, metrics, and reports before running

    [switch]$PersistContainer,  # Keep SonarQube Docker container running after script ends

    [switch]$KeepScanFolder,   # Keep the temp scan folder after script ends (for debugging)
    [switch]$DirectScan        # Skip temp folder — scan real repo directly (requires Developer Edition for branch support)
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Import shared module
Import-Module (Join-Path $PSScriptRoot "lib" "PR-Readiness-Common.psm1") -Force

$useAiOutput = $Mode -eq "Ai"

# Handle cleanup flags
$sonarRepoRoot = Split-Path -Parent $PSScriptRoot
if ($CleanAll) {
    Write-Host "Cleaning all logs, metrics, and reports..." -ForegroundColor Yellow
    Invoke-CleanAll -RepoRoot $sonarRepoRoot
} elseif ($CleanLogs -or $CleanMetrics) {
    if ($CleanLogs) { Invoke-CleanLogs -RepoRoot $sonarRepoRoot }
    if ($CleanMetrics) { Invoke-CleanMetrics -RepoRoot $sonarRepoRoot }
}

# Indentation: when called as child from Run-PR.ps1, indent all output
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
        Microsoft.PowerShell.Utility\Write-Host "    ${Object}" @params
    }
    Set-Alias -Name Write-Host -Value Write-IndentedHost -Scope Local
}

# Initialize tee logging
if ($LogFile) {
    $effectiveLogMode = if ($LogMode) { $LogMode } else { $Mode }
    Initialize-TeeLogging -LogFile $LogFile -ConsoleMode $Mode -LogMode $effectiveLogMode
}

# Track timing
$startTime = [DateTime]::UtcNow

# Show estimation from history
$historyFile = Join-Path $PSScriptRoot ".." "logs" "sonar-runs.jsonl"
$estimate = Get-RunEstimate -FilePath $historyFile
$estimateStr = if ($estimate) { $estimate.Formatted } else { "" }

# Branded header (suppressed when called from Run-PR.ps1)
if (-not $NoHeader) {
    $headerParams = @{ Mode = $Mode }
    if ($SkipBuild) { $headerParams["SkipBuild"] = "On" }
    Write-WhizbangHeader -ScriptName "Sonar Runner" -Params $headerParams -Estimate $estimateStr
}

# Local SonarQube configuration (Docker-based, "Sonar way" defaults)
$ProjectKey = "whizbang-local"
$SonarHostUrl = "http://localhost:9000"
$sonarRepoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $sonarRepoRoot "docker-compose.sonarqube.yml"
$tokenFile = Join-Path $sonarRepoRoot ".sonarqube-local.token"

# ── Preflight checks ────────────────────────────────────────────────────────

# 1. Check Docker is installed
$dockerCmd = Get-Command docker -ErrorAction SilentlyContinue
if (-not $dockerCmd) {
    Write-Host "❌ Docker is not installed or not in PATH." -ForegroundColor Red
    Write-Host ""
    Write-Host "  SonarQube analysis requires Docker. Install it from:" -ForegroundColor Yellow
    Write-Host "    macOS:   https://docs.docker.com/desktop/install/mac-install/" -ForegroundColor Gray
    Write-Host "    Windows: https://docs.docker.com/desktop/install/windows-install/" -ForegroundColor Gray
    Write-Host "    Linux:   https://docs.docker.com/engine/install/" -ForegroundColor Gray
    Write-Host ""
    if ($OutputFormat -eq "Json") {
        ConvertTo-JsonResult -Result @{ status = "skipped"; reason = "docker_not_installed" }
    }
    exit 1
}

# 2. Check Docker daemon is running
$dockerRunning = docker info 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Docker daemon is not running." -ForegroundColor Red
    Write-Host ""
    Write-Host "  Start Docker Desktop, then re-run this script." -ForegroundColor Yellow
    Write-Host ""
    if ($OutputFormat -eq "Json") {
        ConvertTo-JsonResult -Result @{ status = "skipped"; reason = "docker_not_running" }
    }
    exit 1
}

# 3. Check docker-compose file exists
if (-not (Test-Path $composeFile)) {
    Write-Host "❌ docker-compose.sonarqube.yml not found at: $composeFile" -ForegroundColor Red
    Write-Host ""
    Write-Host "  This file defines the local SonarQube container setup." -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

# 4. Check dotnet-sonarscanner is available
$scannerInstalled = dotnet tool list 2>$null | Select-String "dotnet-sonarscanner"
$scannerGlobal = dotnet tool list -g 2>$null | Select-String "dotnet-sonarscanner"
if (-not $scannerInstalled -and -not $scannerGlobal) {
    Write-Host "Installing dotnet-sonarscanner..." -ForegroundColor Yellow
    dotnet tool restore 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ Failed to restore dotnet-sonarscanner from tool manifest." -ForegroundColor Red
        Write-Host "  Try: dotnet tool install -g dotnet-sonarscanner" -ForegroundColor Gray
        exit 1
    }
}

# 5. Check if SonarQube container is running, start if not
$sonarContainer = docker ps --filter "name=whizbang-sonar" --format "{{.Status}}" 2>$null
if (-not $sonarContainer) {
    Write-Host "Starting SonarQube container (first run may take 1-2 minutes)..." -ForegroundColor Yellow
    docker compose -f $composeFile up -d 2>&1 | Out-Null

    # Wait for SonarQube to be ready
    Write-Host "Waiting for SonarQube to be ready..." -ForegroundColor Gray -NoNewline
    $maxWait = 180
    $waited = 0
    $ready = $false
    while ($waited -lt $maxWait) {
        try {
            $response = Invoke-RestMethod -Uri "$SonarHostUrl/api/system/status" -TimeoutSec 3 -ErrorAction SilentlyContinue
            if ($response.status -eq "UP") {
                $ready = $true
                break
            }
        } catch { }
        Start-Sleep -Seconds 5
        $waited += 5
        Write-Host "." -NoNewline
    }
    Write-Host ""

    if (-not $ready) {
        Write-Host "❌ SonarQube did not start within $maxWait seconds." -ForegroundColor Red
        Write-Host "  Check logs: docker compose -f $composeFile logs sonarqube" -ForegroundColor Gray
        exit 1
    }
    Write-Host "✅ SonarQube is ready" -ForegroundColor Green
} else {
    Write-Host "SonarQube container already running" -ForegroundColor Gray
}

# 6. Ensure we have a local token
if (-not (Test-Path $tokenFile)) {
    Write-Host "Generating SonarQube token..." -ForegroundColor Yellow
    try {
        $authHeader = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("admin:admin"))
        $tokenResponse = Invoke-RestMethod -Uri "$SonarHostUrl/api/user_tokens/generate" `
            -Method Post `
            -Headers @{ Authorization = "Basic $authHeader" } `
            -Body @{ name = "whizbang-local-$(Get-Date -Format 'yyyyMMdd-HHmmss')" } `
            -ErrorAction Stop
        $tokenResponse.token | Out-File -FilePath $tokenFile -NoNewline -Encoding utf8
        Write-Host "✅ Token generated and cached" -ForegroundColor Green
    } catch {
        Write-Host "⚠️  Could not auto-generate token (admin password may have been changed)" -ForegroundColor Yellow
        Write-Host "  Generate one manually at: $SonarHostUrl/account/security" -ForegroundColor Gray
        Write-Host "  Then save it to: $tokenFile" -ForegroundColor Gray
        exit 1
    }
}

# ── Run analysis ─────────────────────────────────────────────────────────────

$analysisScript = Join-Path $PSScriptRoot "Run-LocalSonarAnalysis.ps1"

$splatParams = @{ SkipDocker = $true }  # We already handled Docker above
if ($SkipBuild) { $splatParams["SkipTests"] = $true }
if ($KeepScanFolder) { $splatParams["KeepScanFolder"] = $true }
if ($DirectScan) { $splatParams["DirectScan"] = $true }

$analysisExitCode = 0
try {
    if ($useAiOutput) {
        Write-Host "Running SonarQube analysis..." -ForegroundColor Gray
        & $analysisScript @splatParams 2>&1 | ForEach-Object {
            $line = $_.ToString()
            if ($LogFile) {
                $line | Out-File -FilePath $LogFile -Append -Encoding utf8
            }
            if ($line -match "\[OK\]|\[ERR\]|\[INFO\].*Starting|\[INFO\].*Finishing|\[INFO\].*Dashboard|Analysis complete") {
                Write-Host "  $line" -ForegroundColor DarkGray
            }
        }
    }
    else {
        & $analysisScript @splatParams
    }
    $analysisExitCode = $LASTEXITCODE
}
catch {
    Write-Host "SonarQube analysis failed: $_" -ForegroundColor Red
    $analysisExitCode = 1
}

# Query local SonarQube API for quality gate status and metrics
$qualityGateStatus = $null
$newIssues = @()
$measures = @{}
$apiError = $null

# Read local token for API queries
$sonarToken = if (Test-Path $tokenFile) { (Get-Content $tokenFile -Raw).Trim() } else { $null }

if ($sonarToken -and $analysisExitCode -eq 0) {
    $authHeader = @{ Authorization = "Bearer $sonarToken" }

    # Wait a moment for SonarQube to process results
    Start-Sleep -Seconds 3

    try {
        # Quality gate status
        $qgResponse = Invoke-RestMethod -Uri "$SonarHostUrl/api/qualitygates/project_status?projectKey=$ProjectKey" `
            -Headers $authHeader -ErrorAction Stop
        $qualityGateStatus = $qgResponse.projectStatus.status

        # Issues
        $issuesResponse = Invoke-RestMethod -Uri "$SonarHostUrl/api/issues/search?projectKeys=$ProjectKey&ps=100&statuses=OPEN,CONFIRMED" `
            -Headers $authHeader -ErrorAction Stop
        $newIssues = @($issuesResponse.issues)

        # Measures
        $metricsKeys = "coverage,bugs,vulnerabilities,code_smells,ncloc"
        $measuresResponse = Invoke-RestMethod -Uri "$SonarHostUrl/api/measures/component?component=$ProjectKey&metricKeys=$metricsKeys" `
            -Headers $authHeader -ErrorAction Stop
        foreach ($m in $measuresResponse.component.measures) {
            $measures[$m.metric] = $m.value
        }
    }
    catch {
        $apiError = $_.Exception.Message
        if (-not $useAiOutput) {
            Write-Host "Warning: Could not fetch SonarQube API data: $apiError" -ForegroundColor Yellow
        }
    }
}

# Calculate duration
$duration = ([DateTime]::UtcNow - $startTime).TotalSeconds

# Determine overall status
$overallStatus = if ($analysisExitCode -ne 0) { "failed" }
    elseif ($qualityGateStatus -eq "ERROR") { "quality_gate_failed" }
    else { "passed" }
$hasErrors = $overallStatus -ne "passed"

# Format console summary
if ($OutputFormat -ne "Json") {
    Write-Host ""

    if ($qualityGateStatus) {
        $qgColor = if ($qualityGateStatus -eq "OK") { "Green" } else { "Red" }
        $qgIcon = if ($qualityGateStatus -eq "OK") { "✅" } else { "❌" }
        Write-Host "  Quality Gate: $qgIcon $qualityGateStatus" -ForegroundColor $qgColor
    }

    if ($newIssues.Count -gt 0) {
        $bugCount = @($newIssues | Where-Object { $_.type -eq "BUG" }).Count
        $vulnCount = @($newIssues | Where-Object { $_.type -eq "VULNERABILITY" }).Count
        $smellCount = @($newIssues | Where-Object { $_.type -eq "CODE_SMELL" }).Count
        $parts = @()
        if ($bugCount -gt 0) { $parts += "$bugCount bugs" }
        if ($vulnCount -gt 0) { $parts += "$vulnCount vulnerabilities" }
        if ($smellCount -gt 0) { $parts += "$smellCount code smells" }
        Write-Host "  New Issues: $($newIssues.Count) ($($parts -join ', '))" -ForegroundColor Yellow
    }
    elseif ($qualityGateStatus) {
        Write-Host "  New Issues: 0" -ForegroundColor Green
    }

    if ($measures.ContainsKey("coverage")) {
        Write-Host "  Coverage: $($measures["coverage"])%" -ForegroundColor Cyan
    }

    Write-Host "  Duration: $(Format-Duration -Seconds $duration)" -ForegroundColor Gray
    Write-Host "  Dashboard: $SonarHostUrl/dashboard?id=$ProjectKey" -ForegroundColor DarkCyan
    Write-Host ""

    if ($hasErrors -and -not $NoHeader) {
        Write-AiInstructions -Type SonarFailure
    }
}

# Write history entry
$histEntry = @{
    duration_s    = [math]::Round($duration, 1)
    status        = $overallStatus
    quality_gate  = $qualityGateStatus
    new_issues    = $newIssues.Count
}
if ($measures.ContainsKey("coverage")) { $histEntry["coverage_pct"] = [double]$measures["coverage"] }
Write-HistoryEntry -FilePath $historyFile -Entry $histEntry

# Stop tee logging
if ($LogFile) { Stop-TeeLogging }

# JSON output
if ($OutputFormat -eq "Json") {
    $jsonResult = @{
        status        = $overallStatus
        duration_s    = [math]::Round($duration, 1)
        quality_gate  = $qualityGateStatus
        new_issues    = $newIssues.Count
        dashboard_url = "$SonarHostUrl/dashboard?id=$ProjectKey"
    }
    if ($measures.ContainsKey("coverage")) { $jsonResult["coverage_pct"] = [double]$measures["coverage"] }
    if ($newIssues.Count -gt 0) {
        $jsonResult["issues_by_type"] = @{
            bugs            = @($newIssues | Where-Object { $_.type -eq "BUG" }).Count
            vulnerabilities = @($newIssues | Where-Object { $_.type -eq "VULNERABILITY" }).Count
            code_smells     = @($newIssues | Where-Object { $_.type -eq "CODE_SMELL" }).Count
        }
    }
    ConvertTo-JsonResult -Result $jsonResult
}

# Stop SonarQube container (unless -PersistContainer)
if (-not $PersistContainer) {
    if (Test-Path $composeFile) {
        Write-Host "Stopping SonarQube container..." -ForegroundColor DarkGray
        docker compose -f $composeFile down 2>&1 | Out-Null
    }
}

exit $(if ($hasErrors) { 1 } else { 0 })
