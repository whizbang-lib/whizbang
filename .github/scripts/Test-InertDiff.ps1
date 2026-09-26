#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Tells whether every file that differs between two commits is inert (cannot affect the build or tests).

.DESCRIPTION
    Lists the files that differ from -Base to -Head through the GitHub compare API and matches each against
    .github/inert-paths.txt, the one definition of "docs-only". Exit 0 when every file is inert; exit 1 when
    any file is code, or when the difference cannot be established (a failed lookup, or a diff too large
    for the compare API to list in full). Prints the verdict and the offending files.

    Used by ci.yml (the "Detect changes" plan job, and ff-validated keeping a PR's verdict) and by the
    find-tested-run action. -Base must be an ancestor of -Head (a PR base, a queue commit's PR head, a
    develop merge's parent), so the three-dot compare is exactly the change between the two trees.

    Needs GH_TOKEN and REPO in the environment.

.PARAMETER Base
    The earlier commit.

.PARAMETER Head
    The later commit.

.EXAMPLE
    pwsh .github/scripts/Test-InertDiff.ps1 -Base 0a5497b6a -Head 975a88f61
#>

param(
    [Parameter(Mandatory)]
    [string]$Base,

    [Parameter(Mandatory)]
    [string]$Head
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ** crosses directories, * does not. Everything else is literal.
function ConvertTo-GlobRegex([string]$glob) {
  $sb = [System.Text.StringBuilder]::new('^')
  for ($i = 0; $i -lt $glob.Length; $i++) {
    if ($glob[$i] -eq '*' -and $i + 1 -lt $glob.Length -and $glob[$i + 1] -eq '*') { [void]$sb.Append('.*'); $i++ }
    elseif ($glob[$i] -eq '*') { [void]$sb.Append('[^/]*') }
    else { [void]$sb.Append([regex]::Escape([string]$glob[$i])) }
  }
  [void]$sb.Append('$')
  return [regex]::new($sb.ToString())
}

$listPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'inert-paths.txt'
$patterns = @(Get-Content $listPath | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') } |
  ForEach-Object { ConvertTo-GlobRegex $_ })

$json = gh api "repos/$env:REPO/compare/$Base...$Head" 2>$null
if ($LASTEXITCODE -ne 0 -or -not $json) {
  Write-Output "code: cannot compare $Base...$Head"
  exit 1
}
$files = @(($json | ConvertFrom-Json).files | ForEach-Object { $_.filename })
if ($files.Count -ge 300) {
  Write-Output 'code: 300+ files differ, too many to classify'
  exit 1
}
if ($files.Count -eq 0) {
  Write-Output 'inert: no files differ'
  exit 0
}

$code = @($files | Where-Object { $f = $_; -not ($patterns | Where-Object { $_.IsMatch($f) }) })
if ($code.Count -gt 0) {
  $shown = ($code | Select-Object -First 20) -join ', '
  Write-Output "code: $shown$(if ($code.Count -gt 20) { ' ...' })"
  exit 1
}
Write-Output 'inert: only docs-only paths differ'
exit 0
