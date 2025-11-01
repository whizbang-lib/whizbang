#!/usr/bin/env pwsh
# Show all WHIZ diagnostics from the Whizbang source generator

Write-Host "🔍 Running Whizbang source generator diagnostics..." -ForegroundColor Cyan
Write-Host ""

$output = dotnet build tests/Whizbang.Core.Tests/Whizbang.Core.Tests.csproj --no-incremental -v:detailed 2>&1 | Out-String

$diagnostics = $output -split "`n" | Where-Object { $_ -match "(info|warning|error) WHIZ\d+" }

if ($diagnostics.Count -eq 0) {
    Write-Host "No WHIZ diagnostics found. The generator may not have run (build might be cached)." -ForegroundColor Yellow
    Write-Host "Try making a change to a receptor or running: dotnet clean" -ForegroundColor Yellow
} else {
    $diagnostics | ForEach-Object {
        $line = $_.Trim()
        if ($line -match "info WHIZ") {
            Write-Host $line -ForegroundColor Green
        } elseif ($line -match "warning WHIZ") {
            Write-Host $line -ForegroundColor Yellow
        } elseif ($line -match "error WHIZ") {
            Write-Host $line -ForegroundColor Red
        } else {
            Write-Host $line
        }
    }
}

Write-Host ""
