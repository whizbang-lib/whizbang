#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Lints Whizbang SQL migrations for UNQUALIFIED service-schema table references inside
  PL/pgSQL function bodies — the multi-schema search_path bug class.

.DESCRIPTION
  Whizbang deployments are multi-schema (each service keeps its wh_ tables in its own schema).
  A bare `wh_` table reference inside a CREATE FUNCTION $$...$$ body is NOT rewritten by either
  migration runner, so it resolves against the connection's search_path at execution time and
  silently reads `public` (empty) on a service-schema connection. Every table ref inside a
  function body must be `__SCHEMA__.`-qualified. The rules are numbered in
  src/Whizbang.Data.Postgres/Migrations/README.md; this script enforces 3, 4, 12 and 13.
  (The previous reference here was to a "Writing SQL migrations" contributor page on the docs site,
  which does not exist. A dangling pointer to a rule list invites inventing the rule, so it names the
  file that actually carries them.)

  This lint lexes each migration (tracking strings, line/block comments, and dollar-quoted bodies
  so it doesn't false-positive on those) and reports bare `wh_` refs after a table-introducing
  keyword (FROM/JOIN/UPDATE/INTO/DELETE FROM/INSERT INTO) that appear INSIDE a function body.

  Genuinely-public objects are allow-listed (they must stay bare). A baseline file records the
  existing known debt so CI fails only on NEW violations; fixing a baselined ref and removing it
  from the baseline is enforced (the baseline ratchets down and can never silently grow back).

.PARAMETER MigrationsPath
  Directory of .sql migrations. Defaults to src/Whizbang.Data.Postgres/Migrations.

.PARAMETER BaselinePath
  The accepted-known-debt file. Defaults to scripts/migration-sql-lint-baseline.txt.

.PARAMETER UpdateBaseline
  Regenerate the baseline from the current violations (run after an intentional, reviewed change).

.EXAMPLE
  pwsh scripts/Lint-MigrationSql.ps1                 # check (CI): exit 1 on any NEW violation
  pwsh scripts/Lint-MigrationSql.ps1 -UpdateBaseline # accept current state as the baseline
#>
[CmdletBinding()]
param(
  [string]$MigrationsPath = (Join-Path $PSScriptRoot '..' 'src' 'Whizbang.Data.Postgres' 'Migrations'),
  [string]$BaselinePath   = (Join-Path $PSScriptRoot 'migration-sql-lint-baseline.txt'),
  [string]$DocsBaselinePath = (Join-Path $PSScriptRoot 'migration-sql-docs-baseline.txt'),
  [switch]$UpdateBaseline,
  [switch]$Fix
)

# Repository root, for resolving the paths a <tests> tag names.
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$ErrorActionPreference = 'Stop'

# EMPTY, and it should stay that way. Every framework table belongs to the service schema.
#
# This list once held wh_settings, wh_log, wh_dead_letters and wh_dead_letter_summary, described as
# objects that "genuinely live in public". They did — but only because migrations 028/050/053 omitted
# the __SCHEMA__ prefix that migration 000 had already established, so they resolved through
# search_path into public. The rule was written afterwards and codified the omission as intent.
# Sharing them is not benign: wh_settings.setting_key is the primary key, so co-located services
# cannot hold different values for debug_mode or any retention knob.
#
# Migration 105 carries the state across; qualification is now unconditional. Before adding anything
# here, be sure the object is genuinely shared BY DESIGN rather than by an omitted prefix.
$PublicAllowList = @()

# A table reference is introduced by one of these keywords.
$RefRegex = [regex]::new(
  '(?is)\b(?:FROM|JOIN|UPDATE|INTO|DELETE\s+FROM|INSERT\s+INTO)\s+(wh_[a-z0-9_]+)')

# ---------------------------------------------------------------------------------------------
# Lexer: produce a "masked" copy of the file where only characters that are CODE inside a
# dollar-quoted function body survive; everything else (top-level DDL, strings, comments, the
# dollar delimiters themselves) becomes a space. Newlines are preserved so line numbers are exact.
# ---------------------------------------------------------------------------------------------
function Get-BodyCodeMask([string]$text) {
  $n = $text.Length
  $mask = [char[]]::new($n)
  for ($k = 0; $k -lt $n; $k++) { $mask[$k] = if ($text[$k] -eq "`n") { "`n" } else { ' ' } }

  $i = 0
  $state = 'top'          # top | body | body_string | body_line_comment | body_block_comment | top_line_comment | top_block_comment | top_string
  $tag = $null            # active dollar tag, e.g. '$$' or '$migrate$'

  function Read-DollarTag([string]$s, [int]$pos) {
    # At s[pos] == '$'. Return the full tag "$...$" if a valid dollar-quote tag starts here, else $null.
    if ($s[$pos] -ne '$') { return $null }
    $j = $pos + 1
    while ($j -lt $s.Length -and ($s[$j] -match '[A-Za-z0-9_]')) { $j++ }
    if ($j -lt $s.Length -and $s[$j] -eq '$') { return $s.Substring($pos, $j - $pos + 1) }
    return $null
  }

  while ($i -lt $n) {
    $c = $text[$i]
    switch ($state) {
      'top' {
        if ($c -eq '-' -and $i + 1 -lt $n -and $text[$i + 1] -eq '-') { $state = 'top_line_comment'; $i += 2; continue }
        if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i + 1] -eq '*') { $state = 'top_block_comment'; $i += 2; continue }
        if ($c -eq "'") { $state = 'top_string'; $i++; continue }
        if ($c -eq '$') { $t = Read-DollarTag $text $i; if ($t) { $tag = $t; $state = 'body'; $i += $t.Length; continue } }
        $i++
      }
      'top_line_comment'  { if ($c -eq "`n") { $state = 'top' }; $i++ }
      'top_block_comment' { if ($c -eq '*' -and $i + 1 -lt $n -and $text[$i + 1] -eq '/') { $state = 'top'; $i += 2; continue }; $i++ }
      'top_string'        { if ($c -eq "'") { $state = 'top' }; $i++ }
      'body' {
        # Closing dollar tag?
        if ($c -eq '$') {
          $t = Read-DollarTag $text $i
          if ($t -eq $tag) { $tag = $null; $state = 'top'; $i += $t.Length; continue }
        }
        if ($c -eq '-' -and $i + 1 -lt $n -and $text[$i + 1] -eq '-') { $state = 'body_line_comment'; $i += 2; continue }
        if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i + 1] -eq '*') { $state = 'body_block_comment'; $i += 2; continue }
        if ($c -eq "'") { $state = 'body_string'; $i++; continue }
        # Genuine body code — keep it for matching.
        $mask[$i] = $c
        $i++
      }
      'body_line_comment'  { if ($c -eq "`n") { $state = 'body' }; $i++ }
      'body_block_comment' { if ($c -eq '*' -and $i + 1 -lt $n -and $text[$i + 1] -eq '/') { $state = 'body'; $i += 2; continue }; $i++ }
      'body_string'        { if ($c -eq "'") { $state = 'body' }; $i++ }
    }
  }
  return (-join $mask)
}

function Get-Violations {
  $results = [System.Collections.Generic.List[object]]::new()
  $files = Get-ChildItem -Path $MigrationsPath -Filter '*.sql' | Sort-Object Name
  foreach ($f in $files) {
    $text = Get-Content -Path $f.FullName -Raw
    $masked = Get-BodyCodeMask $text
    foreach ($m in $RefRegex.Matches($masked)) {
      $ref = $m.Groups[1].Value
      if ($PublicAllowList -contains $ref) { continue }
      $idx = $m.Groups[1].Index   # index of the ref in masked == same index in the original text
      $line = ($masked.Substring(0, $idx) -split "`n").Count
      $results.Add([pscustomobject]@{
          File     = $f.Name
          FullName = $f.FullName
          Line     = $line
          Ref      = $ref
          Index    = $idx
          Key      = "$($f.Name)::$ref"
        })
    }
  }
  return $results
}

function Remove-SqlCommentsAndStrings([string]$text) {
  # Length-preserving blanking of -- line comments, /* */ block comments and '...' literals,
  # so line/column positions still line up with the original text. Dollar-quoted bodies are
  # left intact: their contents are real code.
  $n = $text.Length
  $out = [char[]]::new($n)
  $state = 'code'
  $i = 0
  while ($i -lt $n) {
    $c = $text[$i]
    switch ($state) {
      'code' {
        if ($c -eq '-' -and $i + 1 -lt $n -and $text[$i + 1] -eq '-') { $state = 'line'; $out[$i] = ' '; $i++; continue }
        if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i + 1] -eq '*') { $state = 'block'; $out[$i] = ' '; $i++; continue }
        if ($c -eq "'") { $state = 'str'; $out[$i] = ' '; $i++; continue }
        $out[$i] = $c; $i++
      }
      'line'  { if ($c -eq "`n") { $state = 'code'; $out[$i] = $c } else { $out[$i] = ' ' }; $i++ }
      'block' {
        if ($c -eq '*' -and $i + 1 -lt $n -and $text[$i + 1] -eq '/') { $out[$i] = ' '; $out[$i + 1] = ' '; $state = 'code'; $i += 2; continue }
        $out[$i] = ($c -eq "`n") ? $c : ' '; $i++
      }
      'str'   { if ($c -eq "'") { $state = 'code' }; $out[$i] = ($c -eq "`n") ? $c : ' '; $i++ }
    }
  }
  return (-join $out)
}

function Get-DropColumnViolations {
  <#
    Rule 4 — a DROP COLUMN must acknowledge that it does NOT reclaim the space.

    Postgres DROP COLUMN is a catalog operation: the attribute is flagged dropped in
    pg_attribute and every EXISTING row keeps the column's bytes in the heap, forever.
    Autovacuum never returns them; only a table rewrite (pg_repack / VACUUM FULL / CLUSTER)
    does. On a large, hot table that silently multiplies its on-disk footprint and its
    buffer-cache cost, and nothing in the migration tells the operator a rewrite is owed.

    So each DROP COLUMN needs a RECLAIM: note in a comment on the same line or within the
    preceding few lines, saying what the operator should do (or why it does not matter here,
    e.g. a table that is small or empty at this point in the migration order).

    Deliberately NOT baselined: the existing baseline models debt that should ratchet to
    zero, whereas a DROP COLUMN is a legitimate operation that simply carries an obligation.
    An inline note travels with the migration and is visible in review; a baseline entry
    would be neither.
  #>
  $results = [System.Collections.Generic.List[object]]::new()
  $files = Get-ChildItem -Path $MigrationsPath -Filter '*.sql' | Sort-Object Name
  foreach ($f in $files) {
    $text = Get-Content -Path $f.FullName -Raw
    if (-not $text) { continue }
    # NOTE: deliberately NOT Get-BodyCodeMask — that keeps only function-body code and blanks
    # everything else, and DROP COLUMN is top-level DDL, so using it here silently matched
    # nothing. Blank comments and quoted strings only, so a DROP COLUMN written in prose
    # doesn't trip the rule while real DDL (top-level or inside a body) still does.
    $masked = Remove-SqlCommentsAndStrings $text
    $lines = $text -split "`n"
    $maskedLines = $masked -split "`n"
    for ($i = 0; $i -lt $maskedLines.Count; $i++) {
      if ($maskedLines[$i] -notmatch '(?i)\bDROP\s+COLUMN\b') { continue }
      # Walk up from this line to the end of the PREVIOUS statement, so the search covers the
      # whole current statement plus the comment block introducing it. A fixed line window is
      # not good enough — it silently fails a note that happens to be longer than the window,
      # which is the failure mode this rule exists to prevent. The ';' test runs against the
      # masked text so a semicolon inside a comment can't end the walk early.
      $j = $i - 1
      $block = [System.Collections.Generic.List[string]]::new()
      $block.Add($lines[$i])
      while ($j -ge 0) {
        $block.Add($lines[$j])
        if ($maskedLines[$j] -match ';') { break }
        $j--
      }
      if (($block -join "`n") -match '(?i)RECLAIM:') { continue }
      $results.Add([pscustomobject]@{
          File = $f.Name
          Line = $i + 1
          Text = $lines[$i].Trim()
        })
    }
  }
  return $results
}

function Get-FunctionAnnotationViolations {
  <#
    Rule 13 — SQL is code, so a function a migration defines carries the same <docs> and <tests>
    links every C# type and test file in this repository carries.

    The gap this closes, measured: 271 test files carry <docs> tags and the C# types carry both,
    while 0 of 159 migrations carried either — and the data layer is where the load-bearing
    behavior actually lives. 110 migrations already use COMMENT ON FUNCTION across 128 functions,
    so the habit of annotating existed; only the standard was missing.

    Ownership is per FUNCTION, not per migration. A function persists across many migrations, so
    the annotation belongs in whichever migration most recently defines it, and requiring it at
    every definition site is what makes that true without anyone tracking it: the newest migration
    that redefines a function has to carry the links, and the older sites keep the links that were
    accurate when they were written.

    A <tests> tag is VALIDATED, not merely present. It names <path>:<Method>, and both the file and
    the method have to exist. A link to a test that does not exercise the function is worse than an
    admitted gap because it reads as coverage — the same trap as a <tests> tag on a catch no test
    can reach. This checks the weaker property (the target exists) because that is what a script
    can know; whether the test truly reaches the function stays a review question.

    Baselined, because 263 existing definitions predate the rule and nothing is served by
    backfilling them in one change. The baseline only shrinks, so every function touched from here
    on carries its links.
  #>
  $results = [System.Collections.Generic.List[object]]::new()
  $files = Get-ChildItem -Path $MigrationsPath -Filter '*.sql' | Sort-Object Name
  $createRegex = [regex]::new(
    '(?is)CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:__SCHEMA__\.)?([A-Za-z0-9_]+)\s*\(')
  foreach ($f in $files) {
    $text = Get-Content -Path $f.FullName -Raw
    if (-not $text) { continue }
    # Detect against the comment-and-string-blanked copy so a CREATE FUNCTION written in prose does
    # not count as a definition, but read the preceding block from the ORIGINAL text, because the
    # tags live in comments and the mask blanks exactly those.
    $masked = Remove-SqlCommentsAndStrings $text
    $lines = $text -split "`n"
    $maskedLines = $masked -split "`n"
    foreach ($m in $createRegex.Matches($masked)) {
      $fn = $m.Groups[1].Value
      $line = ($masked.Substring(0, $m.Index) -split "`n").Count
      $i = $line - 1
      # Walk up to the end of the previous statement, the same way rule 4 does: a fixed line window
      # silently fails an annotation block longer than the window, which is the failure this rule
      # exists to prevent. The ';' test runs against the masked text so a semicolon inside a comment
      # cannot end the walk early.
      $j = $i - 1
      $block = [System.Collections.Generic.List[string]]::new()
      while ($j -ge 0) {
        $block.Add($lines[$j])
        if ($maskedLines[$j] -match ';') { break }
        $j--
      }
      $blockText = ($block -join "`n")
      $missing = [System.Collections.Generic.List[string]]::new()
      if ($blockText -notmatch '<docs>\s*\S') { $missing.Add('<docs>') }
      $testTags = [regex]::Matches($blockText, '<tests>\s*([^<]+?)\s*</tests>')
      if ($testTags.Count -eq 0) { $missing.Add('<tests>') }
      $badLinks = [System.Collections.Generic.List[string]]::new()
      foreach ($t in $testTags) {
        $spec = $t.Groups[1].Value.Trim()
        $path, $method = $spec -split ':', 2
        $full = Join-Path $RepoRoot $path
        if (-not (Test-Path $full)) {
          $badLinks.Add("no such file: $path")
          continue
        }
        if ($method) {
          $content = Get-Content -Path $full -Raw
          if ($content -notmatch [regex]::Escape($method)) {
            $badLinks.Add("$path has no $method")
          }
        }
      }
      if ($missing.Count -gt 0 -or $badLinks.Count -gt 0) {
        $results.Add([pscustomobject]@{
            File    = $f.Name
            Line    = $line
            Fn      = $fn
            Missing = ($missing -join ' ')
            BadLink = ($badLinks -join '; ')
            Key     = "$($f.Name)::$fn"
          })
      }
    }
  }
  return $results
}

# ---------------------------------------------------------------------------------------------
$violations = Get-Violations

if ($Fix) {
  # Rewrite each flagged bare ref to __SCHEMA__.<ref> in place. Process each file's matches
  # right-to-left so earlier indices stay valid as we insert. The masked-index equals the index in
  # the original text (masking is 1:1, length-preserving), so inserting "__SCHEMA__." at Index is exact.
  $fixedCount = 0
  foreach ($grp in ($violations | Group-Object FullName)) {
    $text = Get-Content -Path $grp.Name -Raw
    foreach ($v in ($grp.Group | Sort-Object Index -Descending)) {
      $text = $text.Insert($v.Index, '__SCHEMA__.')
      $fixedCount++
    }
    Set-Content -Path $grp.Name -Value $text -Encoding utf8 -NoNewline
  }
  Write-Host "Qualified $fixedCount bare ref(s) across $((($violations | Group-Object FullName).Count)) file(s)."
  Write-Host "Next: rebuild (clean .whizbang/cache to defeat the incremental-generator flake), then"
  Write-Host "      pwsh scripts/Lint-MigrationSql.ps1 -UpdateBaseline   (baseline should drop to 0)."
  exit 0
}

$currentKeys = $violations | Select-Object -ExpandProperty Key -Unique | Sort-Object

if ($UpdateBaseline) {
  $header = @(
    '# Whizbang migration SQL lint baseline — known unqualified wh_ refs inside function bodies.',
    '# Each line is <migration file>::<table>. Generated by Lint-MigrationSql.ps1 -UpdateBaseline.',
    '# GOAL: this list only shrinks. Fix a ref (add __SCHEMA__.) then remove its line here.'
  )
  Set-Content -Path $BaselinePath -Value ($header + $currentKeys) -Encoding utf8
  Write-Host "Baseline written: $BaselinePath ($($currentKeys.Count) known refs across function bodies)."

  $docsKeys = Get-FunctionAnnotationViolations | Where-Object { $_.Missing } |
    Select-Object -ExpandProperty Key -Unique | Sort-Object
  $docsHeader = @(
    '# Whizbang migration SQL lint baseline — functions with no <docs>/<tests> links (rule 13).',
    '# Each line is <migration file>::<function>. Generated by Lint-MigrationSql.ps1 -UpdateBaseline.',
    '# GOAL: this list only shrinks. A function is OWNED by whichever migration most recently',
    '# defines it, so add the links at that definition site, then remove its line here.'
  )
  Set-Content -Path $DocsBaselinePath -Value ($docsHeader + $docsKeys) -Encoding utf8
  Write-Host "Baseline written: $DocsBaselinePath ($($docsKeys.Count) functions without docs/tests links)."
  exit 0
}

$baseline = @()
if (Test-Path $BaselinePath) {
  $baseline = Get-Content $BaselinePath | Where-Object { $_ -and -not $_.StartsWith('#') }
}

$new   = $currentKeys | Where-Object { $baseline -notcontains $_ }
$fixed = $baseline    | Where-Object { $currentKeys -notcontains $_ }

$exit = 0
if ($new) {
  $exit = 1
  Write-Host ''
  Write-Host 'NEW unqualified service-schema refs inside function bodies (rule 3 — these fail CI):' -ForegroundColor Red
  foreach ($k in $new) {
    $violations | Where-Object Key -eq $k | ForEach-Object {
      Write-Host ("  {0}:{1}  bare `"{2}`"  ->  __SCHEMA__.{2}" -f $_.File, $_.Line, $_.Ref)
    }
  }
  Write-Host ''
  Write-Host 'Fix: qualify the table with __SCHEMA__. inside the function body. Every framework table'
  Write-Host 'belongs to the service schema — there are no shared public ones. See rule 3.'
}
if ($fixed) {
  $exit = 1
  Write-Host ''
  Write-Host 'Baseline entries that are now FIXED — remove these lines from the baseline (ratchet down):' -ForegroundColor Yellow
  $fixed | ForEach-Object { Write-Host "  $_" }
  Write-Host ''
  Write-Host 'Run:  pwsh scripts/Lint-MigrationSql.ps1 -UpdateBaseline'
}
$dropColumn = Get-DropColumnViolations
if ($dropColumn) {
  $exit = 1
  Write-Host ''
  Write-Host 'DROP COLUMN without a RECLAIM: note (rule 4 — these fail CI):' -ForegroundColor Red
  foreach ($v in $dropColumn) {
    Write-Host ("  {0}:{1}  {2}" -f $v.File, $v.Line, $v.Text)
  }
  Write-Host ''
  Write-Host 'Postgres DROP COLUMN does NOT reclaim space: the attribute is flagged dropped in'
  Write-Host 'pg_attribute and every existing row keeps its bytes in the heap permanently.'
  Write-Host 'Autovacuum never returns them — only a table rewrite (pg_repack / VACUUM FULL /'
  Write-Host 'CLUSTER) does. On a large hot table that silently multiplies both the on-disk'
  Write-Host 'footprint and the buffer-cache cost of every index heap-fetch.'
  Write-Host ''
  Write-Host 'Add a comment on or just above the statement, e.g.:'
  Write-Host '  -- RECLAIM: the dropped bytes persist per existing row; operators should run'
  Write-Host '  --          pg_repack on <table> after this migration. See <runbook>.'
  Write-Host 'or, when it genuinely does not matter:'
  Write-Host '  -- RECLAIM: not required — <table> is created empty earlier in this migration.'
}

# Rule 12 — shared literals come from Migrations/constants.txt. A migration numbered 148 or later must not
# write one of the values raw (a copied body drifting from the one definition), and must not write a token
# nothing defines (a typo reaches the database as an unknown identifier).
$constantsPath = Join-Path $MigrationsPath 'constants.txt'
$constants = [ordered]@{}
if (Test-Path $constantsPath) {
  foreach ($line in Get-Content $constantsPath) {
    $t = $line.Trim()
    if (-not $t -or $t.StartsWith('#')) { continue }
    $eq = $t.IndexOf('=')
    if ($eq -lt 1) { continue }
    $constants[$t.Substring(0, $eq).Trim()] = $t.Substring($eq + 1).Trim()
  }
}
$ruleTwelve = [System.Collections.Generic.List[string]]::new()
foreach ($f in Get-ChildItem -Path $MigrationsPath -Filter '*.sql' | Sort-Object Name) {
  if ($f.Name -notmatch '^(\d{3})_' -or [int]$Matches[1] -lt 148) { continue }
  $lineNo = 0
  foreach ($line in Get-Content -Path $f.FullName) {
    $lineNo++
    if ($line.TrimStart().StartsWith('--')) { continue }
    foreach ($entry in $constants.GetEnumerator()) {
      if ($line.Contains($entry.Value)) {
        $ruleTwelve.Add(("  {0}:{1}  raw {2}  ->  write {3}" -f $f.Name, $lineNo, $entry.Value, $entry.Key))
      }
    }
    foreach ($m in [regex]::Matches($line, '__[A-Z][A-Z0-9_]*__')) {
      $tok = $m.Value
      if ($tok -eq '__SCHEMA__' -or $constants.Contains($tok)) { continue }
      $ruleTwelve.Add(("  {0}:{1}  unknown token {2}  ->  define it in constants.txt or fix the spelling" -f $f.Name, $lineNo, $tok))
    }
  }
}
if ($ruleTwelve.Count -gt 0) {
  $exit = 1
  Write-Host ''
  Write-Host 'Shared literals written raw, or tokens nothing defines (rule 12 — these fail CI):' -ForegroundColor Red
  $ruleTwelve | ForEach-Object { Write-Host $_ }
  Write-Host ''
  Write-Host 'The literals the migrations share are defined once in Migrations/constants.txt and substituted'
  Write-Host 'at apply time on the same path as __SCHEMA__. Write the token, never the value.'
}
# Rule 13 — a function a migration defines carries <docs> and <tests> links, and a <tests> link
# points at something that exists.
$docsViolations = Get-FunctionAnnotationViolations
# The baseline tracks ABSENT annotations only. A function whose tags are present but whose link is
# broken is a different, never-forgiven finding, so it must not also appear as missing-annotation
# debt with an empty list of what is missing.
$docsKeysNow = $docsViolations | Where-Object { $_.Missing } |
  Select-Object -ExpandProperty Key -Unique | Sort-Object
$docsBaseline = @()
if (Test-Path $DocsBaselinePath) {
  $docsBaseline = Get-Content $DocsBaselinePath | Where-Object { $_ -and -not $_.StartsWith('#') }
}
$docsNew = $docsKeysNow | Where-Object { $docsBaseline -notcontains $_ }
$docsFixed = $docsBaseline | Where-Object { $docsKeysNow -notcontains $_ }

# A broken <tests> link fails whether or not the function is baselined. The baseline forgives an
# ABSENT annotation, which is honest debt; it must never forgive a link that points at nothing,
# because that reads as coverage and is the more expensive of the two mistakes.
$brokenLinks = $docsViolations | Where-Object { $_.BadLink }
if ($brokenLinks) {
  $exit = 1
  Write-Host ''
  Write-Host 'A <tests> link points at something that does not exist (rule 13 — these fail CI):' -ForegroundColor Red
  foreach ($v in $brokenLinks) {
    Write-Host ("  {0}:{1}  {2}  ->  {3}" -f $v.File, $v.Line, $v.Fn, $v.BadLink)
  }
  Write-Host ''
  Write-Host 'Name a test you have confirmed reaches the function. A link to a test that does not'
  Write-Host 'exercise it is worse than no link, because it reads as coverage.'
}
if ($docsNew) {
  $exit = 1
  Write-Host ''
  Write-Host 'Functions defined without <docs>/<tests> links (rule 13 — these fail CI):' -ForegroundColor Red
  foreach ($k in $docsNew) {
    $docsViolations | Where-Object Key -eq $k | ForEach-Object {
      Write-Host ("  {0}:{1}  {2}  missing {3}" -f $_.File, $_.Line, $_.Fn, $_.Missing)
    }
  }
  Write-Host ''
  Write-Host 'SQL is code and follows the same standard. In a comment immediately above the function:'
  Write-Host '  -- <docs>fundamentals/work-coordinator/store-outbox-messages</docs>'
  Write-Host '  -- <tests>tests/.../StoreProbeCostScenarioTests.cs:StoreProbe_DrainedStream_AnswersAsync</tests>'
  Write-Host 'Multiple <tests> lines are fine, as in C#. When a function genuinely has no docs page,'
  Write-Host 'say so and list it rather than inventing a path — a missing page is a real gap.'
}
if ($docsFixed) {
  $exit = 1
  Write-Host ''
  Write-Host 'Baselined functions that now carry links — remove these lines (ratchet down):' -ForegroundColor Yellow
  $docsFixed | ForEach-Object { Write-Host "  $_" }
  Write-Host ''
  Write-Host 'Run:  pwsh scripts/Lint-MigrationSql.ps1 -UpdateBaseline'
}

if ($exit -eq 0) {
  Write-Host ("migration SQL lint OK — {0} known refs and {1} un-annotated functions, all baselined; 0 new; DROP COLUMN notes present; every <tests> link resolves." -f $currentKeys.Count, $docsKeysNow.Count) -ForegroundColor Green
}
exit $exit
