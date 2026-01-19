#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Merges all test coverage files into a single merged coverage report.

.DESCRIPTION
    Finds all coverage.cobertura.xml files from test projects and merges them
    using dotnet-coverage merge. The merged file is written to merged_coverage.cobertura.xml
    in the repository root.

.EXAMPLE
    ./scripts/Merge-Coverage.ps1

.OUTPUTS
    Creates merged_coverage.cobertura.xml in the repository root
#>

$ErrorActionPreference = "Stop"

Write-Host "=== Merging All Coverage Files ===" -ForegroundColor Cyan

# Find all coverage files
$coverageFiles = Get-ChildItem -Path "tests" -Recurse -Filter "coverage.cobertura.xml" |
    Where-Object { $_.FullName -match "bin[\\/]Debug[\\/]net10\.0[\\/]TestResults" } |
    Select-Object -ExpandProperty FullName

if ($coverageFiles.Count -eq 0) {
    Write-Host "No coverage files found!" -ForegroundColor Red
    Write-Host "Run tests with coverage first using: ./scripts/Run-AllTestsWithCoverage.ps1"
    exit 1
}

Write-Host "Found $($coverageFiles.Count) coverage files" -ForegroundColor Green

# Create a temporary file list for dotnet-coverage
$tempFile = [System.IO.Path]::GetTempFileName()
$coverageFiles | Out-File -FilePath $tempFile -Encoding UTF8

try {
    # Merge using dotnet-coverage
    $output = dotnet-coverage merge -f cobertura -o merged_coverage.cobertura.xml "@$tempFile" 2>&1

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Merged coverage written to: merged_coverage.cobertura.xml" -ForegroundColor Green
    } else {
        Write-Host "Failed to merge coverage files" -ForegroundColor Red
        Write-Host $output
        exit 1
    }
}
finally {
    Remove-Item $tempFile -ErrorAction SilentlyContinue
}
