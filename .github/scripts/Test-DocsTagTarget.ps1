#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Fails when a <docs> tag names a docs-site page that does not exist, or an anchor that page does not
    have.

.DESCRIPTION
    Library members carry <docs> tags (/// in C#, -- in SQL migrations) that link each member to its page
    on the docs site, and the site's code-to-docs map records whatever a tag says. Nothing checked that the
    page was there, so a renamed or never-written page left the tag sending readers to a 404. This guard
    reads the docs repository (whizbang-lib/whizbang-lib.github.io, develop by default) and checks every
    tag against it.

    What is scanned: every .cs and .sql file under src/, tests/, samples/ and tools/, except bin/ and obj/
    output. Markdown is not scanned: the Markdown files that mention a tag show the convention with
    placeholder targets, which are not links. A tag counts when a comment marker (// or --) comes before
    it on its line: a tag in a comment, and one in a string a generator emits as a comment
    ("/// <docs>...</docs>" written into consumer code), are links. A regex that matches the tag form,
    or a test message that interpolates one ("<docs>{tag}</docs>"), names no page and is not checked.

    A tag's text is "path" or "path#anchor": path segments of letters, digits, '.', '_' and '-' joined by
    single slashes, without the docs version (the site serves the current version), and an anchor with
    no whitespace and no second '#'. Anything else (a placeholder such as {tag}, a URL, a rooted path, a
    . or .. segment, an empty segment or anchor, whitespace) is malformed. A tag must open and close on
    one line.

    How a path finds its page, as the site's router does (src/app/pages/docs/markdown.page.ts): under
    src/assets/docs, in each version folder (v1.0.0 and so on, newest first) and then at the root (for
    contributors/, proposals/ and the other state folders), the first of <path>.md, <path>/_folder.md
    and <path>/<last segment>.md. Matching is case-sensitive. A page that exists only under drafts/ does
    not answer a tag without the drafts/ prefix, because the site does not serve it there.

    How an anchor is found, as the site's header processor does
    (src/app/services/header-processor.service.ts): each Markdown heading line outside a fenced code
    block gets the id given by a trailing {#id}, or else a slug of its text (lowercased, characters
    other than ASCII letters, digits, '_', whitespace and '-' removed, whitespace runs turned into '-',
    '-' runs collapsed, one leading and one trailing '-' removed, "header" when nothing is left), and a
    slug already used on the page gets -1, -2 and so on. The id or name attribute of an HTML element in
    the page outside a code block (<a id="audit"></a>) is an anchor too. Matching is case-sensitive.

    The baseline (docs-tag-target-baseline.txt next to this script) lists the targets that were already
    dangling when the guard was added, one per line as "target | reason"; # starts a comment. A tag whose
    target is baselined is not reported, so the guard blocks every new dangling target while the old ones
    are fixed. An entry that no tag names any more is stale and fails the run, so a change here that
    drops a tag also drops its entry. An entry whose target now resolves, because a page or heading was
    added on the docs side, is reported as a warning only: no pull request here caused it, so it must not
    fail one. Delete such an entry when the warning appears. Never add an entry to get a change through; point the tag at a page that exists.

    Exits 0 when every target resolves or is baselined and no baseline entry is unused (resolved entries
    only warn); 1 listing each unbaselined dangling tag (file, line, tag and why) and each unused entry, or when no tag is found
    at all; 2 when the docs repository cannot be fetched or holds no pages, so a failed fetch is never a
    pass. Dot-source the script to load its functions without running the check.

.PARAMETER Root
    The repository root. Default: two levels above this script.

.PARAMETER BaselinePath
    The baseline file. Default: docs-tag-target-baseline.txt next to this script. A missing file is an
    empty baseline.

.PARAMETER DocsRoot
    An existing checkout of the docs repository to check against. When omitted, DocsRepository is cloned
    (shallow, src/assets/docs only) into a temporary folder that is removed afterwards.

.PARAMETER DocsRepository
    The docs repository to clone. Default: https://github.com/whizbang-lib/whizbang-lib.github.io.git.

.PARAMETER DocsRef
    The branch of the docs repository to clone. Default: develop.

.EXAMPLE
    pwsh .github/scripts/Test-DocsTagTarget.ps1

.EXAMPLE
    pwsh .github/scripts/Test-DocsTagTarget.ps1 -DocsRoot ../whizbang-lib.github.io
#>

param(
    [Parameter()] [string]$Root = (Join-Path -Path $PSScriptRoot -ChildPath '../..'),
    [Parameter()] [string]$BaselinePath = (Join-Path -Path $PSScriptRoot -ChildPath 'docs-tag-target-baseline.txt'),
    [Parameter()] [string]$DocsRoot,
    [Parameter()] [string]$DocsRepository = 'https://github.com/whizbang-lib/whizbang-lib.github.io.git',
    [Parameter()] [string]$DocsRef = 'develop'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ScannedFolders = @('src', 'tests', 'samples', 'tools')
$script:ScannedExtensions = @('.cs', '.sql')
$script:DocsFolder = 'src/assets/docs'
# A tag that closes on its own line captures "text"; one that does not captures the rest of the line as
# "open", so an unterminated tag is reported instead of silently skipped.
$script:TagPattern = [regex]'<docs>(?:(?<text>[^\r\n]*?)</docs>|(?<open>[^\r\n]*))'
$script:TargetPattern = '^(?<path>[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*)(?:#(?<anchor>[^\s#]+))?$'
$script:VersionFolderPattern = '^v\d+(\.\d+){0,3}$'
# The site's header regex, applied to the whole page with fenced code blocks removed.
$script:HeadingPattern = [regex]::new('^(#{1,6})\s+(.+?)(\s*\{#([^}]+)\})?$', [System.Text.RegularExpressions.RegexOptions]::Multiline)
$script:FencePattern = [regex]'^ {0,3}(?<fence>```|~~~)'
# An HTML element's id or name in the page, such as <a id="audit"></a>, is an anchor too.
$script:HtmlAnchorPattern = [regex]'<[A-Za-z][^<>]*?\s(?:id|name)\s*=\s*(?:"(?<id>[^"]+)"|''(?<id>[^'']+)'')'

# Every <docs> tag in a file's text, with its 1-based line.
function Get-DocsTagInText([string]$Text, [string]$Source) {
  $line = 1
  $from = 0
  foreach ($match in $script:TagPattern.Matches($Text)) {
    # Counting newlines only since the previous tag keeps the scan linear in the file's length.
    $line += $Text.Substring($from, $match.Index - $from).Split([char]10).Length - 1
    $from = $match.Index
    # Only a tag after a comment marker on its line is a link: in a comment, or in a string a generator
    # emits as one. A regex or a message that mentions the tag form is not.
    $lineStart = if ($match.Index -eq 0) { 0 } else { $Text.LastIndexOf([char]10, $match.Index - 1) + 1 }
    $before = $Text.Substring($lineStart, $match.Index - $lineStart)
    if (-not ($before.Contains('//') -or $before.Contains('--'))) { continue }
    $closed = $match.Groups['text'].Success
    $inner = if ($closed) { $match.Groups['text'].Value } else { $match.Groups['open'].Value }
    [pscustomobject]@{ Source = $Source; Line = $line; Text = $inner.Trim(); Closed = $closed; Reason = $null }
  }
}

# Every <docs> tag in the scanned source files.
function Find-DocsTag([string]$Root) {
  foreach ($folder in $script:ScannedFolders) {
    $scanRoot = Join-Path -Path $Root -ChildPath $folder
    if (-not [System.IO.Directory]::Exists($scanRoot)) { continue }
    foreach ($file in [System.IO.Directory]::EnumerateFiles($scanRoot, '*', [System.IO.SearchOption]::AllDirectories)) {
      $relative = [System.IO.Path]::GetRelativePath($Root, $file).Replace('\', '/')
      if ($script:ScannedExtensions -notcontains [System.IO.Path]::GetExtension($file) -or $relative -match '/(bin|obj)/') { continue }
      Get-DocsTagInText -Text ([System.IO.File]::ReadAllText($file)) -Source $relative
    }
  }
}

# The ids the site gives the headings of one Markdown page.
function Get-DocsPageAnchor([string]$Markdown) {
  $kept = [System.Collections.Generic.List[string]]::new()
  $fence = $null
  foreach ($line in ($Markdown -replace "`r`n?", "`n").Split([char]10)) {
    $open = $script:FencePattern.Match($line)
    if ($null -eq $fence -and $open.Success) { $fence = $open.Groups['fence'].Value; $kept.Add(''); continue }
    if ($null -ne $fence) {
      if ($open.Success -and $open.Groups['fence'].Value -eq $fence) { $fence = $null }
      $kept.Add('')
      continue
    }
    $kept.Add($line)
  }
  $ids = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
  foreach ($heading in $script:HeadingPattern.Matches(($kept -join "`n"))) {
    if ($heading.Groups[4].Success) { [void]$ids.Add($heading.Groups[4].Value); continue }
    $slug = $heading.Groups[2].Value.Trim().ToLowerInvariant() -creplace '[^A-Za-z0-9_\s-]', '' -creplace '\s+', '-' -creplace '-+', '-' -creplace '^-|-$', ''
    if (-not $slug) { $slug = 'header' }
    $unique = $slug
    $counter = 1
    while ($ids.Contains($unique)) { $unique = "$slug-$counter"; $counter++ }
    [void]$ids.Add($unique)
  }
  foreach ($element in $script:HtmlAnchorPattern.Matches(($kept -join "`n"))) { [void]$ids.Add($element.Groups['id'].Value) }
  # The leading comma keeps the pipeline from unrolling the set on the way out.
  return , $ids
}

# The pages of a docs repository checkout: every file under src/assets/docs, the version folders newest
# first, and a cache of each page's anchors.
function Read-DocsSite([string]$DocsRoot) {
  $docs = Join-Path -Path $DocsRoot -ChildPath $script:DocsFolder
  $files = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
  if ([System.IO.Directory]::Exists($docs)) {
    foreach ($file in [System.IO.Directory]::EnumerateFiles($docs, '*.md', [System.IO.SearchOption]::AllDirectories)) {
      [void]$files.Add([System.IO.Path]::GetRelativePath($docs, $file).Replace('\', '/'))
    }
  }
  if ($files.Count -eq 0) { throw "no pages under $script:DocsFolder in '$DocsRoot'; the docs checkout is empty or not the docs repository." }
  $versions = @([System.IO.Directory]::EnumerateDirectories($docs) | ForEach-Object { [System.IO.Path]::GetFileName($_) } |
      Where-Object { $_ -match $script:VersionFolderPattern } | Sort-Object -Descending -Property { [version]($_.Substring(1) + ('.0' * (3 - ($_.Split('.').Length - 1)))) })
  return [pscustomobject]@{
    Root     = $docs
    Files    = $files
    Versions = $versions
    Anchors  = [hashtable]::new([System.StringComparer]::Ordinal)
  }
}

# Why a target dangles, or $null when its page (and anchor) exist.
function Resolve-DocsTagTarget([string]$Target, [pscustomobject]$Site) {
  $parsed = [regex]::Match($Target, $script:TargetPattern)
  if (-not $parsed.Success -or ($parsed.Groups['path'].Value -split '/' | Where-Object { $_ -eq '.' -or $_ -eq '..' })) {
    return 'malformed: expected a docs path (segments of letters, digits, ".", "_" or "-"), optionally followed by #anchor'
  }
  $path = $parsed.Groups['path'].Value
  $leaf = ($path -split '/')[-1]
  $page = $null
  foreach ($base in @($Site.Versions | ForEach-Object { "$_/" }) + '') {
    $page = @("$base$path.md", "$base$path/_folder.md", "$base$path/$leaf.md") | Where-Object { $Site.Files.Contains($_) } | Select-Object -First 1
    if ($page) { break }
  }
  if (-not $page) { return "missing page: no $path.md, $path/_folder.md or $path/$leaf.md under $script:DocsFolder, in a version folder or at the root (case-sensitive)" }
  $anchor = $parsed.Groups['anchor']
  if (-not $anchor.Success) { return $null }
  if (-not $Site.Anchors.ContainsKey($page)) {
    $Site.Anchors[$page] = Get-DocsPageAnchor -Markdown ([System.IO.File]::ReadAllText((Join-Path -Path $Site.Root -ChildPath $page)))
  }
  if ($Site.Anchors[$page].Contains($anchor.Value)) { return $null }
  return "missing anchor: $script:DocsFolder/$page has no heading with id '$($anchor.Value)' (a slug of its text or an explicit {#id})"
}

# The baseline as a dictionary keyed by target.
function Read-DocsTagBaseline([string]$Path) {
  $entries = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
  if (-not (Test-Path -LiteralPath $Path)) { return , $entries }
  $number = 0
  foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
    $number++
    if ($line -match '^\s*(#|$)') { continue }
    $parts = @($line -split '\|', 2 | ForEach-Object { $_.Trim() })
    if ($parts.Count -ne 2 -or $parts -contains '') { throw "${Path}:${number}: expected 'target | reason', got '$line'." }
    if ($entries.ContainsKey($parts[0])) { throw "${Path}:${number}: duplicates line $($entries[$parts[0]].Line)." }
    $entries[$parts[0]] = [pscustomobject]@{ Text = $parts[0]; Reason = $parts[1]; Line = $number }
  }
  return , $entries
}

# The dangling tags the baseline does not cover, and the baseline entries that cover nothing dangling.
function Compare-DocsTagBaseline([object[]]$Tags, [System.Collections.Generic.Dictionary[string, object]]$Baseline) {
  $all = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
  $dangling = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
  # @() around each loop: a loop that emits nothing would otherwise assign $null.
  $unbaselined = @(foreach ($tag in $Tags) {
    [void]$all.Add($tag.Text)
    if ($null -eq $tag.Reason) { continue }
    [void]$dangling.Add($tag.Text)
    if (-not $Baseline.ContainsKey($tag.Text)) { $tag }
  })
  $stale = @(foreach ($entry in $Baseline.Values) {
    if ($dangling.Contains($entry.Text)) { continue }
    $resolved = $all.Contains($entry.Text)
    $why = if ($resolved) { 'the target now resolves; delete the entry' } else { 'no tag names that target any more; delete the entry' }
    [pscustomobject]@{ Entry = $entry; Resolved = $resolved; Why = $why }
  })
  return [pscustomobject]@{ Unbaselined = $unbaselined; Stale = $stale; Dangling = $dangling }
}

# A shallow clone of one branch of the docs repository, with only the docs pages checked out. Throws
# with git's own output when any step fails.
function Copy-DocsRepository([string]$Repository, [string]$Ref, [string]$Destination) {
  $steps = @(
    , @('clone', '--quiet', '--depth', '1', '--single-branch', '--branch', $Ref, '--sparse', $Repository, $Destination)
    , @('-C', $Destination, 'sparse-checkout', 'set', $script:DocsFolder)
  )
  foreach ($arguments in $steps) {
    $output = & git @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($arguments -join ' ') exited $LASTEXITCODE`: $($output -join ' ')" }
  }
}

# Dot-sourcing for tests loads the functions without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$repositoryRoot = [System.IO.Path]::GetFullPath($Root)
$clone = $null
try {
  try {
    if (-not $DocsRoot) {
      $clone = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath "whizbang-docs-$([guid]::NewGuid().ToString('N'))"
      Copy-DocsRepository -Repository $DocsRepository -Ref $DocsRef -Destination $clone
      $DocsRoot = $clone
    }
    $site = Read-DocsSite -DocsRoot $DocsRoot
  } catch {
    $what = if ($clone) { "could not fetch the docs repository $DocsRepository ($DocsRef)" } else { "could not read the docs repository at $DocsRoot" }
    Write-Output "${what}: $($_.Exception.Message)"
    Write-Output 'The <docs> targets cannot be checked without it, so the check fails rather than passing unchecked.'
    exit 2
  }

  $tags = @(Find-DocsTag -Root $repositoryRoot)
  if ($tags.Count -eq 0) {
    Write-Output "Found no <docs> tag under $($script:ScannedFolders -join ', ') in $repositoryRoot; a scan that finds nothing cannot pass."
    exit 1
  }
  $reasons = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
  foreach ($tag in $tags) {
    if (-not $tag.Closed) { $tag.Reason = 'unterminated: a <docs> tag must open and close on one line'; continue }
    if (-not $reasons.ContainsKey($tag.Text)) { $reasons[$tag.Text] = Resolve-DocsTagTarget -Target $tag.Text -Site $site }
    $tag.Reason = $reasons[$tag.Text]
  }
  $baseline = Read-DocsTagBaseline -Path $BaselinePath
  $comparison = Compare-DocsTagBaseline -Tags $tags -Baseline $baseline
  foreach ($tag in $comparison.Unbaselined) {
    Write-Output "$($tag.Source):$($tag.Line): <docs>$($tag.Text)</docs>: $($tag.Reason)"
  }
  foreach ($stale in $comparison.Stale) {
    if ($stale.Resolved) {
      # A docs-side change made it resolve: no pull request here caused it, so it must not fail one. Warn until deleted.
      Write-Output "::warning file=${BaselinePath},line=$($stale.Entry.Line)::stale entry '$($stale.Entry.Text)': $($stale.Why)"
    } else {
      Write-Output "${BaselinePath}:$($stale.Entry.Line): stale entry '$($stale.Entry.Text)': $($stale.Why)"
    }
  }
  $distinct = @($tags | ForEach-Object Text | Sort-Object -Unique -CaseSensitive).Count
  $baselined = @($comparison.Dangling | Where-Object { $baseline.ContainsKey($_) }).Count
  Write-Output ("Checked $($tags.Count) <docs> tags ($distinct distinct targets): $($comparison.Dangling.Count) dangling, " +
    "$baselined baselined, $($comparison.Stale.Count) stale baseline entries.")
  $unused = @($comparison.Stale | Where-Object { -not $_.Resolved })
  if ($comparison.Unbaselined.Count -gt 0 -or $unused.Count -gt 0) { exit 1 }
  exit 0
} finally {
  if ($clone -and (Test-Path -LiteralPath $clone)) { Remove-Item -LiteralPath $clone -Recurse -Force }
}
