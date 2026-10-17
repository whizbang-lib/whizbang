#!/usr/bin/env pwsh
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
        Path = $m.Groups['b'].Value; IsNew = $false; IsDeleted = $false
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

function Find-DiffFindings {
  <#
  .SYNOPSIS
      The deterministic checks on a pull request's added lines.
  .DESCRIPTION
      Generated code, deleted files and paths matching IgnorePaths (globs, e.g. from
      .github/review-checks-ignore.txt) are skipped, as is any line containing "review-checks:allow".
      Each finding's id is "rule:path:line", so a re-run on the same diff produces the same ids and a
      draft review never gets the same comment twice.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][AllowEmptyString()][string]$DiffText,
    [AllowEmptyCollection()][string[]]$IgnorePaths = @()
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

    if ($path -match '^src/[^/]+/Migrations/[^/]+\.sql$' -and -not $file.IsNew -and $added.Count -gt 0) {
      New-ReviewFinding -Id "migrations.edited:${path}:$($added[0].Line)" -Rule 'migrations.edited' -Severity blocking `
        -Path $path -Line $added[0].Line `
        -Body 'This migration already exists and is being edited. A database that has run it never runs it again, so the change would reach new databases only. Add a new migration instead.'
    }

    foreach ($l in $added) {
      $where = "${path}:$($l.Line)"
      if ($isText) {
        $words = @([regex]::Matches($l.Text, $script:EnGbPattern) | Where-Object { -not (_isLiteralStatus $l.Text $_) } | ForEach-Object Value | Select-Object -Unique)
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
  #>
  [CmdletBinding()]
  param(
    [AllowEmptyString()][string]$Body = '',
    [Parameter(Mandatory)][int]$ClosingIssueCount,
    [Parameter(Mandatory)][string]$BaseRef,
    [Parameter(Mandatory)][string]$HeadRef
  )
  if ($ClosingIssueCount -eq 0) {
    $text = if ($Body -match '(?i)`[^`]*\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#\d+[^`]*`') {
      'The closing keyword is inside backticks, so GitHub ignores it and no issue is linked. Remove the backticks around it while the pull request is still open; the link cannot be added after merge.'
    } else {
      'No issue is linked to close. If this resolves an issue, add "Fixes #N" to the description now: the link cannot be added after merge.'
    }
    New-ReviewFinding -Id 'pr.closing-keyword' -Rule 'pr.closing-keyword' -Severity warning -Body $text
  }
  $fromReleaseLine = $HeadRef -match '^(?:release|hotfix)/'
  $allowed = $BaseRef -eq 'develop' -or $BaseRef -like 'release/*' -or ($BaseRef -eq 'main' -and $fromReleaseLine)
  if (-not $allowed) {
    New-ReviewFinding -Id 'pr.base-branch' -Rule 'pr.base-branch' -Severity blocking `
      -Body "This pull request targets ``$BaseRef``. Changes go to ``develop``; only release and hotfix branches merge into ``main``."
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
  [void]$sb.AppendLine("Checked commit ``$($Sha.Substring(0, [Math]::Min(9, $Sha.Length)))`` ([run]($RunUrl)). These report only; the gates decide what merges.")
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

Export-ModuleMember -Function New-ReviewFinding, ConvertTo-ReviewBlock, ConvertFrom-ReviewBlock, Test-RepoRelativePath,
  Get-GateFindings, Read-UnifiedDiff, Get-DiffFiles, Get-AddedLines, Get-CommentableLines, ConvertTo-EnUsSpelling,
  Find-DiffFindings, Find-PullRequestFindings, ConvertTo-DcoFindings, Format-ReviewChecksComment
