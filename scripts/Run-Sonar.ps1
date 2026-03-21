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

    [switch]$FailFast
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Import shared module
Import-Module (Join-Path $PSScriptRoot "lib" "PR-Readiness-Common.psm1") -Force

$useAiOutput = $Mode -eq "Ai"

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

# Branded header
$headerParams = @{ Mode = $Mode }
if ($SkipBuild) { $headerParams["SkipBuild"] = "On" }
Write-WhizbangHeader -ScriptName "Sonar Runner" -Params $headerParams -Estimate $estimateStr

# Configuration (must match Run-SonarAnalysis.ps1)
$ProjectKey = "whizbang-lib_whizbang"
$SonarHostUrl = "https://sonarcloud.io"

# Resolve token for API calls (same priority as Run-SonarAnalysis.ps1)
$sonarToken = $Token
if (-not $sonarToken) { $sonarToken = $env:SONAR_TOKEN }
if (-not $sonarToken) {
    $tokenFilePath = Join-Path $PSScriptRoot ".." ".sonarcloud.token"
    if (Test-Path $tokenFilePath) {
        $sonarToken = (Get-Content $tokenFilePath -Raw).Trim()
    }
}

# Run the actual SonarCloud analysis
$analysisScript = Join-Path $PSScriptRoot "Run-SonarAnalysis.ps1"

$splatParams = @{}
if ($Token) { $splatParams["Token"] = $Token }
if ($SkipBuild) { $splatParams["SkipBuild"] = $true }

$analysisExitCode = 0
try {
    if ($useAiOutput) {
        Write-Host "Running SonarCloud analysis..." -ForegroundColor Gray
        & $analysisScript @splatParams 2>&1 | ForEach-Object {
            # In AI mode, only show key lines; write everything to log
            $line = $_.ToString()
            if ($LogFile) {
                $line | Out-File -FilePath $LogFile -Append -Encoding utf8
            }
            if ($line -match "Step \d/\d|completed successfully|failed|error" -and $useAiOutput) {
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
    Write-Host "SonarCloud analysis failed: $_" -ForegroundColor Red
    $analysisExitCode = 1
}

# Query SonarCloud API for quality gate status and metrics
$qualityGateStatus = $null
$newIssues = @()
$measures = @{}
$apiError = $null

if ($sonarToken -and $analysisExitCode -eq 0) {
    $authHeader = @{ Authorization = "Bearer $sonarToken" }

    try {
        # Quality gate status
        $qgResponse = Invoke-RestMethod -Uri "$SonarHostUrl/api/qualitygates/project_status?projectKey=$ProjectKey" `
            -Headers $authHeader -ErrorAction Stop
        $qualityGateStatus = $qgResponse.projectStatus.status

        # New issues (since leak period)
        $issuesResponse = Invoke-RestMethod -Uri "$SonarHostUrl/api/issues/search?projectKeys=$ProjectKey&sinceLeakPeriod=true&ps=100&statuses=OPEN,CONFIRMED" `
            -Headers $authHeader -ErrorAction Stop
        $newIssues = @($issuesResponse.issues)

        # Measures
        $metricsKeys = "coverage,new_coverage,bugs,vulnerabilities,code_smells,ncloc"
        $measuresResponse = Invoke-RestMethod -Uri "$SonarHostUrl/api/measures/component?component=$ProjectKey&metricKeys=$metricsKeys" `
            -Headers $authHeader -ErrorAction Stop
        foreach ($m in $measuresResponse.component.measures) {
            $measures[$m.metric] = $m.value
        }
    }
    catch {
        $apiError = $_.Exception.Message
        if (-not $useAiOutput) {
            Write-Host "Warning: Could not fetch SonarCloud API data: $apiError" -ForegroundColor Yellow
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
        $covStr = "  Coverage: $($measures["coverage"])%"
        if ($measures.ContainsKey("new_coverage")) {
            $covStr += " (new code: $($measures["new_coverage"])%)"
        }
        Write-Host $covStr -ForegroundColor Cyan
    }

    Write-Host "  Duration: $(Format-Duration -Seconds $duration)" -ForegroundColor Gray
    Write-Host "  Dashboard: $SonarHostUrl/project/overview?id=$ProjectKey" -ForegroundColor DarkCyan
    Write-Host ""

    if ($hasErrors) {
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
        dashboard_url = "$SonarHostUrl/project/overview?id=$ProjectKey"
    }
    if ($measures.ContainsKey("coverage")) { $jsonResult["coverage_pct"] = [double]$measures["coverage"] }
    if ($measures.ContainsKey("new_coverage")) { $jsonResult["new_coverage_pct"] = [double]$measures["new_coverage"] }
    if ($newIssues.Count -gt 0) {
        $jsonResult["issues_by_type"] = @{
            bugs            = @($newIssues | Where-Object { $_.type -eq "BUG" }).Count
            vulnerabilities = @($newIssues | Where-Object { $_.type -eq "VULNERABILITY" }).Count
            code_smells     = @($newIssues | Where-Object { $_.type -eq "CODE_SMELL" }).Count
        }
    }
    ConvertTo-JsonResult -Result $jsonResult
}

exit $(if ($hasErrors) { 1 } else { 0 })
