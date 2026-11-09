#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Computes a release's version from the prereleases of its line (#2136).

.DESCRIPTION
    The rule: for each distinct prerelease label published since the last stable release (alpha, beta, rc
    or any other), take that label's latest number, sum them, and add the sum to the target component of
    the line's base. With the minor as the target the patch resets to 0; the result carries no suffix.

        0.2627.0-alpha.674                 -> 0.3301.0
        + beta.5                           -> 0.3306.0
        + rc.3                             -> 0.3309.0   (alpha 674 + beta 5 + rc 3 = 682)
        alpha.2 + beta.5 + rc.3            -> 0.2637.0

    The target is a setting, rollupTarget in .github/release-version.json: minor (the default) or patch.

    Betas and rcs are published from the release branch after it is cut, so the number is computed twice:

      -Mode Cut     Start Release. The base and the alpha number are GitVersion's for the cut commit, the
                    only place the alpha number is a fact. The result names the release branch, and a
                    rollup record (an HTML comment) goes into the release PR's body: base, target, labels.
      -Mode Stable  The stable publish (and Release Prerelease's preview). The record is read back from the
                    release PR's body, checked against the branch name, and every label tagged on the line
                    (v<line>-<label>.<n>) is added. Labels are discovered from the tags, not listed here.
                    A release PR with no record (a major, minor, patch or manual override, a hotfix, or a
                    cut from before #2136) ships its branch's own number.

    Outputs (to GITHUB_OUTPUT when set): version and summary; Cut adds marker, Stable adds line and rolled.
    The summary line (e.g. "alpha 674 + beta 5 + rc 3 = 682 -> 0.3309.0") also goes to the step summary.
    Exit 1 with an ::error:: annotation, and no version written, on anything it cannot prove.

.EXAMPLE
    pwsh .github/scripts/Get-ReleaseVersion.ps1 -Mode Cut -Base 0.2627.0 -PreReleaseLabel alpha -PreReleaseNumber 674

.EXAMPLE
    RELEASE_PR_BODY="$BODY" pwsh .github/scripts/Get-ReleaseVersion.ps1 -Mode Stable -Line 0.3301.0 -Tags "$TAGS"
#>

param(
    [string]$Mode = '',
    # Cut: GitVersion's MajorMinorPatch for the cut commit (or the open series it continues).
    [string]$Base = '',
    # Cut: GitVersion's PreReleaseLabel and PreReleaseNumber for the cut commit; empty = none to add.
    [string]$PreReleaseLabel = '',
    [string]$PreReleaseNumber = '',
    # Stable: the release branch's version (release/v<Line>).
    [string]$Line = '',
    # Stable: published tag names; each element may hold several, separated by whitespace.
    [string[]]$Tags = @(),
    # Stable: the release PR's body, from the environment so no shell quoting touches it.
    [string]$PullRequestBody = $env:RELEASE_PR_BODY,
    [string]$ConfigPath = (Join-Path $PSScriptRoot '..' 'release-version.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:VersionPattern = '^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?$'
# A SemVer prerelease identifier that is not purely numeric: a label, not a number.
$script:LabelPattern = '^(?=[0-9A-Za-z-]*[A-Za-z-])[0-9A-Za-z-]+$'

# The rollup itself: base + the latest number of each label, on the target component.
function Get-ReleaseRollup {
  param(
      [Parameter(Mandatory)][string]$Base,
      [System.Collections.IDictionary]$Prerelease = @{},
      [ValidateSet('minor', 'patch')][string]$Target = 'minor'
  )
  if ($Base -notmatch $script:VersionPattern) { throw "'$Base' is not a version (expected X.Y.Z)." }
  $major = [long]$Matches[1]; $minor = [long]$Matches[2]; $patch = [long]$Matches[3]
  # SemVer orders labels ASCII-wise and nuget.org ignores case: alpha < beta < preview < rc.
  $labels = @($Prerelease.Keys | Sort-Object -Property @{ Expression = { $_.ToLowerInvariant() } } -CaseSensitive)
  $parts = [System.Collections.Generic.List[string]]::new()
  $sum = [long]0
  foreach ($label in $labels) {
    $n = [long]$Prerelease[$label]
    if ($n -lt 0) { throw "Prerelease $label has a negative number ($n)." }
    $sum += $n
    $parts.Add("$label $n")
  }
  $version = if ($Target -eq 'minor') { "$major.$($minor + $sum).0" } else { "$major.$minor.$($patch + $sum)" }
  $summary = if ($parts.Count -eq 0) { "no prereleases -> $version" } else { "$($parts -join ' + ') = $sum -> $version" }
  return [pscustomobject]@{ Version = $version; Sum = $sum; Parts = @($parts); Summary = $summary }
}

# Every label published on a line, from its tags v<line>-<label>.<n>, with its latest number.
function Get-LinePrerelease {
  param([Parameter(Mandatory)][string]$Line, [string[]]$Tags = @())
  $pattern = '^v' + [regex]::Escape($Line) + '-([0-9A-Za-z-]+)\.(\d+)$'
  $found = [ordered]@{}
  foreach ($tag in @($Tags | ForEach-Object { $_ -replace '^refs/tags/', '' } | Sort-Object)) {
    if ($tag -notmatch $pattern) { continue }
    $label = $Matches[1]; $n = [long]$Matches[2]
    if ($label -notmatch $script:LabelPattern) { continue }
    if (-not $found.Contains($label) -or $found[$label] -lt $n) { $found[$label] = $n }
  }
  $sorted = [ordered]@{}
  foreach ($k in @($found.Keys | Sort-Object)) { $sorted[$k] = $found[$k] }
  return $sorted
}

# The record the cut leaves in the release PR's body, read back by the stable publish.
function ConvertTo-RollupMarker {
  param(
      [Parameter(Mandatory)][string]$Base,
      [Parameter(Mandatory)][ValidateSet('minor', 'patch')][string]$Target,
      [System.Collections.IDictionary]$Prerelease = @{}
  )
  $tokens = @("base=$Base", "target=$Target") + @($Prerelease.Keys | ForEach-Object { "$_=$($Prerelease[$_])" })
  return "<!-- release-rollup: $($tokens -join ' ') -->"
}

function ConvertFrom-RollupMarker {
  param([string]$Text)
  if (-not $Text -or $Text -notmatch '<!--\s*release-rollup:\s*(.*?)\s*-->') { return $null }
  $raw = $Matches[1]
  $base = $null; $target = $null; $labels = [ordered]@{}
  foreach ($token in @($raw -split '\s+' | Where-Object { $_ })) {
    if ($token -notmatch '^([^=]+)=(.+)$') { throw "The release PR's rollup record has a malformed entry '$token': $raw" }
    $key = $Matches[1]; $value = $Matches[2]
    switch ($key) {
      'base' { $base = $value }
      'target' { $target = $value }
      default {
        if ($key -notmatch $script:LabelPattern -or $value -notmatch '^\d+$') {
          throw "The release PR's rollup record has a malformed entry '$token': $raw"
        }
        $labels[$key] = [long]$value
      }
    }
  }
  if (-not $base -or $base -notmatch '^\d+\.\d+\.\d+$') { throw "The release PR's rollup record has no base version: $raw" }
  if ($target -notin @('minor', 'patch')) { throw "The release PR's rollup record has no target (minor or patch): $raw" }
  return [pscustomobject]@{ Base = $base; Target = $target; Prerelease = $labels }
}

# The target component, a configuration setting: minor unless .github/release-version.json says patch.
function Get-RollupTarget {
  param([Parameter(Mandatory)][string]$ConfigPath)
  if (-not (Test-Path -LiteralPath $ConfigPath)) { return 'minor' }
  $config = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
  $target = if ($config.PSObject.Properties['rollupTarget']) { [string]$config.rollupTarget } else { 'minor' }
  if ($target -notin @('minor', 'patch')) { throw "rollupTarget in $ConfigPath is '$target'; it must be minor or patch." }
  return $target
}

function Resolve-CutVersion {
  param(
      [Parameter(Mandatory)][string]$Base,
      [string]$PreReleaseLabel = '',
      [string]$PreReleaseNumber = '',
      [Parameter(Mandatory)][string]$Target
  )
  $prerelease = [ordered]@{}
  if ($PreReleaseNumber) {
    if (-not $PreReleaseLabel) { throw "A prerelease number ($PreReleaseNumber) needs its label." }
    if ($PreReleaseNumber -notmatch '^\d+$') { throw "The prerelease number '$PreReleaseNumber' is not a number." }
    $prerelease[$PreReleaseLabel] = [long]$PreReleaseNumber
  }
  $rollup = Get-ReleaseRollup -Base $Base -Prerelease $prerelease -Target $Target
  $core = ($Base -split '-', 2)[0]
  return [pscustomobject]@{
    Version = $rollup.Version; Summary = $rollup.Summary
    Marker = ConvertTo-RollupMarker -Base $core -Target $Target -Prerelease $prerelease
  }
}

function Resolve-StableVersion {
  param([Parameter(Mandatory)][string]$Line, [string]$Body = '', [string[]]$Tags = @())
  $record = ConvertFrom-RollupMarker -Text $Body
  if (-not $record) {
    return [pscustomobject]@{
      Version = $Line; Line = $Line; Rolled = $false
      Summary = "release/v$Line carries no rollup record (an override, a hotfix, or a cut from before the rollup): ships its own number -> $Line"
    }
  }
  $cut = Get-ReleaseRollup -Base $record.Base -Prerelease $record.Prerelease -Target $record.Target
  if ($cut.Version -ne $Line) {
    throw ("The release PR's rollup record ($($cut.Summary) from $($record.Base)) computes $($cut.Version), not the branch's " +
      "$Line. Fix the record in the release PR body, or publish an explicit version: gh workflow run release.yml --ref main " +
      "-f version=X.Y.Z -f release_type=auto -f dry_run=false")
  }
  $all = [ordered]@{}
  foreach ($k in $record.Prerelease.Keys) { $all[$k] = $record.Prerelease[$k] }
  $tagged = Get-LinePrerelease -Line $Line -Tags $Tags
  foreach ($k in $tagged.Keys) {
    if (-not $all.Contains($k) -or $all[$k] -lt $tagged[$k]) { $all[$k] = $tagged[$k] }
  }
  $final = Get-ReleaseRollup -Base $record.Base -Prerelease $all -Target $record.Target
  return [pscustomobject]@{ Version = $final.Version; Line = $Line; Rolled = $true; Summary = $final.Summary }
}

# Dot-sourcing for tests loads the functions without running anything.
if ($MyInvocation.InvocationName -eq '.') { return }

try {
  $tagList = @($Tags | ForEach-Object { $_ -split '\s+' } | Where-Object { $_ })
  $outputs = [ordered]@{}
  switch ($Mode) {
    'Cut' {
      $r = Resolve-CutVersion -Base $Base -PreReleaseLabel $PreReleaseLabel -PreReleaseNumber $PreReleaseNumber -Target (Get-RollupTarget -ConfigPath $ConfigPath)
      $outputs['version'] = $r.Version; $outputs['marker'] = $r.Marker; $outputs['summary'] = $r.Summary
    }
    'Stable' {
      $r = Resolve-StableVersion -Line $Line -Body $PullRequestBody -Tags $tagList
      $outputs['version'] = $r.Version; $outputs['line'] = $r.Line
      $outputs['rolled'] = if ($r.Rolled) { 'true' } else { 'false' }; $outputs['summary'] = $r.Summary
    }
    default { throw "-Mode must be Cut or Stable (got '$Mode')." }
  }
} catch {
  Write-Output "::error::$($_.Exception.Message)"
  exit 1
}

Write-Output "Release version: $($outputs['summary'])"
if ($env:GITHUB_OUTPUT) { $outputs.Keys | ForEach-Object { "$_=$($outputs[$_])" } | Add-Content -Path $env:GITHUB_OUTPUT }
if ($env:GITHUB_STEP_SUMMARY) { "**Release version:** ``$($outputs['summary'])``" | Add-Content -Path $env:GITHUB_STEP_SUMMARY }
exit 0
