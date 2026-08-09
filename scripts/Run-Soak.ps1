#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Runs the load / stress / soak suite. Never part of the CI gate.

.DESCRIPTION
  Whizbang.Soak.Tests measures emergent behaviour under sustained load -- latency, growth,
  responsiveness. Those are wall-clock properties, so they are kept out of the pull-request gate
  where a busy runner would make them flap. Run-Tests.ps1 excludes the project by construction: it
  filters on WhizbangTestType, and the soak project declares "Soak" rather than Unit/Integration.

  This script is the deliberate way in. See tests/Whizbang.Soak.Tests/README.md for what belongs
  in the suite and how to read a failure (a soak failure is the start of an investigation, not a
  verdict -- these tests measure the machine they run on).

.PARAMETER Filter
  Substring matched against the test name, e.g. "Starvation".

.PARAMETER Configuration
  Build configuration. Defaults to Release, which is what a load measurement should use.

.EXAMPLE
  pwsh scripts/Run-Soak.ps1
  pwsh scripts/Run-Soak.ps1 -Filter Starvation
#>
param(
  [string]$Filter = "",
  [ValidateSet("Debug", "Release")]
  [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..' 'tests' 'Whizbang.Soak.Tests'
if (-not (Test-Path $project)) {
  Write-Host "soak project not found at $project"
  exit 1
}

# Docker is required for the scenarios backed by a real PostgreSQL. Fail with a clear reason
# rather than a container timeout thirty seconds in.
$dockerOk = $true
try { docker info *> $null; $dockerOk = ($LASTEXITCODE -eq 0) } catch { $dockerOk = $false }
if (-not $dockerOk) {
  Write-Host "Docker is not available — soak scenarios that need a real PostgreSQL will fail."
  Write-Host "Start Docker and re-run, or use -Filter to select scenarios that do not need it."
}

Write-Host "Running soak suite ($Configuration)$(if ($Filter) { " — filter: $Filter" })"
Write-Host "NOTE: these measure the machine they run on. Compare against a known baseline before"
Write-Host "      calling a failure a regression."
Write-Host ""

$testArgs = @('run', '--project', $project, '--configuration', $Configuration)
if ($Filter) {
  $testArgs += @('--', '--treenode-filter', "/*/*/*$Filter*/*")
}

& dotnet @testArgs
$code = $LASTEXITCODE

Write-Host ""
if ($code -eq 0) {
  Write-Host "soak suite PASSED on this machine."
} else {
  Write-Host "soak suite FAILED (exit $code) — investigate before treating it as a code regression."
}
exit $code
