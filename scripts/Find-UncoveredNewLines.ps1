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

    Merging reports: each test process writes its own report, and a report line says how many outcomes
    of a decision that process took, not which, so the best count per line under-reports a decision
    whose outcomes ran in different processes. Each process also writes a binary report (*.coverage)
    whose block hits merge exactly; when they are present (CI uploads them) they are merged with
    dotnet-coverage and a decision counts as fully covered once every block of its function ran in
    some process (Merge-BlockCoverage). Without them the script warns that counts may be overstated.

    The whole library: every run also prints, and with -SummaryOutFile saves, the coverage of all
    hand-written library code: lines, and outcomes of hand-written decisions by the same classifier.
    It is informational (the PR comment's last row); -FailOnAny considers new code only.

    Standard practice: every new line and hand-written branch is covered before a PR opens. Run this
    against the CI artifacts (`gh run download <run> -n coverage-unit -D coverage/unit`, and the same for
    every coverage-* artifact) or against a local coverage run.

.PARAMETER CoverageRoot
    Directory searched recursively for *.cobertura.xml.

.PARAMETER BaseRef
    The ref the branch is compared against, e.g. origin/develop. The three-dot diff is used, so the
    merge base is the comparison point. Without it only the merge and the whole-library summary run.

.PARAMETER SourceRoot
    The repository root the report paths are relative to; defaults to the current directory.

.PARAMETER MergedOutFile
    Optional path; the merged coverage is written there as one Cobertura report (CI feeds it to
    ReportGenerator, so Sonar counts the same outcomes as this script).

.PARAMETER SummaryOutFile
    Optional path; the whole-library summary is written there as JSON (Lines, CoveredLines, Outcomes,
    CoveredOutcomes, BlockUnion, Text).

.PARAMETER LibraryGapOutFile
    Optional path; the whole library's gap is written there, one per line: every library line no test
    ran, as path:line: (never ran) source, and every line with a hand-written decision some outcome of
    which no test took, as path:line: (covered/total conditions) source.

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
  [string]$BaseRef,
  [string]$OutFile,
  [string]$BranchOutFile,
  [switch]$FailOnAny,
  [string]$DownloadFromRun,
  [string]$SourceRoot,
  [string]$MergedOutFile,
  [string]$SummaryOutFile,
  [string]$LibraryGapOutFile
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
  The outcomes of each condition of one report line, when the collector's detail can say: every
  condition is a two-outcome jump whose coverage is 0%, 50% or 100%. $null when it cannot (a switch, whose
  outcome count the detail omits), and then only the line's covered count is usable.
#>
function Get-JumpOutcomes([System.Xml.XmlElement]$Line, [int]$Total) {
  $conditions = @($Line.SelectNodes('conditions/condition'))
  if ($conditions.Count -eq 0 -or $conditions.Count * 2 -ne $Total) { return $null }
  $outcomes = [int[]]::new($conditions.Count)
  for ($i = 0; $i -lt $conditions.Count; $i++) {
    if ($conditions[$i].GetAttribute('type') -ne 'jump') { return $null }
    $percent = [double]::Parse(($conditions[$i].GetAttribute('coverage') -replace '%', ''), [System.Globalization.CultureInfo]::InvariantCulture)
    $outcomes[$i] = [int][math]::Round($percent / 50)
  }
  return , $outcomes
}

<#
  Merges Cobertura reports into: relative path -> @{ Hits = @{ line -> max hits };
  Conditions = @{ line -> @(covered, total) } }.

  A report records how many outcomes of a line its process took, not which ones, so two reports that each
  took one outcome of a jump cannot be told from two that took the same one: the covered count kept is the
  best any single report saw, never their sum. Where the collector's per-condition detail allows, each
  condition of the line keeps its own best (a line whose first condition one process covered and whose
  second another did is then fully covered), and the line keeps the larger of the two counts. Both are what
  some report observed, so neither can claim an outcome no test took. Outcomes split across processes on
  one condition are recovered from block data instead (Merge-BlockCoverage).
#>
function Read-CoberturaCoverage([string[]]$ReportPaths, [scriptblock]$ReadSource = $null) {
  $coverage = @{}
  $sourceCache = @{}
  foreach ($reportPath in $ReportPaths) {
    [xml]$xml = Get-Content -Path $reportPath -Raw
    $sources = @($xml.coverage.sources.source | Where-Object { $_ })
    foreach ($cls in $xml.SelectNodes('//class')) {
      $path = Get-RelativeSourcePath ([string]$cls.GetAttribute('filename')) $sources
      if (-not $coverage.ContainsKey($path)) { $coverage[$path] = @{ Hits = @{}; Conditions = @{}; LineBest = @{}; JumpBest = @{}; IfOutcomes = @{} } }
      $entry = $coverage[$path]
      $classHits = @{}
      $halfJumps = [System.Collections.Generic.List[int]]::new()
      foreach ($line in $cls.SelectNodes('lines/line')) {
        $n = [int]$line.GetAttribute('number')
        $h = [int]$line.GetAttribute('hits')
        $classHits[$n] = $h
        if (-not $entry.Hits.ContainsKey($n) -or $entry.Hits[$n] -lt $h) { $entry.Hits[$n] = $h }
        if ($line.GetAttribute('condition-coverage') -match '\((\d+)/(\d+)\)') {
          $covered = [int]$Matches[1]; $total = [int]$Matches[2]
          if ($h -gt 0 -and $covered -eq 1 -and $total -eq 2) { $halfJumps.Add($n) }
          if (-not $entry.LineBest.ContainsKey($n) -or $entry.LineBest[$n] -lt $covered) { $entry.LineBest[$n] = $covered }
          $jumps = Get-JumpOutcomes $line $total
          if (-not $entry.JumpBest.ContainsKey($n)) {
            $entry.JumpBest[$n] = $jumps
          } elseif ($null -eq $jumps -or $null -eq $entry.JumpBest[$n] -or $jumps.Count -ne $entry.JumpBest[$n].Count) {
            $entry.JumpBest[$n] = $null
          } else {
            for ($i = 0; $i -lt $jumps.Count; $i++) {
              if ($entry.JumpBest[$n][$i] -lt $jumps[$i]) { $entry.JumpBest[$n][$i] = $jumps[$i] }
            }
          }
          $best = $entry.LineBest[$n]
          if ($null -ne $entry.JumpBest[$n]) {
            $union = ($entry.JumpBest[$n] | Measure-Object -Sum).Sum
            if ($union -gt $best) { $best = [int]$union }
          }
          $entry.Conditions[$n] = @($best, $total)
        }
      }
      if ($null -ne $ReadSource -and $halfJumps.Count -gt 0) {
        if (-not $sourceCache.ContainsKey($path)) {
          $text = & $ReadSource $path
          $sourceCache[$path] = if ($null -eq $text) { $null } else { [string[]]@($text) }
        }
        Add-IfBodyEvidence $entry $sourceCache[$path] $classHits $halfJumps
      }
    }
  }
  foreach ($entry in $coverage.Values) {
    foreach ($n in @($entry.IfOutcomes.Keys)) {
      if ($entry.IfOutcomes[$n] -eq 3 -and $entry.Conditions[$n][0] -lt 2) { $entry.Conditions[$n] = @(2, 2) }
    }
  }
  return $coverage
}

<#
  Records which outcome each one-of-two `if (...) {` line took in one report, from whether that report
  ran the body: 1 for true, 2 for false, OR'ed across reports. A braced body is entered only through
  the decision's true outcome (C# cannot jump into a block from outside it, and a local function
  declared in it is callable only inside it), so a report that ran any line strictly inside the braces
  took true, and one that ran the decision with a single outcome and none of the body took false. A
  decision both ways across reports has had both its outcomes taken, which the per-line counts alone
  cannot show. Only a single-jump condition counts (total 2), and only a body on lines of its own: a
  body on the decision's line, or an empty one, gives no evidence.
#>
function Add-IfBodyEvidence([hashtable]$Entry, [string[]]$Source, [hashtable]$ClassHits, $HalfJumps) {
  if ($null -eq $Source) { return }
  foreach ($n in $HalfJumps) {
    if ($n -gt $Source.Count) { continue }
    $code = (Get-CodeText $Source[$n - 1]).Trim()
    if ($code -notmatch '^(\}\s*else\s+)?if\s*\(.*\)\s*\{$') { continue }
    $close = Find-ClosingBraceLine $Source $n
    if ($close -lt 0) { continue }
    $body = @($ClassHits.Keys | Where-Object { $_ -gt $n -and $_ -lt $close } | Sort-Object | Select-Object -First 1)
    if ($body.Count -eq 0) { continue }
    $outcome = if ($ClassHits[$body[0]] -gt 0) { 1 } else { 2 }
    $previous = if ($Entry.IfOutcomes.ContainsKey($n)) { $Entry.IfOutcomes[$n] } else { 0 }
    $Entry.IfOutcomes[$n] = $previous -bor $outcome
  }
}

<#
  The line holding the brace that closes the block opened at the end of line $Line (1-based), or -1.
  Strings and comments are skipped (Get-CodeText), so only code braces count.
#>
function Find-ClosingBraceLine([string[]]$Source, [int]$Line) {
  $depth = 1
  for ($i = $Line; $i -lt $Source.Count; $i++) {
    foreach ($c in (Get-CodeText $Source[$i]).ToCharArray()) {
      if ($c -eq '{') { $depth++ } elseif ($c -eq '}') { $depth-- }
      if ($depth -eq 0) { return $i + 1 }
    }
  }
  return -1
}

<#
  Reads the block data `dotnet-coverage merge <*.coverage> -f xml` writes: relative path ->
  @{ Complete = lines some function every block of which ran touches; Incomplete = lines some function
  with a block that never ran touches }. The binary reports record each block's hit, and merging them is
  an exact union across test processes, which the per-line Cobertura summaries are not.
#>
function Read-BlockCoverage([string]$XmlPath) {
  $blocks = @{}
  $settings = [System.Xml.XmlReaderSettings]::new()
  $settings.IgnoreWhitespace = $true
  $reader = [System.Xml.XmlReader]::Create($XmlPath, $settings)
  try {
    $functions = [System.Collections.Generic.List[object]]::new()
    $current = $null
    $files = @{}
    while ($reader.Read()) {
      if ($reader.NodeType -eq [System.Xml.XmlNodeType]::EndElement -and $reader.Name -eq 'module') {
        Add-ModuleBlocks $blocks $functions $files
        $functions.Clear(); $files = @{}
        continue
      }
      if ($reader.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
      switch ($reader.Name) {
        'function' {
          $current = @{ Complete = ([int]$reader.GetAttribute('blocks_not_covered') -eq 0); Ranges = [System.Collections.Generic.List[int[]]]::new() }
          $functions.Add($current)
        }
        'range' {
          $current.Ranges.Add(@([int]$reader.GetAttribute('source_id'), [int]$reader.GetAttribute('start_line'), [int]$reader.GetAttribute('end_line')))
        }
        'source_file' {
          $files[[int]$reader.GetAttribute('id')] = Get-RelativeSourcePath ([string]$reader.GetAttribute('path')) @()
        }
      }
    }
  } finally {
    $reader.Dispose()
  }
  return $blocks
}

function Add-ModuleBlocks([hashtable]$Blocks, $Functions, [hashtable]$Files) {
  foreach ($function in $Functions) {
    $set = if ($function.Complete) { 'Complete' } else { 'Incomplete' }
    foreach ($range in $function.Ranges) {
      $path = $Files[$range[0]]
      if (-not $path) { continue }
      if (-not $Blocks.ContainsKey($path)) {
        $Blocks[$path] = @{ Complete = [System.Collections.Generic.HashSet[int]]::new(); Incomplete = [System.Collections.Generic.HashSet[int]]::new() }
      }
      for ($line = $range[1]; $line -le $range[2]; $line++) { [void]$Blocks[$path][$set].Add($line) }
    }
  }
}

<#
  Marks fully covered every line whose conditions the block data proves were all taken, and returns how
  many lines it changed. The collector counts an outcome of a condition as taken when the block it leads
  to ran (its cobertura writer derives conditions from block hits), and both ends of a condition are
  blocks of the function that contains it. So when every block of every function touching a line ran in
  some test process, every outcome on that line was taken, whichever process took it. A line some function
  with an unrun block touches is left as the reports say: which of its outcomes is missing is unknowable
  without the IL, so nothing is claimed for it.
#>
function Merge-BlockCoverage([hashtable]$Coverage, [hashtable]$Blocks) {
  $changed = 0
  foreach ($path in $Coverage.Keys) {
    if (-not $Blocks.ContainsKey($path)) { continue }
    $proof = $Blocks[$path]
    $entry = $Coverage[$path]
    foreach ($n in @($entry.Conditions.Keys)) {
      $c = $entry.Conditions[$n]
      if ($c[0] -ge $c[1]) { continue }
      if ($proof.Complete.Contains($n) -and -not $proof.Incomplete.Contains($n)) {
        $entry.Conditions[$n] = @($c[1], $c[1])
        $changed++
      }
    }
  }
  return $changed
}

# The dotnet-coverage version CI installs; the same one reads the binary reports locally.
$script:DotnetCoverageVersion = '18.12.0'

<#
  Merges every binary report (*.coverage, written beside each Cobertura report by the collector) under
  $CoverageRoot into one block-data XML at $OutFile and returns its path; $null when there are none. Binary
  reports that the tool is missing to read is an error, not a silent fall back to the weaker merge.
#>
function Get-BlockCoverageXml([string]$CoverageRoot, [string]$OutFile, [string]$Tool = 'dotnet-coverage') {
  $binaries = @(Get-ChildItem -Path $CoverageRoot -Recurse -Filter '*.coverage' -File | ForEach-Object { $_.FullName })
  if ($binaries.Count -eq 0) { return $null }
  if (-not (Get-Command $Tool -ErrorAction SilentlyContinue)) {
    throw "Found $($binaries.Count) binary coverage report(s) under '$CoverageRoot' but '$Tool' is not on PATH, so outcomes taken in different test processes cannot be unioned. Install it: dotnet tool install --global dotnet-coverage --version $script:DotnetCoverageVersion"
  }
  $dir = Split-Path -Parent $OutFile
  if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  & $Tool merge @binaries --output-format xml --output $OutFile --nologo --disable-console-output | Out-Null
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutFile)) {
    throw "'$Tool merge' failed (exit $LASTEXITCODE) merging $($binaries.Count) binary coverage report(s) under '$CoverageRoot'."
  }
  return $OutFile
}

<#
  Writes the merged coverage as one Cobertura report, the input ReportGenerator turns into Sonar's
  coverage, so Sonar counts the same outcomes the gate does. Paths stay relative to $SourceRoot, which is
  written as the report's source directory.
#>
function Write-MergedCobertura([hashtable]$Coverage, [string]$OutFile, [string]$SourceRoot) {
  $dir = Split-Path -Parent $OutFile
  if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  $settings = [System.Xml.XmlWriterSettings]::new()
  $settings.Indent = $true
  $writer = [System.Xml.XmlWriter]::Create($OutFile, $settings)
  try {
    $writer.WriteStartElement('coverage')
    $writer.WriteAttributeString('version', '1.9')
    $writer.WriteStartElement('sources')
    $writer.WriteElementString('source', $SourceRoot)
    $writer.WriteEndElement()
    $writer.WriteStartElement('packages')
    $writer.WriteStartElement('package')
    $writer.WriteAttributeString('name', 'merged')
    $writer.WriteStartElement('classes')
    foreach ($path in ($Coverage.Keys | Sort-Object)) {
      $entry = $Coverage[$path]
      $writer.WriteStartElement('class')
      $writer.WriteAttributeString('name', $path)
      $writer.WriteAttributeString('filename', $path)
      $writer.WriteStartElement('lines')
      foreach ($n in ($entry.Hits.Keys | Sort-Object)) {
        $writer.WriteStartElement('line')
        $writer.WriteAttributeString('number', [string]$n)
        $writer.WriteAttributeString('hits', [string]$entry.Hits[$n])
        if ($entry.Conditions.ContainsKey($n)) {
          $c = $entry.Conditions[$n]
          $percent = [int][math]::Floor(100 * $c[0] / $c[1])
          $writer.WriteAttributeString('branch', 'True')
          $writer.WriteAttributeString('condition-coverage', "$percent% ($($c[0])/$($c[1]))")
        } else {
          $writer.WriteAttributeString('branch', 'False')
        }
        $writer.WriteEndElement()
      }
      $writer.WriteEndElement()
      $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
  } finally {
    $writer.Dispose()
  }
}

<#
  True for hand-written library source: under src/, outside src/Whizbang.Testing (which the collector does
  not instrument), and not generated (*.g.cs, obj/, the .whizbang generator cache), the same scope the
  merged report's file filters give Sonar.
#>
function Test-LibrarySourcePath([string]$Path) {
  if (-not $Path.StartsWith('src/') -or $Path.StartsWith('src/Whizbang.Testing/')) { return $false }
  if ($Path.EndsWith('.g.cs') -or $Path.Contains('/obj/') -or $Path.Contains('/.whizbang/')) { return $false }
  return $true
}

<#
  The whole library's coverage: every line the collector knows in hand-written library source, and every
  outcome of every hand-written decision (the same classifier as the new-code gate, Test-HandWrittenDecision).
  A file $ReadSource cannot return is skipped: its lines cannot be classified.
#>
function Get-WholeLibraryCoverage([hashtable]$Coverage, [scriptblock]$ReadSource) {
  $lines = 0; $coveredLines = 0; $outcomes = 0; $coveredOutcomes = 0
  # The gap, in path and line order: every line no test ran, and every line that ran with a
  # hand-written decision some outcome of which no test took.
  $gap = [System.Collections.Generic.List[string]]::new()
  foreach ($path in ($Coverage.Keys | Sort-Object)) {
    if (-not (Test-LibrarySourcePath $path)) { continue }
    $source = & $ReadSource $path
    if ($null -eq $source) { continue }
    $source = @($source)
    $entry = $Coverage[$path]
    foreach ($n in ($entry.Hits.Keys | Sort-Object)) {
      $lines++
      $text = if ($n -le $source.Count) { ([string]$source[$n - 1]).Trim() } else { '' }
      if ($entry.Hits[$n] -gt 0) { $coveredLines++ } else { $gap.Add("${path}:${n}: (never ran) $text") }
      if (-not $entry.Conditions.ContainsKey($n) -or -not (Test-HandWrittenDecision $text)) { continue }
      $c = $entry.Conditions[$n]
      $outcomes += $c[1]
      $coveredOutcomes += $c[0]
      if ($entry.Hits[$n] -gt 0 -and $c[0] -lt $c[1]) { $gap.Add("${path}:${n}: ($($c[0])/$($c[1]) conditions) $text") }
    }
  }
  return [pscustomobject]@{
    Lines = $lines; CoveredLines = $coveredLines; Outcomes = $outcomes; CoveredOutcomes = $coveredOutcomes; Gap = $gap
  }
}

# A percentage truncated to one decimal, so a gap never reads as 100%.
function Format-Percent([long]$Covered, [long]$Total) {
  if ($Total -eq 0) { return '100%' }
  $tenths = [math]::Floor([double]$Covered * 1000 / $Total) / 10
  return $tenths.ToString('0.#', [System.Globalization.CultureInfo]::InvariantCulture) + '%'
}

<#
  The whole-library line of the PR quality-gate comment and of /pr-health. Informational: the gate fails
  on new code only.
#>
function Format-WholeLibraryLine($Summary) {
  $text = "Whole library: lines $(Format-Percent $Summary.CoveredLines $Summary.Lines), hand-written branches $(Format-Percent $Summary.CoveredOutcomes $Summary.Outcomes)"
  $untested = $Summary.Outcomes - $Summary.CoveredOutcomes
  if ($untested -eq 0) { return "$text, every hand-written decision in the library is covered" }
  $noun = if ($untested -eq 1) { 'outcome' } else { 'outcomes' }
  $caveat = if ($Summary.BlockUnion) { '' } else { '; no block data to union outcomes across test processes, so the gap may be overstated' }
  return "$text ($untested $noun untested$caveat)"
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
if (-not $SourceRoot) { $SourceRoot = (Get-Location).Path }
$readSource = {
  param($p)
  $f = Join-Path $SourceRoot $p
  if (Test-Path -LiteralPath $f) { Get-Content -LiteralPath $f } else { $null }
}.GetNewClosure()

$coverage = Read-CoberturaCoverage @($reports | ForEach-Object { $_.FullName }) -ReadSource $readSource
if ($coverage.Count -eq 0) {
  Write-Error "The $($reports.Count) report(s) under '$CoverageRoot' measured no source file. The collector skipped every assembly."
}

# 1b. Union the outcomes taken in different test processes, from the binary reports' block data.
$blockXml = Get-BlockCoverageXml -CoverageRoot $CoverageRoot -OutFile (Join-Path ([System.IO.Path]::GetTempPath()) "whizbang-blocks-$PID.xml")
$blockUnion = $null -ne $blockXml
if ($blockUnion) {
  $proven = Merge-BlockCoverage -Coverage $coverage -Blocks (Read-BlockCoverage $blockXml)
  Remove-Item -LiteralPath $blockXml -ErrorAction SilentlyContinue
  Write-Host "Block data: $proven line(s) whose outcomes ran in different test processes are fully covered."
} else {
  Write-Host "::warning::No binary (*.coverage) reports under '$CoverageRoot': outcomes of one line taken in different test processes cannot be unioned, so branch counts may be overstated."
}

if ($MergedOutFile) {
  Write-MergedCobertura -Coverage $coverage -OutFile $MergedOutFile -SourceRoot $SourceRoot
  Write-Host "Merged report: $MergedOutFile"
}

# 1c. The whole library, informational.
$whole = Get-WholeLibraryCoverage -Coverage $coverage -ReadSource $readSource
$whole | Add-Member -NotePropertyName BlockUnion -NotePropertyValue $blockUnion
$whole | Add-Member -NotePropertyName Text -NotePropertyValue (Format-WholeLibraryLine $whole)
Write-Host $whole.Text
if ($SummaryOutFile) {
  $dir = Split-Path -Parent $SummaryOutFile
  if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  $fields = $whole | Select-Object Lines, CoveredLines, Outcomes, CoveredOutcomes, BlockUnion, Text
  [System.IO.File]::WriteAllText($SummaryOutFile, ($fields | ConvertTo-Json))
}
if ($LibraryGapOutFile) {
  $dir = Split-Path -Parent $LibraryGapOutFile
  if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  # Written even when empty: an empty list is the evidence of 100%.
  [System.IO.File]::WriteAllLines($LibraryGapOutFile, [string[]]$whole.Gap.ToArray())
}

if (-not $BaseRef) { exit 0 }

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
$result = Get-UncoveredNewCode -Added $added -Coverage $coverage -ReadSource $readSource

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
