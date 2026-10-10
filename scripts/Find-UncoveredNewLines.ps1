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
      AND whose statement contains a hand-written decision (see Test-HandWrittenStatement). The collector
      reports a statement's conditions on its first line, so the whole statement is read, continuation
      lines included. Conditions on a statement with no decision construct (a bare await, an object
      initializer) are the compiler's: async state machines and initializer null checks that no test can
      target. They are not counted. The rule and its reason are in ai-docs/coverage-exclusions.md, "What
      100% of branches means".

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
    some process, or once every block of its own statement ran when the statement only branches within
    itself, as a ?. or ?? does (Merge-BlockCoverage). Without them the script warns that counts may be
    overstated.

    The whole library: every run also prints, and with -SummaryOutFile saves, the coverage of all
    hand-written library code: lines, and outcomes of hand-written decisions by the same classifier.
    Both are gated at 100%: with -FailOnWholeLibrary the script exits 1 while any library line is never
    run, or any hand-written decision outcome in the library is untested (CI fails the quality job on the
    same two counts). A member excluded with [ExcludeFromCodeCoverage] is absent from the reports and so
    is never counted. -FailOnAny considers new code only.

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
    CoveredOutcomes, Untested, UncoveredLines, BlockUnion, Text).

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

.PARAMETER FailOnWholeLibrary
    Exit with code 1 when any line anywhere in the library is never run, or any hand-written decision
    outcome anywhere in the library is untested: the whole-library gate, which holds the library at 100%
    of both once it is there.

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
  [switch]$FailOnWholeLibrary,
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
      if (-not $coverage.ContainsKey($path)) { $coverage[$path] = @{ Hits = @{}; Conditions = @{}; LineBest = @{}; JumpBest = @{}; Totals = @{}; IfOutcomes = @{} } }
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
          # Shared source compiled into several assemblies reports a line once per copy, and copies
          # whose IL differs report different totals. Outcomes are only comparable, and unioned,
          # between copies with the same total; the line then reports the group with the larger gap,
          # so one copy's covered count never meets another copy's total.
          $key = "${n}|${total}"
          if (-not $entry.LineBest.ContainsKey($key) -or $entry.LineBest[$key] -lt $covered) { $entry.LineBest[$key] = $covered }
          $jumps = Get-JumpOutcomes $line $total
          if (-not $entry.JumpBest.ContainsKey($key)) {
            $entry.JumpBest[$key] = $jumps
          } elseif ($null -eq $jumps -or $null -eq $entry.JumpBest[$key] -or $jumps.Count -ne $entry.JumpBest[$key].Count) {
            $entry.JumpBest[$key] = $null
          } else {
            for ($i = 0; $i -lt $jumps.Count; $i++) {
              if ($entry.JumpBest[$key][$i] -lt $jumps[$i]) { $entry.JumpBest[$key][$i] = $jumps[$i] }
            }
          }
          $best = $entry.LineBest[$key]
          if ($null -ne $entry.JumpBest[$key]) {
            $union = ($entry.JumpBest[$key] | Measure-Object -Sum).Sum
            if ($union -gt $best) { $best = [int]$union }
          }
          $best = [math]::Min($best, $total)
          if (-not $entry.Totals.ContainsKey($n)) { $entry.Totals[$n] = @{} }
          $entry.Totals[$n][$total] = $best
          $worst = $null
          foreach ($t in $entry.Totals[$n].Keys) {
            $gapOf = $t - $entry.Totals[$n][$t]
            if ($null -eq $worst -or $gapOf -gt ($worst[1] - $worst[0])) { $worst = @($entry.Totals[$n][$t], $t) }
          }
          $entry.Conditions[$n] = $worst
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
  with a block that never ran touches; NotRun = lines a range touches some block of which never ran
  (covered="no" or "partial"); Starts = lines a range starts on; Unreached = lines a range starts on whose
  next range in its function, a different statement, never ran }. The binary reports record each block's
  hit, and merging them is an exact union across test processes, which the per-line Cobertura summaries
  are not. A function lists its ranges in IL order, so the range after a statement's last one is the code
  it falls through to.
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
          $current = @{ Complete = ([int]$reader.GetAttribute('blocks_not_covered') -eq 0); Ranges = [System.Collections.Generic.List[object]]::new() }
          $functions.Add($current)
        }
        'range' {
          $current.Ranges.Add(@([int]$reader.GetAttribute('source_id'), [int]$reader.GetAttribute('start_line'), [int]$reader.GetAttribute('end_line'), [string]$reader.GetAttribute('covered')))
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
    for ($r = 0; $r -lt $function.Ranges.Count; $r++) {
      $range = $function.Ranges[$r]
      $path = $Files[$range[0]]
      if (-not $path) { continue }
      if (-not $Blocks.ContainsKey($path)) {
        $Blocks[$path] = @{}
        foreach ($name in @('Complete', 'Incomplete', 'NotRun', 'Starts', 'Unreached')) { $Blocks[$path][$name] = [System.Collections.Generic.HashSet[int]]::new() }
      }
      $entry = $Blocks[$path]
      [void]$entry.Starts.Add($range[1])
      for ($line = $range[1]; $line -le $range[2]; $line++) {
        [void]$entry[$set].Add($line)
        if ($range[3] -ne 'yes') { [void]$entry.NotRun.Add($line) }
      }
      if ($r + 1 -lt $function.Ranges.Count) {
        $next = $function.Ranges[$r + 1]
        $sameLine = $next[0] -eq $range[0] -and $next[1] -eq $range[1]
        if (-not $sameLine -and $next[3] -eq 'no') { [void]$entry.Unreached.Add($range[1]) }
      }
    }
  }
}

<#
  Marks fully covered every line whose conditions the block data proves were all taken, and returns how
  many lines it changed. The collector counts an outcome of a condition as taken when the block it leads
  to ran (its cobertura writer derives conditions from block hits), and merged block hits are an exact
  union across test processes. Two proofs, either of which is enough:

  - Every block of every function touching the line ran in some test process: both ends of every
    condition are blocks of the function that contains it, so every outcome on the line was taken.
  - The line proves itself (Test-LineProvenByItsBlocks, with $ReadSource): every block on the statement
    ran, the code it falls through to ran, and the statement is one that only branches within itself.
    This is the case of a ?. or ?? whose outcomes ran in two processes inside a function some other
    statement of which no test ran (#1305).

  Any other line is left as the reports say: which of its outcomes is missing is unknowable without the
  IL, so nothing is claimed for it. Without $ReadSource only the first proof is used.
#>
function Merge-BlockCoverage([hashtable]$Coverage, [hashtable]$Blocks, [scriptblock]$ReadSource = $null) {
  $changed = 0
  foreach ($path in $Coverage.Keys) {
    if (-not $Blocks.ContainsKey($path)) { continue }
    $proof = $Blocks[$path]
    $entry = $Coverage[$path]
    $fileLines = $null
    $sourceRead = $false
    foreach ($n in @($entry.Conditions.Keys)) {
      $c = $entry.Conditions[$n]
      if ($c[0] -ge $c[1]) { continue }
      $proven = $proof.Complete.Contains($n) -and -not $proof.Incomplete.Contains($n)
      if (-not $proven -and $null -ne $ReadSource) {
        if (-not $sourceRead) {
          $read = & $ReadSource $path
          $fileLines = if ($null -eq $read) { $null } else { [string[]]@($read) }
          $sourceRead = $true
        }
        $proven = $null -ne $fileLines -and (Test-LineProvenByItsBlocks $proof $fileLines $n)
      }
      if ($proven) {
        $entry.Conditions[$n] = @($c[1], $c[1])
        $changed++
      }
    }
  }
  return $changed
}

<#
  True when the block data and the source prove every outcome of the conditions on line $Line was taken,
  though its function has a block no test ran. Each condition's two outcomes lead to blocks, and the
  collector counts an outcome as taken when the block it leads to ran. For a statement that only branches
  within itself, a hand-written ?., ??, ?:, && or || (Test-HandWrittenStatement) with none of if, else, a
  loop, switch, case, when, catch, goto, break, continue, yield, await, using, lock, fixed, try or finally,
  each outcome leads either to a block of the statement itself or to the code the statement falls through
  to. So the line is proven when:

  - a statement starts on it, and every range touching it is covered="yes" (every block ran);
  - the range after the statement's in its function did not go unrun (the fall-through target ran: a ?.
    whose only path throws never reaches it, and its null outcome was never taken);
  - the statement is hand-written and branches only within itself, as above.

  An if, a loop or a switch leads its outcomes to blocks elsewhere in the function, and await adds the
  state machine's hidden blocks, so such a line is never proven this way.
#>
function Test-LineProvenByItsBlocks([hashtable]$Proof, [string[]]$Source, [int]$Line) {
  if (-not $Proof.Starts.Contains($Line) -or $Proof.NotRun.Contains($Line) -or $Proof.Unreached.Contains($Line)) { return $false }
  if (-not (Test-HandWrittenStatement $Source $Line)) { return $false }
  $code = (Get-StatementCode $Source $Line) -join ' '
  return $code -cnotmatch '\b(if|else|while|for|foreach|do|switch|case|when|catch|goto|break|continue|yield|await|using|lock|fixed|try|finally)\b'
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
  outcome of every hand-written decision (the same classifier as the new-code gate, Test-HandWrittenStatement).
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
    $source = [string[]]@($source)
    $entry = $Coverage[$path]
    foreach ($n in ($entry.Hits.Keys | Sort-Object)) {
      $lines++
      $text = if ($n -le $source.Count) { ([string]$source[$n - 1]).Trim() } else { '' }
      if ($entry.Hits[$n] -gt 0) { $coveredLines++ } else { $gap.Add("${path}:${n}: (never ran) $text") }
      if (-not $entry.Conditions.ContainsKey($n) -or -not (Test-HandWrittenStatement $source $n)) { continue }
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
  Whether the whole library passes its gate: every line is run by some test, and every hand-written
  decision outcome is taken by some test. Counts only what the reports measured, so a member excluded with
  [ExcludeFromCodeCoverage] (absent from the reports) is never counted; an exclusion is the documented
  decision, not a gap.

  Both halves gate. A line carrying no decision still states something a caller relies on (a documented
  default, a value a record carries, the body of a default interface method), and an uncovered one is the
  same question as an untested outcome: a missing test until someone writes down why not.
#>
function Get-WholeLibraryGateResult($Summary) {
  $untested = [long]($Summary.Outcomes - $Summary.CoveredOutcomes)
  $uncoveredLines = [long]($Summary.Lines - $Summary.CoveredLines)
  $parts = [System.Collections.Generic.List[string]]::new()
  if ($uncoveredLines -gt 0) {
    $lineNoun = if ($uncoveredLines -eq 1) { 'line' } else { 'lines' }
    $parts.Add("$uncoveredLines library $lineNoun no test runs")
  }
  if ($untested -gt 0) {
    $outcomeNoun = if ($untested -eq 1) { 'outcome' } else { 'outcomes' }
    $parts.Add("$untested hand-written decision $outcomeNoun no test takes")
  }
  $message = if ($parts.Count -eq 0) { '' } else {
    "Whole-library gate: $($parts -join ', and '). Every line and every hand-written decision in the library is covered (cover it, remove it with a behavior-neutral refactor, or exclude it by ai-docs/coverage-exclusions.md); the list is in library-gap.txt."
  }
  return [pscustomobject]@{
    Untested = $untested; UncoveredLines = $uncoveredLines; Passed = ($parts.Count -eq 0); Message = $message
  }
}

<#
  The whole-library line of the PR quality-gate comment and of /pr-health. The gate on it is
  Get-WholeLibraryGateResult.
#>
function Format-WholeLibraryLine($Summary) {
  # The percentage alone cannot tell 17 uncovered lines from 80, because it is truncated to a tenth and
  # a library this size floors both to the same figure. The count is what an operator acts on.
  $uncoveredLines = [long]($Summary.Lines - $Summary.CoveredLines)
  $linesText = Format-Percent $Summary.CoveredLines $Summary.Lines
  if ($uncoveredLines -gt 0) {
    $lineNoun = if ($uncoveredLines -eq 1) { 'line' } else { 'lines' }
    $linesText = "$linesText ($uncoveredLines $lineNoun never run)"
  }
  $text = "Whole library: lines $linesText, hand-written branches $(Format-Percent $Summary.CoveredOutcomes $Summary.Outcomes)"
  $untested = $Summary.Outcomes - $Summary.CoveredOutcomes
  if ($untested -eq 0) {
    if ($uncoveredLines -eq 0) { return "$text, every line and every hand-written decision in the library is covered" }
    return $text
  }
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
  True when the statement whose conditions the collector reports on line $Line (1-based) contains a
  hand-written decision (Test-HandWrittenDecision) on any of its lines. The collector reports every
  condition of a statement on the statement's first line, so a decision written on a continuation line,
  a ?. starting the second line of a call chain or a ?? inside an initializer, is counted on a line whose
  own text has none (#1305). A ?. or ?[ split at the line break ("x?" then ".Member") counts too.
#>
function Test-HandWrittenStatement([string[]]$Source, [int]$Line) {
  if ($Line -gt $Source.Count) { return $false }
  if (Test-HandWrittenDecision $Source[$Line - 1]) { return $true }
  # Most lines are whole statements: code ending in ; with every ( closed and no brace. Nothing to read on.
  $firstCode = (Get-CodeText $Source[$Line - 1]).TrimEnd()
  if ($firstCode.EndsWith(';') -and $firstCode.Split('(').Count -eq $firstCode.Split(')').Count -and $firstCode.IndexOfAny([char[]]'{}') -lt 0) { return $false }
  $codes = Get-StatementCode $Source $Line
  for ($i = 1; $i -lt $codes.Count; $i++) {
    if (Test-HandWrittenDecision $codes[$i]) { return $true }
    if ($codes[$i - 1].TrimEnd().EndsWith('?') -and $codes[$i].TrimStart() -match '^[.\[]') { return $true }
  }
  return $false
}

<#
  The code of the statement that starts on line $Line (1-based), one string per source line from $Line to
  the line the statement ends on, with comments and the text of literals removed (Get-CodeText).

  The statement ends at a ';' outside its parentheses, at the '{' that opens a block (after ')' that is not
  an object creation's argument list, or after else, try, finally, do or an accessor keyword), at a '}'
  closing the block it is in, or at a ')' or ']' closing a bracket opened before it. Any other '{' (an
  object or collection initializer, an anonymous object, a switch expression) is part of the statement.
  A lambda's body is a function of its own, whose conditions the collector reports on the lambda's own
  lines, so a block body is left out after line $Line, and so is an expression body that starts on a
  continuation line; on line $Line everything is kept, as the single-line rule always did. A '}' that
  opens line $Line ("} else {") belongs to the block before it, and a line that is nothing but closing
  braces ends there.

  Reading stops after a line that opens a multi-line string ("""raw""" or @"verbatim") or block comment:
  a per-line reader would take the text after it for code (a raw SQL string's CASE WHEN for a decision).
#>
function Get-StatementCode([string[]]$Source, [int]$Line) {
  $blockWords = '^(else|try|finally|do|get|set|init|add|remove|checked|unchecked|unsafe)$'
  $codes = [System.Collections.Generic.List[string]]::new()
  $paren = 0          # ( and [ the statement opened and has not closed
  $brace = 0          # initializer, collection and switch-expression braces it opened
  $creations = [System.Collections.Generic.Stack[bool]]::new()  # per open (: whether it is an object creation's
  $closedCreation = $false  # whether the last ) closed an object creation's argument list
  $body = 0           # brace depth inside a lambda's block body
  $exprBody = -1      # bracket depth inside a lambda's expression body that began on a continuation line
  $arrow = -1         # the line of a => whose body has not begun
  $seen = $false      # whether any code but a closing brace has been read
  $last = ' '         # the last code character read
  $word = ''; $lastWord = ''
  for ($i = $Line - 1; $i -lt $Source.Count; $i++) {
    $first = $i -eq $Line - 1
    $raw = [string]$Source[$i]
    $code = Get-CodeText $raw
    $kept = [System.Text.StringBuilder]::new()
    $end = $false
    for ($k = 0; $k -lt $code.Length -and -not $end; $k++) {
      $c = $code[$k]
      $space = [char]::IsWhiteSpace($c)
      if ($arrow -ge 0 -and -not $space) {
        $bodyLine = $arrow
        $arrow = -1
        if ($c -eq '{') {
          $body = 1
          if ($first) { [void]$kept.Append($c) }
          continue
        }
        if ($bodyLine -gt $Line - 1) { $exprBody = 0 }
      }
      if ($body -gt 0) {
        if ($c -eq '{') { $body++ } elseif ($c -eq '}') { $body-- }
        if ($first) { [void]$kept.Append($c) }
        continue
      }
      if ($exprBody -ge 0) {
        if ('([{'.IndexOf($c) -ge 0) { $exprBody++; continue }
        $close = ')]}'.IndexOf($c) -ge 0
        if ($close -and $exprBody -gt 0) { $exprBody--; continue }
        if (-not $close -and -not (($c -eq ',' -or $c -eq ';') -and $exprBody -eq 0)) { continue }
        $exprBody = -1
      }
      if ([char]::IsLetterOrDigit($c) -or $c -eq '_') { $word += $c } elseif ($word) { $lastWord = $word; $word = '' }
      if ($c -eq '=' -and $k + 1 -lt $code.Length -and $code[$k + 1] -eq '>') {
        $k++
        $arrow = $i
        $seen = $true
        $last = '>'
        if ($first) { [void]$kept.Append('=>') }
        continue
      }
      if ($c -eq '(' -or $c -eq '[') {
        $paren++
        $creations.Push($c -eq '(' -and $kept.ToString() -match '\bnew\b[^;(){}=]*$')
      } elseif ($c -eq ')' -or $c -eq ']') {
        $paren--
        if ($paren -lt 0) { $end = $true; break }
        $closedCreation = $creations.Pop()
      } elseif ($c -eq '{') {
        $opensBlock = ($last -eq ')' -and -not $closedCreation) -or ([string]$last -match '\w' -and $lastWord -cmatch $blockWords)
        if ($paren -eq 0 -and $brace -eq 0 -and $opensBlock) { $end = $true } else { $brace++ }
      } elseif ($c -eq '}') {
        if ($brace -gt 0) {
          $brace--
        } elseif ($seen) {
          $end = $true
          break
        } else {
          continue
        }
      } elseif ($c -eq ';' -and $paren -le 0) {
        $end = $true
      }
      [void]$kept.Append($c)
      if (-not $space) { $last = $c; $seen = $true }
    }
    if ($word) { $lastWord = $word; $word = '' }
    $codes.Add($kept.ToString())
    if ($end -or -not $seen) { break }
    if ($raw.Contains('"""') -or $raw.Contains('@"') -or $raw.LastIndexOf('/*') -gt $raw.LastIndexOf('*/')) { break }
  }
  return , $codes.ToArray()
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
      if ($null -eq $source) { $source = [string[]]@(& $ReadSource $path) }
      $text = if ($n -le $source.Count) { ([string]$source[$n - 1]).Trim() } else { '' }
      if ($uncoveredLine) {
        $lines.Add("${path}:${n}: $text")
      } elseif (Test-HandWrittenStatement $source $n) {
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
  $proven = Merge-BlockCoverage -Coverage $coverage -Blocks (Read-BlockCoverage $blockXml) -ReadSource $readSource
  Remove-Item -LiteralPath $blockXml -ErrorAction SilentlyContinue
  Write-Host "Block data: $proven line(s) whose outcomes ran in different test processes are fully covered."
} else {
  Write-Host "::warning::No binary (*.coverage) reports under '$CoverageRoot': outcomes of one line taken in different test processes cannot be unioned, so branch counts may be overstated."
}

if ($MergedOutFile) {
  Write-MergedCobertura -Coverage $coverage -OutFile $MergedOutFile -SourceRoot $SourceRoot
  Write-Host "Merged report: $MergedOutFile"
}

# 1c. The whole library, gated at 100%.
$whole = Get-WholeLibraryCoverage -Coverage $coverage -ReadSource $readSource
$whole | Add-Member -NotePropertyName BlockUnion -NotePropertyValue $blockUnion
$whole | Add-Member -NotePropertyName Text -NotePropertyValue (Format-WholeLibraryLine $whole)
$wholeGate = Get-WholeLibraryGateResult $whole
$whole | Add-Member -NotePropertyName Untested -NotePropertyValue $wholeGate.Untested
$whole | Add-Member -NotePropertyName UncoveredLines -NotePropertyValue $wholeGate.UncoveredLines
Write-Host $whole.Text
if ($SummaryOutFile) {
  $dir = Split-Path -Parent $SummaryOutFile
  if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  $fields = $whole | Select-Object Lines, CoveredLines, Outcomes, CoveredOutcomes, Untested, UncoveredLines, BlockUnion, Text
  [System.IO.File]::WriteAllText($SummaryOutFile, ($fields | ConvertTo-Json))
}
if ($LibraryGapOutFile) {
  $dir = Split-Path -Parent $LibraryGapOutFile
  if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  # Written even when empty: an empty list is the evidence of 100%.
  [System.IO.File]::WriteAllLines($LibraryGapOutFile, [string[]]$whole.Gap.ToArray())
}

if ($FailOnWholeLibrary -and -not $wholeGate.Passed) {
  Write-Host "::error::$($wholeGate.Message)"
  exit 1
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
