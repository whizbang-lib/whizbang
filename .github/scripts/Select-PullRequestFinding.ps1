#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Keeps, in each quality-gate findings file, only the findings in files one pull request changed.

.DESCRIPTION
    The merge queue tests a group of pull requests as one tree and runs the quality gate once for the group
    (#2070). Its uncovered new lines and branches, and the Sonar findings on new code, are the whole group's,
    so each pull request is reported only the findings in the files it changed: a finding in a file two
    pull requests both changed goes to both. The library-wide gap is not filtered; it belongs to every
    pull request in the group.

    Each findings file has one finding per line, "path:line: text" (a Sonar finding about a whole file has
    an empty line number). Each file named by -Path is rewritten in place; a file that does not exist is
    left missing (that check found nothing), and one whose findings are all dropped is written empty.

.PARAMETER FileList
    A file listing the pull request's changed files, one repository-relative path per line.

.PARAMETER Path
    The findings files to filter.

.EXAMPLE
    pwsh .github/scripts/Select-PullRequestFinding.ps1 -FileList pr-files.txt -Path coverage/uncovered-new-lines.txt, coverage/sonar-findings.txt
#>

param(
    [string]$FileList = '',
    [string[]]$Path = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The findings whose path is exactly one of the pull request's files.
function Select-PullRequestFindingLine {
  param(
      [AllowEmptyCollection()][string[]]$Line = @(),
      [AllowEmptyCollection()][string[]]$File = @()
  )
  $set = [System.Collections.Generic.HashSet[string]]::new([string[]]@($File), [System.StringComparer]::Ordinal)
  foreach ($l in $Line) {
    if ([string]::IsNullOrWhiteSpace($l)) { continue }
    $colon = $l.IndexOf(':')
    if ($colon -gt 0 -and $set.Contains($l.Substring(0, $colon))) { $l }
  }
}

# Dot-sourcing for tests loads the function without filtering anything.
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $FileList -or -not (Test-Path -LiteralPath $FileList)) {
  Write-Output "::error::The list of the pull request's changed files ('$FileList') could not be read."
  exit 1
}
$files = @(Get-Content -LiteralPath $FileList | Where-Object { $_ })
foreach ($p in $Path) {
  if (-not (Test-Path -LiteralPath $p)) { continue }
  $all = @(Get-Content -LiteralPath $p)
  $kept = @(Select-PullRequestFindingLine -Line $all -File $files)
  # WriteAllLines, not Set-Content: an empty list must still leave the file there.
  [System.IO.File]::WriteAllLines((Resolve-Path -LiteralPath $p).Path, [string[]]$kept)
  Write-Output "${p}: kept $($kept.Count) of $(@($all | Where-Object { $_ }).Count) finding(s), those in the pull request's $($files.Count) changed file(s)."
}
exit 0
