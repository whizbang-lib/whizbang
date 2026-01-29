#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Unlists legacy NuGet packages that were published without SoftwareExtravaganza prefix.

.DESCRIPTION
    These packages are duplicates of the correctly-named packages and should be hidden
    from NuGet search results.

.PARAMETER ApiKey
    NuGet API key with "Unlist package" permission.
    Can also be set via NUGET_API_KEY environment variable.

.EXAMPLE
    ./scripts/Unlist-LegacyPackages.ps1 -ApiKey "your-api-key"

.EXAMPLE
    $env:NUGET_API_KEY = "your-api-key"
    ./scripts/Unlist-LegacyPackages.ps1

.NOTES
    Get an API key from: https://www.nuget.org/account/apikeys
    Ensure "Unlist package" permission is checked.
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ApiKey = $env:NUGET_API_KEY
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Error "API key required. Use -ApiKey parameter or set NUGET_API_KEY environment variable."
    exit 1
}

$Source = "https://api.nuget.org/v3/index.json"

$Packages = @(
    @{ Id = "Whizbang.CLI"; Version = "0.1.0" }
    @{ Id = "Whizbang.Data.Schema"; Version = "0.1.0" }
    @{ Id = "Whizbang.Generators.Shared"; Version = "0.1.0" }
    @{ Id = "Whizbang.Transports.AzureServiceBus"; Version = "0.1.0" }
    @{ Id = "Whizbang.Hosting.Azure.ServiceBus"; Version = "0.1.0" }
)

Write-Host "Unlisting legacy NuGet packages..." -ForegroundColor Cyan
Write-Host "==================================" -ForegroundColor Cyan
Write-Host ""

$failed = 0

foreach ($pkg in $Packages) {
    Write-Host "Unlisting $($pkg.Id) $($pkg.Version)..." -ForegroundColor Yellow

    try {
        dotnet nuget delete $pkg.Id $pkg.Version `
            --source $Source `
            --api-key $ApiKey `
            --non-interactive

        Write-Host "  Done" -ForegroundColor Green
    }
    catch {
        Write-Host "  Failed: $_" -ForegroundColor Red
        $failed++
    }

    Write-Host ""
}

Write-Host "==================================" -ForegroundColor Cyan

if ($failed -gt 0) {
    Write-Host "$failed package(s) failed to unlist" -ForegroundColor Red
    exit 1
}

Write-Host "All legacy packages unlisted!" -ForegroundColor Green
Write-Host ""
Write-Host "Note: Unlisted packages are still available by direct URL but won't appear in search results."
Write-Host "Users should migrate to SoftwareExtravaganza.* prefixed packages."
