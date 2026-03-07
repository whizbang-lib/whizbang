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

.PARAMETER IncrementVersion
    Increment the prerelease version number before packing to avoid NuGet cache issues.
    Handles multiple formats:
    - 0.9.0-local -> 0.9.0-local.1 (local dev version)
    - 0.5.1-alpha.2 -> 0.5.1-alpha.3 (numbered prerelease)
    - 0.5.1 -> 0.5.1-alpha.1 (stable to prerelease)

.PARAMETER Version
    Explicit version to use for all packages. When specified, this takes precedence
    over -IncrementVersion. Useful for testing specific versions or matching CI builds.
    For example: 0.9.0-alpha.119

.PARAMETER Version
    Explicit version to use for all packages. When specified, this takes precedence
    over -IncrementVersion. Useful for testing specific versions or matching CI builds.
    For example: 0.9.0-alpha.119
<<<<<<< release/v0.9.1-alpha.2
=======

.PARAMETER Version
    Explicit version to use for all packages. When specified, this takes precedence
    over -IncrementVersion. Useful for testing specific versions or matching CI builds.
    For example: 0.9.0-alpha.119
>>>>>>> main

.EXAMPLE
    ./scripts/Pack-LocalPackages.ps1
    Packs all packages in Debug configuration.

.EXAMPLE
    ./scripts/Pack-LocalPackages.ps1 -Configuration Release -Clean
    Cleans local-packages and packs all packages in Release configuration.

.EXAMPLE
    ./scripts/Pack-LocalPackages.ps1 -IncrementVersion
    Increments the prerelease version and packs all packages.

.EXAMPLE
    ./scripts/Pack-LocalPackages.ps1 -Version 0.9.0-alpha.119
    Uses the specified version for all packages (no auto-increment).
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Clean = $true,

    [switch]$IncrementVersion = $true,

    [string]$Version
)

$ErrorActionPreference = "Stop"

# Find repo root
$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Get-Location
}
$repoRoot = Split-Path $scriptDir -Parent

# Handle version: explicit -Version takes precedence over -IncrementVersion
$propsFile = Join-Path $repoRoot "Directory.Build.props"
$propsContent = Get-Content $propsFile -Raw
<<<<<<< release/v0.9.1-alpha.2

if ($Version) {
    # Use explicit version
    if ($propsContent -match '<Version>([^<]+)</Version>') {
        $oldVersion = $Matches[1]
        $propsContent = $propsContent -replace "<Version>$([regex]::Escape($oldVersion))</Version>", "<Version>$Version</Version>"
        Set-Content $propsFile $propsContent -NoNewline

=======

if ($Version) {
    # Use explicit version
    if ($propsContent -match '<Version>([^<]+)</Version>') {
        $oldVersion = $Matches[1]
        $propsContent = $propsContent -replace "<Version>$([regex]::Escape($oldVersion))</Version>", "<Version>$Version</Version>"
        Set-Content $propsFile $propsContent -NoNewline

>>>>>>> main
        Write-Host "Version set: $oldVersion -> $Version" -ForegroundColor Green
    }
    else {
        Write-Error "Could not find <Version> element in Directory.Build.props"
        exit 1
    }
}
elseif ($IncrementVersion) {
    # Match version like 0.5.1-alpha.2
    if ($propsContent -match '<Version>(\d+\.\d+\.\d+)-([a-z]+)\.(\d+)</Version>') {
        $baseVersion = $Matches[1]
        $prerelease = $Matches[2]
        $prereleaseNum = [int]$Matches[3]
        $newPrereleaseNum = $prereleaseNum + 1
        $oldVersion = "$baseVersion-$prerelease.$prereleaseNum"
        $newVersion = "$baseVersion-$prerelease.$newPrereleaseNum"

        $propsContent = $propsContent -replace "<Version>$([regex]::Escape($oldVersion))</Version>", "<Version>$newVersion</Version>"
        Set-Content $propsFile $propsContent -NoNewline

        Write-Host "Version incremented: $oldVersion -> $newVersion" -ForegroundColor Green
    }
    elseif ($propsContent -match '<Version>(\d+\.\d+\.\d+)-([a-z]+)</Version>') {
        # Match version like 0.9.0-local (prerelease without number)
        $baseVersion = $Matches[1]
        $prerelease = $Matches[2]
        $oldVersion = "$baseVersion-$prerelease"
        $newVersion = "$baseVersion-$prerelease.1"

        $propsContent = $propsContent -replace "<Version>$([regex]::Escape($oldVersion))</Version>", "<Version>$newVersion</Version>"
        Set-Content $propsFile $propsContent -NoNewline

        Write-Host "Version incremented: $oldVersion -> $newVersion" -ForegroundColor Green
    }
    elseif ($propsContent -match '<Version>(\d+\.\d+\.\d+)</Version>') {
        # No prerelease suffix, add one
        $baseVersion = $Matches[1]
        $oldVersion = $baseVersion
        $newVersion = "$baseVersion-alpha.1"

        $propsContent = $propsContent -replace "<Version>$([regex]::Escape($oldVersion))</Version>", "<Version>$newVersion</Version>"
        Set-Content $propsFile $propsContent -NoNewline

        Write-Host "Version incremented: $oldVersion -> $newVersion" -ForegroundColor Green
    }
    else {
        Write-Host "Could not parse version from Directory.Build.props" -ForegroundColor Yellow
        Write-Host "Expected format: X.Y.Z, X.Y.Z-prerelease, or X.Y.Z-prerelease.N" -ForegroundColor Yellow
    }
}

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

# Internal packages to skip (IsPackable=false - not published to NuGet)
$internalPackages = @(
    'Whizbang.Generators.Shared',    # ILMerged into generator packages
    'Whizbang.Testing'               # Empty placeholder
)

# Find all Whizbang projects
$allProjects = Get-ChildItem -Path $srcDir -Filter "Whizbang.*.csproj" -Recurse |
    Where-Object { $_.FullName -notmatch "\\obj\\" -and $_.FullName -notmatch "\\bin\\" }

# Filter out internal packages
$projects = $allProjects | Where-Object { $_.BaseName -notin $internalPackages }
$skippedProjects = $allProjects | Where-Object { $_.BaseName -in $internalPackages }

Write-Host "Found $($projects.Count) projects to pack:" -ForegroundColor Green
$projects | ForEach-Object { Write-Host "  - $($_.BaseName)" -ForegroundColor Gray }

if ($skippedProjects.Count -gt 0) {
    Write-Host ""
    Write-Host "Skipping $($skippedProjects.Count) internal packages (IsPackable=false):" -ForegroundColor DarkGray
    $skippedProjects | ForEach-Object { Write-Host "  - $($_.BaseName)" -ForegroundColor DarkGray }
}
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
