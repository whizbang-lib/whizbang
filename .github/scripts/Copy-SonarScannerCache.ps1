#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Carries what SonarScanner begin downloaded into its cache (the JRE and the scanner engine) with the analysis
    state, so the job that ends the scan finds them on any runner.

.DESCRIPTION
    SonarScanner begin downloads a JRE and the scanner engine into ~/.sonar/cache and names them in the analysis
    configuration (.sonarqube/conf/SonarQubeAnalysisConfig.xml) by absolute path; SonarScanner end runs them from
    there. The two halves of the quality gate run in different jobs, and re-running only the failed jobs re-runs the
    half that ends the scan on a fresh runner without the half that began it (#2422). The shared actions/cache entry
    is not a guarantee either: its key never changes, so it keeps whatever the first run saved.

    Save (in the job that began the scan) packs the cache entries the configuration names, and only those, into the
    state directory with the cache path they were downloaded to. Restore (in the job that ends it) unpacks them at that
    same path when any is missing. When the state carries no cache and the runner lacks an entry, Restore fails saying
    a full re-run is needed, instead of letting the end step crash on a missing file.

.PARAMETER Action
    Save or Restore.

.PARAMETER ConfigPath
    The analysis configuration, normally .sonarqube/conf/SonarQubeAnalysisConfig.xml.

.PARAMETER CacheRoot
    Save only: the scanner's cache directory, normally ~/.sonar/cache.

.PARAMETER StateDir
    The analysis state directory the two jobs hand over.

.EXAMPLE
    pwsh .github/scripts/Copy-SonarScannerCache.ps1 -Action Save -ConfigPath .sonarqube/conf/SonarQubeAnalysisConfig.xml -CacheRoot ~/.sonar/cache -StateDir sonar-state
#>

param(
    [ValidateSet('Save', 'Restore')]
    [string]$Action = 'Restore',
    [string]$ConfigPath = '',
    [string]$CacheRoot = '',
    [string]$StateDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The cache entries (the directories directly under the cache) the configuration names a path inside, each once.
function Get-SonarCacheEntry {
  param(
      [Parameter(Mandatory)][AllowEmptyString()][string]$ConfigText,
      [Parameter(Mandatory)][string]$CacheRoot
  )
  $pattern = [regex]::Escape($CacheRoot.TrimEnd('/')) + '/([^/<>"]+)/'
  @([regex]::Matches($ConfigText, $pattern) | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
}

# Dot-sourcing for tests loads the function without touching a file.
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $ConfigPath -or -not (Test-Path -LiteralPath $ConfigPath)) {
  Write-Output "::error::No SonarScanner analysis configuration at '$ConfigPath'."
  exit 1
}
$config = Get-Content -Raw -LiteralPath $ConfigPath
$rootFile = Join-Path $StateDir 'scanner-cache-root.txt'
$archive = Join-Path $StateDir 'scanner-cache.tgz'

if ($Action -eq 'Save') {
  $root = $CacheRoot.TrimEnd('/')
  $entries = @(Get-SonarCacheEntry -ConfigText $config -CacheRoot $root)
  $absent = @($entries | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) })
  if ($absent.Count -gt 0) {
    Write-Output "::error::The analysis configuration names $($absent -join ', ') under $root, which begin did not leave there."
    exit 1
  }
  Set-Content -LiteralPath $rootFile -Value $root -NoNewline
  if ($entries.Count -gt 0) {
    & tar -czf $archive -C $root @entries
    if ($LASTEXITCODE -ne 0) { throw "tar could not pack $($entries -join ', ') from $root." }
  }
  Write-Output "Saved the scanner cache entries $(if ($entries.Count) { $entries -join ', ' } else { '(none)' }) from $root."
  exit 0
}

$root = if (Test-Path -LiteralPath $rootFile) { (Get-Content -Raw -LiteralPath $rootFile).Trim() } else { '' }
$entries = @(if ($root) { Get-SonarCacheEntry -ConfigText $config -CacheRoot $root })
if ($entries.Count -eq 0) {
  Write-Output 'The analysis configuration names nothing in the scanner cache to restore.'
  exit 0
}
$missing = @($entries | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) })
if ($missing.Count -eq 0) {
  Write-Output "The scanner cache at $root already holds $($entries -join ', ')."
  exit 0
}
if (-not (Test-Path -LiteralPath $archive)) {
  $names = $missing -join ', '
  Write-Output "::error::The analysis configuration names $names under $root, which this runner does not have and the analysis state does not carry. Re-run all jobs (a full re-run) so the scan begins again."
  exit 1
}
New-Item -ItemType Directory -Path $root -Force | Out-Null
& tar -xzf $archive -C $root @missing
if ($LASTEXITCODE -ne 0) { throw "tar could not restore $($missing -join ', ') to $root." }
Write-Output "Restored the scanner cache entries $($missing -join ', ') to $root."
exit 0
