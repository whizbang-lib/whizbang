#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Runs SonarCloud analysis locally for the Whizbang solution.

.DESCRIPTION
    This script performs local SonarCloud analysis to check code quality before pushing changes.
    It handles token management securely and provides clear instructions if setup is needed.

    Token Priority (checked in order):
    1. SONAR_TOKEN environment variable
    2. .sonarcloud.token file (gitignored, created if missing)

    The analysis includes:
    - Code smells, bugs, vulnerabilities detection
    - Technical debt calculation
    - Cognitive complexity analysis
    - Exclusions for samples, benchmarks, and generated code

.PARAMETER Token
    SonarCloud authentication token. If not provided, will check environment variable or file.

.PARAMETER SkipBuild
    Skip the build step and only run the SonarCloud end analysis. Useful if you just built.

.EXAMPLE
    .\Run-SonarAnalysis.ps1
    Run analysis using token from environment variable or .sonarcloud.token file

.EXAMPLE
    .\Run-SonarAnalysis.ps1 -Token "your-token-here"
    Run analysis with explicitly provided token

.EXAMPLE
    .\Run-SonarAnalysis.ps1 -SkipBuild
    Run analysis without rebuilding (faster if you just built)

.NOTES
    First-time setup:
    1. Generate a token at: https://sonarcloud.io/account/security
    2. Run this script - it will guide you through saving the token
    3. Token is stored in .sonarcloud.token (gitignored, never committed)

    Project: whizbang-lib/whizbang
    Dashboard: https://sonarcloud.io/project/overview?id=whizbang-lib_whizbang
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Token,

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Colors for output
function Write-Info { param($Message) Write-Host "ℹ️  $Message" -ForegroundColor Cyan }
function Write-Success { param($Message) Write-Host "✅ $Message" -ForegroundColor Green }
function Write-Warning2 { param($Message) Write-Host "⚠️  $Message" -ForegroundColor Yellow }
function Write-Error2 { param($Message) Write-Host "❌ $Message" -ForegroundColor Red }

# Configuration
$ProjectKey = "whizbang-lib_whizbang"
$Organization = "whizbang-lib"
$SonarHostUrl = "https://sonarcloud.io"
$TokenFilePath = Join-Path $PSScriptRoot ".." ".sonarcloud.token"

# Get token from parameter, environment variable, or file
function Get-SonarToken {
    # Priority 1: Parameter
    if ($Token) {
        Write-Info "Using token from parameter"
        return $Token
    }

    # Priority 2: Environment variable
    if ($env:SONAR_TOKEN) {
        Write-Info "Using token from SONAR_TOKEN environment variable"
        return $env:SONAR_TOKEN
    }

    # Priority 3: Token file
    if (Test-Path $TokenFilePath) {
        Write-Info "Using token from .sonarcloud.token file"
        return (Get-Content $TokenFilePath -Raw).Trim()
    }

    return $null
}

# Setup token if missing
function Initialize-SonarToken {
    Write-Host ""
    Write-Host "╔════════════════════════════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║         SonarCloud Token Setup Required                       ║" -ForegroundColor Magenta
    Write-Host "╚════════════════════════════════════════════════════════════════╝" -ForegroundColor Magenta
    Write-Host ""
    Write-Info "No SonarCloud token found. You need to generate one:"
    Write-Host ""
    Write-Host "  1. Visit: " -NoNewline
    Write-Host "https://sonarcloud.io/account/security" -ForegroundColor Yellow
    Write-Host "  2. Generate a new token (give it a name like 'Local Development')"
    Write-Host "  3. Copy the token (you won't see it again!)"
    Write-Host ""
    Write-Host "Options to provide the token:" -ForegroundColor Cyan
    Write-Host "  • Paste it below to save in .sonarcloud.token (gitignored)"
    Write-Host "  • Set SONAR_TOKEN environment variable"
    Write-Host "  • Pass as parameter: -Token 'your-token'"
    Write-Host ""

    $response = Read-Host "Paste your token here (or press Enter to cancel)"

    if ([string]::IsNullOrWhiteSpace($response)) {
        Write-Warning2 "Setup cancelled. Run the script again when you have a token."
        exit 1
    }

    # Save to file
    try {
        $response.Trim() | Out-File -FilePath $TokenFilePath -NoNewline -Encoding utf8
        Write-Success "Token saved to .sonarcloud.token"
        Write-Info "This file is gitignored and will never be committed"
        return $response.Trim()
    }
    catch {
        Write-Error2 "Failed to save token: $_"
        exit 1
    }
}

# Restore tools from manifest if needed
function Restore-Tools {
    Write-Info "Restoring dotnet tools from manifest (.config/dotnet-tools.json)..."
    try {
        dotnet tool restore 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Tool restore failed with exit code $LASTEXITCODE"
        }
        Write-Success "Tools restored successfully"
    }
    catch {
        Write-Error2 "Failed to restore tools: $_"
        Write-Info "Try manually: dotnet tool restore"
        exit 1
    }
}

# Main execution
try {
    Write-Host ""
    Write-Host "╔════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host "║              Whizbang SonarCloud Analysis                      ║" -ForegroundColor Cyan
    Write-Host "╚════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
    Write-Host ""

    # Restore dotnet tools from manifest
    Restore-Tools

    # Get or setup token
    $sonarToken = Get-SonarToken
    if (-not $sonarToken) {
        $sonarToken = Initialize-SonarToken
    }

    Write-Host ""
    Write-Info "Starting SonarCloud analysis..."
    Write-Host "  Project: $ProjectKey"
    Write-Host "  Organization: $Organization"
    Write-Host ""

    # Begin analysis
    Write-Info "Step 1/3: Begin analysis configuration..."
    $beginArgs = @(
        "begin",
        "/k:$ProjectKey",
        "/o:$Organization",
        "/d:sonar.token=$sonarToken",
        "/d:sonar.host.url=$SonarHostUrl",
        "/d:sonar.exclusions=**/samples/**,**/benchmarks/**,**/*Generated.cs,**/.whizbang-generated/**",
        "/d:sonar.coverage.exclusions=**/samples/**,**/benchmarks/**,**/tests/**",
        "/d:sonar.issue.ignore.multicriteria=e1,e2,e3,e4,e5,e6",
        "/d:sonar.issue.ignore.multicriteria.e1.ruleKey=csharpsquid:S1192",
        "/d:sonar.issue.ignore.multicriteria.e1.resourceKey=src/Whizbang.Generators/**",
        "/d:sonar.issue.ignore.multicriteria.e2.ruleKey=csharpsquid:S1192",
        "/d:sonar.issue.ignore.multicriteria.e2.resourceKey=src/Whizbang.Generators.Shared/**",
        "/d:sonar.issue.ignore.multicriteria.e3.ruleKey=csharpsquid:S1192",
        "/d:sonar.issue.ignore.multicriteria.e3.resourceKey=src/Whizbang.Data.EFCore.Postgres.Generators/**",
        "/d:sonar.issue.ignore.multicriteria.e4.ruleKey=csharpsquid:S1192",
        "/d:sonar.issue.ignore.multicriteria.e4.resourceKey=src/Whizbang.Data.Schema/Schemas/**",
        "/d:sonar.issue.ignore.multicriteria.e5.ruleKey=csharpsquid:S6444",
        "/d:sonar.issue.ignore.multicriteria.e5.resourceKey=src/Whizbang.Generators.Shared/**",
        "/d:sonar.issue.ignore.multicriteria.e6.ruleKey=csharpsquid:S3776",
        "/d:sonar.issue.ignore.multicriteria.e6.resourceKey=src/Whizbang.Generators/**"
    )

    # Run sonar scanner (global tool should be in PATH now)
    & dotnet-sonarscanner @beginArgs
    if ($LASTEXITCODE -ne 0) {
        throw "SonarCloud begin failed with exit code $LASTEXITCODE"
    }

    if (-not $SkipBuild) {
        Write-Host ""
        Write-Info "Step 2/3: Building solution (Release configuration)..."
        dotnet build --configuration Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
    }
    else {
        Write-Host ""
        Write-Info "Step 2/3: Skipping build (using existing build artifacts)..."
    }

    Write-Host ""
    Write-Info "Step 3/3: Ending analysis and uploading results..."
    & dotnet-sonarscanner end /d:sonar.token="$sonarToken"
    if ($LASTEXITCODE -ne 0) {
        throw "SonarCloud end failed with exit code $LASTEXITCODE"
    }

    Write-Host ""
    Write-Success "SonarCloud analysis completed successfully!"
    Write-Host ""
    Write-Info "View results at:"
    Write-Host "  https://sonarcloud.io/project/overview?id=$ProjectKey" -ForegroundColor Yellow
    Write-Host ""
}
catch {
    Write-Host ""
    Write-Error2 "Analysis failed: $_"
    Write-Host ""
    Write-Info "Troubleshooting:"
    Write-Host "  • Check your token is valid at https://sonarcloud.io/account/security"
    Write-Host "  • Ensure you have internet connectivity"
    Write-Host "  • Try regenerating your token if it's expired"
    Write-Host ""
    exit 1
}
