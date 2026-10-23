#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    The actions the workflows use, and whether the repository's allowed-actions setting lets each run.

.DESCRIPTION
    The repository allows only selected actions (#1467): those GitHub owns (the actions and github
    organizations), those by verified Marketplace creators, and the patterns in .github/allowed-actions.txt.
    A new action is therefore a reviewed change to that file, which CODEOWNERS gives to build-admins, and to
    the setting, which only an admin can change.

    The checks here are deliberately stricter than the setting: they do not count verified creators, so
    every non-GitHub action is named in the file, and the file is the whole list of what can run.
    Get-AllowedActions.Tests.ps1 fails when a workflow uses an action the file does not name, or the file
    names one nothing uses.

    Dot-source it for the functions; run it to print every reference and whether it is allowed.

.EXAMPLE
    pwsh .github/scripts/Get-AllowedActions.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-AllowedActionList {
  <# .SYNOPSIS The patterns in an allowed-actions file: one per line, # starts a comment. #>
  param([Parameter(Mandatory)][string]$Path)
  foreach ($line in Get-Content -LiteralPath $Path) {
    $pattern = ($line -replace '#.*$', '').Trim()
    if ($pattern) { $pattern }
  }
}

function Test-ActionAllowed {
  <#
  .SYNOPSIS
      Whether a uses: reference (owner/repo[/path]@ref) is a GitHub-owned action or matches a pattern.
      In a pattern only * is a wildcard, as in the setting.
  #>
  param(
    [Parameter(Mandatory)][string]$Reference,
    [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Patterns
  )
  if ($Reference -match '^(actions|github)/') { return $true }
  foreach ($pattern in $Patterns) {
    $regex = '^' + (([regex]::Escape($pattern)) -replace '\\\*', '.*') + '$'
    if ($Reference -match $regex) { return $true }
  }
  return $false
}

function Get-ActionReference {
  <#
  .SYNOPSIS
      Every remote action a workflow or composite action uses: File, Line and Reference. Local actions and
      workflows (./...) and docker:// images are not actions the setting governs.
  #>
  param([Parameter(Mandatory)][string]$GitHubDirectory)
  $files = @(
    Get-ChildItem -Path (Join-Path $GitHubDirectory 'workflows') -Include '*.yml', '*.yaml' -Recurse -File -ErrorAction SilentlyContinue
    Get-ChildItem -Path (Join-Path $GitHubDirectory 'actions') -Include 'action.yml', 'action.yaml' -Recurse -File -ErrorAction SilentlyContinue
  )
  foreach ($file in $files) {
    $number = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
      $number++
      if ($line -notmatch '^\s*(-\s*)?uses:\s*[''"]?([^''"\s#]+)') { continue }
      $reference = $Matches[2]
      if ($reference.StartsWith('./') -or $reference.StartsWith('docker://')) { continue }
      [pscustomobject]@{ File = $file.FullName; Line = $number; Reference = $reference }
    }
  }
}

# Dot-sourcing for tests loads the functions without running anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$github = Join-Path $PSScriptRoot '..'
$patterns = @(Read-AllowedActionList -Path (Join-Path $github 'allowed-actions.txt'))
Get-ActionReference -GitHubDirectory $github |
  ForEach-Object { [pscustomobject]@{ Allowed = (Test-ActionAllowed -Reference $_.Reference -Patterns $patterns); Reference = $_.Reference; File = (Resolve-Path -Relative $_.File) } } |
  Sort-Object Allowed, Reference |
  Format-Table -AutoSize
