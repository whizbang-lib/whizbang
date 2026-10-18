#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Fails when one build diagnostic ID means two different things.

.DESCRIPTION
    Every analyzer and generator package defines its own diagnostics, and nothing stops two packages
    from picking the same ID. When they do, a developer who looks the ID up lands on the wrong
    explanation, and a NoWarn or #pragma that suppresses one silently suppresses the other.

    The check reads every DiagnosticDescriptor construction in src/ (fields and descriptors built
    inline in a generator, named or positional arguments) and groups them by ID. One ID with two
    different titles is a conflict, unless it is listed in $AllowedSharedIds with the reason the
    meaning is the same. A construction site whose ID and title are not literals is reported too, so
    the scan cannot silently cover fewer descriptors than exist.

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

function Get-RepositoryDiagnosticDescriptor {
  <# .SYNOPSIS Every descriptor constructed under src/, with repository-relative paths. #>
  param([Parameter(Mandatory)][string]$Root)
  $src = Join-Path $Root 'src'
  $rootFull = (Resolve-Path $Root).Path
  foreach ($file in Get-ChildItem -Path $src -Recurse -Filter '*.cs' -File) {
    $relative = [System.IO.Path]::GetRelativePath($rootFull, $file.FullName).Replace('\', '/')
    Get-DiagnosticDescriptor -Text (Get-Content -Raw -Path $file.FullName) -Path $relative
  }
}

# Dot-sourcing for tests loads the functions without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$problems = @(Find-DiagnosticIdConflict -Descriptors @(Get-RepositoryDiagnosticDescriptor -Root $Root) -Allowed $script:AllowedSharedIds)
if ($problems.Count -eq 0) {
  Write-Output 'Every diagnostic ID has one meaning.'
  exit 0
}
$problems | ForEach-Object { Write-Output "::error::$_" }
Write-Output 'Give each meaning its own unused ID. See CONTRIBUTING.md and the error reference on the docs site.'
exit 1
