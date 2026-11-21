#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Writes a solution filter over Whizbang.slnx holding exactly the projects that ship a package.

.DESCRIPTION
    The reproducible-build check (#2484) builds a commit twice and compares the packages. Building the whole
    solution would build every test project twice for nothing, so it builds this filter instead: one project
    per id in .github/nuget-packages.txt (the shipped set verify-packages enforces), found at
    src/<name>/<name>.csproj or tools/<name>/<name>.csproj, where <name> is the id without the
    SoftwareExtravaganza. prefix. A package with no project fails, so the check never builds less than ships.

.PARAMETER OutputPath
    Where to write the .slnf.

.PARAMETER RepoRoot
    The repository root. Defaults to the root this script lives in.

.EXAMPLE
    pwsh .github/scripts/New-PackableSolutionFilter.ps1 -OutputPath packable.slnf
#>

param(
  [Parameter(Mandatory)][string]$OutputPath,
  [string]$RepoRoot = (Join-Path $PSScriptRoot '../..')
)

Set-StrictMode -Version Latest

function Get-PackableProject {
  <#
  .SYNOPSIS
      The repository-relative project path of each shipped package id, in list order.
  #>
  param([Parameter(Mandatory)][AllowEmptyString()][string[]]$PackageIds, [Parameter(Mandatory)][string]$RepoRoot)
  foreach ($line in $PackageIds) {
    $id = $line.Trim()
    if (-not $id -or $id.StartsWith('#')) { continue }
    $name = $id -replace '^SoftwareExtravaganza\.', ''
    $found = $null
    foreach ($dir in 'src', 'tools') {
      $relative = "$dir/$name/$name.csproj"
      if (Test-Path -LiteralPath (Join-Path $RepoRoot $relative)) { $found = $relative; break }
    }
    if (-not $found) { throw "Package '$id' ships, but there is no src/$name/$name.csproj or tools/$name/$name.csproj." }
    $found
  }
}

# Dot-sourcing for tests loads the functions without writing anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$root = (Resolve-Path -LiteralPath $RepoRoot).Path
$ids = Get-Content -LiteralPath (Join-Path $root '.github/nuget-packages.txt')
$projects = @(Get-PackableProject -PackageIds $ids -RepoRoot $root)
$filter = [ordered]@{ solution = [ordered]@{ path = (Join-Path $root 'Whizbang.slnx'); projects = $projects } }
$filter | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath
Write-Output "Wrote $OutputPath with $($projects.Count) packable projects."
