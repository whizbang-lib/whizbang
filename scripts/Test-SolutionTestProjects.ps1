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

  It also fails on a test project that declares no <WhizbangTestType>, or one that no tool knows (not
  in $KnownTestTypes): every runner selects projects by that property and skips anything else without
  a word. Whizbang.LanguageServer.Tests ran in no suite that way until #1264.
#>
[CmdletBinding()]
param(
  [string]$Root = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'

# Every type the test runners know (Directory.Build.targets <WhizbangKnownTestTypes>). Adding one means
# teaching every reader; .github/scripts/Test-WhizbangTestType.ps1 checks that each one knows it.
$KnownTestTypes = @('Unit', 'Component', 'Integration', 'Benchmark', 'Soak')

# Path relative to the repository root -> why it is not in the solution.
$Excluded = @{
  'tests/Whizbang.Soak.Tests/Whizbang.Soak.Tests.csproj' = 'long-running soak suite, run on its own schedule'
  'benchmarks/Whizbang.Benchmarks.Postgres/Whizbang.Benchmarks.Postgres.csproj' = 'benchmark, run on demand against a live database'
}

$root = (Resolve-Path $Root).Path
$solution = Get-Content (Join-Path $root 'Whizbang.slnx') -Raw
$inSolution = [regex]::Matches($solution, 'Path="([^"]+\.csproj)"') | ForEach-Object { $_.Groups[1].Value -replace '\\', '/' }

$testProjects = @(Get-ChildItem -Path $root -Recurse -Filter '*.csproj' -File |
  Where-Object { $_.FullName.Substring($root.Length) -notmatch '[\\/](bin|obj|node_modules|\.[^\\/]+)[\\/]' } |
  ForEach-Object {
    $content = Get-Content $_.FullName -Raw
    $type = if ($content -match '<WhizbangTestType>\s*([^<\s]+)\s*</WhizbangTestType>') { $Matches[1] } else { '' }
    if ($type -or $content -match '<IsTestProject>\s*true\s*</IsTestProject>') {
      [pscustomobject]@{ Path = [System.IO.Path]::GetRelativePath($root, $_.FullName) -replace '\\', '/'; Type = $type }
    }
  })

$missing = $testProjects | ForEach-Object { $_.Path } |
  Where-Object { $_ -notin $inSolution -and -not $Excluded.ContainsKey($_) } |
  Sort-Object

$untyped = $testProjects | Where-Object { $_.Type -notin $KnownTestTypes } |
  ForEach-Object { if ($_.Type) { "$($_.Path) (declares '$($_.Type)')" } else { "$($_.Path) (declares none)" } } |
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
if ($untyped) {
  Write-Host "Test projects whose <WhizbangTestType> no runner knows (their tests never run):" -ForegroundColor Red
  $untyped | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  Write-Host "Declare one of: $($KnownTestTypes -join ', ') (docs/TEST-PROJECTS.md)."
}
if ($missing -or $stale -or $untyped) {
  exit 1
}
Write-Host "Every test project is in Whizbang.slnx ($($Excluded.Count) deliberately excluded)."
