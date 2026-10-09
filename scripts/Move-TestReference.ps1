#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Relinks every reference to a test file or test folder that moves: the <tests> tags in src, the
    library's docs, and (on request) the docs site. Reports the counts a test-move PR must state.

.DESCRIPTION
    Library <tests> tags name test files by repository path (tests/Whizbang.Core.Tests/Async/X.cs or
    tests/Whizbang.Core.Tests/Async/X.cs:MethodAsync), and docs, plans and READMEs cite the same paths.
    Moving a test file (or a whole test folder or project) breaks each of them unless it is relinked.
    This script takes one or more moves and rewrites every reference to the old path:

      Library, rewritten:
        - src/**: <tests> tags in any text file (C# XML doc comments, SQL comments, ...), counted as
          tags, and any other mention of the path (a plain comment), counted separately.
        - ai-docs/**, plans/**/*.md, docs/**/*.md, every README*.md and every CLAUDE.md.

      Docs site (default: the sibling folder whizbang-lib.github.io next to the library checkout):
        - Hand-written files (doc pages with testReferences front matter, ai-docs, standards,
          workflows): reported; rewritten only with -UpdateDocsSite.
        - Script code (.mjs, .js, .cjs, .ts): reported for review by hand, never rewritten. A path
          there may be a deliberate fixture (a generator test that cites a missing file to check
          its warning), which a rewrite would break.
        - Generated files (code-tests-map.json, the VS Code feed, test-status/*.json, the static docs
          page, search indexes, audit reports): never rewritten. They are listed as "regenerate" items
          with the generator that rebuilds them. A move between test projects also lists the
          test-status shards of both projects (one JSON per test project) and its index.
        - Class-based markers (code fence tests=["Class.Method"] and {verified: Class.Method}) name a
          class, not a path. A move keeps the class name, so they stay valid; the script counts the
          ones that name a moved class so a reviewer can see what the move affects.

    Matching is exact on path boundaries: moving tests/A.Tests/Foo.cs does not touch
    tests/A.Tests/FooBar.cs, and moving tests/A.Tests does not touch tests/A.Tests.Extra/. A
    reference may be prefixed (../tests/..., a URL ending in the path). All moves of a run apply in
    one pass, the longest (most specific) old path first, so a move never rewrites another move's
    result. Matching is case-sensitive, like the repository paths.

    Each move must exist on one side: the old path (run before git mv) or the new path (run after).
    A move whose paths exist on neither side stops the run before anything is written, since a typo
    would otherwise relink nothing and still report success.

    A rewritten file keeps its encoding exactly: UTF-8 with or without a byte order mark, its line
    endings and its trailing newline (or lack of one). Files without a reference are never written.
    Binary files (a NUL byte in the first 8000 bytes, as git decides) are skipped. A file that is
    not valid UTF-8 is never rewritten; if it holds a reference, the script warns and lists it under
    Skipped so it can be fixed by hand.

    Bin, obj, node_modules, TestResults, dist, .angular, .git and .claude/worktrees are not scanned.

    Output: a summary on the host (the counts for the PR text) and a result object on the pipeline:
    DryRun, Moves, TagsRewritten, TagFiles, SourceReferencesRewritten, SourceReferenceFiles,
    LibraryDocReferencesRewritten, LibraryDocFiles, DocsSiteRoot, DocsSiteUpdated,
    DocsSiteReferencesFound, DocsSiteFiles, DocsSiteReferencesRewritten, DocsSiteCodeReferences,
    DocsSiteCodeFiles, DocsSiteClassMarkers, Regenerate, Changes and Skipped. In a dry run the "Rewritten" counts are what a real run would
    rewrite.

.PARAMETER From
    Old repository-relative path(s): a test file or a folder (a whole project folder, or a folder in
    one). Forward or back slashes; a leading ./ and a trailing slash are ignored. Pair each with -To.

.PARAMETER To
    New repository-relative path(s), one per -From, in the same order.

.PARAMETER MapFile
    A CSV file with From and To columns, one move per row. Combines with -From/-To.

.PARAMETER RepositoryRoot
    The library checkout. Default: the folder above this script.

.PARAMETER DocsSiteRoot
    The docs-site checkout. Default: whizbang-lib.github.io next to the library checkout. When the
    default is missing, docs-site references are not reported and the script warns; when a given
    folder is missing, or -UpdateDocsSite is set, the script stops.

.PARAMETER UpdateDocsSite
    Also rewrite the docs site's hand-written references. Without it the docs site is report-only.
    Generated docs-site files and docs-site script code are never rewritten either way.

.PARAMETER DryRun
    Change nothing; report what a real run would rewrite. -WhatIf does the same and also lists each
    file a real run would write.

.EXAMPLE
    pwsh scripts/Move-TestReference.ps1 -From tests/Example.Tests/Async/SignalTests.cs -To tests/Example.Component.Tests/Async/SignalTests.cs -DryRun

    Reports what moving one test file would relink, without writing anything.

.EXAMPLE
    pwsh scripts/Move-TestReference.ps1 -MapFile moves.csv -UpdateDocsSite

    Relinks every move in moves.csv (columns From,To) in the library and the docs site.

.EXAMPLE
    pwsh scripts/Move-TestReference.ps1 -From tests/Example.Tests/Workers -To tests/Example.Component.Tests/Workers -WhatIf

    Lists each file a folder move would rewrite.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter()] [string[]]$From,
  [Parameter()] [string[]]$To,
  [Parameter()] [string]$MapFile,
  [Parameter()] [string]$RepositoryRoot,
  [Parameter()] [string]$DocsSiteRoot,
  [Parameter()] [switch]$UpdateDocsSite,
  [Parameter()] [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A repository-relative path in the one spelling references use: forward slashes, no leading ./,
# no trailing slash.
function ConvertTo-RepoRelativePath([string]$Path) {
  $normalized = $Path.Trim().Replace('\', '/')
  while ($normalized.StartsWith('./')) { $normalized = $normalized.Substring(2) }
  return $normalized.TrimEnd('/')
}

# The test project a path sits in: the folder right after the first "tests" folder
# (tests/<Project>/..., samples/App/tests/<Project>/...). Test-status shards are named after it.
function Get-TestProjectName([string]$Path, [string]$Kind) {
  $segments = @($Path -split '/')
  # Index of the last folder segment: a file's own name is not a folder.
  $lastFolder = if ($Kind -eq 'File') { $segments.Count - 2 } else { $segments.Count - 1 }
  for ($i = 0; $i -lt $lastFolder; $i++) {
    if ($segments[$i] -ceq 'tests') { return $segments[$i + 1] }
  }
  return $null
}

# Every file under a root as forward-slash relative paths, skipping build output, dependencies and
# nested worktrees.
function Get-RepositoryFile([string]$Root, [string[]]$ExcludeRelative = @()) {
  $excludedNames = @('.git', 'bin', 'obj', 'node_modules', 'TestResults', 'dist', '.angular')
  $found = [System.Collections.Generic.List[string]]::new()
  $pending = [System.Collections.Generic.Stack[string]]::new()
  $pending.Push($Root)
  while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    foreach ($file in [System.IO.Directory]::EnumerateFiles($directory)) {
      $found.Add([System.IO.Path]::GetRelativePath($Root, $file).Replace('\', '/'))
    }
    foreach ($sub in [System.IO.Directory]::EnumerateDirectories($directory)) {
      $relative = [System.IO.Path]::GetRelativePath($Root, $sub).Replace('\', '/')
      if ($excludedNames -contains [System.IO.Path]::GetFileName($sub) -or $ExcludeRelative -contains $relative) { continue }
      $pending.Push($sub)
    }
  }
  return @($found | Sort-Object -CaseSensitive)
}

# The class and record names a moved file or folder declares, for counting class-based markers.
function Get-MovedClassName([string]$Path, [string]$Kind) {
  $files = if ($Kind -eq 'File') { @($Path) } else {
    @(Get-RepositoryFile -Root $Path | Where-Object { $_.EndsWith('.cs') } | ForEach-Object { Join-Path -Path $Path -ChildPath $_ })
  }
  $names = foreach ($file in $files) {
    [regex]::Matches([System.IO.File]::ReadAllText($file), '\b(?:class|record(?:\s+(?:class|struct))?)\s+([A-Za-z_]\w*)') |
      ForEach-Object { $_.Groups[1].Value }
  }
  return @($names | Sort-Object -Unique -CaseSensitive)
}

# Validates and resolves the moves given as -From/-To pairs and map-file rows. Stops before anything
# is written when a move is malformed, repeated, or names paths that exist on neither side.
function Resolve-TestMove([string]$RepositoryRoot, [string[]]$From, [string[]]$To, [string]$MapFile) {
  $fromList = @($From | Where-Object { $_ })
  $toList = @($To | Where-Object { $_ })
  if ($fromList.Count -ne $toList.Count) {
    throw "-From has $($fromList.Count) path(s) and -To has $($toList.Count); give one -To per -From, in the same order."
  }
  $pairs = [System.Collections.Generic.List[object]]::new()
  for ($i = 0; $i -lt $fromList.Count; $i++) { $pairs.Add([pscustomobject]@{ From = $fromList[$i]; To = $toList[$i] }) }
  if ($MapFile) {
    if (-not (Test-Path -LiteralPath $MapFile -PathType Leaf)) { throw "Map file not found: $MapFile" }
    $rows = @(Import-Csv -LiteralPath $MapFile)
    if ($rows.Count -gt 0) {
      $columns = @($rows[0].PSObject.Properties.Name)
      if ($columns -notcontains 'From' -or $columns -notcontains 'To') { throw "Map file $MapFile needs From and To columns; it has: $($columns -join ', ')." }
    }
    foreach ($row in $rows) { $pairs.Add([pscustomobject]@{ From = [string]$row.From; To = [string]$row.To }) }
  }
  if ($pairs.Count -eq 0) { throw 'No move given: pass -From and -To, or -MapFile.' }

  $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
  foreach ($pair in $pairs) {
    $old = ConvertTo-RepoRelativePath $pair.From
    $new = ConvertTo-RepoRelativePath $pair.To
    if (-not $old -or -not $new) { throw "A move has an empty side: '$($pair.From)' -> '$($pair.To)'." }
    if ($old -ceq $new) { throw "Move '$old' names the same path on both sides." }
    if (-not $seen.Add($old)) { throw "'$old' is moved more than once." }
    $oldFull = Join-Path -Path $RepositoryRoot -ChildPath $old
    $newFull = Join-Path -Path $RepositoryRoot -ChildPath $new
    # The old path exists before git mv, the new one after; either order works.
    $existing = if (Test-Path -LiteralPath $oldFull) { $oldFull } elseif (Test-Path -LiteralPath $newFull) { $newFull } else {
      throw "Neither '$old' nor '$new' exists under $RepositoryRoot. A mistyped path would relink nothing; give repository-relative paths."
    }
    $kind = if (Test-Path -LiteralPath $existing -PathType Leaf) { 'File' } else { 'Directory' }
    [pscustomobject]@{
      From       = $old
      To         = $new
      Kind       = $kind
      OldProject = Get-TestProjectName -Path $old -Kind $kind
      NewProject = Get-TestProjectName -Path $new -Kind $kind
      Classes    = @(Get-MovedClassName -Path $existing -Kind $kind)
    }
  }
}

# The docs-site checkout to scan, or $null to skip it (warned) when the default is absent.
function Resolve-DocsSiteRoot([string]$RepositoryRoot, [string]$DocsSiteRoot, [switch]$UpdateDocsSite) {
  $explicit = [bool]$DocsSiteRoot
  $candidate = if ($explicit) { $DocsSiteRoot } else {
    Join-Path -Path (Split-Path -Path $RepositoryRoot -Parent) -ChildPath 'whizbang-lib.github.io'
  }
  if (Test-Path -LiteralPath $candidate -PathType Container) { return (Resolve-Path -LiteralPath $candidate).Path }
  if ($explicit -or $UpdateDocsSite) { throw "Docs site not found at $candidate; pass -DocsSiteRoot with its checkout." }
  Write-Warning "Docs site not found at $candidate, so its references are not reported. Pass -DocsSiteRoot to include it."
  return $null
}

# The regexes and old-to-new map for a set of moves. One alternation, longest old path first, so
# a file move inside a moved folder wins and no move rewrites another's result.
function New-PathRewriter([object[]]$Moves) {
  $map = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
  foreach ($move in $Moves) { $map[$move.From] = $move.To }
  $alternation = @($Moves | Sort-Object -Property { $_.From.Length } -Descending | ForEach-Object { [regex]::Escape($_.From) }) -join '|'
  # Not preceded by a path character other than "/" (so ../tests/... and URLs match, xtests/... does
  # not); not followed by more of a name (FooBar.cs, Foo.csx, Foo.cs.bak, A.Tests.Extra/).
  $path = "(?<![A-Za-z0-9_.\-])(?<path>$alternation)(?![A-Za-z0-9_\-]|\.[A-Za-z0-9])"
  return [pscustomobject]@{
    Map    = $map
    Path   = [regex]::new($path)
    Source = [regex]::new("(?<tag><tests>(?<body>[^<]*)</tests>)|$path")
  }
}

# Rewrites every reference in a text. Returns the new text and the number of references rewritten.
function Update-PathReference([string]$Text, [object]$Rewriter) {
  $map = $Rewriter.Map
  $count = $Rewriter.Path.Matches($Text).Count
  $new = $Rewriter.Path.Replace($Text, [System.Text.RegularExpressions.MatchEvaluator] { param($m) $map[$m.Groups['path'].Value] })
  return [pscustomobject]@{ Text = $new; Count = $count }
}

# Rewrites a source file, counting rewritten <tests> tags and other references apart.
function Update-SourceReference([string]$Text, [object]$Rewriter) {
  $state = @{ Tags = 0; Other = 0 }
  $new = $Rewriter.Source.Replace($Text, [System.Text.RegularExpressions.MatchEvaluator] {
      param($m)
      if ($m.Groups['tag'].Success) {
        $body = $m.Groups['body'].Value
        $rewritten = (Update-PathReference -Text $body -Rewriter $Rewriter).Text
        if ($rewritten -cne $body) { $state.Tags++ }
        return "<tests>$rewritten</tests>"
      }
      $state.Other++
      return $Rewriter.Map[$m.Groups['path'].Value]
    })
  return [pscustomobject]@{ Text = $new; Tags = $state.Tags; Other = $state.Other }
}

# Reads a file as text, remembering its byte order mark. $null for a binary file (a NUL in the first
# 8000 bytes). Valid is false for bytes that are not UTF-8; Text is then a Latin-1 reading, good
# only for spotting a reference.
function Read-TextFile([string]$Path) {
  $bytes = [System.IO.File]::ReadAllBytes($Path)
  if ([Array]::IndexOf($bytes, [byte]0, 0, [Math]::Min($bytes.Length, 8000)) -ge 0) { return $null }
  $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
  $offset = [int]$bom * 3
  try {
    # Strict decoding: throws on bytes that are not UTF-8, the only way this call fails.
    $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes, $offset, $bytes.Length - $offset)
    return [pscustomobject]@{ Text = $text; Bom = $bom; Valid = $true }
  }
  catch {
    return [pscustomobject]@{ Text = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes); Bom = $bom; Valid = $false }
  }
}

# Writes text back as UTF-8 with the byte order mark it had, and nothing else changed.
function Write-TextFile([string]$Path, [string]$Text, [bool]$Bom) {
  $encoding = [System.Text.UTF8Encoding]::new($Bom)
  $preamble = $encoding.GetPreamble()
  $payload = $encoding.GetBytes($Text)
  $stream = [System.IO.File]::Create($Path)
  try {
    $stream.Write($preamble, 0, $preamble.Length)
    $stream.Write($payload, 0, $payload.Length)
  }
  finally { $stream.Dispose() }
}

# The library files whose references are rewritten, by category; $null for the rest.
function Get-LibraryFileCategory([string]$RelativePath) {
  $leaf = [System.IO.Path]::GetFileName($RelativePath)
  if ($RelativePath.StartsWith('src/')) { return 'Source' }
  if ($RelativePath.StartsWith('ai-docs/')) { return 'Doc' }
  if ($leaf -like 'README*.md' -or $leaf -eq 'CLAUDE.md') { return 'Doc' }
  if (($RelativePath.StartsWith('plans/') -or $RelativePath.StartsWith('docs/')) -and $leaf.EndsWith('.md')) { return 'Doc' }
  return $null
}

# The generator of a docs-site file that must never be hand-edited; $null for a hand-written file.
# A rule ending in "/" covers the folder.
function Get-DocsSiteGeneratedRule([string]$RelativePath) {
  $rules = @(
    @{ Path = 'src/assets/code-tests-map.json'; Generator = 'node src/scripts/generate-code-tests-map.mjs' }
    @{ Path = 'src/assets/vscode-feed.json'; Generator = 'node src/scripts/generate-vscode-feed.mjs' }
    @{ Path = 'src/assets/data/test-status/'; Generator = 'src/scripts/build-test-status.mjs (docs-site CI, from the library test results)' }
    @{ Path = 'src/assets/code-docs-map.json'; Generator = 'node src/scripts/generate-code-docs-map.mjs' }
    @{ Path = 'src/assets/docs-index.json'; Generator = 'node src/scripts/gen-docs-index.mjs' }
    @{ Path = 'src/assets/docs-index-versioned.json'; Generator = 'node src/scripts/gen-docs-index-versioned.mjs' }
    @{ Path = 'src/assets/search-index.json'; Generator = 'node src/scripts/gen-enhanced-search-index.mjs' }
    @{ Path = 'src/assets/enhanced-search-index.json'; Generator = 'node src/scripts/gen-enhanced-search-index.mjs' }
    @{ Path = 'src/static/'; Generator = 'node src/scripts/gen-static-docs.mjs' }
    @{ Path = 'audit-reports/'; Generator = 'node src/scripts/audit-baseline.mjs and audit-stale-pages.mjs' }
  )
  foreach ($rule in $rules) {
    $hit = if ($rule.Path.EndsWith('/')) { $RelativePath.StartsWith($rule.Path) } else { $RelativePath -ceq $rule.Path }
    if ($hit) { return $rule }
  }
  return $null
}

# Script code in the docs site: a path there may be a deliberate fixture, so it is only reported.
function Test-DocsSiteCodeFile([string]$RelativePath) {
  return [System.IO.Path]::GetExtension($RelativePath) -in @('.mjs', '.js', '.cjs', '.ts')
}

# Counts tests=["Class.Method", ...] and {verified: Class.Method, ...} entries naming a moved class.
function Measure-ClassMarker([string]$Text, [System.Collections.Generic.HashSet[string]]$ClassName) {
  $count = 0
  foreach ($list in [regex]::Matches($Text, 'tests=\[(?<list>[^\]]*)\]|\{verified:(?<list>[^}]*)\}')) {
    foreach ($item in [regex]::Matches($list.Groups['list'].Value, '(?<![\w.])(?<class>[A-Za-z_]\w*)\.\w+')) {
      if ($ClassName.Contains($item.Groups['class'].Value)) { $count++ }
    }
  }
  return $count
}

# Reads a file for rewriting: $null when binary or not UTF-8. A non-UTF-8 file holding a reference
# is warned about and listed in Skipped, since it cannot be rewritten safely.
function Read-RepositoryText([string]$Root, [string]$RelativePath, [string]$Repository, [object]$Rewriter, [System.Collections.Generic.List[object]]$Skipped) {
  $file = Read-TextFile (Join-Path -Path $Root -ChildPath $RelativePath)
  if ($null -eq $file) { return $null }
  if (-not $file.Valid) {
    if ($Rewriter.Path.IsMatch($file.Text)) {
      Write-Warning "$Repository ${RelativePath}: not valid UTF-8, so its test reference was not rewritten; fix it by hand."
      $Skipped.Add([pscustomobject]@{ Repository = $Repository; Path = $RelativePath; Reason = 'not valid UTF-8' })
    }
    return $null
  }
  return $file
}

# Sums one count over the changes of a repository and category: the total and the files carrying it.
function Get-ChangeTotal([object[]]$Change, [string]$Repository, [string]$Category, [string]$Property) {
  $total = 0
  $files = 0
  foreach ($c in $Change) {
    if ($c.Repository -ceq $Repository -and $c.Category -ceq $Category -and $c.$Property -gt 0) {
      $total += $c.$Property
      $files++
    }
  }
  return [pscustomobject]@{ Total = $total; Files = $files }
}

# Applies resolved moves: rewrites the library, scans (and with -UpdateDocsSite rewrites) the docs
# site, and returns the result object. -DryRun or -WhatIf writes nothing.
function Invoke-TestReferenceMove {
  [CmdletBinding(SupportsShouldProcess)]
  param(
    [Parameter(Mandatory)] [string]$RepositoryRoot,
    [Parameter(Mandatory)] [object[]]$Moves,
    [Parameter()] [string]$DocsSiteRoot,
    [Parameter()] [switch]$UpdateDocsSite,
    [Parameter()] [switch]$DryRun
  )
  # -WhatIf reaches ShouldProcess, which then lists each file instead of writing it.
  $dry = $DryRun -or $WhatIfPreference
  $rewriter = New-PathRewriter $Moves
  $changes = [System.Collections.Generic.List[object]]::new()
  $skipped = [System.Collections.Generic.List[object]]::new()

  foreach ($relative in Get-RepositoryFile -Root $RepositoryRoot -ExcludeRelative '.claude/worktrees') {
    $category = Get-LibraryFileCategory $relative
    if (-not $category) { continue }
    $file = Read-RepositoryText -Root $RepositoryRoot -RelativePath $relative -Repository 'library' -Rewriter $rewriter -Skipped $skipped
    if ($null -eq $file) { continue }
    if ($category -eq 'Source') {
      $update = Update-SourceReference -Text $file.Text -Rewriter $rewriter
      $tags = $update.Tags
      $references = $update.Other
    }
    else {
      $update = Update-PathReference -Text $file.Text -Rewriter $rewriter
      $tags = 0
      $references = $update.Count
    }
    if ($tags + $references -eq 0) { continue }
    $change = [pscustomobject]@{ Repository = 'library'; Path = $relative; Category = $category; Tags = $tags; References = $references; Written = $false }
    $changes.Add($change)
    if (-not $DryRun -and $PSCmdlet.ShouldProcess("library/$relative", 'Rewrite test references')) {
      Write-TextFile -Path (Join-Path -Path $RepositoryRoot -ChildPath $relative) -Text $update.Text -Bom $file.Bom
      $change.Written = $true
    }
  }

  $regenerate = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
  $classMarkers = 0
  if ($DocsSiteRoot) {
    $classes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($move in $Moves) { foreach ($class in $move.Classes) { [void]$classes.Add($class) } }

    foreach ($relative in Get-RepositoryFile -Root $DocsSiteRoot -ExcludeRelative '.claude/worktrees') {
      $file = Read-RepositoryText -Root $DocsSiteRoot -RelativePath $relative -Repository 'docs-site' -Rewriter $rewriter -Skipped $skipped
      if ($null -eq $file) { continue }
      $rule = Get-DocsSiteGeneratedRule $relative
      if ($rule) {
        $cited = $rewriter.Path.Matches($file.Text).Count
        if ($cited -gt 0) { $regenerate[$relative] = [pscustomobject]@{ Path = $relative; Generator = $rule.Generator; Reason = "cites $cited moved test path(s)" } }
        continue
      }
      $classMarkers += Measure-ClassMarker -Text $file.Text -ClassName $classes
      $update = Update-PathReference -Text $file.Text -Rewriter $rewriter
      if ($update.Count -eq 0) { continue }
      $isCode = Test-DocsSiteCodeFile $relative
      $category = if ($isCode) { 'DocsSiteCode' } else { 'DocsSite' }
      $change = [pscustomobject]@{ Repository = 'docs-site'; Path = $relative; Category = $category; Tags = 0; References = $update.Count; Written = $false }
      $changes.Add($change)
      if (-not $isCode -and $UpdateDocsSite -and -not $DryRun -and $PSCmdlet.ShouldProcess("docs-site/$relative", 'Rewrite test references')) {
        Write-TextFile -Path (Join-Path -Path $DocsSiteRoot -ChildPath $relative) -Text $update.Text -Bom $file.Bom
        $change.Written = $true
      }
    }

    # The VS Code feed is built from the code-tests map, so it follows it.
    $feed = 'src/assets/vscode-feed.json'
    if ($regenerate.ContainsKey('src/assets/code-tests-map.json') -and -not $regenerate.ContainsKey($feed)) {
      $regenerate[$feed] = [pscustomobject]@{ Path = $feed; Generator = (Get-DocsSiteGeneratedRule $feed).Generator; Reason = 'built from code-tests-map.json' }
    }
    # Test status is one shard per test project, so a move between projects changes two shards
    # (the new project may not have one yet) and the index that lists them.
    foreach ($move in $Moves) {
      if (-not $move.OldProject -or -not $move.NewProject -or $move.OldProject -ceq $move.NewProject) { continue }
      foreach ($project in @($move.OldProject, $move.NewProject)) {
        $shard = "src/assets/data/test-status/$project.json"
        if ($regenerate.ContainsKey($shard)) { continue }
        $reason = if (Test-Path -LiteralPath (Join-Path -Path $DocsSiteRoot -ChildPath $shard)) { "tests move from $($move.OldProject) to $($move.NewProject)" } else {
          "project $project has no status shard yet; the generator must emit one"
        }
        $regenerate[$shard] = [pscustomobject]@{ Path = $shard; Generator = (Get-DocsSiteGeneratedRule $shard).Generator; Reason = $reason }
      }
      $index = 'src/assets/data/test-status/index.json'
      if (-not $regenerate.ContainsKey($index)) {
        $regenerate[$index] = [pscustomobject]@{ Path = $index; Generator = (Get-DocsSiteGeneratedRule $index).Generator; Reason = 'lists the per-project shards' }
      }
    }
  }

  $all = @($changes)
  $tagTotal = Get-ChangeTotal -Change $all -Repository 'library' -Category 'Source' -Property 'Tags'
  $sourceTotal = Get-ChangeTotal -Change $all -Repository 'library' -Category 'Source' -Property 'References'
  $docTotal = Get-ChangeTotal -Change $all -Repository 'library' -Category 'Doc' -Property 'References'
  $siteTotal = Get-ChangeTotal -Change $all -Repository 'docs-site' -Category 'DocsSite' -Property 'References'
  $codeTotal = Get-ChangeTotal -Change $all -Repository 'docs-site' -Category 'DocsSiteCode' -Property 'References'
  return [pscustomobject]@{
    DryRun                        = [bool]$dry
    Moves                         = @($Moves)
    TagsRewritten                 = $tagTotal.Total
    TagFiles                      = $tagTotal.Files
    SourceReferencesRewritten     = $sourceTotal.Total
    SourceReferenceFiles          = $sourceTotal.Files
    LibraryDocReferencesRewritten = $docTotal.Total
    LibraryDocFiles               = $docTotal.Files
    DocsSiteRoot                  = if ($DocsSiteRoot) { $DocsSiteRoot } else { $null }
    DocsSiteUpdated               = [bool]$UpdateDocsSite
    DocsSiteReferencesFound       = $siteTotal.Total
    DocsSiteFiles                 = $siteTotal.Files
    DocsSiteReferencesRewritten   = if ($UpdateDocsSite) { $siteTotal.Total } else { 0 }
    DocsSiteCodeReferences        = $codeTotal.Total
    DocsSiteCodeFiles             = $codeTotal.Files
    DocsSiteClassMarkers          = $classMarkers
    Regenerate                    = @($regenerate.Values | Sort-Object -Property Path -CaseSensitive)
    Changes                       = $all
    Skipped                       = @($skipped)
  }
}

# Prints the counts a test-move PR states, and each file the run rewrites (or would).
function Write-TestReferenceSummary([object]$Result) {
  $verb = if ($Result.DryRun) { 'would be rewritten' } else { 'rewritten' }
  Write-Host "Test reference relink$(if ($Result.DryRun) { ' (dry run: nothing written)' })"
  Write-Host "  Moves: $(@($Result.Moves).Count)"
  foreach ($move in $Result.Moves) { Write-Host "    [$($move.Kind)] $($move.From) -> $($move.To)" }
  Write-Host "  <tests> tags ${verb}: $($Result.TagsRewritten) in $($Result.TagFiles) file(s)"
  Write-Host "  Other source references ${verb}: $($Result.SourceReferencesRewritten) in $($Result.SourceReferenceFiles) file(s)"
  Write-Host "  Library doc references ${verb}: $($Result.LibraryDocReferencesRewritten) in $($Result.LibraryDocFiles) file(s)"
  Write-Host "  Skipped (not valid UTF-8, fix by hand): $(@($Result.Skipped).Count)"
  foreach ($item in $Result.Skipped) { Write-Host "    $($item.Repository): $($item.Path)" }
  if (-not $Result.DocsSiteRoot) {
    Write-Host '  Docs site: not scanned'
  }
  else {
    $mode = if ($Result.DocsSiteUpdated) { $verb } else { 'rewritten (report only: pass -UpdateDocsSite to rewrite them)' }
    Write-Host "  Docs site ($($Result.DocsSiteRoot)): $($Result.DocsSiteReferencesFound) reference(s) found in $($Result.DocsSiteFiles) file(s); $($Result.DocsSiteReferencesRewritten) $mode"
    Write-Host "  Docs site script code citing a moved path (review by hand, never rewritten): $($Result.DocsSiteCodeReferences) in $($Result.DocsSiteCodeFiles) file(s)"
    Write-Host "  Docs site class-based markers naming a moved class: $($Result.DocsSiteClassMarkers) (tests=[...] and {verified: ...} name Class.Method, not a path, so a move leaves them valid)"
    Write-Host "  Docs site generated artifacts to regenerate (never hand-edited): $(@($Result.Regenerate).Count)"
    foreach ($item in $Result.Regenerate) { Write-Host "    $($item.Path): $($item.Reason) [$($item.Generator)]" }
  }
  Write-Host '  Files:'
  foreach ($change in $Result.Changes) {
    Write-Host "    $($change.Repository) $($change.Path): $($change.Tags) tag(s), $($change.References) other reference(s)$(if ($change.Written) { ', written' })"
  }
}

# Dot-sourcing for tests loads the functions without doing anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$root = if ($RepositoryRoot) { (Resolve-Path -LiteralPath $RepositoryRoot).Path } else { Join-Path -Path $PSScriptRoot -ChildPath '..' -Resolve }
$moves = @(Resolve-TestMove -RepositoryRoot $root -From $From -To $To -MapFile $MapFile)
$docsSite = Resolve-DocsSiteRoot -RepositoryRoot $root -DocsSiteRoot $DocsSiteRoot -UpdateDocsSite:$UpdateDocsSite
$result = Invoke-TestReferenceMove -RepositoryRoot $root -Moves $moves -DocsSiteRoot $docsSite -UpdateDocsSite:$UpdateDocsSite -DryRun:$DryRun
Write-TestReferenceSummary -Result $result
$result
