#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Fails a test suite whose coverage reports measured no library code.

.DESCRIPTION
    Each test module writes <guid>.cobertura.xml directly under its own TestResults folder. An empty
    report (`<packages />`) is what the collector writes when it skipped every assembly, and the tests
    still pass, so the upload alone cannot tell it from real coverage. This warns for each empty report
    (a project may legitimately load no library assembly) and fails when the whole suite measured
    nothing. The copy TRX attaches deeper under TestResults is not counted.

    Runs on any runner OS, where the bash step it replaced ran on Linux only.

    Exit 0 when at least one report measured library code, 1 otherwise.

.PARAMETER Root
    The checkout; reports are looked for under its tests/ and samples/. Default: the current directory.

.EXAMPLE
    pwsh .github/scripts/Test-CoverageCollected.ps1
#>

param(
    [string]$Root = (Get-Location).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The reports directly under a TestResults folder in tests/ and samples/: Reports, Measured, and the Empty ones.
function Get-CoverageReportVerdict {
  param([Parameter(Mandatory)][string]$Root)
  $trees = @('tests', 'samples' | ForEach-Object { Join-Path $Root $_ } | Where-Object { Test-Path -LiteralPath $_ })
  $reports = @($trees | ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Filter '*.cobertura.xml' } |
      Where-Object { $_.Directory.Name -eq 'TestResults' })
  $empty = @($reports | Where-Object { -not (Select-String -LiteralPath $_.FullName -Pattern '<package ' -SimpleMatch -Quiet) } |
      ForEach-Object { $_.FullName })
  return [pscustomobject]@{ Reports = $reports.Count; Measured = $reports.Count - $empty.Count; Empty = $empty }
}

# Reports the verdict; returns the exit code.
function Invoke-TestCoverageCollected {
  param([Parameter(Mandatory)][string]$Root)
  $verdict = Get-CoverageReportVerdict -Root $Root
  foreach ($path in $verdict.Empty) { Write-Host "::warning::empty coverage report: $path" }
  Write-Host "$($verdict.Measured) of $($verdict.Reports) coverage reports measured library code"
  if ($verdict.Measured -eq 0) {
    Write-Host '::error::No coverage report measured any library assembly. The collector skipped them all: check that Update-CoverageSourceRoots.ps1 ran, and the source-roots mapping note in reusable-build.yml.'
    return 1
  }
  return 0
}

# Dot-sourcing for tests loads the functions without checking anything.
if ($MyInvocation.InvocationName -eq '.') { return }

exit (Invoke-TestCoverageCollected -Root $Root)
