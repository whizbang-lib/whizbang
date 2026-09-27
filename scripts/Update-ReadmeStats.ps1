#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Rewrites the README's generated quality block from the numbers CI already publishes.

.DESCRIPTION
    The README carries a block between `<!-- auto:quality -->` and `<!-- /auto:quality -->`. This script
    regenerates only that block, from two published sources, so the README never states a number nobody
    measured:

    - Tests: the latest develop run's totals, which CI publishes to the docs site as test-status/index.json.
    - Coverage: Codecov's totals for the main branch.

    The test count is rounded DOWN to the nearest thousand ("26,000+") so the file changes when the suite
    grows by a thousand, not on every run. Coverage is shown to one decimal, or as "100%" when it is 100.

    Nothing outside the block is touched. A source that cannot be read fails the script rather than writing a
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
  [string]$CoverageUrl = 'https://api.codecov.io/api/v2/github/whizbang-lib/repos/whizbang/totals/?branch=main'
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

$readme = [System.IO.File]::ReadAllText((Resolve-Path $ReadmePath))
$start = $readme.IndexOf($begin, [StringComparison]::Ordinal)
$stop = $readme.IndexOf($end, [StringComparison]::Ordinal)
if ($start -lt 0 -or $stop -lt $start) { throw "README has no '$begin' ... '$end' block to update." }

$updated = $readme.Substring(0, $start) + $block + $readme.Substring($stop + $end.Length)
if ($updated -ceq $readme) {
  Write-Output "README quality block is current (tests $tests+, coverage $coverage%)."
  exit 0
}

[System.IO.File]::WriteAllText((Resolve-Path $ReadmePath), $updated, [System.Text.UTF8Encoding]::new($false))
Write-Output "README quality block updated (tests $tests+, coverage $coverage%)."
