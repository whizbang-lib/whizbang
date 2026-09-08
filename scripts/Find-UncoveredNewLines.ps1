#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Lists every library line a branch adds or changes that no test executed, from Cobertura reports.

.DESCRIPTION
    Reproduces SonarCloud's "uncovered new lines" locally and in CI so it can be a gate rather than a
    number on a dashboard. It merges every Cobertura report under -CoverageRoot (maximum hits per line
    across reports), takes the lines this branch adds under src/ from `git diff -U0 <base>...HEAD`, and
    prints each added line that the merged report knows about and that no test hit.

    A line the report does not know about (comments, braces, declarations) is not coverable and is not
    reported. Test projects, tools and generated files are outside src/ or excluded by the coverage
    filters, so they never appear.

    Standard practice: every new line is covered before a PR opens. Run this against the CI artifacts
    (`gh run download <run> -n coverage-unit -D coverage/unit`, and the same for every coverage-*
    artifact) or against a local coverage run.

.PARAMETER CoverageRoot
    Directory searched recursively for *.cobertura.xml.

.PARAMETER BaseRef
    The ref the branch is compared against, e.g. origin/develop. The three-dot diff is used, so the
    merge base is the comparison point.

.PARAMETER OutFile
    Optional path; the same lines are written there, one per line as path:line: source.

.PARAMETER FailOnAny
    Exit with code 1 when any uncovered line is found. CI passes this.

.PARAMETER DownloadFromRun
    A GitHub Actions run id. Every coverage-* artifact of that run is downloaded into -CoverageRoot
    first (gh CLI), so one command reproduces the CI gate for a PR:
    `pwsh scripts/Find-UncoveredNewLines.ps1 -CoverageRoot coverage-ci -BaseRef origin/develop -DownloadFromRun <id>`.
    The run id is in the "CI Result" check's link on the PR, or `gh run list --branch <branch>`.

.EXAMPLE
    pwsh scripts/Find-UncoveredNewLines.ps1 -CoverageRoot coverage -BaseRef origin/develop -FailOnAny
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$CoverageRoot,
  [Parameter(Mandatory = $true)][string]$BaseRef,
  [string]$OutFile,
  [switch]$FailOnAny,
  [string]$DownloadFromRun
)

$ErrorActionPreference = 'Stop'

if ($DownloadFromRun) {
  New-Item -ItemType Directory -Force -Path $CoverageRoot | Out-Null
  $names = gh api "repos/{owner}/{repo}/actions/runs/$DownloadFromRun/artifacts" --paginate --jq '.artifacts[] | select(.name | startswith("coverage-")) | .name'
  if ($LASTEXITCODE -ne 0) { Write-Error "Could not list artifacts of run $DownloadFromRun." }
  foreach ($n in ($names -split "`n" | Where-Object { $_ })) {
    Write-Host "Downloading $n"
    gh run download $DownloadFromRun -n $n -D (Join-Path $CoverageRoot $n) | Out-Null
  }
}

function Get-RelativeSourcePath([string]$fileName, [string[]]$sources) {
  $fn = $fileName -replace '\\', '/'
  foreach ($s in $sources) {
    $prefix = (($s -replace '\\', '/').TrimEnd('/')) + '/'
    if ($fn.StartsWith($prefix)) { $fn = $fn.Substring($prefix.Length) }
  }
  $i = $fn.IndexOf('src/')
  if ($i -gt 0) { $fn = $fn.Substring($i) }
  return $fn
}

# 1. Merge every report: relative path -> (line -> max hits).
$hits = @{}
$reports = Get-ChildItem -Path $CoverageRoot -Recurse -Filter '*.cobertura.xml' -File
if ($reports.Count -eq 0) {
  Write-Error "No *.cobertura.xml under '$CoverageRoot'."
}
foreach ($report in $reports) {
  [xml]$xml = Get-Content -Path $report.FullName -Raw
  $sources = @($xml.coverage.sources.source | Where-Object { $_ })
  foreach ($cls in $xml.SelectNodes('//class')) {
    $path = Get-RelativeSourcePath ([string]$cls.GetAttribute('filename')) $sources
    if (-not $hits.ContainsKey($path)) { $hits[$path] = @{} }
    $perLine = $hits[$path]
    foreach ($line in $cls.SelectNodes('lines/line')) {
      $n = [int]$line.GetAttribute('number')
      $h = [int]$line.GetAttribute('hits')
      if (-not $perLine.ContainsKey($n) -or $perLine[$n] -lt $h) { $perLine[$n] = $h }
    }
  }
}

# 2. Lines this branch adds under src/.
$diff = git diff -U0 "$BaseRef...HEAD" -- src
if ($LASTEXITCODE -ne 0) { Write-Error "git diff against '$BaseRef' failed." }
$added = @{}
$current = $null
foreach ($line in ($diff -split "`n")) {
  if ($line.StartsWith('+++ b/')) { $current = $line.Substring(6); continue }
  if ($current -and $line -match '^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@') {
    $start = [int]$Matches[1]
    $count = if ($null -ne $Matches[2] -and $Matches[2] -ne '') { [int]$Matches[2] } else { 1 }
    if (-not $added.ContainsKey($current)) { $added[$current] = New-Object System.Collections.Generic.HashSet[int] }
    for ($k = $start; $k -lt $start + $count; $k++) { [void]$added[$current].Add($k) }
  }
}

# 3. Intersect.
$uncovered = New-Object System.Collections.Generic.List[string]
foreach ($path in ($added.Keys | Sort-Object)) {
  if (-not $hits.ContainsKey($path)) { continue }
  $perLine = $hits[$path]
  $source = $null
  foreach ($n in ($added[$path] | Sort-Object)) {
    if ($perLine.ContainsKey($n) -and $perLine[$n] -eq 0) {
      if ($null -eq $source) { $source = Get-Content -Path $path }
      $text = if ($n -le $source.Count) { $source[$n - 1].Trim() } else { '' }
      $uncovered.Add("${path}:${n}: $text")
    }
  }
}

$changedCoverable = @($added.Keys | Where-Object { $hits.ContainsKey($_) }).Count
Write-Host "Reports merged: $($reports.Count). Changed library files with coverage data: $changedCoverable. Uncovered new lines: $($uncovered.Count)."
foreach ($u in $uncovered) { Write-Host "  $u" }

if ($OutFile) {
  # Always write the file, even when the list is empty: an empty list is the evidence of 100%, and a
  # missing file reads as "the gate did not run". Set-Content on an empty pipeline writes nothing.
  [System.IO.File]::WriteAllLines($OutFile, [string[]]$uncovered.ToArray())
}

if ($FailOnAny -and $uncovered.Count -gt 0) {
  Write-Host "::error::$($uncovered.Count) new line(s) have no test coverage. Every new line is covered before a PR merges."
  exit 1
}
exit 0
