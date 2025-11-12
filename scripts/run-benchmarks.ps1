#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs Whizbang benchmarks and optionally adds results to BENCHMARK_RESULTS.md

.DESCRIPTION
    This script:
    1. Runs all benchmarks with combined report
    2. Displays a summary of key metrics
    3. Prompts to add results to BENCHMARK_RESULTS.md
    4. Formats and appends results with timestamp

.PARAMETER Filter
    Optional filter for specific benchmarks (e.g., "*Throughput*")

.PARAMETER Quick
    Run with --job dry for quick validation

.EXAMPLE
    ./scripts/run-benchmarks.ps1

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -Filter "*Throughput*"

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -Quick
#>

param(
    [string]$Filter = "*",
    [switch]$Quick
)

$ErrorActionPreference = "Stop"

# Paths
$repoRoot = Split-Path $PSScriptRoot -Parent  # Go up one level from scripts/ to repo root
$benchmarkDir = Join-Path $repoRoot "benchmarks/Whizbang.Benchmarks"
$resultsDir = Join-Path $benchmarkDir "BenchmarkDotNet.Artifacts/results"
$docFile = Join-Path $repoRoot "docs/BENCHMARK_RESULTS.md"

# Verify benchmark directory exists
if (-not (Test-Path $benchmarkDir)) {
    Write-Host "❌ Benchmark directory not found: $benchmarkDir" -ForegroundColor Red
    Write-Host "Expected structure: <repo-root>/benchmarks/Whizbang.Benchmarks" -ForegroundColor Yellow
    exit 1
}

Write-Host "🚀 Running Whizbang Benchmarks" -ForegroundColor Cyan
Write-Host ""

# Navigate to benchmark directory
Push-Location $benchmarkDir

try {
    # Build command arguments
    $dotnetArgs = @("run", "-c", "Release")

    if ($Quick) {
        $dotnetArgs += "--job"
        $dotnetArgs += "dry"
        Write-Host "⚡ Quick mode: Running with --job dry" -ForegroundColor Yellow
    }

    $dotnetArgs += "--join"

    Write-Host "📊 Command: dotnet $($dotnetArgs -join ' ')" -ForegroundColor Gray
    Write-Host "📁 Working directory: $benchmarkDir" -ForegroundColor Gray
    Write-Host ""

    # Run benchmarks
    Write-Host "Running benchmarks (this may take a while)..." -ForegroundColor Yellow

    # Pipe the filter to dotnet stdin
    $Filter | & dotnet $dotnetArgs

    Write-Host ""
    Write-Host "✅ Benchmarks completed!" -ForegroundColor Green
    Write-Host ""

    # Find the combined report
    $reportFile = Join-Path $resultsDir "Combined-report.md"

    if (-not (Test-Path $reportFile)) {
        Write-Host "❌ Could not find Combined-report.md at: $reportFile" -ForegroundColor Red
        exit 1
    }

    # Parse and display key metrics
    Write-Host "📈 Key Results Summary:" -ForegroundColor Cyan
    Write-Host "=" * 80 -ForegroundColor Gray

    $reportContent = Get-Content $reportFile -Raw

    # Extract throughput benchmarks
    $throughputMatches = [regex]::Matches($reportContent, '(?m)^\|\s*([^|]+?Throughput[^|]*?)\s*\|\s*([\d.,]+\s*(?:ns|μs|ms|s))\s*\|.*?\|\s*([\d.,]+\s*[KMGT]?B)\s*\|')

    if ($throughputMatches.Count -gt 0) {
        Write-Host ""
        Write-Host "Throughput Benchmarks:" -ForegroundColor Yellow
        foreach ($match in $throughputMatches | Select-Object -First 10) {
            $name = $match.Groups[1].Value.Trim()
            $time = $match.Groups[2].Value.Trim()
            $allocated = $match.Groups[3].Value.Trim()
            Write-Host "  • $name" -ForegroundColor White
            Write-Host "    Time: $time, Allocated: $allocated" -ForegroundColor Gray
        }
    }

    # Extract other key benchmarks
    $otherMatches = [regex]::Matches($reportContent, '(?m)^\|\s*([^|]+?)\s*\|\s*([\d.,]+\s*(?:ns|μs|ms|s))\s*\|.*?\|\s*([\d.,]+\s*[KMGT]?B)\s*\|')

    Write-Host ""
    Write-Host "Other Key Benchmarks:" -ForegroundColor Yellow
    $displayed = 0
    foreach ($match in $otherMatches) {
        $name = $match.Groups[1].Value.Trim()

        # Skip if already shown in throughput
        if ($name -match "Throughput") { continue }

        # Skip headers
        if ($name -match "Method|^-+$") { continue }

        $time = $match.Groups[2].Value.Trim()
        $allocated = $match.Groups[3].Value.Trim()

        Write-Host "  • $name" -ForegroundColor White
        Write-Host "    Time: $time, Allocated: $allocated" -ForegroundColor Gray

        $displayed++
        if ($displayed -ge 10) { break }
    }

    Write-Host ""
    Write-Host "=" * 80 -ForegroundColor Gray
    Write-Host ""
    Write-Host "📄 Full report: $reportFile" -ForegroundColor Cyan
    Write-Host ""

    # Prompt to add to BENCHMARK_RESULTS.md
    $response = Read-Host "Would you like to add these results to BENCHMARK_RESULTS.md? (y/N)"

    if ($response -eq 'y' -or $response -eq 'Y') {
        Write-Host ""
        Write-Host "📝 Adding results to BENCHMARK_RESULTS.md..." -ForegroundColor Yellow

        # Get current date
        $date = Get-Date -Format "MMMM d, yyyy"
        $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

        # Prompt for description
        Write-Host ""
        $description = Read-Host "Enter a description for this benchmark run (optional)"

        # Build the new section
        $newSection = @"

---

## Benchmark Run - $date

**Date**: $timestamp
**Mode**: $(if ($Quick) { "Quick (--job dry)" } else { "Full Release" })
$(if ($description) { "**Description**: $description" })

### Results Summary

"@

        # Add throughput results
        if ($throughputMatches.Count -gt 0) {
            $newSection += @"

#### Throughput Benchmarks

| Benchmark | Time | Allocated |
|-----------|------|-----------|

"@
            foreach ($match in $throughputMatches | Select-Object -First 15) {
                $name = $match.Groups[1].Value.Trim()
                $time = $match.Groups[2].Value.Trim()
                $allocated = $match.Groups[3].Value.Trim()
                $newSection += "| $name | $time | $allocated |`n"
            }
        }

        # Add link to full report
        $newSection += @"

### Full Report

See [Combined-report.md](../benchmarks/Whizbang.Benchmarks/BenchmarkDotNet.Artifacts/results/Combined-report.md) for complete results.

"@

        # Append to file
        Add-Content -Path $docFile -Value $newSection

        Write-Host ""
        Write-Host "✅ Results added to BENCHMARK_RESULTS.md" -ForegroundColor Green
        Write-Host "📄 File: $docFile" -ForegroundColor Cyan
    } else {
        Write-Host ""
        Write-Host "ℹ️  Results not added to BENCHMARK_RESULTS.md" -ForegroundColor Gray
    }

} finally {
    Pop-Location
}

Write-Host ""
Write-Host "✨ Done!" -ForegroundColor Green
