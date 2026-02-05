#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Packs all Whizbang NuGet packages to local-packages directory.

.DESCRIPTION
    Builds and packs all src/Whizbang.* projects to the local-packages directory
    for local development and testing.

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Debug

.PARAMETER Clean
    Clean the local-packages directory before packing.

.EXAMPLE
    ./scripts/Pack-LocalPackages.ps1
    Packs all packages in Debug configuration.

.EXAMPLE
    ./scripts/Pack-LocalPackages.ps1 -Configuration Release -Clean
    Cleans local-packages and packs all packages in Release configuration.
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Clean
)

$ErrorActionPreference = "Stop"

# Find repo root
$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Get-Location
}
$repoRoot = Split-Path $scriptDir -Parent

$localPackagesDir = Join-Path $repoRoot "local-packages"
$srcDir = Join-Path $repoRoot "src"

Write-Host "Whizbang Local Package Builder" -ForegroundColor Cyan
Write-Host "===============================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Repository: $repoRoot"
Write-Host "Output:     $localPackagesDir"
Write-Host "Config:     $Configuration"
Write-Host ""

# Clean if requested
if ($Clean) {
    Write-Host "Cleaning local-packages directory..." -ForegroundColor Yellow
    if (Test-Path $localPackagesDir) {
        Remove-Item "$localPackagesDir/*.nupkg" -Force -ErrorAction SilentlyContinue
        Remove-Item "$localPackagesDir/*.snupkg" -Force -ErrorAction SilentlyContinue
    }
}

# Ensure output directory exists
if (-not (Test-Path $localPackagesDir)) {
    New-Item -ItemType Directory -Path $localPackagesDir | Out-Null
}

# Find all Whizbang projects
$projects = Get-ChildItem -Path $srcDir -Filter "Whizbang.*.csproj" -Recurse |
    Where-Object { $_.FullName -notmatch "\\obj\\" -and $_.FullName -notmatch "\\bin\\" }

Write-Host "Found $($projects.Count) projects to pack:" -ForegroundColor Green
$projects | ForEach-Object { Write-Host "  - $($_.BaseName)" -ForegroundColor Gray }
Write-Host ""

$successCount = 0
$failCount = 0
$results = @()

foreach ($project in $projects) {
    $projectName = $project.BaseName
    Write-Host "Packing $projectName..." -ForegroundColor Cyan -NoNewline

    $output = dotnet pack $project.FullName -o $localPackagesDir -c $Configuration 2>&1
    $exitCode = $LASTEXITCODE

    # Check for success (package created)
    $packageCreated = $output | Select-String "Successfully created package"

    if ($packageCreated) {
        Write-Host " OK" -ForegroundColor Green
        $successCount++
        $results += [PSCustomObject]@{
            Project = $projectName
            Status = "Success"
        }
    } else {
        # Check if it's just the NU5017 error (no content) but package was still created
        $nu5017 = $output | Select-String "NU5017"
        if ($nu5017 -and ($output | Select-String "Successfully created package")) {
            Write-Host " OK (analyzer package)" -ForegroundColor Green
            $successCount++
            $results += [PSCustomObject]@{
                Project = $projectName
                Status = "Success (analyzer)"
            }
        } elseif ($exitCode -ne 0) {
            Write-Host " FAILED" -ForegroundColor Red
            $failCount++
            $results += [PSCustomObject]@{
                Project = $projectName
                Status = "Failed"
            }
            # Show error details
            $output | Where-Object { $_ -match "error" } | ForEach-Object {
                Write-Host "    $_" -ForegroundColor Red
            }
        } else {
            Write-Host " OK" -ForegroundColor Green
            $successCount++
            $results += [PSCustomObject]@{
                Project = $projectName
                Status = "Success"
            }
        }
    }
}

Write-Host ""
Write-Host "===============================" -ForegroundColor Cyan
Write-Host "Results: $successCount succeeded, $failCount failed" -ForegroundColor $(if ($failCount -gt 0) { "Yellow" } else { "Green" })
Write-Host ""

# List created packages
$packages = Get-ChildItem -Path $localPackagesDir -Filter "*.nupkg" | Sort-Object LastWriteTime -Descending
Write-Host "Packages in $localPackagesDir`:" -ForegroundColor Cyan
$packages | ForEach-Object {
    $size = [math]::Round($_.Length / 1KB, 1)
    Write-Host "  $($_.Name) ($size KB)" -ForegroundColor Gray
}

if ($failCount -gt 0) {
    exit 1
}
