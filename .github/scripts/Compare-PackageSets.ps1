#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Fails when two builds of one commit produced packages that are not byte-identical.

.DESCRIPTION
    The reproducible-build check (#2484). Compares every *.nupkg and *.snupkg in -Left with the package of the
    same name in -Right, and reports each way they differ:

      package only in left / right   one build produced a package the other did not
      only in left / only in right   an entry exists in one package only (e.g. a randomly named .psmdcp)
      content                        an entry's bytes differ (e.g. an assembly with a new module id)
      timestamp                      an entry's bytes match but its zip time differs
      entry order                    the same entries were written in a different order

    A package whose file hash matches is identical and is not opened. Two empty directories are refused:
    comparing nothing proves nothing.

    Exit 0 when every package is identical; exit 1 with one ::error:: line per difference.

.PARAMETER Left
    Directory holding the first build's packages.

.PARAMETER Right
    Directory holding the second build's packages.

.PARAMETER ExtractTo
    Optional. Writes both sides of each differing entry under <ExtractTo>/left|right/<package>/<entry>, so the
    difference can be inspected byte by byte (e.g. with a disassembler).

.EXAMPLE
    pwsh .github/scripts/Compare-PackageSets.ps1 -Left ./build-a -Right ./build-b
#>

param(
  [Parameter(Mandatory)][string]$Left,
  [Parameter(Mandatory)][string]$Right,
  [string]$ExtractTo = ''
)

Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression

function Get-Sha256([byte[]]$Bytes) {
  return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes))
}

function Read-EntryBytes([System.IO.Compression.ZipArchiveEntry]$Entry) {
  $stream = $Entry.Open()
  try {
    $buffer = [System.IO.MemoryStream]::new()
    $stream.CopyTo($buffer)
    return , $buffer.ToArray()
  } finally { $stream.Dispose() }
}

function Get-PackageEntry {
  <#
  .SYNOPSIS
      The entries of one package in written order: Name, Sha256, LastWriteTime and Bytes.
  #>
  param([Parameter(Mandatory)][string]$Path)
  $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
  try {
    foreach ($entry in $zip.Entries) {
      $bytes = Read-EntryBytes $entry
      [pscustomobject]@{ Name = $entry.FullName; Sha256 = (Get-Sha256 $bytes); LastWriteTime = $entry.LastWriteTime; Bytes = $bytes }
    }
  } finally { $zip.Dispose() }
}

function New-Difference([string]$Package, [string]$Entry, [string]$Difference, $LeftBytes = $null, $RightBytes = $null) {
  [pscustomobject]@{ Package = $Package; Entry = $Entry; Difference = $Difference; LeftBytes = $LeftBytes; RightBytes = $RightBytes }
}

function Compare-Package {
  <#
  .SYNOPSIS
      Every difference between two packages of the same name: Package, Entry, Difference (and both sides' bytes).
  #>
  param([Parameter(Mandatory)][string]$LeftPath, [Parameter(Mandatory)][string]$RightPath)
  $package = Split-Path -Leaf $LeftPath
  $leftEntries = @(Get-PackageEntry -Path $LeftPath)
  $rightEntries = @(Get-PackageEntry -Path $RightPath)
  $rightByName = @{}
  foreach ($e in $rightEntries) { $rightByName[$e.Name] = $e }
  $leftNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
  foreach ($l in $leftEntries) {
    $null = $leftNames.Add($l.Name)
    if (-not $rightByName.ContainsKey($l.Name)) { New-Difference $package $l.Name 'only in left' $l.Bytes; continue }
    $r = $rightByName[$l.Name]
    if ($l.Sha256 -ne $r.Sha256) { New-Difference $package $l.Name 'content' $l.Bytes $r.Bytes }
    elseif ($l.LastWriteTime -ne $r.LastWriteTime) { New-Difference $package $l.Name 'timestamp' }
  }
  foreach ($r in $rightEntries) {
    if (-not $leftNames.Contains($r.Name)) { New-Difference $package $r.Name 'only in right' $null $r.Bytes }
  }
  $common = @($leftEntries | Where-Object { $rightByName.ContainsKey($_.Name) } | ForEach-Object Name)
  $commonRight = @($rightEntries | Where-Object { $leftNames.Contains($_.Name) } | ForEach-Object Name)
  if (($common -join "`n") -cne ($commonRight -join "`n")) { New-Difference $package '' 'entry order' }
}

function Get-PackageFile([string]$Directory) {
  return @(Get-ChildItem -LiteralPath $Directory -File | Where-Object { $_.Extension -in '.nupkg', '.snupkg' } | Sort-Object Name)
}

function Compare-PackageSet {
  <#
  .SYNOPSIS
      Every difference between two directories of packages; throws when neither holds a package.
  #>
  param([Parameter(Mandatory)][string]$Left, [Parameter(Mandatory)][string]$Right)
  $leftFiles = @(Get-PackageFile $Left)
  $rightFiles = @(Get-PackageFile $Right)
  if ($leftFiles.Count -eq 0 -and $rightFiles.Count -eq 0) {
    throw "No packages in '$Left' or '$Right': there is nothing to compare."
  }
  $rightByName = @{}
  foreach ($f in $rightFiles) { $rightByName[$f.Name] = $f }
  foreach ($l in $leftFiles) {
    if (-not $rightByName.ContainsKey($l.Name)) { New-Difference $l.Name '' 'package only in left'; continue }
    $r = $rightByName[$l.Name]
    if ((Get-FileHash -LiteralPath $l.FullName).Hash -eq (Get-FileHash -LiteralPath $r.FullName).Hash) { continue }
    Compare-Package -LeftPath $l.FullName -RightPath $r.FullName
  }
  $leftNames = @($leftFiles | ForEach-Object Name)
  foreach ($r in $rightFiles) {
    if ($leftNames -notcontains $r.Name) { New-Difference $r.Name '' 'package only in right' }
  }
}

function Save-DifferenceBytes([object[]]$Differences, [string]$Root) {
  foreach ($d in $Differences) {
    foreach ($side in @(@('left', $d.LeftBytes), @('right', $d.RightBytes))) {
      if ($null -eq $side[1]) { continue }
      $path = Join-Path -Path $Root -ChildPath $side[0] -AdditionalChildPath $d.Package, $d.Entry
      $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path)
      [System.IO.File]::WriteAllBytes($path, [byte[]]$side[1])
    }
  }
}

# Dot-sourcing for tests loads the functions without comparing anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$differences = @(Compare-PackageSet -Left $Left -Right $Right)
$count = @(Get-PackageFile $Left).Count
if ($differences.Count -eq 0) {
  Write-Output "All $count package(s) are byte-identical between the two builds."
  exit 0
}
if ($ExtractTo) { Save-DifferenceBytes -Differences $differences -Root $ExtractTo }
foreach ($d in $differences) {
  $where = if ($d.Entry) { "$($d.Package): $($d.Entry)" } else { $d.Package }
  Write-Output "::error::$where differs between the two builds ($($d.Difference))."
}
$packages = @($differences | ForEach-Object Package | Sort-Object -Unique)
Write-Output "$($differences.Count) difference(s) in $($packages.Count) of $count package(s): the build is not reproducible. See docs/RELEASING.md, 'Reproducible builds'."
exit 1
