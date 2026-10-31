#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Compares .github/pipeline-tool-licenses.txt with the licenses GitHub and NuGet report today.

.DESCRIPTION
    Optional and report-only: it changes no file and gates nothing. The committed list is the source of truth
    for Test-PipelineToolLicenses.ps1, which runs without network access; run this when adding a tool or now
    and then, to notice a tool whose license changed upstream.

    An action's license is its repository's, from the GitHub license endpoint (GH_TOKEN or GITHUB_TOKEN is
    used when set, to avoid the anonymous rate limit). A dotnet tool's is the license expression of its newest
    version in the NuGet registration; a package that declares a license file or URL instead is reported as
    such, for a person to read.

    Dot-source it for the functions; run it to print every entry, the upstream license and whether they match.

.EXAMPLE
    pwsh .github/scripts/Compare-PipelineToolLicenses.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Test-PipelineToolLicenses.ps1')

function Get-UpstreamLicense {
  <# .SYNOPSIS The license GitHub or NuGet reports for one list entry, or a sentence saying why there is none. #>
  param([Parameter(Mandatory)][object]$Entry)
  try {
    if ($Entry.Kind -eq 'action') {
      $headers = @{ Accept = 'application/vnd.github+json' }
      $token = $env:GH_TOKEN ?? $env:GITHUB_TOKEN
      if ($token) { $headers.Authorization = "Bearer $token" }
      return (Invoke-RestMethod -Uri "https://api.github.com/repos/$($Entry.Id)/license" -Headers $headers).license.spdx_id
    }
    $index = Invoke-RestMethod -Uri "https://api.nuget.org/v3/registration5-gz-semver2/$($Entry.Id)/index.json"
    $page = @($index.items)[-1]
    if (-not $page.PSObject.Properties['items']) { $page = Invoke-RestMethod -Uri $page.'@id' }
    $catalog = @($page.items)[-1].catalogEntry
    if ($catalog.PSObject.Properties['licenseExpression'] -and $catalog.licenseExpression) { return $catalog.licenseExpression }
    return "no SPDX expression ($($catalog.licenseUrl))"
  } catch {
    return "lookup failed: $($_.Exception.Message)"
  }
}

function Compare-ToolLicense {
  <# .SYNOPSIS Kind, Id, Listed, Upstream and Matches for every list entry. #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$List)
  foreach ($entry in $List) {
    $upstream = Get-UpstreamLicense -Entry $entry
    [pscustomobject]@{ Kind = $entry.Kind; Id = $entry.Id; Listed = $entry.License; Upstream = $upstream; Matches = ($upstream -eq $entry.License) }
  }
}

# Dot-sourcing for tests loads the functions without calling any API.
if ($MyInvocation.InvocationName -eq '.') { return }

$list = @(Read-ToolLicenseList -Path (Join-Path $PSScriptRoot '..' 'pipeline-tool-licenses.txt'))
Compare-ToolLicense -List $list | Sort-Object Matches, Kind, Id | Format-Table -AutoSize | Out-String -Width 200
