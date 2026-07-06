#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Runs Stryker.NET mutation testing on a Whizbang source project.

.DESCRIPTION
    Executes mutation testing using Stryker.NET to identify weaknesses in the test suite.
    Stryker systematically introduces small bugs (mutants) into source code and checks whether
    tests detect them. Surviving mutants indicate gaps in test coverage or weak assertions.

    Each target project must have a stryker-config.json in its corresponding test project directory.

.PARAMETER Project
    The source project to mutate (default: Whizbang.Core).
    Must match a test project that has a stryker-config.json file.

.PARAMETER Mutate
    Optional file filter for mutation (glob pattern). Only files matching this pattern will be mutated.
    Example: "**/MessageEnvelope.cs" to mutate only MessageEnvelope.cs

.PARAMETER Open
    Open the HTML mutation report in a browser after completion.

.PARAMETER Since
    Enable diff-based mutation testing. Only mutate files changed since the specified git target.
    Example: "main" to only mutate files changed since the main branch.

.PARAMETER Concurrency
    Override the number of parallel test runners (default: use config value).

.PARAMETER Configuration
    Build configuration (default: Debug).

.EXAMPLE
    ./run-mutation-tests.ps1
    Runs mutation testing on Whizbang.Core (default)

.EXAMPLE
    ./run-mutation-tests.ps1 -Project Whizbang.Data.Schema
    Runs mutation testing on Whizbang.Data.Schema

.EXAMPLE
    ./run-mutation-tests.ps1 -Mutate "**/MessageEnvelope.cs"
    Runs mutation testing only on MessageEnvelope.cs

.EXAMPLE
    ./run-mutation-tests.ps1 -Since main -Open
    Runs mutation testing only on files changed since main, opens report
#>

[CmdletBinding()]
param(
    [ArgumentCompleter({
        param($commandName, $parameterName, $wordToComplete)
        $repoRoot = git rev-parse --show-toplevel 2>$null
        if ($repoRoot) {
            Get-ChildItem -Path "$repoRoot/tests/*/stryker-config.json" -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $configContent = Get-Content $_.FullName -Raw | ConvertFrom-Json
                    $configContent.'stryker-config'.project -replace '\.csproj$', ''
                } |
                Where-Object { $_ -like "*$wordToComplete*" }
        }
    })]
    [string]$Project = 'Whizbang.Core',

    [string]$Mutate,

    [switch]$Open,

    [string]$Since,

    [int]$Concurrency = 0,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = git rev-parse --show-toplevel 2>$null
if (-not $repoRoot) {
    Write-Host "ERROR: Not in a git repository" -ForegroundColor Red
    exit 1
}

# Ensure dotnet-stryker is available
$strykerAvailable = dotnet stryker --help 2>&1 | Select-String -Pattern "Stryker mutator" -Quiet
if (-not $strykerAvailable) {
    Write-Host "dotnet-stryker not found. Running 'dotnet tool restore'..." -ForegroundColor Yellow
    dotnet tool restore
    $strykerAvailable = dotnet stryker --help 2>&1 | Select-String -Pattern "Stryker mutator" -Quiet
    if (-not $strykerAvailable) {
        Write-Host "ERROR: dotnet-stryker could not be restored. Check .config/dotnet-tools.json" -ForegroundColor Red
        exit 1
    }
}

# Find the test project directory with a stryker-config.json that targets this source project
$configFiles = Get-ChildItem -Path "$repoRoot/tests/*/stryker-config.json" -ErrorAction SilentlyContinue
$testProjectDir = $null

foreach ($configFile in $configFiles) {
    $config = Get-Content $configFile.FullName -Raw | ConvertFrom-Json
    $sourceProject = $config.'stryker-config'.project -replace '\.csproj$', ''
    if ($sourceProject -eq $Project) {
        $testProjectDir = $configFile.DirectoryName
        break
    }
}

if (-not $testProjectDir) {
    Write-Host "ERROR: No stryker-config.json found targeting project '$Project'" -ForegroundColor Red
    Write-Host ""
    Write-Host "Available projects:" -ForegroundColor Yellow
    foreach ($configFile in $configFiles) {
        $config = Get-Content $configFile.FullName -Raw | ConvertFrom-Json
        $name = $config.'stryker-config'.project -replace '\.csproj$', ''
        Write-Host "  - $name" -ForegroundColor Cyan
    }
    Write-Host ""
    Write-Host "To add a new project, create a stryker-config.json in the corresponding test project directory." -ForegroundColor Yellow
    exit 1
}

$testProjectName = Split-Path $testProjectDir -Leaf

Write-Host ""
Write-Host "=== Stryker.NET Mutation Testing ===" -ForegroundColor Cyan
Write-Host "  Source project: $Project" -ForegroundColor White
Write-Host "  Test project:   $testProjectName" -ForegroundColor White
Write-Host "  Configuration:  $Configuration" -ForegroundColor White
if ($Mutate) {
    Write-Host "  File filter:    $Mutate" -ForegroundColor White
}
if ($Since) {
    Write-Host "  Diff target:    $Since" -ForegroundColor White
}
Write-Host ""

# Build the stryker command
$strykerArgs = @()

if ($Configuration -ne 'Debug') {
    $strykerArgs += "--configuration", $Configuration
}

if ($Mutate) {
    $strykerArgs += "--mutate", $Mutate
}

if ($Since) {
    $strykerArgs += "--since", "--since-target", $Since
}

if ($Concurrency -gt 0) {
    $strykerArgs += "--concurrency", $Concurrency
}

if ($Open) {
    $strykerArgs += "--open-report"
}

# Run Stryker from the test project directory
Write-Host "Running: dotnet stryker $($strykerArgs -join ' ')" -ForegroundColor DarkGray
Write-Host ""

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Push-Location $testProjectDir
try {
    & dotnet stryker @strykerArgs
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

$stopwatch.Stop()
$elapsed = $stopwatch.Elapsed

Write-Host ""
Write-Host "=== Mutation Testing Complete ===" -ForegroundColor Cyan
Write-Host "  Duration: $($elapsed.ToString('mm\:ss'))" -ForegroundColor White

# Find the latest report
$latestReport = Get-ChildItem -Path "$testProjectDir/StrykerOutput/*/reports/mutation-report.html" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($latestReport) {
    Write-Host "  Report:   $($latestReport.FullName)" -ForegroundColor White
}

Write-Host ""

exit $exitCode
