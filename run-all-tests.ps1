#!/usr/bin/env pwsh
# Script to run all test projects and summarize results

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "WHIZBANG TEST SUITE" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$totalTests = 0
$totalPassed = 0
$totalFailed = 0
$totalSkipped = 0
$results = @()

# Find all test projects
$testProjects = Get-ChildItem -Path "tests","samples/ECommerce/tests","samples/ECommerce" -Recurse -Filter "*.csproj" |
    Where-Object { $_.Name -match "Tests\.csproj$" }

foreach ($project in $testProjects) {
    $projectName = $project.BaseName
    Write-Host "Running $projectName..." -ForegroundColor Yellow

    Push-Location $project.DirectoryName
    $output = dotnet run 2>&1 | Out-String
    Pop-Location

    # Parse output
    if ($output -match "Test run summary: (Passed|Failed)!") {
        $status = $matches[1]
    }

    if ($output -match "total:\s*(\d+)") {
        $total = [int]$matches[1]
        $totalTests += $total
    } else {
        $total = 0
    }

    if ($output -match "failed:\s*(\d+)") {
        $failed = [int]$matches[1]
        $totalFailed += $failed
    } else {
        $failed = 0
    }

    if ($output -match "succeeded:\s*(\d+)") {
        $succeeded = [int]$matches[1]
        $totalPassed += $succeeded
    } else {
        $succeeded = 0
    }

    if ($output -match "skipped:\s*(\d+)") {
        $skipped = [int]$matches[1]
        $totalSkipped += $skipped
    } else {
        $skipped = 0
    }

    $results += [PSCustomObject]@{
        Project = $projectName
        Status = $status
        Total = $total
        Passed = $succeeded
        Failed = $failed
        Skipped = $skipped
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "TEST RESULTS BY PROJECT" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""

foreach ($result in $results | Sort-Object Project) {
    $color = if ($result.Failed -eq 0) { "Green" } else { "Red" }
    $statusIcon = if ($result.Failed -eq 0) { "✓" } else { "✗" }

    Write-Host "$statusIcon " -ForegroundColor $color -NoNewline
    Write-Host ("{0,-50}" -f $result.Project) -NoNewline
    Write-Host (" {0,4}/{1,-4}" -f $result.Passed, $result.Total) -NoNewline

    if ($result.Failed -gt 0) {
        Write-Host (" ({0} failed)" -f $result.Failed) -ForegroundColor Red
    } elseif ($result.Skipped -gt 0) {
        Write-Host (" ({0} skipped)" -f $result.Skipped) -ForegroundColor Yellow
    } else {
        Write-Host ""
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "OVERALL SUMMARY" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "Total Tests:   $totalTests" -ForegroundColor White
Write-Host "Passed:        $totalPassed" -ForegroundColor Green
Write-Host "Failed:        $totalFailed" -ForegroundColor $(if ($totalFailed -eq 0) { "Green" } else { "Red" })
Write-Host "Skipped:       $totalSkipped" -ForegroundColor $(if ($totalSkipped -eq 0) { "Gray" } else { "Yellow" })

$passRate = if ($totalTests -gt 0) { [math]::Round(($totalPassed / $totalTests) * 100, 2) } else { 0 }
Write-Host "Pass Rate:     $passRate%" -ForegroundColor $(if ($passRate -ge 99) { "Green" } elseif ($passRate -ge 95) { "Yellow" } else { "Red" })
Write-Host ""
