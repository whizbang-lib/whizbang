#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Fails when a <tests> tag in the library source names a test file that does not exist, or a test
    method that file does not declare.

.DESCRIPTION
    Library members carry <tests> tags in their comments (/// in C#, -- in SQL migrations) that link
    each member to the tests that cover it. Nothing checked those links, so when a test file was renamed
    or moved, or a test method was renamed, the tag kept pointing at nothing and no one noticed. Moving
    test files between projects would break hundreds at once, so this guard makes every broken link a
    CI failure.

    What is scanned: every .cs and .sql file under src/, except bin/ and obj/ output. Markdown is not
    scanned: the one Markdown file with tags (the migrations README) shows the convention with elided
    example paths, which are not links.

    Tag forms and the rule for each:
      <tests>path</tests>          The file must exist. The path is repository-relative with forward
                                   slashes. Most start with tests/, but contract test bases live under
                                   src/ (src/Whizbang.Testing/Contracts/...), and those are equally valid.
                                   Matching is case-sensitive, segment by segment, as on the Linux CI
                                   runners, so a wrong-case path fails even on a case-insensitive disk.
                                   A rooted path, a . or .. segment, an empty segment or a backslash
                                   never names a file and fails as missing.
      <tests>path:Name</tests>     The file must exist and declare Name: a member declaration (Name( or
                                   Name<T>( after a return type, modifier or constructor's access
                                   modifier), or a class, struct, interface, enum or record named Name.
                                   A type name is accepted because a few tags name a whole test class.
      <tests>No tests found</tests> An explicit statement that the member has no tests. Accepted, and
                                   counted in the summary. Only this exact text.
      Whitespace around the text   Trimmed.
      Anything else                Malformed: several targets in one tag (use one tag per target), a
                                   File.cs:Class.Method form, whitespace inside the text, an empty tag,
                                   or a Name that is not an identifier.
      A tag not closed on its line Unterminated: a tag must open and close on one line.

    How a declaration is recognized (kept simple on purpose): line by line, skipping lines that start
    with //, /* or * (comments), Name followed by an optional generic argument list and an opening
    parenthesis counts when the token before it is a word that is not a call-site keyword (await,
    return, new, throw, yield, else, case, in, is, as, using, not, and, or, when), or a >, ], ) or ?
    that ends a return type (not one after whitespace or =, as in "=> Name(" or "c ? Name("). So
    "public async Task Name()" and "Task<int> Name<T>()" count, while "await Name()", "var x =
    Name()", "new Name()" and a #region named Name do not. A declaration split so that Name starts a
    line, or a member declared only in another file of a partial class, is not found: point the tag at
    the file that declares it.

    The baseline (tests-tag-link-baseline.txt next to this script) lists the known-broken tags that
    could not be fixed with confidence, one per line as "source | tag | reason", where source is the
    repository-relative file holding the tag and tag is its trimmed text; # starts a comment. A
    baselined tag is not reported, so the guard still blocks every new break. An entry whose tag now
    resolves, or whose tag is gone from that file, is stale and fails the run too, so the baseline only
    shrinks.

    Exits 1 listing each unbaselined broken tag (file, line, tag and why) and each stale baseline entry;
    exits 0 otherwise. Dot-source the script to load its functions without running the check.

.PARAMETER Root
    The repository root. Default: two levels above this script.

.PARAMETER BaselinePath
    The baseline file. Default: tests-tag-link-baseline.txt next to this script. A missing file is an
    empty baseline.

.EXAMPLE
    pwsh .github/scripts/Test-TestsTagLink.ps1
#>

param(
    [Parameter()] [string]$Root = (Join-Path -Path $PSScriptRoot -ChildPath '../..'),
    [Parameter()] [string]$BaselinePath = (Join-Path -Path $PSScriptRoot -ChildPath 'tests-tag-link-baseline.txt')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:NoTestsPlaceholder = 'No tests found'
$script:ScannedExtensions = @('.cs', '.sql')
# A tag that closes on its own line captures "text"; one that does not captures the rest of the line
# as "open", so an unterminated tag is reported instead of silently skipped.
$script:TagPattern = [regex]'<tests>(?:(?<text>[^\r\n]*?)</tests>|(?<open>[^\r\n]*))'
# A path (no whitespace, no colon) and an optional identifier. Anything else is malformed.
$script:TargetPattern = [regex]'^(?<path>[^\s:]+)(?::(?<member>[A-Za-z_][A-Za-z0-9_]*))?$'
# Words that put a name in a call or expression rather than a declaration.
$script:CallKeywords = @('await', 'return', 'new', 'throw', 'yield', 'else', 'case', 'in', 'is', 'as', 'using', 'not', 'and', 'or', 'when')

# Caches shared across one run, so each directory is listed once and each test file read once.
# Ordinal keys: a path that differs only in case is a different path.
function New-TestsTagCache {
  return @{
    Entries = [hashtable]::new([System.StringComparer]::Ordinal)
    Content = [hashtable]::new([System.StringComparer]::Ordinal)
  }
}

# Every <tests> tag in a file's text, with its 1-based line.
function Get-TestsTagInText([string]$Text, [string]$Source) {
  $line = 1
  $from = 0
  foreach ($match in $script:TagPattern.Matches($Text)) {
    # Counting newlines only since the previous tag keeps the scan linear in the file's length.
    $line += $Text.Substring($from, $match.Index - $from).Split([char]10).Length - 1
    $from = $match.Index
    $closed = $match.Groups['text'].Success
    $inner = if ($closed) { $match.Groups['text'].Value } else { $match.Groups['open'].Value }
    [pscustomobject]@{ Source = $Source; Line = $line; Text = $inner.Trim(); Closed = $closed; Reason = $null }
  }
}

# Every <tests> tag in the scanned source files under src/.
function Find-TestsTag([string]$Root) {
  $sourceRoot = Join-Path -Path $Root -ChildPath 'src'
  foreach ($file in [System.IO.Directory]::EnumerateFiles($sourceRoot, '*', [System.IO.SearchOption]::AllDirectories)) {
    $relative = [System.IO.Path]::GetRelativePath($Root, $file).Replace('\', '/')
    if ($script:ScannedExtensions -notcontains [System.IO.Path]::GetExtension($file) -or $relative -match '/(bin|obj)/') { continue }
    Get-TestsTagInText -Text ([System.IO.File]::ReadAllText($file)) -Source $relative
  }
}

# Whether a repository-relative path names an existing file, matching each segment's case exactly.
# Walking real directory listings, rather than asking the file system, is what makes the check
# case-sensitive on macOS and Windows too.
function Test-TestsTagPath([string]$Root, [string]$Path, [hashtable]$Cache) {
  $current = $Root
  foreach ($segment in $Path -split '/') {
    if (-not $Cache.ContainsKey($current)) {
      $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
      if ([System.IO.Directory]::Exists($current)) {
        foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($current)) { [void]$names.Add([System.IO.Path]::GetFileName($entry)) }
      }
      $Cache[$current] = $names
    }
    if (-not $Cache[$current].Contains($segment)) { return $false }
    $current = Join-Path -Path $current -ChildPath $segment
  }
  return [System.IO.File]::Exists($current)
}

# Whether a file's text declares a member or type named Name (see the help for the rule).
function Test-TestsTagMember([string]$Content, [string]$Name) {
  $escaped = [regex]::Escape($Name)
  $type = [regex]"\b(?:class|struct|interface|enum|record)[ \t]+$escaped\b"
  $member = [regex]"(?:(?<word>\w+)|(?<![\s=])[>\])?])[ \t]+$escaped[ \t]*(?:<[^()\r\n]*>)?[ \t]*\("
  # Only the lines that mention the name are examined: an ordinal IndexOf jumps between them, which
  # keeps thousands of lookups over large test files within a second or two.
  $at = $Content.IndexOf($Name, [System.StringComparison]::Ordinal)
  while ($at -ge 0) {
    $start = $Content.LastIndexOf([char]10, $at) + 1
    $end = $Content.IndexOf([char]10, $at)
    if ($end -lt 0) { $end = $Content.Length }
    $code = $Content.Substring($start, $end - $start).Trim()
    $at = $Content.IndexOf($Name, $end, [System.StringComparison]::Ordinal)
    if ($code -match '^(//|/\*|\*)') { continue }
    if ($type.IsMatch($code)) { return $true }
    foreach ($match in $member.Matches($code)) {
      if ($script:CallKeywords -cnotcontains $match.Groups['word'].Value) { return $true }
    }
  }
  return $false
}

# Why a tag is broken, or $null when it resolves.
function Resolve-TestsTag([pscustomobject]$Tag, [string]$Root, [hashtable]$Cache) {
  if (-not $Tag.Closed) { return 'unterminated: a <tests> tag must open and close on one line' }
  if ($Tag.Text -ceq $script:NoTestsPlaceholder) { return $null }
  $target = $script:TargetPattern.Match($Tag.Text)
  if (-not $target.Success) { return 'malformed: expected one repository-relative path, optionally followed by :MethodOrTypeName' }
  $path = $target.Groups['path'].Value
  if (-not (Test-TestsTagPath -Root $Root -Path $path -Cache $Cache.Entries)) { return 'missing file: no file at that repository-relative path (case-sensitive)' }
  $member = $target.Groups['member']
  if (-not $member.Success) { return $null }
  if (-not $Cache.Content.ContainsKey($path)) { $Cache.Content[$path] = [System.IO.File]::ReadAllText((Join-Path -Path $Root -ChildPath $path)) }
  if (Test-TestsTagMember -Content $Cache.Content[$path] -Name $member.Value) { return $null }
  return "missing method: $path declares no method or type named $($member.Value)"
}

# The baseline as a dictionary keyed "source|tag".
function Read-TestsTagBaseline([string]$Path) {
  $entries = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
  # The leading comma keeps the pipeline from unrolling the dictionary on the way out.
  if (-not (Test-Path -LiteralPath $Path)) { return , $entries }
  $number = 0
  foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
    $number++
    if ($line -match '^\s*(#|$)') { continue }
    $parts = @($line -split '\|', 3 | ForEach-Object { $_.Trim() })
    if ($parts.Count -ne 3 -or $parts -contains '') { throw "${Path}:${number}: expected 'source | tag | reason', got '$line'." }
    $key = "$($parts[0])|$($parts[1])"
    if ($entries.ContainsKey($key)) { throw "${Path}:${number}: duplicates line $($entries[$key].Line)." }
    $entries[$key] = [pscustomobject]@{ Source = $parts[0]; Text = $parts[1]; Reason = $parts[2]; Line = $number }
  }
  return , $entries
}

# The broken tags the baseline does not cover, and the baseline entries that cover nothing broken.
function Compare-TestsTagBaseline([object[]]$Tags, [System.Collections.Generic.Dictionary[string, object]]$Baseline) {
  $allKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
  $brokenKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
  # @() around each loop: a loop that emits nothing would otherwise assign $null, a one-element array.
  $unbaselined = @(foreach ($tag in $Tags) {
    $key = "$($tag.Source)|$($tag.Text)"
    [void]$allKeys.Add($key)
    if ($null -eq $tag.Reason) { continue }
    [void]$brokenKeys.Add($key)
    if (-not $Baseline.ContainsKey($key)) { $tag }
  })
  $stale = @(foreach ($entry in $Baseline.Values) {
    $key = "$($entry.Source)|$($entry.Text)"
    if ($brokenKeys.Contains($key)) { continue }
    $why = if ($allKeys.Contains($key)) { 'the tag now resolves; delete the entry' } else { 'no such tag in that file any more; delete the entry' }
    [pscustomobject]@{ Entry = $entry; Why = $why }
  })
  return [pscustomobject]@{ Unbaselined = $unbaselined; Stale = $stale }
}

# Dot-sourcing for tests loads the functions without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$repositoryRoot = [System.IO.Path]::GetFullPath($Root)
$cache = New-TestsTagCache
$tags = @(Find-TestsTag -Root $repositoryRoot)
foreach ($tag in $tags) { $tag.Reason = Resolve-TestsTag -Tag $tag -Root $repositoryRoot -Cache $cache }
$broken = @($tags | Where-Object { $null -ne $_.Reason })
$comparison = Compare-TestsTagBaseline -Tags $tags -Baseline (Read-TestsTagBaseline -Path $BaselinePath)
foreach ($tag in $comparison.Unbaselined) {
  Write-Output "$($tag.Source):$($tag.Line): <tests>$($tag.Text)</tests>: $($tag.Reason)"
}
foreach ($stale in $comparison.Stale) {
  Write-Output "${BaselinePath}:$($stale.Entry.Line): stale entry '$($stale.Entry.Source) | $($stale.Entry.Text)': $($stale.Why)"
}
$placeholders = @($tags | Where-Object { $_.Closed -and $_.Text -ceq $script:NoTestsPlaceholder }).Count
Write-Output ("Checked $($tags.Count) <tests> tags ($placeholders ""$script:NoTestsPlaceholder""): $($broken.Count) broken, " +
  "$($broken.Count - $comparison.Unbaselined.Count) baselined, $($comparison.Stale.Count) stale baseline entries.")
if ($comparison.Unbaselined.Count -gt 0 -or $comparison.Stale.Count -gt 0) { exit 1 }
exit 0
