#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Lists every library line a branch adds or changes that no test executed, and every added line with a
    hand-written decision some outcome of which no test took, from Cobertura reports.

.DESCRIPTION
    Reproduces SonarCloud's "uncovered new lines" locally and in CI so it can be a gate rather than a
    number on a dashboard, and extends it to branches. It merges every Cobertura report under
    -CoverageRoot (maximum hits, and maximum covered conditions, per line across reports), takes the lines
    this branch adds under src/ from `git diff -U0 <base>...HEAD`, and reports two lists:

    - Uncovered new lines: an added line the merged report knows about and that no test hit.
    - Uncovered new branches: an added line that ran, carries conditions, not all of which any test took,
      AND whose source contains a hand-written decision (see Test-HandWrittenDecision). Conditions on a
      line with no decision construct (a bare await, an object initializer) are the compiler's: async
      state machines and initializer null checks that no test can target. They are not counted. The rule
      and its reason are in ai-docs/coverage-exclusions.md, "What 100% of branches means".

    A line the report does not know about (comments, braces, declarations) is not coverable and is not
    reported. Test projects, tools and generated files are outside src/ or excluded by the coverage
    filters, so they never appear.

    src/Whizbang.Testing is excluded from the diff side as well, because the coverage settings exclude
    that assembly from instrumentation: its lines are unmeasurable here, and counting them would let
    this script report "every added library line is covered" over lines it cannot see. See L12 in
    plans/archive/db-load-under-bulk-import.md.

    Standard practice: every new line and hand-written branch is covered before a PR opens. Run this
    against the CI artifacts (`gh run download <run> -n coverage-unit -D coverage/unit`, and the same for
    every coverage-* artifact) or against a local coverage run.

.PARAMETER CoverageRoot
    Directory searched recursively for *.cobertura.xml.

.PARAMETER BaseRef
    The ref the branch is compared against, e.g. origin/develop. The three-dot diff is used, so the
    merge base is the comparison point.

.PARAMETER OutFile
    Optional path; the uncovered lines are written there, one per line as path:line: source.

.PARAMETER BranchOutFile
    Optional path; the uncovered branches are written there, one per line as
    path:line: (covered/total conditions) source.

.PARAMETER FailOnAny
    Exit with code 1 when any uncovered line or branch is found.

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
  [string]$BranchOutFile,
  [switch]$FailOnAny,
  [string]$DownloadFromRun
)

$ErrorActionPreference = 'Stop'

function Get-RelativeSourcePath([string]$fileName, [string[]]$sources) {
  $fn = $fileName -replace '\\', '/'
  foreach ($s in $sources) {
    $prefix = (($s -replace '\\', '/').TrimEnd('/')) + '/'
    if ($fn.StartsWith($prefix)) { $fn = $fn.Substring($prefix.Length) }
  }
  # Anchor on the LAST '/src/', not the first 'src/'. A checkout living under a path that itself
  # contains a 'src' segment (a developer's ~/src/<repo>, which is an ordinary layout) normalized to
  # 'src/<user-dirs>/.../src/Whizbang.Core/X.cs', which matches no path git diff reports. The gate
  # then found zero changed files with coverage and printed a clean result, so running it locally
  # said "nothing uncovered" no matter what the branch actually did. CI was unaffected only because
  # its checkout path happens to contain 'src' exactly once.
  $i = $fn.LastIndexOf('/src/')
  if ($i -ge 0) { $fn = $fn.Substring($i + 1) }
  if ($fn.StartsWith('/_/')) { $fn = $fn.Substring(3) }
  return $fn
}

<#
  Merges Cobertura reports into: relative path -> @{ Hits = @{ line -> max hits };
  Conditions = @{ line -> @(covered, total) } }, keeping the best covered count seen for a line.
  Every report instruments the same IL, so a line's condition set is the same in each; the maximum
  covered count is the closest a per-line summary can get to the union of the outcomes taken.
#>
function Read-CoberturaCoverage([string[]]$ReportPaths) {
  $coverage = @{}
  foreach ($reportPath in $ReportPaths) {
    [xml]$xml = Get-Content -Path $reportPath -Raw
    $sources = @($xml.coverage.sources.source | Where-Object { $_ })
    foreach ($cls in $xml.SelectNodes('//class')) {
      $path = Get-RelativeSourcePath ([string]$cls.GetAttribute('filename')) $sources
      if (-not $coverage.ContainsKey($path)) { $coverage[$path] = @{ Hits = @{}; Conditions = @{} } }
      $entry = $coverage[$path]
      foreach ($line in $cls.SelectNodes('lines/line')) {
        $n = [int]$line.GetAttribute('number')
        $h = [int]$line.GetAttribute('hits')
        if (-not $entry.Hits.ContainsKey($n) -or $entry.Hits[$n] -lt $h) { $entry.Hits[$n] = $h }
        if ($line.GetAttribute('condition-coverage') -match '\((\d+)/(\d+)\)') {
          $covered = [int]$Matches[1]; $total = [int]$Matches[2]
          if (-not $entry.Conditions.ContainsKey($n) -or $entry.Conditions[$n][0] -lt $covered) {
            $entry.Conditions[$n] = @($covered, $total)
          }
        }
      }
    }
  }
  return $coverage
}

<#
  Removes comments and the literal text of strings and chars from one line of C#, keeping the code
  inside interpolation holes ($"...{code}..."), so a construct is only ever matched where it is code.
#>
function Get-CodeText([string]$Line) {
  $sb = [System.Text.StringBuilder]::new()
  $i = 0
  $n = $Line.Length
  while ($i -lt $n) {
    $c = $Line[$i]
    if ($c -eq '/' -and $i + 1 -lt $n -and $Line[$i + 1] -eq '/') { break }
    if ($c -eq '/' -and $i + 1 -lt $n -and $Line[$i + 1] -eq '*') {
      $end = $Line.IndexOf('*/', $i + 2)
      if ($end -lt 0) { break }
      $i = $end + 2; [void]$sb.Append(' '); continue
    }
    if ($c -eq "'") {
      # A char literal: 'x', '\n', '\''.
      $j = $i + 1
      if ($j -lt $n -and $Line[$j] -eq '\') { $j += 2 } else { $j += 1 }
      while ($j -lt $n -and $Line[$j] -ne "'") { $j++ }
      $i = $j + 1; [void]$sb.Append(' '); continue
    }
    if ($c -eq '"' -or (($c -eq '$' -or $c -eq '@') -and $i + 1 -lt $n -and ($Line[$i + 1] -eq '"' -or (($Line[$i + 1] -eq '$' -or $Line[$i + 1] -eq '@') -and $i + 2 -lt $n -and $Line[$i + 2] -eq '"')))) {
      $prefixEnd = $Line.IndexOf('"', $i)
      $prefix = $Line.Substring($i, $prefixEnd - $i)
      $interpolated = $prefix.Contains('$')
      $verbatim = $prefix.Contains('@')
      $j = $prefixEnd + 1
      while ($j -lt $n) {
        $d = $Line[$j]
        if (-not $verbatim -and $d -eq '\') { $j += 2; continue }
        if ($d -eq '"') {
          if ($verbatim -and $j + 1 -lt $n -and $Line[$j + 1] -eq '"') { $j += 2; continue }
          break
        }
        if ($interpolated -and $d -eq '{') {
          if ($j + 1 -lt $n -and $Line[$j + 1] -eq '{') { $j += 2; continue }
          # Keep the hole's code, up to its matching brace.
          $depth = 1; $k = $j + 1
          while ($k -lt $n -and $depth -gt 0) {
            if ($Line[$k] -eq '{') { $depth++ } elseif ($Line[$k] -eq '}') { $depth-- }
            if ($depth -gt 0) { [void]$sb.Append($Line[$k]) }
            $k++
          }
          [void]$sb.Append(' ')
          $j = $k; continue
        }
        $j++
      }
      $i = $j + 1; [void]$sb.Append(' '); continue
    }
    [void]$sb.Append($c)
    $i++
  }
  return $sb.ToString()
}

<#
  True when one source line contains a hand-written decision: if / else if, a conditional ?:, ??, ??=,
  ?. or ?[, &&, ||, switch / case / a switch-expression arm, when, catch, while / for / foreach, or an
  `is` pattern test. Only conditions on such a line are the author's to cover; conditions on a line with
  none of these (a bare await, an object initializer) are compiler-generated and the gate excludes them.
#>
function Test-HandWrittenDecision([string]$Line) {
  $code = Get-CodeText $Line
  if ([string]::IsNullOrWhiteSpace($code)) { return $false }
  $keyword = '\b(if|while|for|foreach|switch|case|when|catch|is)\b'
  if ($code -match $keyword) { return $true }
  if ($code -match '\?\?|\?\.|\?\[|&&|\|\|') { return $true }
  # A conditional operator: the formatter puts a space on each side of ? and of :, which a nullable
  # annotation (string? x) never has before its ?.
  $trimmed = $code.Trim()
  if ($code -match '\s\?\s' -and $code -match '\s:(\s|$)') { return $true }
  # The continuation lines of a conditional split across lines.
  if ($trimmed -match '^[?:]\s') { return $true }
  # A switch-expression arm on its own line: "<pattern> => <result>" where the pattern is not a lambda
  # parameter list and the line is neither a member declaration nor a statement (it ends in neither ;
  # nor {).
  if ($trimmed -match '^(?!\.|\(|return\b|var\b|get\b|set\b|init\b|public\b|private\b|protected\b|internal\b|static\b|override\b|async\b)(?:[^()=]|=(?!>))*?\s=>\s.*[^;{]$') { return $true }
  return $false
}

<#
  Intersects the added lines with the merged coverage. $Added: path -> HashSet[int]; $Coverage: from
  Read-CoberturaCoverage; $ReadSource: a scriptblock taking a path and returning its lines.
#>
function Get-UncoveredNewCode([hashtable]$Added, [hashtable]$Coverage, [scriptblock]$ReadSource) {
  $lines = [System.Collections.Generic.List[string]]::new()
  $branches = [System.Collections.Generic.List[string]]::new()
  foreach ($path in ($Added.Keys | Sort-Object)) {
    if (-not $Coverage.ContainsKey($path)) { continue }
    $entry = $Coverage[$path]
    $source = $null
    foreach ($n in ($Added[$path] | Sort-Object)) {
      if (-not $entry.Hits.ContainsKey($n)) { continue }
      $uncoveredLine = $entry.Hits[$n] -eq 0
      $partial = $entry.Conditions.ContainsKey($n) -and $entry.Conditions[$n][0] -lt $entry.Conditions[$n][1]
      if (-not $uncoveredLine -and -not $partial) { continue }
      if ($null -eq $source) { $source = @(& $ReadSource $path) }
      $text = if ($n -le $source.Count) { ([string]$source[$n - 1]).Trim() } else { '' }
      if ($uncoveredLine) {
        $lines.Add("${path}:${n}: $text")
      } elseif (Test-HandWrittenDecision $text) {
        $c = $entry.Conditions[$n]
        $branches.Add("${path}:${n}: ($($c[0])/$($c[1]) conditions) $text")
      }
    }
  }
  return [pscustomobject]@{ Lines = $lines; Branches = $branches }
}

# Dot-sourced by the tests for its functions only.
if ($MyInvocation.InvocationName -eq '.') { return }

if ($DownloadFromRun) {
  New-Item -ItemType Directory -Force -Path $CoverageRoot | Out-Null
  $names = gh api "repos/{owner}/{repo}/actions/runs/$DownloadFromRun/artifacts" --paginate --jq '.artifacts[] | select(.name | startswith("coverage-")) | .name'
  if ($LASTEXITCODE -ne 0) { Write-Error "Could not list artifacts of run $DownloadFromRun." }
  foreach ($n in ($names -split "`n" | Where-Object { $_ })) {
    Write-Host "Downloading $n"
    gh run download $DownloadFromRun -n $n -D (Join-Path $CoverageRoot $n) | Out-Null
  }
}

# 1. Merge every report.
$reports = @(Get-ChildItem -Path $CoverageRoot -Recurse -Filter '*.cobertura.xml' -File)
if ($reports.Count -eq 0) {
  Write-Error "No *.cobertura.xml under '$CoverageRoot'."
}
$coverage = Read-CoberturaCoverage @($reports | ForEach-Object { $_.FullName })

# 2. Lines this branch adds under src/, less the projects the coverage side cannot see.
#
# codecoverage.config excludes test-infrastructure assemblies from instrumentation
# (`.*\.Testing\.dll$`, under "Include only Whizbang production assemblies in coverage"), so no
# report ever carries a line from src/Whizbang.Testing/. Counting those lines here would have this
# script claim coverage over lines it cannot measure: they would read as covered whether or not a
# test executed them, and the gate would be green either way. Excluding them keeps the claim honest
# and narrow, which is the decision recorded as L12 in plans/archive/db-load-under-bulk-import.md. The two
# lists are one project each today and must stay in step: a new project excluded there belongs here.
$excludedFromDiff = @(':(exclude)src/Whizbang.Testing/**')
$diff = git diff -U0 "$BaseRef...HEAD" -- src $excludedFromDiff
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
$result = Get-UncoveredNewCode -Added $added -Coverage $coverage -ReadSource { param($p) Get-Content -Path $p }

$changedCoverable = @($added.Keys | Where-Object { $coverage.ContainsKey($_) }).Count
Write-Host "Reports merged: $($reports.Count). Changed library files with coverage data: $changedCoverable. Uncovered new lines: $($result.Lines.Count). Uncovered new branches: $($result.Branches.Count)."
foreach ($u in $result.Lines) { Write-Host "  line   $u" }
foreach ($u in $result.Branches) { Write-Host "  branch $u" }

# Always write the files, even when a list is empty: an empty list is the evidence of 100%, and a
# missing file reads as "the gate did not run". Set-Content on an empty pipeline writes nothing.
if ($OutFile) { [System.IO.File]::WriteAllLines($OutFile, [string[]]$result.Lines.ToArray()) }
if ($BranchOutFile) { [System.IO.File]::WriteAllLines($BranchOutFile, [string[]]$result.Branches.ToArray()) }

if ($FailOnAny -and ($result.Lines.Count -gt 0 -or $result.Branches.Count -gt 0)) {
  Write-Host "::error::$($result.Lines.Count) new line(s) and $($result.Branches.Count) new branch(es) have no test coverage. Every new line and hand-written branch is covered before a PR merges."
  exit 1
}
exit 0
