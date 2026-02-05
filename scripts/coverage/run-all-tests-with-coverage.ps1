#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs all test projects with code coverage collection.

.DESCRIPTION
    Executes all test projects in the solution and collects coverage data
    from each project. Coverage files are written to each project's TestResults directory.

.EXAMPLE
    ./scripts/Run-AllTestsWithCoverage.ps1
#>

$ErrorActionPreference = "Stop"

Write-Host "=== Running All Tests with Coverage ===" -ForegroundColor Cyan
Write-Host ""

$testProjects = @(
    "Whizbang.Core.Tests",
    "Whizbang.Observability.Tests",
    "Whizbang.Policies.Tests",
    "Whizbang.Execution.Tests",
    "Whizbang.Partitioning.Tests",
    "Whizbang.Transports.Tests",
    "Whizbang.Sequencing.Tests",
    "Whizbang.Data.Tests",
    "Whizbang.Data.Postgres.Tests",
    "Whizbang.Transports.Mutations.Tests",
    "Whizbang.Transports.HotChocolate.Tests",
    "Whizbang.Transports.FastEndpoints.Tests"
)

$passed = 0
$failed = 0

foreach ($project in $testProjects) {
    Write-Host "Running: $project" -ForegroundColor Yellow

    Push-Location "tests/$project"

    try {
        $output = dotnet run -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml 2>&1

        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ Passed" -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  ✗ Failed (exit code: $LASTEXITCODE)" -ForegroundColor Red
            $failed++
        }
    }
    finally {
        Pop-Location
    }
}

Write-Host ""
Write-Host "=== Test Summary ===" -ForegroundColor Cyan
Write-Host "Total Projects: $($testProjects.Count)"
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })

exit $(if ($failed -gt 0) { 1 } else { 0 })
