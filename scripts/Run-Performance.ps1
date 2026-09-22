#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Runs the performance scenarios and prints the report against the committed baseline.

.DESCRIPTION
  The performance suite measures WORK DONE -- tuples read, scans taken, pages touched, rows written
  -- never wall clock. Time moves with the machine, its neighbors and its cache; a plan's page and
  tuple counts do not, which is what makes a number from a laptop comparable with a number from a
  server and with the number a deployed fleet reports.

  These scenarios are NOT part of CI. They carry [Category("Benchmark")] and no shard category, and
  the CI matrix selects only [Category=Shard1] through [Category=Shard4], so they run in no slice by
  construction. Nothing in the workflow has to exclude them. Run them here, on demand, when you want
  to know what a change costs before it ships.

  Every measure is reported with its drift against tests/Whizbang.Data.EFCore.Postgres.Tests/
  Performance/baseline.tsv. A run FAILS only on a ceiling that file declares and justifies, so a
  small drift between two machines is information rather than an alarm, while a regression of the
  size this suite exists to catch cannot be missed.

.PARAMETER Scenario
  Substring of the scenario class to run. Default: all of them.

.PARAMETER ReportDirectory
  Where to write the rendered reports. Default: artifacts/performance under the repository root.

.PARAMETER UpdateBaseline
  Print the baseline lines this run would contribute, for pasting into baseline.tsv after a
  deliberate change. Never writes the file: a baseline moves because a person decided it should.

.EXAMPLE
  pwsh scripts/Run-Performance.ps1
.EXAMPLE
  pwsh scripts/Run-Performance.ps1 -Scenario Doorbell -UpdateBaseline
#>
[CmdletBinding()]
param(
  [string]$Scenario = '*',
  [string]$ReportDirectory,
  [switch]$UpdateBaseline
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tests/Whizbang.Data.EFCore.Postgres.Tests'

if (-not $ReportDirectory) {
  $ReportDirectory = Join-Path $repoRoot 'artifacts/performance'
}
New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null
$env:WHIZBANG_PERF_REPORT_DIR = $ReportDirectory

# The scenarios carry both categories; filtering by class name keeps the selection readable and
# lets a caller ask for one scenario by the name they think of it under.
$filter = "/*/*/*${Scenario}ScenarioTests/*"

Write-Host "Performance scenarios: $filter" -ForegroundColor Cyan
Write-Host "Reports:               $ReportDirectory" -ForegroundColor Cyan
Write-Host ''

Push-Location $project
try {
  dotnet build --verbosity quiet
  if ($LASTEXITCODE -ne 0) { throw "build failed" }
  dotnet run --no-build -- --treenode-filter $filter
  $runExit = $LASTEXITCODE
} finally {
  Pop-Location
}

Write-Host ''
foreach ($report in Get-ChildItem -Path $ReportDirectory -Filter '*.txt' -ErrorAction SilentlyContinue) {
  if ($UpdateBaseline) {
    Get-Content $report.FullName
  } else {
    # Everything above the baseline-lines block is the part a person reads.
    $text = Get-Content $report.FullName -Raw
    Write-Host ($text -split 'Baseline lines for this run:')[0]
  }
}

if ($runExit -ne 0) {
  Write-Host 'A declared ceiling was passed. The report above names the measure and the ceiling.' -ForegroundColor Red
}
exit $runExit
