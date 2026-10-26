#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Installs the .NET SDK on a Windows runner, choosing the channel exactly as the Linux and macOS step
    of .github/actions/setup-dotnet-retry does.

.DESCRIPTION
    The channel is the feature band of global.json's sdk.version (10.0.100 installs 10.0.1xx), so a newer
    feature band with a broken SDK is never picked up; without a usable sdk.version it is -FallbackVersion
    with any trailing .x removed. dotnet-install.ps1 installs it into -InstallDir for the machine's own
    architecture (ARM64 gets arm64). The SDK directory and its global tools directory are appended to
    $GITHUB_PATH, and DOTNET_ROOT to $GITHUB_ENV.

    A team Windows machine has no bash on the runner service's PATH (or only the WSL stub), and the
    action's bash step failed every job routed there before anything else ran, which is why this exists.

    Throws on any failure, so the action's retry tries again.

.PARAMETER GlobalJsonPath
    Default: global.json in the current directory.

.PARAMETER FallbackVersion
    The action's dotnet-version input, used when global.json gives no version. Default: 10.0.x.

.PARAMETER InstallDir
    Default: $env:USERPROFILE\.dotnet.

.EXAMPLE
    pwsh .github/scripts/Install-DotNetSdkWindows.ps1 -FallbackVersion 10.0.x
#>

param(
    [string]$GlobalJsonPath = (Join-Path (Get-Location).Path 'global.json'),
    [string]$FallbackVersion = '10.0.x',
    [string]$InstallDir = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dotnet')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:InstallScriptUri = 'https://dot.net/v1/dotnet-install.ps1'

# The channel to install, and where it came from.
function Get-DotNetChannel {
  param([string]$GlobalJsonPath, [string]$FallbackVersion)
  $version = ''
  if (Test-Path -LiteralPath $GlobalJsonPath) {
    try {
      $json = Get-Content -Raw -LiteralPath $GlobalJsonPath | ConvertFrom-Json
      if ($json.PSObject.Properties['sdk'] -and $json.sdk.PSObject.Properties['version']) { $version = [string]$json.sdk.version }
    } catch {
      Write-Host "global.json could not be read ($($_.Exception.Message)); using the input channel."
    }
  }
  if ($version -match '^(.*)\d\d$') {
    return [pscustomobject]@{ Channel = "$($Matches[1])xx"; Source = "feature band $($Matches[1])xx (from global.json version $version)" }
  }
  $channel = $FallbackVersion -replace '\.x$', ''
  return [pscustomobject]@{ Channel = $channel; Source = "channel $channel (from the dotnet-version input)" }
}

function Get-DefaultDownload {
  return { param($Uri, $OutFile) Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing }
}

function Get-DefaultInstall {
  return { param($Script, $Channel, $InstallDir) & $Script -Channel $Channel -InstallDir $InstallDir }
}

function Get-DefaultGetVersion {
  return {
    param($Dotnet)
    $out = & $Dotnet --version
    if ($LASTEXITCODE) { throw "$Dotnet --version exited $LASTEXITCODE" }
    $out
  }
}

<#
    Downloads dotnet-install.ps1, installs the channel, puts the SDK on PATH for later steps, and prints
    the installed version. Download, Install and GetVersion are replaceable for tests.
#>
function Install-DotNetSdk {
  param(
      [Parameter(Mandatory)][string]$GlobalJsonPath,
      [Parameter(Mandatory)][string]$FallbackVersion,
      [Parameter(Mandatory)][string]$InstallDir,
      [string]$TempDir = ($env:RUNNER_TEMP ?? [IO.Path]::GetTempPath()),
      [AllowEmptyString()][string]$PathFile = $env:GITHUB_PATH,
      [AllowEmptyString()][string]$EnvFile = $env:GITHUB_ENV,
      [scriptblock]$Download = (Get-DefaultDownload),
      [scriptblock]$Install = (Get-DefaultInstall),
      [scriptblock]$GetVersion = (Get-DefaultGetVersion)
  )
  $choice = Get-DotNetChannel -GlobalJsonPath $GlobalJsonPath -FallbackVersion $FallbackVersion
  "Installing .NET SDK $($choice.Source) into $InstallDir"
  $installer = Join-Path $TempDir 'dotnet-install.ps1'
  & $Download $script:InstallScriptUri $installer
  & $Install $installer $choice.Channel $InstallDir
  $dotnet = Join-Path $InstallDir ($IsWindows ? 'dotnet.exe' : 'dotnet')
  $version = & $GetVersion $dotnet
  if ($PathFile) { Add-Content -LiteralPath $PathFile -Value @($InstallDir, (Join-Path $InstallDir 'tools')) }
  if ($EnvFile) { Add-Content -LiteralPath $EnvFile -Value "DOTNET_ROOT=$InstallDir" }
  "Installed .NET SDK $version"
}

# Dot-sourcing for tests loads the functions without installing anything.
if ($MyInvocation.InvocationName -eq '.') { return }

Install-DotNetSdk -GlobalJsonPath $GlobalJsonPath -FallbackVersion $FallbackVersion -InstallDir $InstallDir
