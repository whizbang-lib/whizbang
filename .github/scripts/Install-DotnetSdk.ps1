#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Installs the .NET SDK the build needs, and fails with the real cause when it cannot.

.DESCRIPTION
    Stub: written test-first for #1773. Not yet implemented.
#>

param(
    [string]$GlobalJsonPath = 'global.json',
    [string]$FallbackChannel = '10.0.x',
    [string]$InstallDir = (Join-Path $HOME '.dotnet'),
    [string]$InstallerUrl = 'https://dot.net/v1/dotnet-install.sh'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SdkChannel {
  param([string]$GlobalJsonPath, [string]$FallbackChannel)
  throw 'not implemented'
}

function Invoke-SdkInstall {
  param([string]$InstallerPath, [string]$Channel, [string]$InstallDir)
  throw 'not implemented'
}

if ($MyInvocation.InvocationName -eq '.') { return }
exit 1
