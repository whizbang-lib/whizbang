#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Unlists packages from nuget.org that should not have been published.

.DESCRIPTION
    Unlists two categories of packages:

    1. INTERNAL PACKAGES (SoftwareExtravaganza.Whizbang.*):
       - Whizbang.Generators.Shared (ILMerged into generator packages)
       - Whizbang.Data.Dapper.Custom (internal base implementation)
       - Whizbang.Data.EFCore.Custom (internal base implementation)
       - Whizbang.Testing (empty placeholder)

    2. LEGACY PACKAGES (Whizbang.* without SoftwareExtravaganza prefix):
       Originally published before the package prefix was changed.
       These are owned by SoftwareExtravaganza, NOT eyu.net's Whizbang.Core.

.PARAMETER ApiKey
    Your nuget.org API key. Get one from: https://www.nuget.org/account/apikeys

.PARAMETER WhatIf
    Shows what would be unlisted without actually unlisting.

.EXAMPLE
    ./Unlist-InternalPackages.ps1 -ApiKey "your-api-key-here"

.EXAMPLE
    ./Unlist-InternalPackages.ps1 -ApiKey "your-api-key-here" -WhatIf
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$ApiKey,

    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# Internal packages that should not be published (SoftwareExtravaganza.Whizbang.*)
$internalPackages = @{
    'SoftwareExtravaganza.Whizbang.Generators.Shared' = @(
        '0.1.0', '0.1.1', '0.3.0-alpha.26', '0.4.0-alpha.1',
        '0.4.0-alpha.4', '0.5.0-alpha.1', '0.5.1-alpha.1'
    )
    'SoftwareExtravaganza.Whizbang.Data.Dapper.Custom' = @(
        '0.3.0-alpha.26', '0.4.0-alpha.1', '0.4.0-alpha.4',
        '0.5.0-alpha.1', '0.5.1-alpha.1'
    )
    'SoftwareExtravaganza.Whizbang.Data.EFCore.Custom' = @(
        '0.1.0', '0.1.1', '0.3.0-alpha.26', '0.4.0-alpha.1',
        '0.4.0-alpha.4', '0.5.0-alpha.1', '0.5.1-alpha.1'
    )
    'SoftwareExtravaganza.Whizbang.Testing' = @(
        '0.3.0-alpha.26', '0.4.0-alpha.1', '0.4.0-alpha.4',
        '0.5.0-alpha.1', '0.5.1-alpha.1'
    )
}

# Legacy packages published before prefix change (Whizbang.* owned by SoftwareExtravaganza)
# NOTE: Whizbang.Core is owned by eyu.net - DO NOT TOUCH
$legacyPackages = @{
    'Whizbang.Generators.Shared'         = @('0.1.0')
    'Whizbang.Transports.AzureServiceBus' = @('0.1.0')
    'Whizbang.Data.Schema'               = @('0.1.0')
    'Whizbang.Hosting.Azure.ServiceBus'  = @('0.1.0')
    'Whizbang.CLI'                       = @('0.1.0')
}

# Combine all packages
$packages = @{}
foreach ($key in $internalPackages.Keys) { $packages[$key] = $internalPackages[$key] }
foreach ($key in $legacyPackages.Keys) { $packages[$key] = $legacyPackages[$key] }

$source = 'https://api.nuget.org/v3/index.json'
$totalCount = ($packages.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
$current = 0
$failed = @()

Write-Host "Unlisting $totalCount package versions from nuget.org..." -ForegroundColor Cyan
Write-Host ""

foreach ($packageId in $packages.Keys) {
    Write-Host "Package: $packageId" -ForegroundColor Yellow

    foreach ($version in $packages[$packageId]) {
        $current++
        $progress = "[$current/$totalCount]"

        if ($WhatIf) {
            Write-Host "  $progress Would unlist $version" -ForegroundColor DarkGray
        } else {
            Write-Host "  $progress Unlisting $version..." -NoNewline

            try {
                dotnet nuget delete $packageId $version `
                    --source $source `
                    --api-key $ApiKey `
                    --non-interactive 2>&1 | Out-Null

                Write-Host " OK" -ForegroundColor Green
            } catch {
                Write-Host " FAILED" -ForegroundColor Red
                $failed += "$packageId $version"
            }
        }
    }
    Write-Host ""
}

if ($WhatIf) {
    Write-Host "WhatIf: No packages were actually unlisted." -ForegroundColor Cyan
} elseif ($failed.Count -gt 0) {
    Write-Host "Completed with $($failed.Count) failures:" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
} else {
    Write-Host "Successfully unlisted all $totalCount package versions." -ForegroundColor Green
}
