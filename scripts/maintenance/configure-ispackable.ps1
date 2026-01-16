#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Configures IsPackable property for all projects in the solution.

.DESCRIPTION
    Adds <IsPackable>true</IsPackable> to library projects in src/ and tools/
    Adds <IsPackable>false</IsPackable> to test projects, sample projects, and benchmarks.
    Skips projects that already have IsPackable configured.

.EXAMPLE
    pwsh scripts/maintenance/configure-ispackable.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Write-Host "=== Configuring IsPackable for all projects ===" -ForegroundColor Cyan

# Find all .csproj files
$allProjects = Get-ChildItem -Path . -Filter "*.csproj" -Recurse

# Categories
$libraryProjects = @()
$nonLibraryProjects = @()

foreach ($project in $allProjects) {
    $relativePath = $project.FullName.Replace((Get-Location).Path + [IO.Path]::DirectorySeparatorChar, "")

    # Check if project already has IsPackable
    $content = Get-Content $project.FullName -Raw
    if ($content -match '<IsPackable>') {
        Write-Host "  [SKIP] $relativePath (already configured)" -ForegroundColor Gray
        continue
    }

    # Categorize based on path
    if ($relativePath -match '^src[\\/]Whizbang\.' -or $relativePath -match '^tools[\\/]') {
        $libraryProjects += $project
    }
    elseif ($relativePath -match '^tests[\\/]' -or
            $relativePath -match '^samples[\\/]' -or
            $relativePath -match '^benchmarks[\\/]' -or
            $relativePath -match '^test-diagnostics[\\/]') {
        $nonLibraryProjects += $project
    }
    else {
        Write-Host "  [WARN] Unknown project category: $relativePath" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Found projects needing IsPackable configuration:" -ForegroundColor Cyan
Write-Host "  Library projects (IsPackable=true): $($libraryProjects.Count)" -ForegroundColor Green
Write-Host "  Non-library projects (IsPackable=false): $($nonLibraryProjects.Count)" -ForegroundColor Green
Write-Host ""

# Add IsPackable=true to library projects
foreach ($project in $libraryProjects) {
    $relativePath = $project.FullName.Replace((Get-Location).Path + [IO.Path]::DirectorySeparatorChar, "")

    $content = Get-Content $project.FullName -Raw

    # Find first <PropertyGroup> and add IsPackable after it
    if ($content -match '(<PropertyGroup>)') {
        $newContent = $content -replace '(<PropertyGroup>)', "`$1`n    <IsPackable>true</IsPackable>"
        Set-Content -Path $project.FullName -Value $newContent -NoNewline
        Write-Host "  [ADD] $relativePath -> IsPackable=true" -ForegroundColor Green
    }
    else {
        Write-Host "  [ERROR] No <PropertyGroup> found in $relativePath" -ForegroundColor Red
    }
}

# Add IsPackable=false to non-library projects
foreach ($project in $nonLibraryProjects) {
    $relativePath = $project.FullName.Replace((Get-Location).Path + [IO.Path]::DirectorySeparatorChar, "")

    $content = Get-Content $project.FullName -Raw

    # Find first <PropertyGroup> and add IsPackable after it
    if ($content -match '(<PropertyGroup>)') {
        $newContent = $content -replace '(<PropertyGroup>)', "`$1`n    <IsPackable>false</IsPackable>"
        Set-Content -Path $project.FullName -Value $newContent -NoNewline
        Write-Host "  [ADD] $relativePath -> IsPackable=false" -ForegroundColor Cyan
    }
    else {
        Write-Host "  [ERROR] No <PropertyGroup> found in $relativePath" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "=== IsPackable configuration complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Verify build still succeeds: dotnet build --configuration Release" -ForegroundColor Gray
Write-Host "  2. Test package creation: dotnet pack --configuration Release --output /tmp/packages" -ForegroundColor Gray
