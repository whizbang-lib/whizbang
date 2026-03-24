#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Runs SonarQube analysis locally using a Docker-hosted SonarQube instance.

.DESCRIPTION
    This script:
    1. Starts SonarQube via docker-compose (if not already running)
    2. Creates/ensures the project exists in local SonarQube
    3. Runs dotnet test with coverage collection (Cobertura format)
    4. Runs sonarscanner to analyze code and upload coverage
    5. Opens the results dashboard in a browser

    First-time setup takes ~2 minutes for SonarQube to initialize.
    Subsequent runs are faster (SonarQube stays running).

    Default credentials: admin/admin (you'll be prompted to change on first login)

.PARAMETER SkipDocker
    Skip starting Docker containers (assumes SonarQube is already running)

.PARAMETER SkipTests
    Skip running tests and coverage collection (faster, no coverage data)

.PARAMETER Down
    Stop and remove the SonarQube containers

.PARAMETER TestFilter
    Optional test filter to run a subset of tests for coverage

.EXAMPLE
    .\Run-LocalSonarAnalysis.ps1
    Full analysis: start Docker, run tests with coverage, analyze, open dashboard

.EXAMPLE
    .\Run-LocalSonarAnalysis.ps1 -SkipDocker -SkipTests
    Re-analyze without restarting Docker or re-running tests

.EXAMPLE
    .\Run-LocalSonarAnalysis.ps1 -Down
    Stop the local SonarQube instance
#>

[CmdletBinding()]
param(
    [switch]$SkipDocker,
    [switch]$SkipTests,
    [switch]$Down,
    [string]$TestFilter
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Configuration
$SonarUrl = "http://localhost:9000"
$ProjectKey = "whizbang-local"
$ProjectName = "Whizbang (Local)"
$Token = "squ_local" # Will be replaced with actual token
$RepoRoot = Join-Path $PSScriptRoot ".."
$ComposeFile = Join-Path $RepoRoot "docker-compose.sonarqube.yml"
$CoverageDir = Join-Path $RepoRoot "scripts" "coverage"
$CoverageReport = Join-Path $CoverageDir "coverage.opencover.xml"
$TokenFile = Join-Path $RepoRoot ".sonarqube-local.token"

function Write-Info { param($Message) Write-Host "[INFO] $Message" -ForegroundColor Cyan }
function Write-Success { param($Message) Write-Host "[OK]   $Message" -ForegroundColor Green }
function Write-Warn { param($Message) Write-Host "[WARN] $Message" -ForegroundColor Yellow }
function Write-Err { param($Message) Write-Host "[ERR]  $Message" -ForegroundColor Red }

# Stop containers and exit
if ($Down) {
    Write-Info "Stopping SonarQube containers..."
    docker compose -f $ComposeFile down
    Write-Success "SonarQube stopped"
    exit 0
}

# Start SonarQube via Docker
if (-not $SkipDocker) {
    Write-Info "Starting SonarQube via docker-compose..."
    docker compose -f $ComposeFile up -d

    Write-Info "Waiting for SonarQube to be ready (this can take 1-2 minutes on first run)..."
    $maxWait = 180
    $waited = 0
    while ($waited -lt $maxWait) {
        try {
            $response = Invoke-RestMethod -Uri "$SonarUrl/api/system/status" -TimeoutSec 3 -ErrorAction SilentlyContinue
            if ($response.status -eq "UP") {
                Write-Success "SonarQube is ready!"
                break
            }
        } catch {
            # Not ready yet
        }
        Start-Sleep -Seconds 5
        $waited += 5
        if ($waited % 15 -eq 0) {
            Write-Info "Still waiting... ($waited seconds elapsed)"
        }
    }

    if ($waited -ge $maxWait) {
        Write-Err "SonarQube did not start within $maxWait seconds"
        Write-Info "Check logs: docker compose -f $ComposeFile logs sonarqube"
        exit 1
    }
}

# Generate a user token if we don't have one cached
if (Test-Path $TokenFile) {
    $Token = (Get-Content $TokenFile -Raw).Trim()
    Write-Info "Using cached local SonarQube token"
} else {
    Write-Info "Generating SonarQube user token..."
    try {
        # Try generating token with default admin credentials
        $authHeader = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("admin:admin"))
        $tokenResponse = Invoke-RestMethod -Uri "$SonarUrl/api/user_tokens/generate" `
            -Method Post `
            -Headers @{ Authorization = "Basic $authHeader" } `
            -Body @{ name = "whizbang-local-$(Get-Date -Format 'yyyyMMdd')" } `
            -ErrorAction Stop
        $Token = $tokenResponse.token
        $Token | Out-File -FilePath $TokenFile -NoNewline -Encoding utf8
        Write-Success "Token generated and cached in .sonarqube-local.token"
    } catch {
        Write-Warn "Could not auto-generate token (password may have been changed)"
        Write-Info "Please generate a token manually at: $SonarUrl/account/security"
        $Token = Read-Host "Paste your token"
        if ([string]::IsNullOrWhiteSpace($Token)) {
            Write-Err "No token provided"
            exit 1
        }
        $Token.Trim() | Out-File -FilePath $TokenFile -NoNewline -Encoding utf8
    }
}

# Ensure dotnet tools are restored
Write-Info "Restoring dotnet tools..."
Push-Location $RepoRoot
try {
    dotnet tool restore 2>&1 | Out-Null
} finally {
    Pop-Location
}

# Run tests with coverage
if (-not $SkipTests) {
    Write-Info "Running tests with coverage collection..."
    New-Item -ItemType Directory -Path $CoverageDir -Force | Out-Null

    $testArgs = @(
        "test",
        "--configuration", "Debug",
        "--collect:XPlat Code Coverage",
        "--results-directory", $CoverageDir,
        "--max-parallel-test-modules", "8",
        "--",
        "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover"
    )

    if ($TestFilter) {
        $testArgs += "--filter", $TestFilter
    }

    Push-Location $RepoRoot
    try {
        & dotnet @testArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Warn "Some tests failed (coverage still collected)"
        }
    } finally {
        Pop-Location
    }

    # Merge coverage files
    $coverageFiles = @(Get-ChildItem -Path $CoverageDir -Recurse -Filter "coverage.opencover.xml")
    if ($coverageFiles.Count -gt 0) {
        Write-Success "Coverage collected: $($coverageFiles.Count) report(s)"
    } else {
        Write-Warn "No coverage files generated"
    }
}

# Find coverage report for sonar
# Prefer the merged SonarQube-format report (generated by Run-PR.ps1 Coverage Report step)
# Falls back to opencover files from direct test runs
$sonarCoverageReport = Join-Path $RepoRoot "coverage" "sonarqube" "SonarQube.xml"
$coveragePaths = ""
if (Test-Path $sonarCoverageReport) {
    Write-Info "Using merged SonarQube coverage report"
    $coveragePaths = $sonarCoverageReport
} else {
    $coverageFiles = @(Get-ChildItem -Path $CoverageDir -Recurse -Filter "coverage.opencover.xml" -ErrorAction SilentlyContinue)
    if ($coverageFiles) {
        $coveragePaths = ($coverageFiles | ForEach-Object { $_.FullName }) -join ","
    }
}
$useSonarQubeFormat = $coveragePaths -eq $sonarCoverageReport

# Run SonarScanner
Write-Info "Starting SonarQube analysis..."
Push-Location $RepoRoot
try {
    # Load shared exclusion config (single source of truth with CI)
    $exclusionConfigPath = Join-Path $RepoRoot "sonar.config"
    $sonarExclusions = "**/samples/**,**/benchmarks/**,**/*Generated.cs,**/.whizbang-generated/**"
    $sonarCoverageExclusions = "**/samples/**,**/benchmarks/**,**/tests/**"
    $sonarCpdExclusions = ""
    $sonarExtraArgs = @()
    if (Test-Path $exclusionConfigPath) {
        Get-Content $exclusionConfigPath | Where-Object { $_ -match "^[^#].*=" } | ForEach-Object {
            $parts = $_ -split "=", 2
            $key = $parts[0].Trim()
            $value = $parts[1].Trim()
            switch ($key) {
                "sonar.exclusions" { $sonarExclusions = $value }
                "sonar.coverage.exclusions" { $sonarCoverageExclusions = $value }
                "sonar.cpd.exclusions" { $sonarCpdExclusions = $value }
                default {
                    # Pass through all other sonar.* properties (e.g., issue ignore rules)
                    if ($key.StartsWith("sonar.")) {
                        $sonarExtraArgs += "/d:${key}=${value}"
                    }
                }
            }
        }
        Write-Info "Loaded exclusions from sonar.config"
    }

    $beginArgs = @(
        "begin",
        "/k:$ProjectKey",
        "/n:$ProjectName",
        "/d:sonar.login=$Token",
        "/d:sonar.host.url=$SonarUrl",
        "/d:sonar.exclusions=$sonarExclusions",
        "/d:sonar.coverage.exclusions=$sonarCoverageExclusions"
    )
    if ($sonarCpdExclusions) { $beginArgs += "/d:sonar.cpd.exclusions=$sonarCpdExclusions" }
    $beginArgs += $sonarExtraArgs
    # Use the correct coverage property based on report format
    if ($useSonarQubeFormat) {
        $beginArgs += "/d:sonar.coverageReportPaths=$coveragePaths"
    } elseif ($coveragePaths) {
        $beginArgs += "/d:sonar.cs.opencover.reportsPaths=$coveragePaths"
    }

    & dotnet-sonarscanner @beginArgs
    if ($LASTEXITCODE -ne 0) { throw "sonarscanner begin failed" }

    Write-Info "Building solution..."
    dotnet build --configuration Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }

    Write-Info "Finishing analysis..."
    & dotnet-sonarscanner end /d:sonar.login="$Token"
    if ($LASTEXITCODE -ne 0) { throw "sonarscanner end failed" }
} finally {
    Pop-Location
}

Write-Host ""
Write-Success "Analysis complete!"
Write-Host ""
Write-Info "Dashboard: $SonarUrl/dashboard?id=$ProjectKey"
Write-Info "Coverage:  $SonarUrl/component_measures?id=$ProjectKey&metric=coverage&view=list"
Write-Host ""

# Open in browser
if ($IsWindows) { Start-Process "$SonarUrl/dashboard?id=$ProjectKey" }
elseif ($IsMacOS) { & open "$SonarUrl/dashboard?id=$ProjectKey" }
elseif ($IsLinux) { & xdg-open "$SonarUrl/dashboard?id=$ProjectKey" 2>/dev/null }
