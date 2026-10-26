#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Points each test project's coverage source-root mapping at this checkout, so a suite that runs the
    build on another machine still collects coverage.

.DESCRIPTION
    The build writes a hidden .msCoverageExtensionSourceRootsMapping_<project> file into each test
    project's output. Its lines read "<project file>|<local root>=<deterministic root>" and tell the
    coverage collector, inside the test process, where the sources behind the pdbs' /_/ paths are. The
    collector skips every assembly whose sources it cannot find and writes an empty report while the tests
    still pass.

    Those roots are the build machine's checkout. A suite on a GitHub-hosted runner checks out at the same
    path, so it never mattered; a suite on a self-hosted machine, or on Windows or macOS, checks out
    somewhere else. This rewrites every mapping under tests/ and samples/ to -Root (and the NuGet root to
    -NuGetRoot), with the platform's separator, before the tests run. A mapping already pointing here is
    left untouched; one with no /_/ line is named and left alone.

    Measured with the Linux build's component tests in a Linux x64 container: at
    /home/runner/work/whizbang/whizbang they wrote a 14 MB report, anywhere else an empty one.

    Exit 0 always; Test-CoverageCollected.ps1 is what fails a suite that measured nothing.

.PARAMETER Root
    The checkout the tests run from. Default: the current directory.

.PARAMETER NuGetRoot
    The local NuGet package folder. Default: $env:NUGET_PACKAGES, else ~/.nuget/packages.

.EXAMPLE
    pwsh .github/scripts/Update-CoverageSourceRoots.ps1
#>

param(
    [string]$Root = (Get-Location).Path,
    [string]$NuGetRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:MappingPrefix = '.msCoverageExtensionSourceRootsMapping_'
$script:RepoTarget = '/_/'
$script:NuGetTarget = '/_1/'

# The local NuGet package folder.
function Get-DefaultNuGetRoot {
  if ($env:NUGET_PACKAGES) { return $env:NUGET_PACKAGES }
  return (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget' 'packages')
}

# The repository root the build mapped to /_/, from a mapping's lines; empty when none maps it.
function Get-BuildSourceRoot {
  param([string[]]$Lines)
  foreach ($line in $Lines) {
    if ($line -match '^[^|]*\|(.+)=/_/$') { return $Matches[1] }
  }
  return ''
}

# A path under OldRoot moved under NewRoot, with Separator between its segments; any other path unchanged.
function Move-SourcePath {
  param([string]$Path, [string]$OldRoot, [string]$NewRoot, [string]$Separator)
  if (-not $Path.StartsWith($OldRoot, [StringComparison]::Ordinal)) { return $Path }
  $rest = $Path.Substring($OldRoot.Length) -replace '[\\/]', $Separator
  return $NewRoot + $rest
}

# One mapping line rewritten for this checkout; a line of any other shape comes back unchanged.
function Convert-SourceRootLine {
  param([string]$Line, [string]$OldRoot, [string]$NewRoot, [string]$NuGetRoot, [string]$Separator)
  if ($Line -notmatch '^([^|]*)\|(.+)=(/_\d*/)$') { return $Line }
  $project = $Matches[1]; $from = $Matches[2]; $to = $Matches[3]
  $project = Move-SourcePath -Path $project -OldRoot $OldRoot -NewRoot $NewRoot -Separator $Separator
  if ($to -eq $script:RepoTarget) { $from = $NewRoot }
  elseif ($to -eq $script:NuGetTarget) { $from = $NuGetRoot }
  return "$project|$from=$to"
}

<#
    Rewrites every mapping under tests/ and samples/. Returns Rewritten and Unchanged counts and the
    Skipped files (no /_/ line).
#>
function Update-CoverageSourceRoots {
  param(
      [Parameter(Mandatory)][string]$Root,
      [string]$NuGetRoot = (Get-DefaultNuGetRoot),
      [string]$Separator = [string][IO.Path]::DirectorySeparatorChar
  )
  $newRoot = $Root.TrimEnd('/', '\') + $Separator
  $nuget = $NuGetRoot.TrimEnd('/', '\') + $Separator
  $encoding = [Text.UnicodeEncoding]::new($false, $true)
  $rewritten = 0; $unchanged = 0
  $skipped = [System.Collections.Generic.List[string]]::new()

  $trees = @('tests', 'samples' | ForEach-Object { Join-Path $Root $_ } | Where-Object { Test-Path -LiteralPath $_ })
  $files = @($trees | ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -Force -File -Filter "$script:MappingPrefix*" })
  foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::Unicode)
    $lines = @($text -split "\r?\n" | Where-Object { $_ })
    $oldRoot = Get-BuildSourceRoot -Lines $lines
    if (-not $oldRoot) { $skipped.Add($file.FullName); continue }
    $converted = @($lines | ForEach-Object {
        Convert-SourceRootLine -Line $_ -OldRoot $oldRoot -NewRoot $newRoot -NuGetRoot $nuget -Separator $Separator
      })
    if (($lines -join "`n") -ceq ($converted -join "`n")) { $unchanged++; continue }
    [IO.File]::WriteAllText($file.FullName, (($converted -join "`n") + "`n"), $encoding)
    $rewritten++
  }
  return [pscustomobject]@{ Rewritten = $rewritten; Unchanged = $unchanged; Skipped = $skipped.ToArray() }
}

# Rewrites the mappings and returns the lines to print.
function Invoke-UpdateCoverageSourceRoots {
  param([Parameter(Mandatory)][string]$Root, [string]$NuGetRoot = '')
  $parameters = @{ Root = $Root }
  if ($NuGetRoot) { $parameters.NuGetRoot = $NuGetRoot }
  $result = Update-CoverageSourceRoots @parameters
  foreach ($file in $result.Skipped) { "::warning::coverage source-root mapping with no /_/ root left as built: $file" }
  "Coverage source roots: $($result.Rewritten) rewritten to $Root, $($result.Unchanged) already here, $($result.Skipped.Count) skipped."
}

# Dot-sourcing for tests loads the functions without rewriting anything.
if ($MyInvocation.InvocationName -eq '.') { return }

Invoke-UpdateCoverageSourceRoots -Root $Root -NuGetRoot $NuGetRoot
exit 0
