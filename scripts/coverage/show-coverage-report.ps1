#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Displays a coverage report from the merged coverage file.

.DESCRIPTION
    Parses the merged_coverage.cobertura.xml file and displays package-level coverage
    statistics, highlighting classes that need additional test coverage.

.PARAMETER DetailLevel
    Level of detail to show: Summary, Package, or Class. Default is Package.

.EXAMPLE
    ./scripts/Show-CoverageReport.ps1
    ./scripts/Show-CoverageReport.ps1 -DetailLevel Class
#>

param(
    [ValidateSet("Summary", "Package", "Class")]
    [string]$DetailLevel = "Package"
)

$ErrorActionPreference = "Stop"

$coverageFile = "merged_coverage.cobertura.xml"

if (-not (Test-Path $coverageFile)) {
    Write-Host "Coverage file not found: $coverageFile" -ForegroundColor Red
    Write-Host "Run tests and merge coverage first:"
    Write-Host "  ./scripts/Run-AllTestsWithCoverage.ps1"
    Write-Host "  ./scripts/Merge-Coverage.ps1"
    exit 1
}

[xml]$coverage = Get-Content $coverageFile

Write-Host "=== Coverage Report from $coverageFile ===" -ForegroundColor Cyan
Write-Host ""

$packages = $coverage.coverage.packages.package |
    Where-Object { $_.name -match "^Whizbang\." -and $_.name -notmatch "\.Tests$" }

$packagesNeedingWork = @()
$packages100Percent = @()

foreach ($package in $packages) {
    $lineCoverage = [double]$package.'line-rate' * 100
    $branchCoverage = [double]$package.'branch-rate' * 100

    if ($lineCoverage -eq 100 -and $branchCoverage -eq 100) {
        $status = "✓ 100%"
        $color = "Green"
        $packages100Percent += $package.name
    }
    elseif ($lineCoverage -ge 95 -and $branchCoverage -ge 95) {
        $status = "⚠ Near"
        $color = "Yellow"
        $packagesNeedingWork += $package.name
    }
    else {
        $status = "✗ Needs Work"
        $color = "Red"
        $packagesNeedingWork += $package.name
    }

    $packageName = $package.name.PadRight(45)
    Write-Host "$packageName Line: $("{0,5:F1}" -f $lineCoverage)% | Branch: $("{0,5:F1}" -f $branchCoverage)% $status" -ForegroundColor $color

    if ($DetailLevel -eq "Class" -and ($lineCoverage -lt 100 -or $branchCoverage -lt 100)) {
        $classesNeedingWork = $package.classes.class |
            Where-Object {
                $classLineCoverage = [double]$_.'line-rate' * 100
                $classBranchCoverage = [double]$_.'branch-rate' * 100
                $className = $_.name.Split('.')[-1]

                # Skip perfect coverage and compiler-generated
                ($classLineCoverage -lt 100 -or $classBranchCoverage -lt 100) -and
                -not ($className.StartsWith('<') -or $className.StartsWith('__'))
            }

        if ($classesNeedingWork) {
            Write-Host "  Classes needing coverage:" -ForegroundColor Gray
            foreach ($class in $classesNeedingWork | Select-Object -First 10) {
                $className = $class.name.Split('.')[-1].PadRight(50)
                $classLineCoverage = [double]$class.'line-rate' * 100
                $classBranchCoverage = [double]$class.'branch-rate' * 100
                Write-Host "    • $className Line: $("{0,5:F1}" -f $classLineCoverage)% | Branch: $("{0,5:F1}" -f $classBranchCoverage)%" -ForegroundColor Gray
            }
            if ($classesNeedingWork.Count -gt 10) {
                Write-Host "    ... and $($classesNeedingWork.Count - 10) more classes" -ForegroundColor Gray
            }
            Write-Host ""
        }
    }
}

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host "Total Packages: $($packages.Count)"
Write-Host "100% Coverage: $($packages100Percent.Count)" -ForegroundColor Green
Write-Host "Needs Work: $($packagesNeedingWork.Count)" -ForegroundColor $(if ($packagesNeedingWork.Count -gt 0) { "Red" } else { "Green" })

if ($packagesNeedingWork.Count -gt 0) {
    Write-Host ""
    Write-Host "Packages needing work:" -ForegroundColor Yellow
    $packagesNeedingWork | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
}
