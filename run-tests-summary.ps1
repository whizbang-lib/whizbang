#!/usr/bin/env pwsh
# Script to run all tests in Whizbang solution and provide a clean summary

Write-Host "Running all tests in Whizbang.slnx..." -ForegroundColor Cyan
Write-Host ""

# Run dotnet test and capture output
$output = dotnet test Whizbang.slnx 2>&1 | Out-String

# Extract test run summary lines
$summaryLines = $output -split "`n" | Where-Object { $_ -match "Test run summary:|total:|failed:|succeeded:|skipped:|duration:" }

# Display summary
Write-Host "========================================" -ForegroundColor Green
Write-Host "TEST RESULTS SUMMARY" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""

$currentProject = ""
$totalTests = 0
$totalPassed = 0
$totalFailed = 0
$totalSkipped = 0

foreach ($line in $summaryLines) {
    if ($line -match "Test run summary: (Passed|Failed).*?- (.+\.dll)") {
        $status = $matches[1]
        $dllPath = $matches[2]
        $projectName = [System.IO.Path]::GetFileNameWithoutExtension($dllPath)
        $currentProject = $projectName

        $color = if ($status -eq "Passed") { "Green" } else { "Red" }
        Write-Host "`n$projectName" -ForegroundColor $color -NoNewline
        Write-Host " - $status" -ForegroundColor $color
    }
    elseif ($line -match "^\s*total:\s*(\d+)") {
        $total = [int]$matches[1]
        $totalTests += $total
        Write-Host "  Total: $total" -ForegroundColor White
    }
    elseif ($line -match "^\s*failed:\s*(\d+)") {
        $failed = [int]$matches[1]
        $totalFailed += $failed
        if ($failed -gt 0) {
            Write-Host "  Failed: $failed" -ForegroundColor Red
        } else {
            Write-Host "  Failed: $failed" -ForegroundColor Gray
        }
    }
    elseif ($line -match "^\s*succeeded:\s*(\d+)") {
        $succeeded = [int]$matches[1]
        $totalPassed += $succeeded
        Write-Host "  Passed: $succeeded" -ForegroundColor Green
    }
    elseif ($line -match "^\s*skipped:\s*(\d+)") {
        $skipped = [int]$matches[1]
        $totalSkipped += $skipped
        if ($skipped -gt 0) {
            Write-Host "  Skipped: $skipped" -ForegroundColor Yellow
        }
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

# Check build status
if ($output -match "0 Error\(s\)") {
    Write-Host "Build: SUCCESS (0 errors)" -ForegroundColor Green
} else {
    Write-Host "Build: FAILED (errors detected)" -ForegroundColor Red
}

Write-Host ""
