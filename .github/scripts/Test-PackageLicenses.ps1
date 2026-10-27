#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Fails when a package would ship a dependency under a license outside the allow list.

.DESCRIPTION
    Reads the CycloneDX SBOMs the package-sbom action wrote (one <id>.<version>.cdx.json per package) and
    checks the license of every dependency a consumer receives: components whose scope is not "excluded"
    (development-only references never reach a consumer) and that are not Whizbang packages themselves.

    The allow list is the one GitHub's dependency review uses, .github/dependency-review-config.yml
    (allow-licenses), so a license is allowed in one place for both. Reviewed exceptions are its
    allow-dependencies-licenses entries that name a NuGet package (pkg:nuget/<id>).

    This is the NuGet half of #1441: GitHub's dependency graph reads NuGet project files without versions,
    so dependency review cannot see a NuGet package's license. Every package's full graph is checked on
    every pack, not only what a pull request adds, so a version bump that changes a license is caught too.

    A license expression passes when any alternative of an OR is allowed and every part of an AND is. A
    dependency with no license, or a license name that is not an SPDX identifier, fails unless excepted.

    Exit 0 when everything is allowed; exit 1 with each offending dependency and the packages that bring it.

.PARAMETER Directory
    Directory holding the *.cdx.json SBOMs.

.PARAMETER Config
    The dependency review configuration holding the allow list.

.EXAMPLE
    pwsh .github/scripts/Test-PackageLicenses.ps1 -Directory ./packages
#>

param(
    [Parameter(Mandatory)]
    [string]$Directory,

    [Parameter()]
    [string]$Config = (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'dependency-review-config.yml')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-YamlList {
  <#
  .SYNOPSIS
      The items of one top-level list in a flat YAML file ("key:" followed by "  - item" lines). Comments
      and quotes are dropped. Enough for the review configuration, which is nothing but such lists.
  #>
  param([Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines, [Parameter(Mandatory)][string]$Key)
  $inList = $false
  foreach ($line in $Lines) {
    $text = ($line -replace '\s+#.*$', '' -replace '^#.*$', '').TrimEnd()
    if (-not $text) { continue }
    if ($text -match '^(\S[^:]*):') { $inList = ($Matches[1] -eq $Key); continue }
    if ($inList -and $text -match '^\s+-\s+(.+)$') { $Matches[1].Trim().Trim('"', "'") }
  }
}

function Get-LicensePolicy {
  <# .SYNOPSIS The allowed SPDX identifiers and the NuGet package ids excepted from the check. #>
  param([Parameter(Mandatory)][string]$Path)
  $lines = @(Get-Content -LiteralPath $Path)
  $allowed = @(Read-YamlList -Lines $lines -Key 'allow-licenses')
  if ($allowed.Count -eq 0) { throw "No allow-licenses list in $Path" }
  $excepted = @(Read-YamlList -Lines $lines -Key 'allow-dependencies-licenses' |
      Where-Object { $_ -like 'pkg:nuget/*' } | ForEach-Object { ($_ -replace '^pkg:nuget/', '' -replace '@.*$', '') })
  [pscustomobject]@{ Allowed = $allowed; Excepted = $excepted }
}

function Test-LicenseExpression {
  <# .SYNOPSIS Whether an SPDX expression is allowed: any alternative of an OR, every part of an AND. #>
  param([Parameter(Mandatory)][string]$Expression, [Parameter(Mandatory)][string[]]$Allowed)
  $expr = $Expression.Trim().Trim('(', ')').Trim()
  if ($expr -match '\s+OR\s+') {
    return [bool](@($expr -split '\s+OR\s+') | Where-Object { Test-LicenseExpression -Expression $_ -Allowed $Allowed })
  }
  foreach ($part in @($expr -split '\s+AND\s+')) {
    $id = ($part.Trim().Trim('(', ')').Trim() -split '\s+WITH\s+')[0].Trim()
    if ($Allowed -notcontains $id) { return $false }
  }
  return $true
}

function Get-ComponentLicense {
  <#
  .SYNOPSIS
      A component's license as one expression, or $null when it declares none. A non-SPDX license name comes
      back as "name:<text>", which no allow list matches.
  #>
  param([Parameter(Mandatory)][object]$Component)
  $licenses = if ($Component.PSObject.Properties['licenses']) { @($Component.licenses) } else { @() }
  $parts = foreach ($entry in $licenses) {
    if ($entry.PSObject.Properties['expression'] -and $entry.expression) { "($($entry.expression))" }
    elseif ($entry.PSObject.Properties['license']) {
      $license = $entry.license
      if ($license.PSObject.Properties['id'] -and $license.id) { $license.id }
      elseif ($license.PSObject.Properties['name'] -and $license.name) { "name:$($license.name)" }
    }
  }
  if (@($parts).Count -eq 0) { return $null }
  # Several entries on one component each apply (CycloneDX lists concluded licenses side by side).
  return (@($parts) -join ' AND ')
}

function Get-LicenseViolation {
  <#
  .SYNOPSIS
      Every shipped dependency whose license is not allowed: Dependency (name@version), License and the
      Packages that bring it.
  #>
  param([Parameter(Mandatory)][object[]]$Sboms, [Parameter(Mandatory)][object]$Policy)
  $found = [ordered]@{}
  foreach ($sbom in $Sboms) {
    $package = $sbom.metadata.component.name
    $components = if ($sbom.PSObject.Properties['components']) { @($sbom.components) } else { @() }
    foreach ($component in $components) {
      if ($component.PSObject.Properties['scope'] -and $component.scope -eq 'excluded') { continue }
      if ($component.name -like 'SoftwareExtravaganza.*') { continue }
      if ($Policy.Excepted -contains $component.name) { continue }
      $license = Get-ComponentLicense -Component $component
      if ($license -and (Test-LicenseExpression -Expression $license -Allowed $Policy.Allowed)) { continue }
      $key = "$($component.name)@$($component.version)"
      if (-not $found.Contains($key)) {
        $found[$key] = [pscustomobject]@{ Dependency = $key; License = ($license ?? 'none declared'); Packages = [System.Collections.Generic.List[string]]::new() }
      }
      if (-not $found[$key].Packages.Contains($package)) { $found[$key].Packages.Add($package) }
    }
  }
  return @($found.Values)
}

# Dot-sourcing for tests loads the functions without reading any SBOM.
if ($MyInvocation.InvocationName -eq '.') { return }

$files = @(Get-ChildItem -LiteralPath $Directory -Filter '*.cdx.json' -File)
if ($files.Count -eq 0) {
  Write-Output "::error::No SBOMs (*.cdx.json) in $Directory, so no license can be checked."
  exit 1
}
$policy = Get-LicensePolicy -Path $Config
$sboms = @($files | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
$violations = @(Get-LicenseViolation -Sboms $sboms -Policy $policy)
if ($violations.Count -eq 0) {
  Write-Output "Every dependency of $($files.Count) packages is under an allowed license ($($policy.Allowed -join ', '))."
  exit 0
}
foreach ($v in $violations) {
  Write-Output "::error::$($v.Dependency) is licensed '$($v.License)', which is not on the allow list; shipped by $($v.Packages -join ', ')."
}
Write-Output "$($violations.Count) dependency license(s) not allowed. Replace the dependency, or add a reviewed exception (allow-dependencies-licenses: pkg:nuget/<id>) to .github/dependency-review-config.yml."
exit 1
