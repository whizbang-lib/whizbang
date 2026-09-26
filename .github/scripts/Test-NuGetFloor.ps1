#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Refuses a version that would sort below one already on nuget.org.

.DESCRIPTION
    Exit 0 when -Version is at or above the highest version nuget.org holds for any package in
    .github/nuget-packages.txt (equal passes: a re-run of a run that already published). Exit 1, naming the
    offending package and version, when it is lower. Exit 2 when nuget.org cannot be read: an unreadable
    feed must never count as a pass.

    Unlisted versions count: they still exist, can still be restored by exact version, and a push of the
    same number would collide. SemVer 2.0 precedence, prerelease labels compared case-insensitively as
    nuget.org does.

    Why this exists: an abandoned release cut rewinds develop's computed version below the alphas it
    published while the cut was open, and nothing else notices (#872; docs/RELEASING.md, Recovery). Used by
    the ci.yml gate "Alpha is not below NuGet".

.PARAMETER Version
    The version about to be published.

.EXAMPLE
    pwsh .github/scripts/Test-NuGetFloor.ps1 -Version 0.2452.0-alpha.90
#>

param(
    [Parameter(Mandatory)]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Any unexpected failure exits 2 ("could not check"), never 1: exit 1 means "lower than NuGet".
trap {
  Write-Output "::error::Test-NuGetFloor failed unexpectedly: $_"
  exit 2
}

# Compare two versions by SemVer 2.0 precedence (build metadata ignored): -1, 0 or 1.
function Compare-SemVer([string]$a, [string]$b) {
  function Split-Version([string]$v) {
    $v = ($v -split '\+', 2)[0]
    $core, $pre = $v -split '-', 2
    $nums = @($core -split '\.' | ForEach-Object { [long]$_ })
    while ($nums.Count -lt 3) { $nums += 0 }
    # Assigned, not returned from an if-expression: PowerShell unrolls an empty array to $null there.
    $ids = @()
    if ($pre) { $ids = @($pre -split '\.') }
    return @{ Nums = $nums; Pre = $ids }
  }
  $x = Split-Version $a; $y = Split-Version $b
  for ($i = 0; $i -lt 3; $i++) {
    if ($x.Nums[$i] -ne $y.Nums[$i]) { return [Math]::Sign($x.Nums[$i] - $y.Nums[$i]) }
  }
  # A release sorts above every prerelease of the same core.
  if ($x.Pre.Count -eq 0 -and $y.Pre.Count -eq 0) { return 0 }
  if ($x.Pre.Count -eq 0) { return 1 }
  if ($y.Pre.Count -eq 0) { return -1 }
  for ($i = 0; $i -lt [Math]::Min($x.Pre.Count, $y.Pre.Count); $i++) {
    $p = $x.Pre[$i]; $q = $y.Pre[$i]
    $pn = $p -match '^\d+$'; $qn = $q -match '^\d+$'
    if ($pn -and $qn) {
      if ([long]$p -ne [long]$q) { return [Math]::Sign([long]$p - [long]$q) }
    } elseif ($pn) { return -1 }        # numeric identifiers sort below alphanumeric ones
    elseif ($qn) { return 1 }
    else {
      $c = [string]::Compare($p, $q, [StringComparison]::OrdinalIgnoreCase)
      if ($c -ne 0) { return [Math]::Sign($c) }
    }
  }
  # More identifiers sort higher when all shared ones are equal.
  return [Math]::Sign($x.Pre.Count - $y.Pre.Count)
}

# Dot-sourcing for tests loads Compare-SemVer without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$listPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'nuget-packages.txt'
$ids = @(Get-Content $listPath | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })

$highest = $null; $owner = $null
foreach ($id in $ids) {
  $url = "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())/index.json"
  try {
    $versions = @((Invoke-RestMethod -Uri $url -TimeoutSec 30).versions)
  } catch {
    $status = if ($_.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException]) { [int]$_.Exception.Response.StatusCode } else { 0 }
    if ($status -eq 404) { continue }   # never published: nothing to sort below
    Write-Output "::error::Cannot read nuget.org for ${id}: $($_.Exception.Message)"
    exit 2
  }
  foreach ($v in $versions) {
    if ($null -eq $highest -or (Compare-SemVer $v $highest) -gt 0) { $highest = $v; $owner = $id }
  }
}

if ($null -eq $highest) {
  Write-Output 'No package in the list has been published yet.'
  exit 0
}
if ((Compare-SemVer $Version $highest) -lt 0) {
  Write-Output ("::error::$Version sorts BELOW $highest, already on nuget.org for $owner. Publishing it would hide it " +
    'from latest-version resolution. Usual cause: a release cut was abandoned after develop published in its ' +
    "band (#872). Recovery (docs/RELEASING.md, Recovery): tag the commit that published $highest as v$highest " +
    'so develop computes above it, then re-run this run.')
  exit 1
}
Write-Output "$Version is at or above the highest version on nuget.org ($highest, $owner)."
exit 0
