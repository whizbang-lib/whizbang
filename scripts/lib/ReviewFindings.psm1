#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Review findings: what CI checks find in a pull request, written so people and a script can read it.

.DESCRIPTION
    CI posts its findings in a pull request comment twice over: a table for people, and a hidden block
    for scripts/Invoke-PrReviewDraft.ps1, which turns them into the maintainer's own draft review:

        <!-- whizbang-review:v1
        {"source":"review-checks","sha":"<head commit>","findings":[{"id":"...","rule":"...", ...}]}
        -->

    The block is versioned (v1) so the format can change without breaking an older reader. "--" never
    appears inside it (each is written as "-\u002d", which JSON reads back as "--"), so a finding's text
    cannot close the HTML comment early.

    This module holds every piece both sides need: the block writer and reader, the readers for the files
    the PR quality gate writes, a unified-diff reader, the deterministic checks, and the draft-review plan.
    Nothing here calls GitHub or git; the entry scripts do, so every rule is tested on plain text.

    Import:
        Import-Module (Join-Path $PSScriptRoot 'lib' 'ReviewFindings.psm1') -Force
#>

Set-StrictMode -Version Latest

$script:BlockStart = '<!-- whizbang-review:v1'
$script:Severities = @('blocking', 'warning')
$script:BotLogin = 'github-actions[bot]'

# ============================================================================
# Findings and the block
# ============================================================================

function New-ReviewFinding {
  <#
  .SYNOPSIS
      One finding. A Path with no Line is about a file; no Path at all is about the pull request.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$Id,
    [Parameter(Mandatory)][string]$Rule,
    [Parameter(Mandatory)][ValidateSet('blocking', 'warning')][string]$Severity,
    [Parameter(Mandatory)][string]$Body,
    [string]$Path,
    [Nullable[int]]$Line
  )
  [pscustomobject]@{
    Id       = $Id
    Rule     = $Rule
    Severity = $Severity
    Body     = $Body
    Path     = if ($Path) { $Path } else { $null }
    Line     = $Line
  }
}

function ConvertTo-ReviewBlock {
  <#
  .SYNOPSIS
      Writes findings as the hidden block. Findings past MaxLength are left out and counted.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Sha,
    [AllowEmptyCollection()][object[]]$Findings = @(),
    [int]$MaxLength = 60000
  )
  $kept = [System.Collections.Generic.List[object]]::new()
  $text = _formatBlock -Source $Source -Sha $Sha -Findings @() -Omitted 0
  for ($i = 0; $i -lt $Findings.Count; $i++) {
    $kept.Add((_toJsonFinding $Findings[$i]))
    $next = _formatBlock -Source $Source -Sha $Sha -Findings $kept -Omitted ($Findings.Count - $kept.Count)
    if ($next.Length -gt $MaxLength) {
      $kept.RemoveAt($kept.Count - 1)
      return _formatBlock -Source $Source -Sha $Sha -Findings $kept -Omitted ($Findings.Count - $kept.Count)
    }
    $text = $next
  }
  return $text
}

function _toJsonFinding($f) {
  $o = [ordered]@{ id = $f.Id; rule = $f.Rule; severity = $f.Severity; body = $f.Body }
  if ($f.Path) { $o.path = $f.Path }
  if ($null -ne $f.Line) { $o.line = [int]$f.Line }
  return $o
}

function _formatBlock([string]$Source, [string]$Sha, $Findings, [int]$Omitted) {
  $o = [ordered]@{ source = $Source; sha = $Sha; findings = @($Findings) }
  if ($Omitted -gt 0) { $o.omitted = $Omitted }
  $json = (ConvertTo-Json -InputObject $o -Depth 5 -Compress).Replace('--', '-\u002d')
  return "$script:BlockStart`n$json`n-->"
}

function ConvertFrom-ReviewBlock {
  <#
  .SYNOPSIS
      Reads every block in a text. Malformed blocks and findings are dropped with a warning.
  .DESCRIPTION
      The text comes from a pull request comment, so it is data: every field is checked, a path must
      be relative and stay inside the repository, and a line needs a path.
  #>
  [CmdletBinding()]
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

  $pattern = '(?s)' + [regex]::Escape($script:BlockStart) + '\s*(.*?)\s*-->'
  foreach ($m in [regex]::Matches($Text, $pattern)) {
    try {
      $data = $m.Groups[1].Value | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    } catch {
      Write-Warning "A review findings block is not valid JSON and was skipped: $($_.Exception.Message)"
      continue
    }
    if ($data -isnot [hashtable] -or $data['sha'] -isnot [string] -or $data['sha'] -notmatch '^[0-9a-f]{7,40}$' -or
        $data['source'] -isnot [string] -or $data['findings'] -isnot [System.Collections.IList]) {
      Write-Warning 'A review findings block is missing its source, commit or findings and was skipped.'
      continue
    }
    $findings = [System.Collections.Generic.List[object]]::new()
    foreach ($raw in $data['findings']) {
      $f = _readFinding $raw
      if ($null -eq $f) {
        Write-Warning 'A malformed finding was skipped.'
        continue
      }
      $findings.Add($f)
    }
    $omitted = if ($data['omitted'] -is [long] -or $data['omitted'] -is [int]) { [int]$data['omitted'] } else { 0 }
    [pscustomobject]@{ Source = $data['source']; Sha = $data['sha']; Findings = $findings.ToArray(); Omitted = $omitted }
  }
}

function _readFinding($raw) {
  if ($raw -isnot [hashtable]) { return $null }
  foreach ($key in 'id', 'rule', 'body') {
    if ($raw[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($raw[$key])) { return $null }
  }
  if ($raw['id'].Length -gt 300 -or $raw['severity'] -notin $script:Severities) { return $null }
  $path = $null
  if ($raw.ContainsKey('path')) {
    $path = $raw['path']
    if ($path -isnot [string] -or -not (Test-RepoRelativePath $path)) { return $null }
  }
  $line = $null
  if ($raw.ContainsKey('line')) {
    if (($raw['line'] -isnot [long] -and $raw['line'] -isnot [int]) -or $raw['line'] -lt 1 -or -not $path) { return $null }
    $line = [int]$raw['line']
  }
  return New-ReviewFinding -Id $raw['id'] -Rule $raw['rule'] -Severity $raw['severity'] -Body $raw['body'] -Path $path -Line $line
}

function Test-RepoRelativePath {
  <#
  .SYNOPSIS
      True for a forward-slash path relative to the repository root that does not leave it.
  #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Path)
  if ([string]::IsNullOrWhiteSpace($Path) -or $Path.StartsWith('/') -or $Path.Contains('\') -or $Path.Contains(':')) {
    return $false
  }
  return -not (@($Path -split '/') -contains '..')
}

# ============================================================================
# The PR quality gate's files
# ============================================================================

function Get-GateFindings {
  <#
  .SYNOPSIS
      Reads the uncovered-line, uncovered-branch and Sonar files the PR quality gate writes.
  .DESCRIPTION
      Each file has one finding per line, as "path:line: text". A Sonar finding about a whole file has
      no line ("path:: rule message"). A file that does not exist means that check found nothing.
  #>
  [CmdletBinding()]
  param(
    [string]$UncoveredLinesFile,
    [string]$UncoveredBranchesFile,
    [string]$SonarFile
  )
  foreach ($entry in _readGateFile $UncoveredLinesFile) {
    New-ReviewFinding -Id "coverage-line:$($entry.Path):$($entry.Line)" -Rule 'coverage.new-line' -Severity blocking `
      -Path $entry.Path -Line $entry.Line `
      -Body "No test runs this line: ``$($entry.Text.Trim())``. The PR gate requires every added library line to be executed by a test."
  }
  foreach ($entry in _readGateFile $UncoveredBranchesFile) {
    New-ReviewFinding -Id "coverage-branch:$($entry.Path):$($entry.Line)" -Rule 'coverage.new-branch' -Severity blocking `
      -Path $entry.Path -Line $entry.Line `
      -Body "A decision on this line has an outcome no test takes: ``$($entry.Text.Trim())``. The PR gate requires every outcome of an added hand-written decision to be tested."
  }
  foreach ($entry in _readGateFile $SonarFile) {
    $rule, $message = $entry.Text.Trim() -split '\s+', 2
    New-ReviewFinding -Id "sonar:${rule}:$($entry.Path):$($entry.Line)" -Rule "sonar.$rule" -Severity blocking `
      -Path $entry.Path -Line $entry.Line -Body "Sonar ``$rule``: $message"
  }
}

function _readGateFile([string]$File) {
  if (-not $File -or -not (Test-Path -LiteralPath $File)) { return }
  foreach ($text in Get-Content -LiteralPath $File) {
    $m = [regex]::Match($text, '^(?<path>[^:]+):(?<line>\d*):\s?(?<rest>.*)$')
    if (-not $m.Success -or -not (Test-RepoRelativePath $m.Groups['path'].Value)) { continue }
    [pscustomobject]@{
      Path = $m.Groups['path'].Value
      Line = if ($m.Groups['line'].Value) { [int]$m.Groups['line'].Value } else { $null }
      Text = $m.Groups['rest'].Value
    }
  }
}

# ============================================================================
# Reading a unified diff
# ============================================================================

function Read-UnifiedDiff {
  <#
  .SYNOPSIS
      Every file in a unified diff, with each new-side line it shows (added or context).
  #>
  [CmdletBinding()]
  param([Parameter(Mandatory)][AllowEmptyString()][string]$DiffText)

  $files = [System.Collections.Generic.List[object]]::new()
  $file = $null
  $inHunk = $false
  $next = 0
  foreach ($line in $DiffText -split "`r?`n") {
    if ($line.StartsWith('diff --git ')) {
      $m = [regex]::Match($line, '^diff --git a/(?<a>.+) b/(?<b>.+)$')
      $file = [pscustomobject]@{
        Path = $m.Groups['b'].Value; OldPath = $m.Groups['a'].Value; IsNew = $false; IsDeleted = $false
        Lines = [System.Collections.Generic.List[object]]::new()
      }
      $files.Add($file)
      $inHunk = $false
      continue
    }
    if ($null -eq $file) { continue }
    if (-not $inHunk) {
      if ($line.StartsWith('new file mode')) { $file.IsNew = $true }
      elseif ($line.StartsWith('deleted file mode')) { $file.IsDeleted = $true }
      elseif ($line.StartsWith('--- a/')) { $file.OldPath = $line.Substring(6) }
      elseif ($line.StartsWith('+++ b/')) { $file.Path = $line.Substring(6) }
    }
    $hunk = [regex]::Match($line, '^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,\d+)? @@')
    if ($hunk.Success) {
      $inHunk = $true
      $next = [int]$hunk.Groups['start'].Value
      continue
    }
    if (-not $inHunk) { continue }
    if ($line.StartsWith('+')) {
      $file.Lines.Add([pscustomobject]@{ Line = $next; Text = $line.Substring(1); Added = $true })
      $next++
    } elseif ($line.StartsWith(' ') -or $line -eq '') {
      $file.Lines.Add([pscustomobject]@{ Line = $next; Text = if ($line) { $line.Substring(1) } else { '' }; Added = $false })
      $next++
    }
    # "-" lines are old-side only; "\ No newline at end of file" is not a line.
  }
  return $files.ToArray()
}

function Get-DiffFiles {
  <# .SYNOPSIS Each file in a diff, and whether the diff adds or deletes it. #>
  [CmdletBinding()]
  param([Parameter(Mandatory)][AllowEmptyString()][string]$DiffText)
  Read-UnifiedDiff -DiffText $DiffText | ForEach-Object {
    [pscustomobject]@{ Path = $_.Path; IsNew = $_.IsNew; IsDeleted = $_.IsDeleted }
  }
}

function Get-AddedLines {
  <# .SYNOPSIS Each added line, with its path and new-side line number. #>
  [CmdletBinding()]
  param([Parameter(Mandatory)][AllowEmptyString()][string]$DiffText)
  foreach ($file in Read-UnifiedDiff -DiffText $DiffText) {
    foreach ($l in $file.Lines) {
      if ($l.Added) { [pscustomobject]@{ Path = $file.Path; Line = $l.Line; Text = $l.Text } }
    }
  }
}

function Get-CommentableLines {
  <#
  .SYNOPSIS
      "path:line" for every new-side line the diff shows: the lines a review comment can be placed on.
  #>
  [CmdletBinding()]
  param([Parameter(Mandatory)][AllowEmptyString()][string]$DiffText)
  $set = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
  foreach ($file in Read-UnifiedDiff -DiffText $DiffText) {
    foreach ($l in $file.Lines) { [void]$set.Add("$($file.Path):$($l.Line)") }
  }
  return , $set
}

# ============================================================================
# Deterministic checks on the diff and the pull request
# ============================================================================

$script:TextExtensions = @('.cs', '.md', '.ps1', '.psm1', '.yml', '.yaml', '.sql', '.json', '.props', '.targets', '.csproj', '.sh', '.txt', '.ts', '.js')

# en-GB word forms the repository writes in en-US (CLAUDE.md, "Language").
$script:EnGbPattern = '(?i)\b(?:' + (@(
    '(?:behaviour|colour|favour|honour)s?'
    '(?:recogni|initiali|seriali|deseriali|organi|normali|optimi|prioriti|utili|finali|customi|minimi|maximi|authori|synchroni|summari|categori|visuali|reali|materiali|speciali|standardi)s(?:e|ed|es|ing|ation|ations|er|ers)'
    'analys(?:e|ed|ing)'
    'cancell(?:ed|ing)'
    'catalogues?'
    'centres?'
    'licences?'
    'artefacts?'
  ) -join '|') + ')\b'

$script:RealTimePatterns = [ordered]@{
  'Task.Delay'           = '\bTask\.Delay\s*\('
  'Thread.Sleep'         = '\bThread\.Sleep\s*\('
  'DateTime.Now'         = '\bDateTime(?:Offset)?\.(?:Utc)?Now\b'
  'Stopwatch'            = '\bStopwatch\b'
  'TimeProvider.System'  = '\bTimeProvider\.System\b'
}

function ConvertTo-EnUsSpelling {
  <# .SYNOPSIS The en-US form of an en-GB word the check matched, keeping its leading capital. #>
  param([Parameter(Mandatory)][string]$Word)
  $us = $Word.ToLowerInvariant()
  $us = $us -replace '^(behavi|col|fav|hon)our', '$1or'
  $us = $us -replace '^(\w+?i)s(e|ed|es|ing|ation|ations|er|ers)$', '$1z$2'
  $us = $us -replace '^analys', 'analyz'
  $us = $us -replace '^cancell', 'cancel'
  $us = $us -replace '^catalogue', 'catalog'
  $us = $us -replace '^centre', 'center'
  $us = $us -replace '^licence', 'license'
  $us = $us -replace '^artefact', 'artifact'
  if ([char]::IsUpper($Word[0])) { $us = [char]::ToUpperInvariant($us[0]) + $us.Substring(1) }
  return $us
}

function ConvertTo-NormalizedSql {
  <#
  .SYNOPSIS
      A migration's SQL with everything that changes nothing executable removed.
  .DESCRIPTION
      The port of Whizbang.Data.Postgres.MigrationSqlNormalizer, the form every driver hashes to decide
      whether a migration changed. tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigrationSqlNormalizerTests.cs
      and .github/scripts/tests/ReviewFindings.Tests.ps1 hold the two to the same cases.

      Removed: -- line comments and /* */ block comments (nested, as PostgreSQL reads them), spaces and tabs
      at line edges and runs of them between tokens (one space), blank lines, and the line ending
      convention. Copied verbatim: '...' strings, E'...' strings with backslash escapes, "..." identifiers
      and dollar-quoted bodies ($$...$$, $tag$...$tag$), so a comment marker inside a literal is text.
  #>
  [CmdletBinding()]
  [OutputType([string])]
  param([Parameter(Mandatory, Position = 0)][AllowEmptyString()][string]$Sql)

  # A hashtable so the helper below can update it in place.
  $state = @{ Out = [System.Text.StringBuilder]::new($Sql.Length); PendingSpace = $false; LineHasContent = $false; PendingNewline = $false }
  $n = $Sql.Length
  $i = 0
  while ($i -lt $n) {
    $c = $Sql[$i]
    $next = if ($i + 1 -lt $n) { $Sql[$i + 1] } else { [char]0 }
    if ($c -eq "`n" -or $c -eq "`r") {
      $i++
      if ($c -eq "`r" -and $next -eq "`n") { $i++ }
      $state.PendingSpace = $false
      if ($state.LineHasContent) { $state.PendingNewline = $true; $state.LineHasContent = $false }
    } elseif ($c -eq ' ' -or $c -eq "`t" -or $c -eq "`f" -or $c -eq "`v") {
      $state.PendingSpace = $true
      $i++
    } elseif ($c -eq '-' -and $next -eq '-') {
      while ($i -lt $n -and $Sql[$i] -ne "`n" -and $Sql[$i] -ne "`r") { $i++ }
    } elseif ($c -eq '/' -and $next -eq '*') {
      $depth = 0
      while ($i -lt $n) {
        $after = if ($i + 1 -lt $n) { $Sql[$i + 1] } else { [char]0 }
        if ($Sql[$i] -eq '/' -and $after -eq '*') { $depth++; $i += 2 }
        elseif ($Sql[$i] -eq '*' -and $after -eq '/') { $depth--; $i += 2; if ($depth -eq 0) { break } }
        else { $i++ }
      }
      $state.PendingSpace = $true
    } elseif ($c -eq "'" -or $c -eq '"') {
      $escapes = $c -eq "'" -and $i -gt 0 -and ($Sql[$i - 1] -ceq 'E' -or $Sql[$i - 1] -ceq 'e') -and
        ($i -lt 2 -or -not (_isSqlIdentifierChar $Sql[$i - 2]))
      $end = _quotedEnd $Sql $i $escapes
      _appendSqlContent $state $Sql $i ($end - $i)
      $i = $end
    } elseif ($c -eq '$' -and ($tag = _dollarTagLength $Sql $i) -gt 0) {
      $close = $Sql.IndexOf($Sql.Substring($i, $tag), $i + $tag, [StringComparison]::Ordinal)
      $end = if ($close -lt 0) { $n } else { $close + $tag }
      _appendSqlContent $state $Sql $i ($end - $i)
      $i = $end
    } else {
      _appendSqlContent $state $Sql $i 1
      $i++
    }
  }
  return $state.Out.ToString()
}

function _appendSqlContent([hashtable]$State, [string]$Sql, [int]$Start, [int]$Length) {
  # Content, preceded by whatever separator is pending: a line ending, or one space within a line.
  if ($State.PendingNewline) { [void]$State.Out.Append("`n"); $State.PendingNewline = $false }
  elseif ($State.PendingSpace -and $State.LineHasContent) { [void]$State.Out.Append(' ') }
  $State.PendingSpace = $false
  $State.LineHasContent = $true
  [void]$State.Out.Append($Sql, $Start, $Length)
}

function _quotedEnd([string]$Sql, [int]$At, [bool]$Escapes) {
  # Where the quoted literal opening at At ends (its doubled quote escapes it); the text's end when unterminated.
  $quote = $Sql[$At]
  $j = $At + 1
  while ($j -lt $Sql.Length) {
    if ($Escapes -and $Sql[$j] -eq '\') { $j += 2 }
    elseif ($Sql[$j] -eq $quote) {
      if ($j + 1 -lt $Sql.Length -and $Sql[$j + 1] -eq $quote) { $j += 2 } else { return $j + 1 }
    } else { $j++ }
  }
  return $Sql.Length
}

function _isSqlIdentifierChar([char]$C) { return [char]::IsLetterOrDigit($C) -or $C -eq '_' -or $C -eq '$' }

function _dollarTagLength([string]$Sql, [int]$At) {
  # The length of the $$ or $tag$ delimiter at At, or 0 when the dollar opens no quote.
  if ($At -gt 0 -and (_isSqlIdentifierChar $Sql[$At - 1])) { return 0 }
  $j = $At + 1
  while ($j -lt $Sql.Length -and $Sql[$j] -ne '$') {
    $ch = $Sql[$j]
    $valid = if ($j -eq $At + 1) { [char]::IsLetter($ch) -or $ch -eq '_' } else { [char]::IsLetterOrDigit($ch) -or $ch -eq '_' }
    if (-not $valid) { return 0 }
    $j++
  }
  if ($j -lt $Sql.Length) { return $j - $At + 1 }
  return 0
}

function _isInertMigrationEdit($File, [scriptblock]$ReadMigrationVersions) {
  # True only when both sides were read and normalize to the same SQL; anything unreadable is flagged.
  if (-not $ReadMigrationVersions) { return $false }
  $versions = & $ReadMigrationVersions $File.OldPath $File.Path
  if ($null -eq $versions) { return $false }
  return (ConvertTo-NormalizedSql ([string]$versions.Old)) -ceq (ConvertTo-NormalizedSql ([string]$versions.New))
}

function Find-DiffFindings {
  <#
  .SYNOPSIS
      The deterministic checks on a pull request's added lines.
  .DESCRIPTION
      Generated code, deleted files and paths matching IgnorePaths (globs, e.g. from
      .github/review-checks-ignore.txt) are skipped, as is any line containing "review-checks:allow".
      Each finding's id is "rule:path:line", so a re-run on the same diff produces the same ids and a
      draft review never gets the same comment twice.

      ReadMigrationVersions, when given, is called with an edited migration's old and new path and returns
      an object with Old and New (the file's text on each side), or $null when either cannot be read. An
      edit whose normalized SQL is unchanged (ConvertTo-NormalizedSql: comments, line-edge whitespace,
      blank lines and line endings only) changes nothing a database executes and is not flagged. Without
      it, or when the text cannot be read, every edit to an existing migration is flagged.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][AllowEmptyString()][string]$DiffText,
    [AllowEmptyCollection()][string[]]$IgnorePaths = @(),
    [scriptblock]$ReadMigrationVersions
  )

  foreach ($file in Read-UnifiedDiff -DiffText $DiffText) {
    $path = $file.Path
    if ($file.IsDeleted -or $path -like '*.g.cs' -or $path -like '*/.whizbang/cache/*') { continue }
    if (@($IgnorePaths | Where-Object { $path -like $_ }).Count -gt 0) { continue }
    $extension = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
    $isText = $script:TextExtensions -contains $extension
    $isTest = $path.StartsWith('tests/') -and $extension -eq '.cs'
    $isLibrary = $path.StartsWith('src/') -and $extension -eq '.cs'
    $added = @($file.Lines | Where-Object { $_.Added -and -not $_.Text.Contains('review-checks:allow') })

    if ($path -match '^src/[^/]+/Migrations/[^/]+\.sql$' -and -not $file.IsNew -and $added.Count -gt 0 -and
      -not (_isInertMigrationEdit $file $ReadMigrationVersions)) {
      New-ReviewFinding -Id "migrations.edited:${path}:$($added[0].Line)" -Rule 'migrations.edited' -Severity blocking `
        -Path $path -Line $added[0].Line `
        -Body 'This migration already exists and is being edited. A database that has run it never runs it again, so the change would reach new databases only. Add a new migration instead.'
    }

    foreach ($l in $added) {
      $where = "${path}:$($l.Line)"
      if ($isText) {
        $words = @([regex]::Matches($l.Text, $script:EnGbPattern) | Where-Object { -not (_isLiteralStatus $l.Text $_) } | ForEach-Object { $_.Value } | Select-Object -Unique)
        if ($words.Count -gt 0) {
          $pairs = ($words | ForEach-Object { """$_"" → ""$(ConvertTo-EnUsSpelling $_)""" }) -join ', '
          New-ReviewFinding -Id "style.en-us:$where" -Rule 'style.en-us' -Severity warning -Path $path -Line $l.Line `
            -Body "Use en-US spelling: $pairs."
        }
        if ($l.Text.Contains([char]0x2014)) {
          New-ReviewFinding -Id "style.em-dash:$where" -Rule 'style.em-dash' -Severity warning -Path $path -Line $l.Line `
            -Body 'House style has no em-dashes. Use a comma, a colon, parentheses or a new sentence.'
        }
      }
      if ($isTest) {
        $hits = @($script:RealTimePatterns.Keys | Where-Object { $l.Text -match $script:RealTimePatterns[$_] })
        if ($hits.Count -gt 0) {
          $names = ($hits | ForEach-Object { "``$_``" }) -join ', '
          New-ReviewFinding -Id "tests.real-time:$where" -Rule 'tests.real-time' -Severity warning -Path $path -Line $l.Line `
            -Body "$names in a test: a test that depends on real time is a race. Wait on the signal for the exact transition instead, or inject ``TimeProvider`` and drive a ``FakeTimeProvider`` (testing skill, ""Determinism"")."
        }
      }
      if ($isLibrary -and $l.Text -match '\bGuid\.NewGuid\s*\(') {
        New-ReviewFinding -Id "code.tracked-guid:$where" -Rule 'code.tracked-guid' -Severity warning -Path $path -Line $l.Line `
          -Body 'Use ``TrackedGuid.NewMedo()`` rather than ``Guid.NewGuid()``: it is time-ordered and records where it came from.'
      }
    }
  }
}

# "cancelled" as a quoted literal or a call is a value someone else defined (a GitHub Actions status,
# its cancelled() function), not prose to correct.
function _isLiteralStatus([string]$Text, [System.Text.RegularExpressions.Match]$Match) {
  if ($Match.Value -notmatch '^(?i)cancell') { return $false }
  $before = if ($Match.Index -gt 0) { $Text[$Match.Index - 1] } else { ' ' }
  $end = $Match.Index + $Match.Length
  $after = if ($end -lt $Text.Length) { $Text[$end] } else { ' ' }
  return ($before -in "'", '"' -and $after -in "'", '"') -or $after -eq '('
}

function Find-PullRequestFindings {
  <#
  .SYNOPSIS
      Checks on the pull request itself: a closing keyword, and the branch it targets.
  .DESCRIPTION
      The closing keyword is required ("Gate - Linked issue", .github/scripts/Test-LinkedIssue.ps1), with the
      same exemptions: a pull request opened by a bot, and the release pull request into main.
  #>
  [CmdletBinding()]
  param(
    [AllowEmptyString()][string]$Body = '',
    [Parameter(Mandatory)][int]$ClosingIssueCount,
    [Parameter(Mandatory)][string]$BaseRef,
    [Parameter(Mandatory)][string]$HeadRef,
    [switch]$AuthorIsBot
  )
  $exempt = $AuthorIsBot -or ($BaseRef -eq 'main' -and $HeadRef -like 'release/*')
  if ($ClosingIssueCount -eq 0 -and -not $exempt) {
    $text = if ($Body -match '(?i)`[^`]*\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#\d+[^`]*`') {
      'The closing keyword is inside backticks, so GitHub ignores it and no issue is linked; the merge requires one. Remove the backticks around it while the pull request is still open; the link cannot be added after merge.'
    } else {
      'No issue is linked to close, and the merge requires one. Open an issue first (CONTRIBUTING.md, "Open an issue first"), add "Fixes #N" to the description, and re-run the "Gate · Linked issue" job.'
    }
    New-ReviewFinding -Id 'pr.closing-keyword' -Rule 'pr.closing-keyword' -Severity blocking -Body $text
  }
  $fromReleaseLine = $HeadRef -match '^(?:release|hotfix)/'
  $allowed = $BaseRef -eq 'develop' -or $BaseRef -like 'release/*' -or ($BaseRef -eq 'main' -and $fromReleaseLine)
  if (-not $allowed) {
    New-ReviewFinding -Id 'pr.base-branch' -Rule 'pr.base-branch' -Severity blocking `
      -Body "This pull request targets ``$BaseRef``. Changes go to ``develop``; only release and hotfix branches merge into ``main``."
  }
}

function Find-PullRequestTypeFindings {
  <#
  .SYNOPSIS
      What a pull request's type requires of it, from its Conventional Commit title.
  .DESCRIPTION
      A fix closes an issue labeled bug and, when it changes the library, changes a test. A library feat, fix
      or perf has a docs-site pull request that is open or merged and links back to this one
      (.github/scripts/Get-DocsPullRequests.ps1 finds it), unless a maintainer added docs:not-needed. A
      library feat or fix adds a CHANGELOG.md entry.
      The title itself, and a breaking change's upgrade note, are the "Gate - Pull request type" check's
      (.github/scripts/Test-PullRequestType.ps1); a title that is not a Conventional Commit asks nothing here.
      Bots and the release pull request into main are exempt, as the gates exempt them.
  #>
  [CmdletBinding()]
  param(
    [AllowEmptyString()][string]$Title = '',
    [AllowEmptyString()][string]$Body = '',
    [AllowEmptyCollection()][string[]]$ChangedPaths = @(),
    [AllowEmptyCollection()][string[]]$ClosingIssueLabels = @(),
    # The pull request's own labels (docs:not-needed).
    [AllowEmptyCollection()][string[]]$Labels = @(),
    # The docs-site pull requests found for this one: Number, State (OPEN, MERGED, CLOSED), ReferencesThis.
    [AllowEmptyCollection()][object[]]$DocsPullRequests = @(),
    [Parameter(Mandatory)][string]$BaseRef,
    [Parameter(Mandatory)][string]$HeadRef,
    [switch]$AuthorIsBot
  )
  if ($AuthorIsBot -or ($BaseRef -eq 'main' -and $HeadRef -like 'release/*')) { return }
  if ($Title -notmatch '^(?<type>feat|fix|perf)(?:\([^)]*\))?!?: ') { return }
  $type = $Matches['type']
  $library = @($ChangedPaths | Where-Object { $_ -like 'src/*' }).Count -gt 0

  if ($type -eq 'fix' -and $ClosingIssueLabels -notcontains 'bug') {
    New-ReviewFinding -Id 'pr.fix-bug-issue' -Rule 'pr.fix-bug-issue' -Severity blocking `
      -Body 'A fix closes the bug report it fixes: none of the issues this closes is labeled `bug`. Link the bug report, or retitle the pull request if it is not a fix.'
  }
  if ($type -eq 'fix' -and $library -and -not ($ChangedPaths | Where-Object { $_ -like 'tests/*' })) {
    New-ReviewFinding -Id 'pr.fix-test' -Rule 'pr.fix-test' -Severity blocking `
      -Body 'A fix to the library adds or changes a test that fails without it (CONTRIBUTING.md, "Testing Standards"). This pull request changes no test.'
  }
  if ($library -and $Labels -notcontains 'docs:not-needed') {
    $live = @($DocsPullRequests | Where-Object { $_.State -in @('OPEN', 'MERGED') })
    if ($live.Count -eq 0) {
      New-ReviewFinding -Id 'pr.docs-link' -Rule 'pr.docs-link' -Severity blocking `
        -Body ('A library change is documented in a docs-site pull request (whizbang-lib/whizbang-lib.github.io), so the change and its documentation are reviewed together. ' +
          'Open it with "Docs for whizbang-lib/whizbang#<this pull request>" in its description: that one link is all it takes, GitHub shows it on this pull request and this check finds it. ' +
          'Then comment /recheck here. If no documentation changes, a maintainer comments /docs-not-needed.')
    } elseif (-not ($live | Where-Object { $_.ReferencesThis })) {
      $numbers = ($live | ForEach-Object { "#$($_.Number)" }) -join ', '
      New-ReviewFinding -Id 'pr.docs-backlink' -Rule 'pr.docs-backlink' -Severity blocking `
        -Body "The docs-site pull request ($numbers) doesn't name this one. Add ""Docs for whizbang-lib/whizbang#<this pull request>"" to its description, then comment /recheck here."
    }
  }
  if ($type -ne 'perf' -and $library -and $ChangedPaths -notcontains 'CHANGELOG.md') {
    New-ReviewFinding -Id 'pr.changelog' -Rule 'pr.changelog' -Severity blocking `
      -Body 'A library feat or fix adds an entry under Unreleased in CHANGELOG.md, saying what changed for a consumer.'
  }
}

function ConvertTo-DcoFindings {
  <#
  .SYNOPSIS
      One finding per commit the DCO check (Test-DcoSignoff.ps1, Get-DcoVerdict) rejected.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)]$Verdict,
    [string]$BaseRef = 'develop'
  )
  if ($Verdict.Pass) { return }
  foreach ($problem in $Verdict.Problems) {
    $sha = ($problem -split ' ', 2)[0]
    New-ReviewFinding -Id "dco:$sha" -Rule 'dco.signoff' -Severity blocking `
      -Body "Commit $problem. Sign it off with your author email and force-push: ``git rebase --signoff origin/$BaseRef`` then ``git push --force-with-lease``."
  }
}

# ============================================================================
# The review-checks comment
# ============================================================================

function Format-ReviewChecksComment {
  <#
  .SYNOPSIS
      The review-checks PR comment: a table for people, then the block for the draft script.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$Sha,
    [Parameter(Mandatory)][string]$RunUrl,
    [AllowEmptyCollection()][object[]]$Findings = @(),
    [int]$PullRequest = 0,
    [int]$MaxRows = 150
  )
  $sb = [System.Text.StringBuilder]::new()
  [void]$sb.AppendLine('<!-- pr-review-checks -->')
  [void]$sb.AppendLine('## PR review checks')
  [void]$sb.AppendLine()
  [void]$sb.AppendLine("Checked commit ``$($Sha.Substring(0, [Math]::Min(9, $Sha.Length)))`` ([run]($RunUrl)). The **Gate · Review checks** check fails while any finding below is open: fix it, or mark a false positive with ``review-checks:allow`` on the line.")
  [void]$sb.AppendLine()
  if ($Findings.Count -eq 0) {
    [void]$sb.AppendLine('No findings.')
  } else {
    [void]$sb.AppendLine('| Severity | Where | Check | Finding |')
    [void]$sb.AppendLine('|---|---|---|---|')
    foreach ($f in @($Findings | Select-Object -First $MaxRows)) {
      $where = if ($f.Path -and $null -ne $f.Line) { "``$($f.Path):$($f.Line)``" } elseif ($f.Path) { "``$($f.Path)``" } else { 'pull request' }
      [void]$sb.AppendLine("| $($f.Severity) | $where | $($f.Rule) | $(_tableCell $f.Body) |")
    }
    if ($Findings.Count -gt $MaxRows) {
      [void]$sb.AppendLine()
      [void]$sb.AppendLine("And $($Findings.Count - $MaxRows) more, listed in the block below.")
    }
  }
  [void]$sb.AppendLine()
  $draft = if ($PullRequest -gt 0) { "pwsh scripts/Invoke-PrReviewDraft.ps1 -PullRequest $PullRequest" } else { 'pwsh scripts/Invoke-PrReviewDraft.ps1 -PullRequest <number>' }
  [void]$sb.AppendLine("A maintainer can turn these into a draft review: ``$draft``")
  [void]$sb.AppendLine()
  $room = [Math]::Max(2000, 64000 - $sb.Length)
  [void]$sb.Append((ConvertTo-ReviewBlock -Source 'review-checks' -Sha $Sha -Findings $Findings -MaxLength $room))
  return $sb.ToString()
}

function _tableCell([string]$Text) {
  $t = ($Text -replace '\r?\n', ' ').Replace('|', '\|')
  if ($t.Length -gt 300) { $t = $t.Substring(0, 297) + '...' }
  return $t
}

# ============================================================================
# The draft review plan
# ============================================================================

function Get-ReviewDraftPlan {
  <#
  .SYNOPSIS
      Which findings go on a line, which go in the review summary, and which are already drafted.
  .DESCRIPTION
      Reads findings only from comments posted by the workflow's bot account: anyone can post text in
      the same format. Every run keeps its comment, so only each source's latest block counts; when that
      block checked a commit other than the pull request's head, the source is stale and is reported, not
      drafted. A finding whose path and line are in the diff becomes a line comment. Any other finding
      becomes a file-level thread, which, like a line comment, must be resolved before merge: on its own
      file when that file is in the diff (naming the line), otherwise on the first file in the diff, saying
      it is pinned there. Only when the diff could not be read does a finding go in the summary. Every body is plain text with a hidden marker naming its
      finding, so a re-run drafts only what is new.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Comments,
    [Parameter(Mandatory)][string]$HeadSha,
    [Parameter(Mandatory)][AllowEmptyString()][string]$DiffText,
    [AllowEmptyCollection()][string[]]$DraftedIds = @()
  )
  $commentable = Get-CommentableLines -DiffText $DiffText
  $files = @(Get-DiffFiles -DiffText $DiffText | ForEach-Object { $_.Path })
  $drafted = [System.Collections.Generic.HashSet[string]]::new([string[]]@($DraftedIds), [StringComparer]::Ordinal)
  $threads = [System.Collections.Generic.List[object]]::new()
  $summary = [System.Collections.Generic.List[object]]::new()
  $stale = [System.Collections.Generic.List[object]]::new()
  $already = 0

  # Comments arrive oldest first, so the last block seen for a source is its latest run.
  $latest = [ordered]@{}
  foreach ($comment in $Comments) {
    if ($comment.Login -ne $script:BotLogin -or $comment.Type -ne 'Bot') { continue }
    foreach ($block in ConvertFrom-ReviewBlock -Text $comment.Body) { $latest[$block.Source] = $block }
  }

  foreach ($block in $latest.Values) {
    if ($block.Sha -ne $HeadSha) {
      $stale.Add([pscustomobject]@{ Source = $block.Source; Sha = $block.Sha })
      continue
    }
    foreach ($f in $block.Findings) {
      $id = _markerId $f.Id
      if (-not $drafted.Add($id)) { $already++; continue }
      $text = Protect-ReviewText $f.Body
      if ($f.Path -and $null -ne $f.Line -and $commentable.Contains("$($f.Path):$($f.Line)")) {
        $threads.Add([pscustomobject]@{ Id = $id; SubjectType = 'LINE'; Path = $f.Path; Line = $f.Line; Body = "$text`n`n<!-- wr:$id -->" })
      } elseif ($files.Count -gt 0) {
        # A file-level thread is resolvable, where the review summary is not.
        if ($f.Path -and $files -contains $f.Path) {
          $path = $f.Path
          $prefix = if ($null -ne $f.Line) { "Line $($f.Line): " } else { '' }
        } else {
          $path = $files[0]
          $about = if ($f.Path -and $null -ne $f.Line) { "``$($f.Path):$($f.Line)``" } elseif ($f.Path) { "``$($f.Path)``" } else { 'the pull request' }
          $prefix = "About $about, pinned to this file so it can be resolved: "
        }
        $threads.Add([pscustomobject]@{ Id = $id; SubjectType = 'FILE'; Path = $path; Line = $null; Body = "$prefix$text`n`n<!-- wr:$id -->" })
      } else {
        $where = if ($f.Path -and $null -ne $f.Line) { "``$($f.Path):$($f.Line)``: " } elseif ($f.Path) { "``$($f.Path)``: " } else { '' }
        $summary.Add([pscustomobject]@{ Id = $id; Body = "- $where$text <!-- wr:$id -->" })
      }
    }
  }
  [pscustomobject]@{ Threads = $threads.ToArray(); Summary = $summary.ToArray(); Stale = $stale.ToArray(); AlreadyDrafted = $already }
}

function Get-DraftedIds {
  <# .SYNOPSIS The finding ids already marked in a draft review's body and comments. #>
  param([AllowEmptyCollection()][string[]]$Texts = @())
  foreach ($t in $Texts) {
    if (-not $t) { continue }
    foreach ($m in [regex]::Matches($t, '<!-- wr:(?<id>[^\s>]+) -->')) { $m.Groups['id'].Value }
  }
}

function Protect-ReviewText {
  <#
  .SYNOPSIS
      Makes CI text safe to post as the maintainer: no HTML comments, no @-mentions, bounded length.
  #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
  $t = $Text.Replace('<!--', '').Replace('-->', '')
  $t = [regex]::Replace($t, '@(?=[A-Za-z0-9])', "@$([char]0x200B)")
  if ($t.Length -gt 3000) { $t = $t.Substring(0, 2997) + '...' }
  return $t
}

function _markerId([string]$Id) {
  return [regex]::Replace($Id, '[^A-Za-z0-9:._/\-]', '_')
}

Export-ModuleMember -Function New-ReviewFinding, Find-PullRequestTypeFindings, ConvertTo-ReviewBlock, ConvertFrom-ReviewBlock, Test-RepoRelativePath,
  Get-GateFindings, Read-UnifiedDiff, Get-DiffFiles, Get-AddedLines, Get-CommentableLines, ConvertTo-EnUsSpelling,
  Find-DiffFindings, Find-PullRequestFindings, ConvertTo-DcoFindings, Format-ReviewChecksComment,
  Get-ReviewDraftPlan, Get-DraftedIds, Protect-ReviewText, ConvertTo-NormalizedSql
