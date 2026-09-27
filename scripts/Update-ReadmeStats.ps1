#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Rewrites the README's generated blocks: the quality numbers and the package table.

.DESCRIPTION
    The README carries a block between `<!-- auto:quality -->` and `<!-- /auto:quality -->`. This script
    regenerates only that block, from two published sources, so the README never states a number nobody
    measured:

    - Tests: the latest develop run's totals, which CI publishes to the docs site as test-status/index.json.
    - Coverage: Codecov's totals for the main branch.

    The test count is rounded DOWN to the nearest thousand ("26,000+") so the file changes when the suite
    grows by a thousand, not on every run. Coverage is shown to one decimal, or as "100%" when it is 100.

    The package table (between `<!-- auto:packages -->` markers) is built from the projects themselves: every
    packable project under src/ and tools/, its <Description> (the same text nuget.org shows), and its
    <WhizbangPackageTier> and <WhizbangPackageArea>, which place it under a titled, described table. A packable
    project without them fails the script, so a new package cannot silently go missing from the README.

    Nothing outside the blocks is touched. A source that cannot be read fails the script rather than writing a
    guess.

.PARAMETER ReadmePath
    The README to update. Defaults to README.md at the repository root.

.PARAMETER TestStatusUrl
    URL of the published test-status index.

.PARAMETER CoverageUrl
    URL of Codecov's totals for the main branch.

.EXAMPLE
    pwsh scripts/Update-ReadmeStats.ps1
    Updates README.md in place; prints whether anything changed.
#>
[CmdletBinding()]
param(
  [string]$ReadmePath = (Join-Path $PSScriptRoot '..' 'README.md'),
  [string]$TestStatusUrl = 'https://raw.githubusercontent.com/whizbang-lib/whizbang-lib.github.io/main/src/assets/data/test-status/index.json',
  [string]$CoverageUrl = 'https://api.codecov.io/api/v2/github/whizbang-lib/repos/whizbang/totals/?branch=main',
  [string]$RepoRoot = (Join-Path $PSScriptRoot '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$begin = '<!-- auto:quality -->'
$end = '<!-- /auto:quality -->'

$status = Invoke-RestMethod -Uri $TestStatusUrl -TimeoutSec 60
$passed = [int]$status.total.passed
if ($passed -le 0) { throw "Test status at $TestStatusUrl reports no passing tests; refusing to write a count." }
$rounded = [math]::Floor($passed / 1000) * 1000
$tests = $rounded.ToString('N0', [System.Globalization.CultureInfo]::InvariantCulture)

$totals = Invoke-RestMethod -Uri $CoverageUrl -TimeoutSec 60
$coverageValue = [double]$totals.totals.coverage
if ($coverageValue -le 0) { throw "Codecov at $CoverageUrl reports no coverage for main; refusing to write a number." }
$coverage = if ($coverageValue -ge 100) { '100' } else { $coverageValue.ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture) }

$block = @(
  $begin
  "- **Tests:** $tests+ across unit, generator, integration and transport suites, run on every pull request."
  "- **Coverage:** $coverage% of library lines; every pull request must cover all of its new lines and add no"
  '  SonarCloud findings.'
  $end
) -join "`n"

function Set-Block([string]$Text, [string]$Name, [string]$Body) {
  $open = "<!-- auto:$Name -->"
  $close = "<!-- /auto:$Name -->"
  $start = $Text.IndexOf($open, [StringComparison]::Ordinal)
  $stop = $Text.IndexOf($close, [StringComparison]::Ordinal)
  if ($start -lt 0 -or $stop -lt $start) { throw "README has no '$open' ... '$close' block to update." }
  return $Text.Substring(0, $start) + $open + "`n" + $Body + "`n" + $close + $Text.Substring($stop + $close.Length)
}

# ---- Package table -------------------------------------------------------------------------------------
# Tiers, then areas within each tier, in reading order. Each carries the one line the README shows under its
# heading; the packages themselves come from the projects (<WhizbangPackageTier>, <WhizbangPackageArea>,
# <Description>), so a new package lands in the right table without editing this script unless it opens a new
# area.
$tiers = [ordered]@{
  'Application' = @{
    Title = 'For your application'
    Intro = 'Install the ones for the choices you make. Generators ship as their own packages: add `Whizbang.Generators` with `Whizbang.Core`, and the matching `.Generators` package next to the EF Core store, HotChocolate, FastEndpoints and Sagas.'
    Areas = [ordered]@{
      'Core'            = 'Always: the runtime, and the generators that wire it at compile time.'
      'Data store'      = 'Pick one: where events, read models and the work queues live. See the grid below the table.'
      'Transport'       = 'Pick one when services talk across processes; in-process needs none.'
      'API surface'     = 'Expose read models and commands over GraphQL or REST, with the matching generator.'
      'Real-time'       = 'Push changes to connected clients.'
      'Hosting'         = 'ASP.NET Core request integration, and Aspire resources for local and cloud hosting.'
      'Message offload' = 'Move large message bodies out of the broker and database (claim check).'
      'Observability'   = 'OpenTelemetry integration for tagged messages.'
      'Tools'           = 'Command-line tools and editor support.'
    }
  }
  'Patterns' = @{
    Title = 'Patterns'
    Intro = 'Higher-level building blocks on top of the core packages, each solving a recurring application problem the same way every time.'
    Areas = [ordered]@{
      'Sagas' = 'Coordinate work that spans many streams, with per-item progress, completion and recovery.'
    }
  }
  'Foundation' = @{
    Title = 'Foundation'
    Intro = 'These arrive as dependencies of the packages above. Reference one directly only to build your own store, transport or pattern on the same base.'
    Areas = [ordered]@{
      'Database server'   = 'Server-specific plumbing shared by every store on that server.'
      'Schema'            = 'Schema definitions and SQL generation shared by the stores.'
      'ORM base'          = 'The base to build a store for another database with the same ORM.'
      'API surface base'  = 'What GraphQL and REST surfaces share: command endpoints and their mapping.'
      'Pattern contracts' = 'Contracts a pattern exposes to code that does not reference its runtime.'
    }
  }
}

$packages = @()
$projects = @(Get-ChildItem -Path (Join-Path $RepoRoot 'src'), (Join-Path $RepoRoot 'tools') -Filter '*.csproj' -Recurse -Depth 1)
foreach ($project in $projects | Sort-Object Name) {
  [xml]$xml = Get-Content -Raw $project.FullName
  $first = { param($field) $node = $xml.SelectSingleNode("/Project/PropertyGroup/$field"); if ($node) { $node.InnerText.Trim() } }
  $name = [System.IO.Path]::GetFileNameWithoutExtension($project.Name)
  # src/ packs by default (src/Directory.Build.props); a project opts out with IsPackable=false.
  if ("$(& $first 'IsPackable')" -eq 'false') { continue }
  $tier = & $first 'WhizbangPackageTier'
  $area = & $first 'WhizbangPackageArea'
  $description = & $first 'Description'
  if (-not $tier -or -not $area) { throw "$name is packable but has no <WhizbangPackageTier>/<WhizbangPackageArea>; add them so the README lists it." }
  if (-not $description) { throw "$name is packable but has no <Description>; nuget.org and the README both need one." }
  if (-not $tiers.Contains($tier)) { throw "$name has unknown tier '$tier'." }
  if (-not $tiers[$tier].Areas.Contains($area)) { throw "$name has area '$area', which tier '$tier' does not describe; add it to this script." }
  $summary = ($description -split '(?<=\.)\s', 2)[0].Trim()
  $packages += [pscustomobject]@{ Name = $name; Tier = $tier; Area = $area; Summary = $summary }
}

function Format-Table($Rows) {
  $lines = @('| Package | Version | What it is for |', '|---|---|---|')
  foreach ($p in ($Rows | Sort-Object Name)) {
    $id = "SoftwareExtravaganza.$($p.Name)"
    $lines += "| [$($p.Name)](https://www.nuget.org/packages/$id/) | [![NuGet](https://img.shields.io/nuget/vpre/$id.svg?label=)](https://www.nuget.org/packages/$id/) | $($p.Summary) |"
  }
  return $lines
}

$out = @()
foreach ($tierName in $tiers.Keys) {
  $tier = $tiers[$tierName]
  $out += "### $($tier.Title)", '', $tier.Intro, ''
  foreach ($areaName in $tier.Areas.Keys) {
    $rows = @($packages | Where-Object { $_.Tier -eq $tierName -and $_.Area -eq $areaName })
    if ($rows.Count -eq 0) { continue }
    $out += "#### $areaName", '', $tier.Areas[$areaName], ''
    $out += Format-Table $rows
    $out += ''
    if ($tierName -eq 'Application' -and $areaName -eq 'Data store') {
      $out += '| ORM \ Database | PostgreSQL | SQLite |', '|---|---|---|'
      $out += '| EF Core | `Whizbang.Data.EFCore.Postgres` + `.Generators` | not available |'
      $out += '| Dapper | `Whizbang.Data.Dapper.Postgres` | `Whizbang.Data.Dapper.Sqlite` (development and tests) |'
      $out += ''
    }
  }
}
$packageBlock = ($out -join "`n").TrimEnd()

# ---- Write -------------------------------------------------------------------------------------------------
$readme = [System.IO.File]::ReadAllText((Resolve-Path $ReadmePath))
$qualityBody = ($block -split "`n" | Select-Object -Skip 1 | Select-Object -SkipLast 1) -join "`n"
$updated = Set-Block $readme 'quality' $qualityBody
$updated = Set-Block $updated 'packages' $packageBlock
if ($updated -ceq $readme) {
  Write-Output "README generated blocks are current (tests $tests+, coverage $coverage%, $($packages.Count) packages)."
  exit 0
}

[System.IO.File]::WriteAllText((Resolve-Path $ReadmePath), $updated, [System.Text.UTF8Encoding]::new($false))
Write-Output "README generated blocks updated (tests $tests+, coverage $coverage%, $($packages.Count) packages)."
