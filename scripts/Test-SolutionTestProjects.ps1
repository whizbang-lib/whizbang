#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Fails when a project that declares <WhizbangTestType> is missing from Whizbang.slnx.

.DESCRIPTION
  CI builds the solution and runs tests from that build, so a test project outside the solution is
  never compiled and its tests never run. Nothing else reports it: discovery simply does not find the
  project. ECommerce.Lifecycle.Integration.Tests sat outside the solution from March to October that
  way (#1160).

  A project that is deliberately run some other way is listed in $Excluded with the reason.
#>
[CmdletBinding()]
param(
  [string]$Root = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'

# Path relative to the repository root -> why it is not in the solution.
$Excluded = @{
  'tests/Whizbang.Soak.Tests/Whizbang.Soak.Tests.csproj' = 'long-running soak suite, run on its own schedule'
  'benchmarks/Whizbang.Benchmarks.Postgres/Whizbang.Benchmarks.Postgres.csproj' = 'benchmark, run on demand against a live database'
}

$root = (Resolve-Path $Root).Path
$solution = Get-Content (Join-Path $root 'Whizbang.slnx') -Raw
$inSolution = [regex]::Matches($solution, 'Path="([^"]+\.csproj)"') | ForEach-Object { $_.Groups[1].Value -replace '\\', '/' }

$missing = Get-ChildItem -Path $root -Recurse -Filter '*.csproj' -File |
  Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' } |
  Where-Object { (Get-Content $_.FullName -Raw) -match '<WhizbangTestType>' } |
  ForEach-Object { [System.IO.Path]::GetRelativePath($root, $_.FullName) -replace '\\', '/' } |
  Where-Object { $_ -notin $inSolution -and -not $Excluded.ContainsKey($_) } |
  Sort-Object

$stale = $Excluded.Keys | Where-Object { $_ -in $inSolution -or -not (Test-Path (Join-Path $root $_)) } | Sort-Object

if ($missing) {
  Write-Host 'Test projects missing from Whizbang.slnx (CI never builds or runs them):' -ForegroundColor Red
  $missing | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  Write-Host 'Add each to Whizbang.slnx, or list it in $Excluded in this script with the reason.'
}
if ($stale) {
  Write-Host 'Exclusions that no longer apply (the project is in the solution or gone):' -ForegroundColor Red
  $stale | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
}
if ($missing -or $stale) {
  exit 1
}
Write-Host "Every test project is in Whizbang.slnx ($($Excluded.Count) deliberately excluded)."
