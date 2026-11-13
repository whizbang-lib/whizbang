#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Installs the .NET SDK the build needs, and fails with the real cause when it cannot.

.DESCRIPTION
    Installs the feature band of global.json's sdk.version (10.0.100 installs the latest 10.0.1xx), or the
    -FallbackChannel when global.json names no SDK, with Microsoft's dotnet-install.sh into -InstallDir. Then
    it checks that the installed dotnet runs, adds it to GITHUB_PATH and sets DOTNET_ROOT.

    Each step is checked and a failure stops the script with an ::error:: annotation naming that step: the
    installer download, the install itself (with its channel and exit code), an install that left no SDK, or
    a dotnet that does not run. Exit 1 on any of them, so the retry wrapper in setup-dotnet-retry tries again.

    Why this exists: the step used to pipe curl into bash with no status checks, so a failed SDK download
    carried on to `dotnet --version` and was reported as exit 127, a missing file, with the real cause
    buried higher in the log (#1773).

.PARAMETER GlobalJsonPath
    The global.json whose sdk.version picks the feature band.

.PARAMETER FallbackChannel
    The channel to install when global.json names no SDK version, such as 10.0.x. A trailing .x is dropped.

.PARAMETER InstallDir
    Where the SDK is installed.

.PARAMETER InstallerUrls
    Where dotnet-install.sh is downloaded from, tried in order: Microsoft's CDN, then the dot.net redirect, so an
    outage of either host alone does not stop the job (#2298).

.EXAMPLE
    pwsh .github/scripts/Install-DotnetSdk.ps1 -FallbackChannel 10.0.x
#>

param(
    [string]$GlobalJsonPath = 'global.json',
    [string]$FallbackChannel = '10.0.x',
    [string]$InstallDir = (Join-Path $HOME '.dotnet'),
    [string[]]$InstallerUrls = @(
        'https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh',
        'https://dot.net/v1/dotnet-install.sh')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:InstallerUrls = $InstallerUrls

# Downloads the installer from the first of Urls that answers. Returns Ok and the Url used, or Ok = false and an
# Error naming every address with its error. Download is replaceable for tests.
function Save-SdkInstaller {
  param(
      [Parameter(Mandatory)][string[]]$Urls,
      [Parameter(Mandatory)][string]$OutFile,
      [scriptblock]$Download = { param($Uri, $Path) Invoke-WebRequest -Uri $Uri -OutFile $Path -MaximumRetryCount 2 -RetryIntervalSec 5 }
  )
  $failures = [System.Collections.Generic.List[string]]::new()
  foreach ($url in $Urls) {
    try {
      & $Download $url $OutFile
      return [pscustomobject]@{ Ok = $true; Url = $url; Error = $null }
    }
    catch {
      $failures.Add("${url}: $($_.Exception.Message)")
    }
  }
  return [pscustomobject]@{ Ok = $false; Url = $null; Error = ($failures -join '; ') }
}

# The dotnet-install.sh channel to install: global.json's feature band (10.0.100 becomes 10.0.1xx), or the
# fallback channel without a trailing .x.
function Get-SdkChannel {
  param(
      [Parameter(Mandatory)][string]$GlobalJsonPath,
      [Parameter(Mandatory)][string]$FallbackChannel
  )
  if (Test-Path -LiteralPath $GlobalJsonPath) {
    $json = Get-Content -LiteralPath $GlobalJsonPath -Raw | ConvertFrom-Json
    $sdk = $json.PSObject.Properties['sdk']
    if ($sdk -and $sdk.Value.PSObject.Properties['version'] -and $sdk.Value.version) {
      $version = [string]$sdk.Value.version
      return $version.Substring(0, $version.Length - 2) + 'xx'
    }
  }
  return $FallbackChannel -replace '\.x$', ''
}

# Runs the installer, then the installed dotnet. Returns Ok and Version, or Ok = false and an Error naming the
# step that failed.
function Invoke-SdkInstall {
  param(
      [Parameter(Mandatory)][string]$InstallerPath,
      [Parameter(Mandatory)][string]$Channel,
      [Parameter(Mandatory)][string]$InstallDir
  )
  # The installer prints its progress to stdout. Send it to the console, not into this function's return value.
  & bash $InstallerPath --channel $Channel --install-dir $InstallDir | Out-Host
  if ($LASTEXITCODE -ne 0) {
    return [pscustomobject]@{
      Ok = $false; Version = $null
      Error = "Installing the .NET SDK for channel $Channel failed (dotnet-install.sh exit $LASTEXITCODE); see the download errors above."
    }
  }
  $dotnet = Join-Path $InstallDir 'dotnet'
  if (-not (Test-Path -LiteralPath $dotnet)) {
    return [pscustomobject]@{
      Ok = $false; Version = $null
      Error = "dotnet-install.sh reported success for channel $Channel, but there is no SDK at $dotnet."
    }
  }
  $version = (& $dotnet --version | Out-String).Trim()
  if ($LASTEXITCODE -ne 0) {
    return [pscustomobject]@{
      Ok = $false; Version = $null
      Error = "The installed SDK at $dotnet does not run (dotnet --version exit $LASTEXITCODE)."
    }
  }
  return [pscustomobject]@{ Ok = $true; Version = $version; Error = $null }
}

if ($MyInvocation.InvocationName -eq '.') { return }

$channel = Get-SdkChannel -GlobalJsonPath $GlobalJsonPath -FallbackChannel $FallbackChannel
Write-Output "Installing .NET SDK channel $channel into $InstallDir"

$installer = Join-Path ([IO.Path]::GetTempPath()) "dotnet-install-$PID.sh"
$download = Save-SdkInstaller -Urls $InstallerUrls -OutFile $installer
if (-not $download.Ok) {
  Write-Output "::error title=.NET SDK install::Could not download dotnet-install.sh from any address: $($download.Error)"
  exit 1
}
Write-Output "Downloaded dotnet-install.sh from $($download.Url)"

$result = Invoke-SdkInstall -InstallerPath $installer -Channel $channel -InstallDir $InstallDir
if (-not $result.Ok) {
  Write-Output "::error title=.NET SDK install::$($result.Error)"
  exit 1
}

if ($env:GITHUB_PATH) { $InstallDir | Add-Content -Path $env:GITHUB_PATH }
if ($env:GITHUB_ENV) { "DOTNET_ROOT=$InstallDir" | Add-Content -Path $env:GITHUB_ENV }
Write-Output "Installed .NET SDK $($result.Version)"
exit 0
