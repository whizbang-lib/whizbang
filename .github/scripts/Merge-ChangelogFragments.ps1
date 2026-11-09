#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Gathers the changelog fragments in changelog.d/ into a new version section of CHANGELOG.md.

.DESCRIPTION
    A pull request into develop records its changelog entry as a fragment, changelog.d/<issue>.md, so two pull
    requests never edit the same lines (changelog.d/README.md describes the format). Cutting a release runs this
    on the release branch (start-release.yml), and the result lands in the same signed commit as the version bump.

    It renders every fragment, together with whatever is still under ## [Unreleased] in CHANGELOG.md, into
    ## [<Version>] - <Date> directly below an emptied ## [Unreleased] heading, and deletes exactly the fragment
    files it read. Entries are ordered by category (Added, Changed, Deprecated, Removed, Fixed, Security), then
    subsection (entries with none first, then the documented order), then fragment number; entries that were
    already under [Unreleased] come first within their group, in their existing order.

    Every fragment, and every heading under [Unreleased], is checked before anything is written: an unknown
    category or subsection, a malformed fragment or a misnamed file fails the run, naming the file, and changes
    nothing.

    Exit 0 on success, 1 on any refusal (printed as an ::error:: annotation).

.PARAMETER Version
    The version the section is for, as the release branch names it (X.Y.Z).

.PARAMETER Date
    The section's date, yyyy-MM-dd. Default: today (UTC).

.PARAMETER Root
    The repository root holding CHANGELOG.md and changelog.d/. Default: this script's repository.

.PARAMETER DryRun
    Print the section the gather would write, and change nothing.

.EXAMPLE
    pwsh .github/scripts/Merge-ChangelogFragments.ps1 -Version 0.3301.0 -DryRun

.EXAMPLE
    pwsh .github/scripts/Merge-ChangelogFragments.ps1 -Version 0.3301.0
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Version,
  [string]$Date = [DateTime]::UtcNow.ToString('yyyy-MM-dd'),
  [string]$Root = (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath '..'),
  [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The headings a fragment may name, in the order the section renders them. changelog.d/README.md lists the same
# two sets, and a test fails when the two differ.
$script:ChangelogCategories = @('Added', 'Changed', 'Deprecated', 'Removed', 'Fixed', 'Security')
$script:ChangelogSubsections = @(
  'Security', 'Rebuilds', 'Storage', 'Audit', 'Startup', 'Messaging & delivery',
  'Work coordination, perspectives & sagas', 'Context, identity & serialization', 'Observability & tooling'
)
$script:FragmentNamePattern = '^(?<number>\d+)(?:-\d+)*\.md$'

function Resolve-ChangelogHeading {
  <# .SYNOPSIS The documented spelling of a category or subsection, matched without regard to case; throws when unknown. #>
  param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Kind, [Parameter(Mandatory)][string]$Value)
  $known = if ($Kind -eq 'category') { $script:ChangelogCategories } else { $script:ChangelogSubsections }
  $match = @($known | Where-Object { $_ -ieq $Value.Trim() })
  if ($match.Count -eq 0) {
    throw "${Source}: unknown $Kind '$($Value.Trim())'. Use one of: $($known -join ', ') (changelog.d/README.md)."
  }
  return $match[0]
}

function Split-ChangelogLine {
  <# .SYNOPSIS The lines of a text, whatever its line endings, without a final empty line. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
  $lines = @($Text -split "\r?\n")
  if ($lines.Count -gt 0 -and $lines[-1] -eq '') { $lines = @($lines | Select-Object -SkipLast 1) }
  return , $lines
}

function Get-TrimmedBlock {
  <# .SYNOPSIS The lines without their leading and trailing blank lines. #>
  param([AllowEmptyCollection()][string[]]$Lines = @())
  $first = 0
  $last = $Lines.Count - 1
  while ($first -le $last -and -not $Lines[$first].Trim()) { $first++ }
  while ($last -ge $first -and -not $Lines[$last].Trim()) { $last-- }
  if ($first -gt $last) { return , @() }
  return , @($Lines[$first..$last])
}

function Read-ChangelogFragment {
  <#
  .SYNOPSIS
      One fragment: Name, Path, Number (the first number in its name), Category, Subsection ('' for none) and
      Body (the entry lines). Throws, naming the file, when it is not a fragment the gather can render.
  #>
  param([Parameter(Mandatory)][string]$Path)
  $name = Split-Path -Leaf $Path
  $source = "changelog.d/$name"
  $nameMatch = [regex]::Match($name, $script:FragmentNamePattern)
  if (-not $nameMatch.Success) {
    throw "${source}: a fragment is named by its issue or pull request number, <number>.md (or <first>-<last>.md for several)."
  }
  $lines = Split-ChangelogLine -Text (Get-Content -Raw -LiteralPath $Path)
  $close = if ($lines.Count -gt 0 -and $lines[0].Trim() -eq '---') { [array]::IndexOf([string[]]@($lines | ForEach-Object Trim), '---', 1) } else { -1 }
  if ($close -lt 1) {
    throw "${source}: a fragment starts with front matter between two '---' lines naming its category (changelog.d/README.md)."
  }
  $category = ''
  $subsection = ''
  foreach ($line in @($lines | Select-Object -Skip 1 -First ($close - 1) | Where-Object { $_.Trim() })) {
    $pair = [regex]::Match($line, '^\s*(?<key>[A-Za-z]+)\s*:\s*(?<value>.*?)\s*$')
    if (-not $pair.Success) { throw "${source}: the front matter line '$line' is not key: value." }
    switch ($pair.Groups['key'].Value.ToLowerInvariant()) {
      'category' { $category = Resolve-ChangelogHeading -Source $source -Kind category -Value $pair.Groups['value'].Value }
      'subsection' { $subsection = Resolve-ChangelogHeading -Source $source -Kind subsection -Value $pair.Groups['value'].Value }
      default { throw "${source}: unknown key '$($pair.Groups['key'].Value)' in the front matter; the keys are category and subsection." }
    }
  }
  if (-not $category) { throw "${source}: the front matter names no category." }
  $body = Get-TrimmedBlock -Lines @($lines | Select-Object -Skip ($close + 1))
  if ($body.Count -eq 0) { throw "${source}: the fragment holds no entry after its front matter." }
  if (-not $body[0].StartsWith('- ')) { throw "${source}: the entry is a markdown list item, starting with '- '." }
  $heading = @($body | Where-Object { $_ -match '^#{1,6}\s' })
  if ($heading.Count -gt 0) {
    throw "${source}: the entry holds a heading ('$($heading[0])'); the gather writes the headings from the front matter."
  }
  return [pscustomobject]@{
    Name = $name; Path = $Path; Number = [long]$nameMatch.Groups['number'].Value
    Category = $category; Subsection = $subsection; Body = $body
  }
}

function Get-ChangelogFragment {
  <# .SYNOPSIS Every fragment in a changelog.d directory, in number order; README.md is not a fragment. #>
  param([Parameter(Mandatory)][string]$Directory)
  if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return , @() }
  $files = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object Name -NE 'README.md')
  $fragments = @($files | ForEach-Object { Read-ChangelogFragment -Path $_.FullName })
  return , @($fragments | Sort-Object -Property Number, @{ Expression = { $_.Name }; Ascending = $true })
}

function ConvertFrom-UnreleasedSection {
  <#
  .SYNOPSIS
      The content under [Unreleased]: Preamble (lines before the first category) and Groups, a dictionary keyed
      "<category>|<subsection>" of the lines in each. Throws on an unknown heading.
  #>
  param([AllowEmptyCollection()][string[]]$Lines = @())
  $preamble = [System.Collections.Generic.List[string]]::new()
  $groups = [ordered]@{}
  $key = $null
  $category = $null
  foreach ($line in $Lines) {
    if ($line -match '^###\s+(?<name>.+?)\s*$' -and $line -notmatch '^####') {
      $category = Resolve-ChangelogHeading -Source 'CHANGELOG.md [Unreleased]' -Kind category -Value $Matches['name']
      $key = "$category|"
    } elseif ($line -match '^####\s+(?<name>.+?)\s*$') {
      if (-not $category) { throw "CHANGELOG.md [Unreleased]: the subsection '$($Matches['name'])' is not under a category." }
      $key = "$category|$(Resolve-ChangelogHeading -Source 'CHANGELOG.md [Unreleased]' -Kind subsection -Value $Matches['name'])"
    } elseif ($null -eq $key) {
      $preamble.Add($line)
      continue
    }
    if (-not $groups.Contains($key)) { $groups[$key] = [System.Collections.Generic.List[string]]::new() }
    if ($line -notmatch '^###') { $groups[$key].Add($line) }
  }
  return [pscustomobject]@{ Preamble = (Get-TrimmedBlock -Lines $preamble.ToArray()); Groups = $groups }
}

function Format-ChangelogSection {
  <# .SYNOPSIS The lines of the version section, from the [Unreleased] content and the fragments. #>
  param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Date,
    [Parameter(Mandatory)]$Unreleased,
    [AllowEmptyCollection()][object[]]$Fragments = @()
  )
  $out = [System.Collections.Generic.List[string]]::new()
  $out.Add("## [$Version] - $Date")
  if ($Unreleased.Preamble.Count -gt 0) { $out.Add(''); $out.AddRange([string[]]$Unreleased.Preamble) }
  $any = $Unreleased.Preamble.Count -gt 0
  foreach ($category in $script:ChangelogCategories) {
    $blocks = [System.Collections.Generic.List[object]]::new()
    foreach ($subsection in @('') + $script:ChangelogSubsections) {
      $key = "$category|$subsection"
      $lines = [System.Collections.Generic.List[string]]::new()
      if ($Unreleased.Groups.Contains($key)) { $lines.AddRange([string[]](Get-TrimmedBlock -Lines $Unreleased.Groups[$key].ToArray())) }
      foreach ($fragment in @($Fragments | Where-Object { $_.Category -eq $category -and $_.Subsection -eq $subsection })) {
        $lines.AddRange([string[]]$fragment.Body)
      }
      if ($lines.Count -gt 0) { $blocks.Add([pscustomobject]@{ Subsection = $subsection; Lines = $lines.ToArray() }) }
    }
    if ($blocks.Count -eq 0) { continue }
    $any = $true
    $out.Add(''); $out.Add("### $category")
    foreach ($block in $blocks) {
      $out.Add('')
      if ($block.Subsection) { $out.Add("#### $($block.Subsection)") }
      $out.AddRange([string[]]$block.Lines)
    }
  }
  if (-not $any) { $out.Add(''); $out.Add('No notable changes.') }
  return , $out.ToArray()
}

function Merge-ChangelogFragments {
  <#
  .SYNOPSIS
      Gathers the fragments and the [Unreleased] content into the version's section. Returns Section (its lines),
      Gathered (the fragment paths read) and LeftoverLineCount (non-blank lines that were under [Unreleased]).
      Without -DryRun it writes CHANGELOG.md and then deletes exactly the gathered fragments.
  #>
  param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Date,
    [Parameter(Mandatory)][string]$Root,
    [switch]$DryRun
  )
  if ($Date -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "The date '$Date' is not yyyy-MM-dd." }
  $changelogPath = Join-Path $Root 'CHANGELOG.md'
  $lines = Split-ChangelogLine -Text (Get-Content -Raw -LiteralPath $changelogPath)
  $start = -1
  for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## \[Unreleased\]\s*$') { $start = $i; break } }
  if ($start -lt 0) { throw 'CHANGELOG.md has no ## [Unreleased] heading to gather under.' }
  $end = $lines.Count
  for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { $end = $i; break } }
  $escaped = [regex]::Escape($Version)
  if (@($lines | Where-Object { $_ -match "^## \[$escaped\]" }).Count -gt 0) {
    throw "CHANGELOG.md: $Version already has a section; a version is gathered once."
  }

  $leftover = if ($end -gt $start + 1) { @($lines[($start + 1)..($end - 1)]) } else { @() }
  $unreleased = ConvertFrom-UnreleasedSection -Lines $leftover
  $fragments = Get-ChangelogFragment -Directory (Join-Path $Root 'changelog.d')
  $section = Format-ChangelogSection -Version $Version -Date $Date -Unreleased $unreleased -Fragments $fragments
  $result = [pscustomobject]@{
    Section = $section
    Gathered = @($fragments | ForEach-Object Path)
    LeftoverLineCount = @($leftover | Where-Object { $_.Trim() -and $_ -notmatch '^###' }).Count
  }
  if ($DryRun) { return $result }

  $before = if ($start -gt 0) { @($lines[0..($start - 1)]) } else { @() }
  $after = if ($end -lt $lines.Count) { @($lines[$end..($lines.Count - 1)]) } else { @() }
  $written = @($before) + @('## [Unreleased]', '') + @($section)
  if ($after.Count -gt 0) { $written += @('') + $after }
  [IO.File]::WriteAllText($changelogPath, (($written -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
  foreach ($path in $result.Gathered) { Remove-Item -LiteralPath $path }
  return $result
}

# Dot-sourcing for tests loads the functions without gathering anything.
if ($MyInvocation.InvocationName -eq '.') { return }

try {
  $result = Merge-ChangelogFragments -Version $Version -Date $Date -Root $Root -DryRun:$DryRun
} catch {
  Write-Output "::error::$($_.Exception.Message)"
  exit 1
}
$verb = if ($DryRun) { 'Would gather' } else { 'Gathered' }
Write-Output "$verb $($result.Gathered.Count) fragment(s) and $($result.LeftoverLineCount) line(s) from [Unreleased] into ## [$Version]:"
Write-Output ''
$result.Section | Write-Output
if ($env:GITHUB_STEP_SUMMARY -and -not $DryRun) {
  @("### Changelog: $($result.Gathered.Count) fragment(s) gathered", '', '```markdown') + $result.Section + @('```') |
    Add-Content -Path $env:GITHUB_STEP_SUMMARY
}
exit 0
