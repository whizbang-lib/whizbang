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

.PARAMETER InstallerUrl
    Where dotnet-install.sh is downloaded from.

.EXAMPLE
    pwsh .github/scripts/Install-DotnetSdk.ps1 -FallbackChannel 10.0.x
#>

param(
    [string]$GlobalJsonPath = 'global.json',
    [string]$FallbackChannel = '10.0.x',
    [string]$InstallDir = (Join-Path $HOME '.dotnet'),
    [string]$InstallerUrl = 'https://dot.net/v1/dotnet-install.sh'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
      # A prerelease SDK (11.0.100-rc.3.x) has no feature-band channel of its own: install that exact version.
      if ($version.Contains('-')) { return $version }
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
  # An exact (prerelease) version installs with --version; a feature band or major.minor with --channel.
  $selector = if ($Channel.Contains('-')) { '--version' } else { '--channel' }
  & bash $InstallerPath $selector $Channel --install-dir $InstallDir | Out-Host
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

# The runtimes the build's other target frameworks need to run their tests (#2135). A release build targets
# net10.0 and net11.0 while the SDK is 11, so its net10.0 test modules need the .NET 10 runtime beside it.
# Directory.Build.props decides the frameworks (its branch detection reads the same GITHUB_REF), so this never
# repeats that decision. Returns major.minor channels, e.g. '10.0', for every framework older than the SDK.
function Get-ExtraRuntimeChannels {
  param(
      [AllowEmptyCollection()][string[]]$TargetFrameworks,
      [Parameter(Mandatory)][string]$SdkVersion
  )
  $sdkMajor = [int]($SdkVersion.Split('.')[0])
  @($TargetFrameworks | ForEach-Object {
      if ($_ -match '^net(\d+)\.(\d+)$' -and [int]$Matches[1] -lt $sdkMajor) { "$($Matches[1]).$($Matches[2])" }
    } | Select-Object -Unique)
}

function Invoke-RuntimeInstall {
  param(
      [Parameter(Mandatory)][string]$InstallerPath,
      [Parameter(Mandatory)][string]$Channel,
      [Parameter(Mandatory)][string]$InstallDir
  )
  # The ASP.NET Core runtime archive carries Microsoft.NETCore.App as well, which is what the test hosts need.
  & bash $InstallerPath --runtime aspnetcore --channel $Channel --install-dir $InstallDir | Out-Host
  if ($LASTEXITCODE -ne 0) {
    return "Installing the .NET $Channel runtime failed (dotnet-install.sh exit $LASTEXITCODE); see the download errors above."
  }
  return $null
}

if ($MyInvocation.InvocationName -eq '.') { return }

$channel = Get-SdkChannel -GlobalJsonPath $GlobalJsonPath -FallbackChannel $FallbackChannel
Write-Output "Installing .NET SDK channel $channel into $InstallDir"

$installer = Join-Path ([IO.Path]::GetTempPath()) "dotnet-install-$PID.sh"
try {
  Invoke-WebRequest -Uri $InstallerUrl -OutFile $installer -MaximumRetryCount 2 -RetryIntervalSec 5
}
catch {
  Write-Output "::error title=.NET SDK install::Could not download dotnet-install.sh from ${InstallerUrl}: $($_.Exception.Message)"
  exit 1
}

$result = Invoke-SdkInstall -InstallerPath $installer -Channel $channel -InstallDir $InstallDir
if (-not $result.Ok) {
  Write-Output "::error title=.NET SDK install::$($result.Error)"
  exit 1
}

$props = if (Test-Path -LiteralPath $GlobalJsonPath) {
  Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $GlobalJsonPath).Path) 'Directory.Build.props'
} else { '' }
if ($props -and (Test-Path -LiteralPath $props)) {
  $frameworks = ((& (Join-Path $InstallDir 'dotnet') msbuild $props -getProperty:TargetFrameworks 2>$null) | Out-String).Trim() -split ';'
  foreach ($runtimeChannel in (Get-ExtraRuntimeChannels -TargetFrameworks $frameworks -SdkVersion $result.Version)) {
    Write-Output "This build also targets .NET $runtimeChannel; installing its runtime into $InstallDir"
    $runtimeError = Invoke-RuntimeInstall -InstallerPath $installer -Channel $runtimeChannel -InstallDir $InstallDir
    if ($runtimeError) {
      Write-Output "::error title=.NET runtime install::$runtimeError"
      exit 1
    }
  }
}

if ($env:GITHUB_PATH) { $InstallDir | Add-Content -Path $env:GITHUB_PATH }
if ($env:GITHUB_ENV) { "DOTNET_ROOT=$InstallDir" | Add-Content -Path $env:GITHUB_ENV }
Write-Output "Installed .NET SDK $($result.Version)"
exit 0
