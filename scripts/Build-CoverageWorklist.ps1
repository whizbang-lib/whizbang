#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Builds a deduplicated coverage worklist from the per-class HTML coverage report.

.DESCRIPTION
    Why not Summary.txt: its rows are per (assembly, class), and ILRepack merges the shared generator
    sources into several assemblies, so the SAME source file appears in several rows. A 0% row and a 96%
    row for one file coexist there routinely, and ranking by those rows sends you to write tests for lines
    that are already covered by another assembly's copy.

    This keys on the source path from each page's <h2> and treats a line as uncovered only if every page
    that reports it says red: one green (or orange) anywhere means covered. Prints the 45 files with the
    most uncovered lines, then totals.

.PARAMETER ReportDirectory
    The ReportGenerator HTML output directory. Default: coverage-report

.EXAMPLE
    pwsh scripts/Build-CoverageWorklist.ps1          # run from the repo root
#>

param(
    [Parameter()]
    [string]$ReportDirectory = 'coverage-report'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$h2 = [regex]::new('<h2[^>]*>\s*(/[^<\n]*?\.cs)\s*</h2>')
$row = [regex]::new('data-coverage="\{[^"]*?''LVS'':\s*''(\w+)''[^"]*?\}\}"[\s\S]{0,700}?id="file(\d+)_line(\d+)"')

# "file|line" -> $true while red everywhere seen, $false once green or orange anywhere.
$state = @{}
$pages = Get-ChildItem -Path $ReportDirectory -Filter '*.html' -File | Where-Object { $_.Name -ne 'index.html' } | Sort-Object Name
foreach ($page in $pages) {
  $text = [System.IO.File]::ReadAllText($page.FullName)
  $files = @($h2.Matches($text) | ForEach-Object { $_.Groups[1].Value })
  if ($files.Count -eq 0) { continue }
  foreach ($m in $row.Matches($text)) {
    $lvs = $m.Groups[1].Value
    $index = [int]$m.Groups[2].Value
    if ($index -ge $files.Count) { continue }
    $key = "$($files[$index])|$([int]$m.Groups[3].Value)"
    if ($lvs -eq 'red') { if (-not $state.ContainsKey($key)) { $state[$key] = $true } }
    elseif ($lvs -eq 'green' -or $lvs -eq 'orange') { $state[$key] = $false }
  }
}

$bySource = @{}
foreach ($entry in $state.GetEnumerator()) {
  if (-not $entry.Value) { continue }
  $source = $entry.Key.Substring(0, $entry.Key.LastIndexOf('|'))
  if (-not $bySource.ContainsKey($source)) { $bySource[$source] = 0 }
  $bySource[$source]++
}

$rows = @($bySource.GetEnumerator() | Sort-Object -Property @{ Expression = 'Value'; Descending = $true }, @{ Expression = 'Key'; Descending = $true })
foreach ($r in ($rows | Select-Object -First 45)) {
  $shown = ($r.Key -split '/whizbang/')[-1]
  Write-Output ('{0,4}  {1}' -f $r.Value, $shown)
}
Write-Output "classes with >=8: $(@($rows | Where-Object { $_.Value -ge 8 }).Count)"
$total = 0
foreach ($r in $rows) { $total += $r.Value }
Write-Output "TOTAL deduped uncovered: $total"
