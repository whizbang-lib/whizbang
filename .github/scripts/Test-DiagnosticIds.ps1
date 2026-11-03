#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Fails when a build diagnostic ID is outside the one ID space, means two things, or is missing from
    the registry.

.DESCRIPTION
    Every diagnostic in the repository, shipped or repository-internal, takes an ID of the form WHIZ
    and three digits, listed once in diagnostic-ids.txt at the repository root with the project that
    defines it and what it means. The next free number is taken from that file.

    The check reads every DiagnosticDescriptor construction under src/ and tools/ (fields and
    descriptors built inline in a generator, named or positional arguments) and every MSBuild
    <Error Code> or <Warning Code> in Directory.Build.targets and Directory.Build.props. It fails on:

    - one ID with two different titles, unless it is listed in $AllowedSharedIds with the reason the
      meaning is the same (a NoWarn or #pragma for one would silently suppress the other);
    - a construction site whose ID and title are not literals, so the scan cannot cover fewer
      descriptors than exist;
    - an ID outside ^WHIZ\d{3}$, unless the registry marks it legacy (shipped before the single form;
      a frozen set that only shrinks);
    - an ID defined but not registered, registered but not defined, or registered under a project that
      does not define it;
    - a retired ID defined again, or a registry's retired set that differs from
      DiagnosticDescriptors.RETIRED_IDS.

    Run by the "Test · Pipeline scripts" job through its Pester test. Exit 0 on pass, 1 on fail.

.PARAMETER Root
    The repository root. Defaults to two levels above this script.
#>
[CmdletBinding()]
param(
  [string]$Root = (Join-Path $PSScriptRoot '..' '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# IDs two packages define on purpose with one meaning. Each entry says why.
$script:AllowedSharedIds = @{
  WHIZ070 = 'Both generator packages report a vector field whose project lacks Pgvector.EntityFrameworkCore; the titles differ only in wording.'
}

$script:ConstructionPattern = 'DiagnosticDescriptor\s+\w+\s*=\s*new\s*\(|new\s+DiagnosticDescriptor\s*\('
# The title is a string literal or a named constant (several generators share one error title).
$script:IdPattern = '^WHIZ\d{3}$'
$script:Statuses = @('active', 'retired', 'legacy')
$script:MSBuildFiles = @('Directory.Build.targets', 'Directory.Build.props')
$script:IdTitlePattern = '^\s*(?:id:\s*)?"(?<id>[A-Z]+[0-9]+)"\s*,\s*(?:title:\s*)?(?:"(?<title>(?:[^"\\]|\\.)*)"|(?<title>[A-Za-z_][\w.]*)\s*[,)])'

function Get-DiagnosticDescriptor {
  <# .SYNOPSIS Every descriptor constructed in one file's text: Id, Title, Path, Line (Id null when unreadable). #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [Parameter(Mandatory)][string]$Path)
  foreach ($site in [regex]::Matches($Text, $script:ConstructionPattern)) {
    $after = $Text.Substring($site.Index + $site.Length)
    $match = [regex]::Match($after, $script:IdTitlePattern)
    $line = ($Text.Substring(0, $site.Index) -split "`n").Count
    [pscustomobject]@{
      Id    = if ($match.Success) { $match.Groups['id'].Value } else { $null }
      Title = if ($match.Success) { $match.Groups['title'].Value } else { $null }
      Path  = $Path
      Line  = $line
    }
  }
}

function Find-DiagnosticIdConflict {
  <# .SYNOPSIS One message per ID with more than one meaning, and per construction site that could not be read. #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Descriptors, [Parameter(Mandatory)][hashtable]$Allowed)
  foreach ($d in @($Descriptors | Where-Object { -not $_.Id })) {
    "A diagnostic descriptor at $($d.Path):$($d.Line) does not start with a literal ID and title, so this check cannot read it."
  }
  $groups = @($Descriptors | Where-Object { $_.Id } | Group-Object -Property Id | Sort-Object -Property Name)
  foreach ($group in $groups) {
    $titles = @($group.Group | Select-Object -ExpandProperty Title -Unique)
    if ($titles.Count -lt 2 -or $Allowed.ContainsKey($group.Name)) { continue }
    $where = ($group.Group | ForEach-Object { "$($_.Path):$($_.Line) `"$($_.Title)`"" }) -join '; '
    "$($group.Name) means more than one thing: $where"
  }
}

function Get-MSBuildDiagnosticCode {
  <# .SYNOPSIS Every <Error Code> and <Warning Code> in one MSBuild file's text, titled by its target. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [Parameter(Mandatory)][string]$Path)
  $targets = @([regex]::Matches($Text, '<Target\s+Name="(?<name>[^"]+)"'))
  foreach ($site in [regex]::Matches($Text, '<(?:Error|Warning)\b[^>]*?\bCode="(?<id>[^"]+)"')) {
    $target = $targets | Where-Object Index -lt $site.Index | Select-Object -Last 1
    $name = if ($target) { $target.Groups['name'].Value } else { '(outside a target)' }
    [pscustomobject]@{
      Id    = $site.Groups['id'].Value
      Title = "MSBuild target $name"
      Path  = $Path
      Line  = ($Text.Substring(0, $site.Index) -split "`n").Count
    }
  }
}

function Get-DiagnosticOwner {
  <# .SYNOPSIS The project that defines a diagnostic: the folder under src/ or tools/, or a root MSBuild file. #>
  param([Parameter(Mandatory)][string]$Path)
  $parts = $Path -split '/'
  if ($parts.Count -gt 2 -and $parts[0] -in @('src', 'tools')) { return $parts[1] }
  $Path
}

function Get-RetiredDiagnosticId {
  <# .SYNOPSIS The IDs in DiagnosticDescriptors.RETIRED_IDS, read from its source text. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
  $list = [regex]::Match($Text, 'RETIRED_IDS\s*=\s*\[(?<ids>[^\]]*)\]')
  if (-not $list.Success) { throw 'DiagnosticDescriptors.RETIRED_IDS was not found, so the retired IDs cannot be checked.' }
  [regex]::Matches($list.Groups['ids'].Value, '"(?<id>[^"]+)"') | ForEach-Object { $_.Groups['id'].Value }
}

function Read-DiagnosticIdRegistry {
  <#
  .SYNOPSIS Rows of diagnostic-ids.txt: "id | status | owner | meaning"; # starts a comment.
  .DESCRIPTION A row that does not have four fields or a known status comes back with no Id, so the check names its line.
  #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [Parameter(Mandatory)][string]$Path)
  $lines = $Text -split "`r?`n"
  for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i].Trim()
    if ($line -eq '' -or $line.StartsWith('#')) { continue }
    $fields = @($line -split '\|' | ForEach-Object { $_.Trim() })
    $ok = $fields.Count -eq 4 -and $fields[1] -in $script:Statuses -and $fields[0] -ne ''
    [pscustomobject]@{
      Id      = if ($ok) { $fields[0] } else { $null }
      Status  = if ($ok) { $fields[1] } else { $null }
      Owner   = if ($ok) { $fields[2] } else { $null }
      Meaning = if ($ok) { $fields[3] } else { $null }
      Line    = $i + 1
    }
  }
}

function Find-DiagnosticIdRegistryProblem {
  <# .SYNOPSIS One message per disagreement between the IDs the code defines, the registry and RETIRED_IDS. #>
  param(
    [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Descriptors,
    [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Registry,
    [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$RetiredInCode
  )
  foreach ($row in @($Registry | Where-Object { -not $_.Id })) {
    "diagnostic-ids.txt line $($row.Line) is not 'id | status | owner | meaning' with a status of $($script:Statuses -join ', ')."
  }
  $rows = @{}
  foreach ($row in @($Registry | Where-Object { $_.Id })) {
    if ($rows.ContainsKey($row.Id)) {
      "$($row.Id) is listed more than once in diagnostic-ids.txt (lines $($rows[$row.Id].Line) and $($row.Line))."
      continue
    }
    $rows[$row.Id] = $row
    if ($row.Status -eq 'legacy' -and $row.Id -match $script:IdPattern) {
      "$($row.Id) is marked legacy in diagnostic-ids.txt, but legacy is only for IDs outside the WHIZ form that shipped before it."
    }
  }

  $defined = @($Descriptors | Where-Object { $_.Id } | Group-Object -Property Id | Sort-Object -Property Name)
  foreach ($group in $defined) {
    $id = $group.Name
    $where = ($group.Group | ForEach-Object { "$($_.Path):$($_.Line)" }) -join '; '
    $row = $rows[$id]
    if ($id -notmatch $script:IdPattern -and -not ($row -and $row.Status -eq 'legacy')) {
      "$id is not of the form WHIZ and three digits ($where). Take the next free WHIZ number from diagnostic-ids.txt."
    }
    if (-not $row) {
      "$id is defined ($where) but not listed in diagnostic-ids.txt. Add it there with its project and meaning."
      continue
    }
    if ($row.Status -eq 'retired') {
      "$id is retired in diagnostic-ids.txt but defined again ($where). A retired ID never takes a new meaning; use the next free number."
      continue
    }
    $owners = (@($group.Group | ForEach-Object { Get-DiagnosticOwner -Path $_.Path } | Sort-Object -Unique)) -join ', '
    if ($row.Owner -ne $owners) {
      "$id is listed under '$($row.Owner)' in diagnostic-ids.txt (line $($row.Line)) but defined in '$owners' ($where)."
    }
  }

  $definedIds = @($defined | Select-Object -ExpandProperty Name)
  foreach ($row in @($rows.Values | Where-Object { $_.Status -ne 'retired' -and $_.Id -notin $definedIds } | Sort-Object -Property Id)) {
    "$($row.Id) is listed in diagnostic-ids.txt (line $($row.Line)) but nothing defines it. Mark it retired, or remove it if it never shipped."
  }
  $retiredRows = @($rows.Values | Where-Object Status -eq 'retired' | Select-Object -ExpandProperty Id)
  foreach ($id in @($retiredRows | Where-Object { $_ -notin $RetiredInCode } | Sort-Object)) {
    "$id is retired in diagnostic-ids.txt but missing from DiagnosticDescriptors.RETIRED_IDS."
  }
  foreach ($id in @($RetiredInCode | Where-Object { $_ -notin $retiredRows } | Sort-Object)) {
    "$id is in DiagnosticDescriptors.RETIRED_IDS but not marked retired in diagnostic-ids.txt."
  }
}

function Get-RepositoryDiagnosticDescriptor {
  <# .SYNOPSIS Every descriptor constructed under src/ and tools/, and every root MSBuild code, with repository-relative paths. #>
  param([Parameter(Mandatory)][string]$Root)
  $rootFull = (Resolve-Path $Root).Path
  foreach ($folder in @('src', 'tools')) {
    foreach ($file in Get-ChildItem -Path (Join-Path $Root $folder) -Recurse -Filter '*.cs' -File) {
      $relative = [System.IO.Path]::GetRelativePath($rootFull, $file.FullName).Replace('\', '/')
      Get-DiagnosticDescriptor -Text (Get-Content -Raw -Path $file.FullName) -Path $relative
    }
  }
  foreach ($name in $script:MSBuildFiles) {
    Get-MSBuildDiagnosticCode -Text (Get-Content -Raw -Path (Join-Path $Root $name)) -Path $name
  }
}

# Dot-sourcing for tests loads the functions without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$descriptors = @(Get-RepositoryDiagnosticDescriptor -Root $Root)
$registry = @(Read-DiagnosticIdRegistry -Text (Get-Content -Raw -Path (Join-Path $Root 'diagnostic-ids.txt')) -Path 'diagnostic-ids.txt')
$retired = @(Get-RetiredDiagnosticId -Text (Get-Content -Raw -Path (Join-Path $Root 'src/Whizbang.Generators/DiagnosticDescriptors.cs')))
$problems = @(Find-DiagnosticIdConflict -Descriptors $descriptors -Allowed $script:AllowedSharedIds) +
  @(Find-DiagnosticIdRegistryProblem -Descriptors $descriptors -Registry $registry -RetiredInCode $retired)
if ($problems.Count -eq 0) {
  Write-Output 'Every diagnostic ID has one meaning and is registered in diagnostic-ids.txt.'
  exit 0
}
$problems | ForEach-Object { Write-Output "::error::$_" }
Write-Output 'Give each meaning its own unused WHIZ ID from diagnostic-ids.txt. See CONTRIBUTING.md and the error reference on the docs site.'
exit 1
