#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Packs all Whizbang NuGet packages to local-packages directory.

.DESCRIPTION
    Builds and packs all src/Whizbang.* projects to the local-packages directory
    for local development and testing. Always uses 'local' as the prerelease tag
    to distinguish local packages from CI/CD builds.

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Debug

.PARAMETER Clean
    Clean the local-packages directory before packing.

.PARAMETER IncrementVersion
    Increment the prerelease version number before packing to avoid NuGet cache issues.
    Handles multiple formats:
    - 0.9.0-local -> 0.9.0-local.1 (local dev version)
    - 0.5.1-alpha.2 -> 0.5.1-local.3 (numbered prerelease, tag changed to local)
    - 0.5.1 -> 0.5.1-local.1 (stable to prerelease)

.PARAMETER Version
    Explicit version to use for all packages. When specified, this takes precedence
    over -IncrementVersion. Useful for testing specific versions or matching CI builds.
    For example: 0.9.0-local.119

.PARAMETER EnableFrameworkDebugging
    Packs a debuggable version of the packages with the WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
    compiler directive set to true. Appends '-debug' to the version suffix so debug packages
    are distinguishable from standard packages.

.PARAMETER CleanBuild
    Run dotnet clean before building to prevent stale DLLs. Off by default for faster iteration.

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
    ./scripts/Pack-LocalPackages.ps1 -Version 0.9.0-local.119
    Uses the specified version for all packages (no auto-increment).

.EXAMPLE
    ./scripts/Pack-LocalPackages.ps1 -EnableFrameworkDebugging
    Packs all packages with WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING defined.
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Clean = $true,

    [switch]$IncrementVersion = $true,

    [string]$Version,

    [switch]$EnableFrameworkDebugging,

    [switch]$CleanBuild
)

$ErrorActionPreference = "Stop"

# Import shared module
Import-Module (Join-Path $PSScriptRoot "lib" "PR-Readiness-Common.psm1") -Force

# Find repo root
$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Get-Location
}
$repoRoot = Split-Path $scriptDir -Parent

# Handle version: explicit -Version takes precedence over -IncrementVersion
$propsFile = Join-Path $repoRoot "Directory.Build.props"
$propsContent = Get-Content $propsFile -Raw

# Strip any leftover -debug suffix from a previous debug pack before version parsing
if ($propsContent -match '<Version>([^<]+)-debug</Version>') {
    $debugVersion = $Matches[1] + "-debug"
    $cleanVersion = $Matches[1]
    $propsContent = $propsContent -replace "<Version>$([regex]::Escape($debugVersion))</Version>", "<Version>$cleanVersion</Version>"
    Set-Content $propsFile $propsContent -NoNewline
    Write-Host "Stripped debug suffix: $debugVersion -> $cleanVersion" -ForegroundColor Yellow
}

if ($Version) {
    # Use explicit version
    if ($propsContent -match '<Version>([^<]+)</Version>') {
        $oldVersion = $Matches[1]
        $propsContent = $propsContent -replace "<Version>$([regex]::Escape($oldVersion))</Version>", "<Version>$Version</Version>"
        Set-Content $propsFile $propsContent -NoNewline

        Write-Host "Version set: $oldVersion -> $Version" -ForegroundColor Green
    }
    else {
        Write-Error "Could not find <Version> element in Directory.Build.props"
        exit 1
    }
}
elseif ($IncrementVersion) {
    # Match version like 0.5.1-alpha.2 or 0.5.1-local.2
    if ($propsContent -match '<Version>(\d+\.\d+\.\d+)-([a-z]+)\.(\d+)</Version>') {
        $baseVersion = $Matches[1]
        $prereleaseNum = [int]$Matches[3]
        $newPrereleaseNum = $prereleaseNum + 1
        $oldVersion = "$baseVersion-$($Matches[2]).$prereleaseNum"
        $newVersion = "$baseVersion-local.$newPrereleaseNum"

        $propsContent = $propsContent -replace "<Version>$([regex]::Escape($oldVersion))</Version>", "<Version>$newVersion</Version>"
        Set-Content $propsFile $propsContent -NoNewline

        Write-Host "Version incremented: $oldVersion -> $newVersion" -ForegroundColor Green
    }
    elseif ($propsContent -match '<Version>(\d+\.\d+\.\d+)-([a-z]+)</Version>') {
        # Match version like 0.9.0-local (prerelease without number)
        $baseVersion = $Matches[1]
        $oldVersion = "$baseVersion-$($Matches[2])"
        $newVersion = "$baseVersion-local.1"

        $propsContent = $propsContent -replace "<Version>$([regex]::Escape($oldVersion))</Version>", "<Version>$newVersion</Version>"
        Set-Content $propsFile $propsContent -NoNewline

        Write-Host "Version incremented: $oldVersion -> $newVersion" -ForegroundColor Green
    }
    elseif ($propsContent -match '<Version>(\d+\.\d+\.\d+)</Version>') {
        # No prerelease suffix, add one
        $baseVersion = $Matches[1]
        $oldVersion = $baseVersion
        $newVersion = "$baseVersion-local.1"

        $propsContent = $propsContent -replace "<Version>$([regex]::Escape($oldVersion))</Version>", "<Version>$newVersion</Version>"
        Set-Content $propsFile $propsContent -NoNewline

        Write-Host "Version incremented: $oldVersion -> $newVersion" -ForegroundColor Green
    }
    else {
        Write-Host "Could not parse version from Directory.Build.props" -ForegroundColor Yellow
        Write-Host "Expected format: X.Y.Z, X.Y.Z-prerelease, or X.Y.Z-prerelease.N" -ForegroundColor Yellow
    }
}

# Apply debug version suffix if framework debugging is enabled
if ($EnableFrameworkDebugging) {
    $propsContent = Get-Content $propsFile -Raw
    if ($propsContent -match '<Version>([^<]+)</Version>') {
        $currentVersion = $Matches[1]
        if ($currentVersion -notmatch '-debug$') {
            $debugVersion = "$currentVersion-debug"
            $propsContent = $propsContent -replace "<Version>$([regex]::Escape($currentVersion))</Version>", "<Version>$debugVersion</Version>"
            Set-Content $propsFile $propsContent -NoNewline
            Write-Host "Debug version: $currentVersion -> $debugVersion" -ForegroundColor Magenta
        }
    }
}

# Build extra pack arguments for compiler directives
$extraPackArgs = @()
if ($EnableFrameworkDebugging) {
    $extraPackArgs += '/p:DefineConstants="$(DefineConstants);WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING"'
}

$localPackagesDir = Join-Path $repoRoot "local-packages"
$srcDir = Join-Path $repoRoot "src"

# Read current version for header display
$currentVersion = if ((Get-Content $propsFile -Raw) -match '<Version>([^<]+)</Version>') { $Matches[1] } else { "unknown" }
$headerParams = @{ Config = $Configuration; Version = $currentVersion }
if ($EnableFrameworkDebugging) { $headerParams["Debug"] = "On" }
Write-WhizbangHeader -ScriptName "Local Pack" -Params $headerParams
Write-Host "Output: $localPackagesDir" -ForegroundColor DarkGray
Write-Host ""

# Clean if requested
if ($Clean) {
    Write-Host "Cleaning local-packages directory..." -ForegroundColor Yellow
    if (Test-Path $localPackagesDir) {
        Remove-Item "$localPackagesDir/*.nupkg" -Force -ErrorAction SilentlyContinue
        Remove-Item "$localPackagesDir/*.snupkg" -Force -ErrorAction SilentlyContinue
        Remove-Item "$localPackagesDir/*.zip" -Force -ErrorAction SilentlyContinue
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

# Filter out internal packages and sort alphabetically
$projects = $allProjects | Where-Object { $_.BaseName -notin $internalPackages } | Sort-Object BaseName
$skippedProjects = $allProjects | Where-Object { $_.BaseName -in $internalPackages } | Sort-Object BaseName

Write-Host "Found $($projects.Count) projects to pack:" -ForegroundColor Green
$projects | ForEach-Object { Write-Host "  - $($_.BaseName)" -ForegroundColor Gray }

if ($skippedProjects.Count -gt 0) {
    Write-Host ""
    Write-Host "Skipping $($skippedProjects.Count) internal packages (IsPackable=false):" -ForegroundColor DarkGray
    $skippedProjects | ForEach-Object { Write-Host "  - $($_.BaseName)" -ForegroundColor DarkGray }
}
Write-Host ""

$slnFile = Get-ChildItem -Path $repoRoot -Include "*.sln","*.slnx" -Depth 0 | Select-Object -First 1

# Clean before building to prevent stale DLLs from being packed (opt-in)
if ($CleanBuild) {
    Write-Host "Cleaning build output to prevent stale artifacts..." -ForegroundColor Cyan
    if ($slnFile) {
        dotnet clean $slnFile.FullName -c $Configuration --verbosity quiet 2>&1 | Out-Null
    }
}

# Build solution to ensure all projects have the new version
Write-Host "Building solution to ensure consistent version across all projects..." -ForegroundColor Cyan
if ($slnFile) {
    $buildOutput = dotnet build $slnFile.FullName -c $Configuration @extraPackArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        $buildOutput | Where-Object { $_ -match "error" } | ForEach-Object {
            Write-Host "  $_" -ForegroundColor Red
        }
        exit 1
    }
    Write-Host "Build succeeded." -ForegroundColor Green
} else {
    Write-Host "No solution file found (.sln or .slnx), building individual projects..." -ForegroundColor Yellow
    $buildFailed = $false
    foreach ($project in $projects) {
        $buildOutput = dotnet build $project.FullName -c $Configuration @extraPackArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  Build failed: $($project.BaseName)" -ForegroundColor Red
            $buildOutput | Where-Object { $_ -match "error" } | ForEach-Object {
                Write-Host "    $_" -ForegroundColor Red
            }
            $buildFailed = $true
        }
    }
    if ($buildFailed) {
        Write-Host "One or more projects failed to build." -ForegroundColor Red
        exit 1
    }
    Write-Host "Build succeeded." -ForegroundColor Green
}
Write-Host ""

$successCount = 0
$failCount = 0
$results = @()

foreach ($project in $projects) {
    $projectName = $project.BaseName
    Write-Host "Packing $projectName..." -ForegroundColor Cyan -NoNewline

    $output = dotnet pack $project.FullName -o $localPackagesDir -c $Configuration @extraPackArgs 2>&1
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

# List created packages alphabetically
$packages = Get-ChildItem -Path $localPackagesDir -Filter "*.nupkg" | Sort-Object Name
Write-Host "Packages in $localPackagesDir`:" -ForegroundColor Cyan
$packages | ForEach-Object {
    $size = [math]::Round($_.Length / 1KB, 1)
    Write-Host "  $($_.Name) ($size KB)" -ForegroundColor Gray
}

# Create zip archive of all packages
$packVersion = if ((Get-Content $propsFile -Raw) -match '<Version>([^<]+)</Version>') { $Matches[1] } else { "unknown" }
$zipDate = (Get-Date).ToString("yyyy-MM-dd")
$zipName = "${packVersion}_${zipDate}.zip"
$zipPath = Join-Path $localPackagesDir $zipName

$packFiles = @(Get-ChildItem -Path (Join-Path $localPackagesDir "*") -Include "*.nupkg","*.snupkg" -File)
if ($packFiles.Count -gt 0) {
    Compress-Archive -Path $packFiles.FullName -DestinationPath $zipPath -Force
    $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Zip: $zipPath ($zipSize MB)" -ForegroundColor Green
}

if ($failCount -gt 0) {
    exit 1
}
