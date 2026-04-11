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

.PARAMETER Fast
    Skip analyzers, XML doc generation, code style enforcement, and zip creation for faster local iteration.

.PARAMETER Output
    Path to a consumer solution/repo root. After packing, deploys packages and updates
    version references there. Without this, the script works as before (pack to local-packages/ only).

.PARAMETER RestoreNuGet
    Requires -Output. Skips pack entirely. Looks up the latest nuget.org version for each
    Whizbang package and replaces local version references in the consumer repo. Clears NuGet cache.

.EXAMPLE
    ./scripts/Pack-LocalPackages.ps1
    Packs all packages in Debug configuration.

.EXAMPLE
    ./scripts/Pack-LocalPackages.ps1 -Output ~/src/JDNext
    Packs all packages and deploys them to the JDNext repo.

.EXAMPLE
    ./scripts/Pack-LocalPackages.ps1 -RestoreNuGet -Output ~/src/JDNext
    Restores JDNext to the latest nuget.org versions (no pack).

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

    [switch]$CleanBuild,

    [bool]$Fast = $true,

    [switch]$NoCopy,

    [string]$Output,

    [switch]$RestoreNuGet
)

$ErrorActionPreference = "Stop"

# Import shared module
Import-Module (Join-Path $PSScriptRoot "lib" "PR-Readiness-Common.psm1") -Force

# --- Helper functions for -Output and -RestoreNuGet ---

function Find-LocalPackageSource {
    param([string]$ConsumerRoot)

    $nugetConfig = Join-Path $ConsumerRoot "nuget.config"
    if (-not (Test-Path $nugetConfig)) {
        Write-Host "No nuget.config found at $ConsumerRoot" -ForegroundColor Red
        Write-Host "Add a nuget.config with a local package source:" -ForegroundColor Yellow
        Write-Host @"
  <packageSources>
    <add key="local-whizbang" value=".local-whizbang-packages" />
  </packageSources>
"@ -ForegroundColor Gray
        return $null
    }

    [xml]$config = Get-Content $nugetConfig
    $sources = $config.configuration.packageSources.add | Where-Object {
        $_.value -and $_.value -notmatch '^https?://'
    }

    if (-not $sources -or $sources.Count -eq 0) {
        Write-Host "No local package sources found in $nugetConfig" -ForegroundColor Red
        Write-Host "Add a local source for Whizbang packages:" -ForegroundColor Yellow
        Write-Host '  <add key="local-whizbang" value=".local-whizbang-packages" />' -ForegroundColor Gray
        return $null
    }

    # Prefer a source with "whizbang" in the key
    $whizbangSource = $sources | Where-Object { $_.key -match 'whizbang' } | Select-Object -First 1
    $source = if ($whizbangSource) { $whizbangSource } else { $sources | Select-Object -First 1 }

    $sourcePath = $source.value
    if (-not [System.IO.Path]::IsPathRooted($sourcePath)) {
        $sourcePath = Join-Path (Split-Path $nugetConfig -Parent) $sourcePath
    }
    $sourcePath = [System.IO.Path]::GetFullPath($sourcePath)

    Write-Host "Local package source: $($source.key) -> $sourcePath" -ForegroundColor Gray
    return $sourcePath
}

function Find-WhizbangVersionFiles {
    param([string]$ConsumerRoot)

    $entries = @()
    $whizbangPattern = 'SoftwareExtravaganza\.Whizbang\.'

    # Check if CPM is enabled
    $dirBuildProps = Join-Path $ConsumerRoot "Directory.Build.props"
    $useCpm = $false
    if (Test-Path $dirBuildProps) {
        $content = Get-Content $dirBuildProps -Raw
        if ($content -match '<ManagePackageVersionsCentrally>\s*true\s*</ManagePackageVersionsCentrally>') {
            $useCpm = $true
        }
    }

    if ($useCpm) {
        # Scan Directory.Packages.props for <PackageVersion> entries
        $packagesProps = Join-Path $ConsumerRoot "Directory.Packages.props"
        if (Test-Path $packagesProps) {
            $content = Get-Content $packagesProps -Raw
            $matches = [regex]::Matches($content, '<PackageVersion\s+Include="(SoftwareExtravaganza\.Whizbang\.[^"]+)"\s+Version="([^"]+)"')
            foreach ($m in $matches) {
                $entries += [PSCustomObject]@{
                    FilePath = $packagesProps
                    PackageName = $m.Groups[1].Value
                    CurrentVersion = $m.Groups[2].Value
                }
            }
        }
    }

    # Also scan .csproj files for direct PackageReference with Version (non-CPM or mixed)
    $csprojFiles = Get-ChildItem -Path $ConsumerRoot -Filter "*.csproj" -Recurse |
        Where-Object { $_.FullName -notmatch '[/\\](obj|bin)[/\\]' }

    foreach ($csproj in $csprojFiles) {
        $content = Get-Content $csproj.FullName -Raw
        $matches = [regex]::Matches($content, '<PackageReference\s+Include="(SoftwareExtravaganza\.Whizbang\.[^"]+)"[^>]*\sVersion="([^"]+)"')
        foreach ($m in $matches) {
            $version = $m.Groups[2].Value
            # Check for MSBuild property indirection
            if ($version -match '^\$\(([^)]+)\)$') {
                $propName = $Matches[1]
                # Search for property definition
                $propFiles = @($dirBuildProps)
                $dirBuildTargets = Join-Path $ConsumerRoot "Directory.Build.targets"
                if (Test-Path $dirBuildTargets) { $propFiles += $dirBuildTargets }
                foreach ($propFile in $propFiles) {
                    if (Test-Path $propFile) {
                        $propContent = Get-Content $propFile -Raw
                        if ($propContent -match "<$propName>([^<]+)</$propName>") {
                            $entries += [PSCustomObject]@{
                                FilePath = $propFile
                                PackageName = $m.Groups[1].Value
                                CurrentVersion = $Matches[1]
                            }
                        }
                    }
                }
            } else {
                $entries += [PSCustomObject]@{
                    FilePath = $csproj.FullName
                    PackageName = $m.Groups[1].Value
                    CurrentVersion = $version
                }
            }
        }
    }

    return $entries
}

function Update-WhizbangVersions {
    param(
        [PSCustomObject[]]$VersionEntries,
        [string]$NewVersion
    )

    $count = 0
    $grouped = $VersionEntries | Group-Object FilePath

    foreach ($group in $grouped) {
        $filePath = $group.Name
        $content = Get-Content $filePath -Raw
        $changed = $false

        foreach ($entry in $group.Group) {
            if ($entry.CurrentVersion -eq $NewVersion) { continue }

            $escapedOld = [regex]::Escape($entry.CurrentVersion)
            $escapedName = [regex]::Escape($entry.PackageName)

            # Replace in PackageVersion (CPM) or PackageReference elements
            $pattern = "(Include=""$escapedName""[^>]*Version="")$escapedOld("")"
            $newContent = [regex]::Replace($content, $pattern, "`${1}$NewVersion`${2}")

            if ($newContent -ne $content) {
                $shortName = $entry.PackageName -replace '^SoftwareExtravaganza\.Whizbang\.', ''
                Write-Host "  $shortName`: $($entry.CurrentVersion) -> $NewVersion" -ForegroundColor Gray
                $content = $newContent
                $changed = $true
                $count++
            }
        }

        if ($changed) {
            Set-Content $filePath $content -NoNewline
        }
    }

    return $count
}

function Copy-WhizbangPackages {
    param([string]$SourceDir, [string]$DestDir)

    if (-not (Test-Path $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir | Out-Null
    }

    # Clear old Whizbang packages
    Get-ChildItem -Path $DestDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'SoftwareExtravaganza\.Whizbang\.' -or $_.Name -match '^\d+\.\d+\.\d+' } |
        Remove-Item -Force

    # Copy new packages
    $files = Get-ChildItem -Path $SourceDir -File | Where-Object { $_.Name -match '\.(nupkg|snupkg|zip)$' }
    $files | Copy-Item -Destination $DestDir -Force
    return $files.Count
}

function Clear-WhizbangNuGetCache {
    $globalPackages = Join-Path $env:HOME ".nuget" "packages"
    if (-not (Test-Path $globalPackages)) { return 0 }

    $dirs = Get-ChildItem -Path $globalPackages -Directory -Filter "softwareextravaganza.whizbang.*" -ErrorAction SilentlyContinue
    $count = 0
    foreach ($dir in $dirs) {
        Remove-Item $dir.FullName -Recurse -Force
        $count++
    }
    return $count
}

function Get-LatestNuGetVersions {
    param([string[]]$PackageNames)

    $versions = @{}

    # All Whizbang packages share the same version — look up Core first
    $corePackage = $PackageNames | Where-Object { $_ -match '\.Core$' } | Select-Object -First 1
    if (-not $corePackage) { $corePackage = $PackageNames | Select-Object -First 1 }

    $coreVersion = Get-SingleNuGetVersion $corePackage
    if (-not $coreVersion) {
        Write-Error "Failed to look up latest version for $corePackage from nuget.org"
        exit 1
    }

    # Use the same version for all packages
    foreach ($name in $PackageNames) {
        $versions[$name] = $coreVersion
    }

    Write-Host "Latest nuget.org version: $coreVersion" -ForegroundColor Green
    return $versions
}

function Get-SingleNuGetVersion {
    param([string]$PackageName)

    $id = $PackageName.ToLower()
    $url = "https://api.nuget.org/v3-flatcontainer/$id/index.json"

    try {
        $response = Invoke-RestMethod -Uri $url -ErrorAction Stop
        # Get the latest non-local version
        $allVersions = $response.versions | Where-Object { $_ -notmatch 'local' }
        return $allVersions | Select-Object -Last 1
    } catch {
        Write-Host "Failed to query nuget.org for $PackageName`: $_" -ForegroundColor Red
        return $null
    }
}

# --- End helper functions ---

# Find repo root
$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Get-Location
}
$repoRoot = Split-Path $scriptDir -Parent

# Validate -Output and handle -RestoreNuGet early exit
if ($RestoreNuGet -and -not $Output) {
    Write-Error "-RestoreNuGet requires -Output to specify the consumer repo path"
    exit 1
}
if ($Output) {
    # Resolve ~ and relative paths to absolute
    $Output = [System.IO.Path]::GetFullPath($Output.Replace('~', $env:HOME))
    if (-not (Test-Path $Output)) {
        Write-Error "Consumer repo path not found: $Output"
        exit 1
    }
}

if ($RestoreNuGet) {
    Write-WhizbangHeader -ScriptName "Restore NuGet" -Params @{ Target = (Split-Path $Output -Leaf) }

    $versionFiles = Find-WhizbangVersionFiles -ConsumerRoot $Output
    if ($versionFiles.Count -eq 0) {
        Write-Host "No Whizbang package references found in $Output" -ForegroundColor Yellow
        exit 0
    }

    Write-Host "Found $($versionFiles.Count) Whizbang package references" -ForegroundColor Cyan
    $packageNames = @($versionFiles | ForEach-Object { $_.PackageName } | Sort-Object -Unique)

    Write-Host "Looking up latest versions from nuget.org..." -ForegroundColor Cyan
    $latestVersions = Get-LatestNuGetVersions -PackageNames $packageNames

    Write-Host "Updating version references..." -ForegroundColor Cyan
    # Use the version from Core (all share same version)
    $nugetVersion = $latestVersions[$packageNames[0]]
    $updated = Update-WhizbangVersions -VersionEntries $versionFiles -NewVersion $nugetVersion

    $cleared = Clear-WhizbangNuGetCache
    Write-Host "Cleared $cleared NuGet cache entries" -ForegroundColor Gray

    Write-Host ""
    Write-Host "Restored $updated package references to v$nugetVersion" -ForegroundColor Green
    exit 0
}

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
if ($Fast) {
    $extraPackArgs += '/p:RunAnalyzers=false'
    $extraPackArgs += '/p:GenerateDocumentationFile=false'
    $extraPackArgs += '/p:EnforceCodeStyleInBuild=false'
}

$localPackagesDir = Join-Path $repoRoot "local-packages"
$srcDir = Join-Path $repoRoot "src"

# Read current version for header display
$currentVersion = if ((Get-Content $propsFile -Raw) -match '<Version>([^<]+)</Version>') { $Matches[1] } else { "unknown" }
$headerParams = @{ Config = $Configuration; Version = $currentVersion }
if ($EnableFrameworkDebugging) { $headerParams["Debug"] = "On" }
if ($Fast) { $headerParams["Fast"] = "On" }
if ($Output) { $headerParams["Deploy"] = Split-Path $Output -Leaf }
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
$totalStart = Get-Date
$cleanTime = [TimeSpan]::Zero
$buildTime = [TimeSpan]::Zero
$packTime = [TimeSpan]::Zero
$zipTime = [TimeSpan]::Zero
$aborted = $false
$deployFailed = $false

# Clean before building to prevent stale DLLs from being packed (opt-in)
if ($CleanBuild) {
    Write-Host "Cleaning build output to prevent stale artifacts..." -ForegroundColor Cyan
    $cleanStart = Get-Date
    if ($slnFile) {
        dotnet clean $slnFile.FullName -c $Configuration --verbosity quiet 2>&1 | Out-Null
    }
    $cleanTime = (Get-Date) - $cleanStart
    Write-Host "Clean completed in $([math]::Round($cleanTime.TotalSeconds, 1))s" -ForegroundColor DarkGray
}

# Build solution to ensure all projects have the new version
Write-Host "Building solution..." -ForegroundColor Cyan
$buildStart = Get-Date
if ($slnFile) {
    $buildOutput = dotnet build $slnFile.FullName -c $Configuration @extraPackArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        $buildOutput | Where-Object { $_ -match "error" } | ForEach-Object {
            Write-Host "  $_" -ForegroundColor Red
        }
        $aborted = $true
    }
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
        $aborted = $true
    }
}
$buildTime = (Get-Date) - $buildStart
if (-not $aborted) {
    Write-Host "Build succeeded in $([math]::Round($buildTime.TotalSeconds, 1))s" -ForegroundColor Green
}
Write-Host ""

$successCount = 0
$failCount = 0
$results = @()
$packStart = Get-Date

if (-not $aborted) {
foreach ($project in $projects) {
    $projectName = $project.BaseName
    Write-Host "Packing $projectName..." -ForegroundColor Cyan -NoNewline

    $packOutput = dotnet pack $project.FullName -o $localPackagesDir -c $Configuration --no-build @extraPackArgs 2>&1
    $exitCode = $LASTEXITCODE

    # Check for success (package created)
    $packageCreated = $packOutput | Select-String "Successfully created package"

    if ($packageCreated) {
        Write-Host " OK" -ForegroundColor Green
        $successCount++
        $results += [PSCustomObject]@{
            Project = $projectName
            Status = "Success"
        }
    } else {
        # Check if it's just the NU5017 error (no content) but package was still created
        $nu5017 = $packOutput | Select-String "NU5017"
        if ($nu5017 -and ($packOutput | Select-String "Successfully created package")) {
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
            $packOutput | Where-Object { $_ -match "error" } | ForEach-Object {
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
$packTime = (Get-Date) - $packStart

Write-Host ""

# List created packages alphabetically
$packages = Get-ChildItem -Path $localPackagesDir -Filter "*.nupkg" -ErrorAction SilentlyContinue | Sort-Object Name
if ($packages) {
    Write-Host "Packages in $localPackagesDir`:" -ForegroundColor Cyan
    $packages | ForEach-Object {
        $size = [math]::Round($_.Length / 1KB, 1)
        Write-Host "  $($_.Name) ($size KB)" -ForegroundColor Gray
    }
}

# Create zip archive of all packages
$packVersion = if ((Get-Content $propsFile -Raw) -match '<Version>([^<]+)</Version>') { $Matches[1] } else { "unknown" }
$zipDate = (Get-Date).ToString("yyyy-MM-dd")
$zipName = "${packVersion}_${zipDate}.zip"
$zipPath = Join-Path $localPackagesDir $zipName

$zipStart = Get-Date
$packFiles = @(Get-ChildItem -Path (Join-Path $localPackagesDir "*") -Include "*.nupkg","*.snupkg" -File -ErrorAction SilentlyContinue)
if ($packFiles.Count -gt 0) {
    Compress-Archive -Path $packFiles.FullName -DestinationPath $zipPath -Force
    $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Zip: $zipPath ($zipSize MB)" -ForegroundColor Green
}
$zipTime = (Get-Date) - $zipStart

# Deploy to consumer repo if -Output specified
$deployTime = [TimeSpan]::Zero
$deployFailed = $false
if ($Output) {
    Write-Host ""
    Write-Host "Deploying to $(Split-Path $Output -Leaf)..." -ForegroundColor Cyan
    $deployStart = Get-Date

    # Find local package source
    $destDir = Find-LocalPackageSource -ConsumerRoot $Output
    if (-not $destDir) {
        Write-Host "Deploy aborted: no local package source found." -ForegroundColor Red
        $deployFailed = $true
    } else {
        # Copy packages
        $copied = Copy-WhizbangPackages -SourceDir $localPackagesDir -DestDir $destDir
        Write-Host "Copied $copied files to $destDir" -ForegroundColor Green

        # Update version references
        $versionFiles = Find-WhizbangVersionFiles -ConsumerRoot $Output
        if ($versionFiles.Count -gt 0) {
            Write-Host "Updating $($versionFiles.Count) version references..." -ForegroundColor Cyan
            $updated = Update-WhizbangVersions -VersionEntries $versionFiles -NewVersion $packVersion
            Write-Host "Updated $updated version references to v$packVersion" -ForegroundColor Green
        } else {
            Write-Host "No Whizbang version references found to update" -ForegroundColor Yellow
        }

        # Clear NuGet cache
        $cleared = Clear-WhizbangNuGetCache
        if ($cleared -gt 0) {
            Write-Host "Cleared $cleared NuGet cache entries" -ForegroundColor Gray
        }
    }

    $deployTime = (Get-Date) - $deployStart
}

} # end if (-not $aborted)

# Runtime summary (always printed)
$packVersion = if ((Get-Content $propsFile -Raw) -match '<Version>([^<]+)</Version>') { $Matches[1] } else { "unknown" }
$totalTime = (Get-Date) - $totalStart
$hasFailed = $aborted -or $failCount -gt 0 -or $deployFailed
Write-Host ""
Write-Host "===============================" -ForegroundColor Cyan
if ($aborted) {
    Write-Host "ABORTED — build failed" -ForegroundColor Red
} else {
    Write-Host "Results: $successCount succeeded, $failCount failed" -ForegroundColor $(if ($failCount -gt 0) { "Yellow" } else { "Green" })
}
if ($deployFailed) {
    Write-Host "Deploy: FAILED" -ForegroundColor Red
} elseif ($Output -and -not $aborted) {
    Write-Host "Deploy: OK" -ForegroundColor Green
}
Write-Host ""
Write-Host "Timing:" -ForegroundColor Cyan
if ($CleanBuild) { Write-Host "  Clean:   $([math]::Round($cleanTime.TotalSeconds, 1))s" -ForegroundColor Gray }
Write-Host "  Build:   $([math]::Round($buildTime.TotalSeconds, 1))s" -ForegroundColor Gray
if (-not $aborted) {
    Write-Host "  Pack:    $([math]::Round($packTime.TotalSeconds, 1))s" -ForegroundColor Gray
    Write-Host "  Zip:     $([math]::Round($zipTime.TotalSeconds, 1))s" -ForegroundColor Gray
    if ($Output) { Write-Host "  Deploy:  $([math]::Round($deployTime.TotalSeconds, 1))s" -ForegroundColor Gray }
}
Write-Host "  Total:   $([math]::Round($totalTime.TotalSeconds, 1))s" -ForegroundColor $(if ($hasFailed) { "Yellow" } else { "Green" })
Write-Host ""
$versionBytes = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($packVersion))
Write-Host "$packVersion" -ForegroundColor White -NoNewline
Write-Host " $(if ($hasFailed) { 'failed' } else { 'completed' }) at $(Get-Date -Format 'h:mm:ss tt')" -ForegroundColor DarkGray
# Copy version to clipboard via OSC 52 (supported by most modern terminals)
if (-not $NoCopy -and -not $hasFailed -and -not $Output) {
    [Console]::Write("`e]52;c;$versionBytes`a")
}

if ($hasFailed) {
    exit 1
}
