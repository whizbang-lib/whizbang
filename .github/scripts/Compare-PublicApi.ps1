#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Compares every library's public API with the published baseline and reports each break.

.DESCRIPTION
    Packs the already-built tree again, with package validation on (WhizbangApiCheck=true, Directory.Build.targets),
    into a throwaway directory, and reads what package validation reports. Every break (an ApiCompat CPnnnn or package
    validation PKVnnn error) becomes one annotation and one row of the step summary, the same break in both target
    frameworks counted once.

    The pack step before this one packs with validation off, so a pack that fails for any other reason fails there,
    on its own. This script still fails (exit 1) on a pack error that is not an API break.

    Whether a break fails the run is API_BREAKS_BLOCK ('true' or 'false'), set by the workflow step. Breaks were
    hidden behind `|| true` until #2605, so the breaks already on develop are triaged before the comparison blocks:
    then the workflow sets it to 'true'. An intended break is recorded in the project's CompatibilitySuppressions.xml
    (docs/RELEASING.md, "Public API baseline"), and is then no longer reported.

    Exit 0: no break, or breaks that do not block. Exit 1: a break that blocks, or a pack error that is not an API
    break. Exit 2: API_BREAKS_BLOCK is neither 'true' nor 'false'.

.PARAMETER Version
    The version the tree was built at. Its assembly version must be at or above the baseline's, or every library
    reports an identity change (CP0003).

.PARAMETER OutputDirectory
    Where the comparison's packages go. They are not the packages a run uploads.

.EXAMPLE
    API_BREAKS_BLOCK=false pwsh .github/scripts/Compare-PublicApi.ps1 -Version 0.4521.0-queue.7
#>

param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$OutputDirectory = (Join-Path ($env:RUNNER_TEMP ?? [IO.Path]::GetTempPath()) 'public-api-packages')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# One break per project, rule and message, read from the pack output. The target framework is taken out of the
# message, so a break in net10.0 and net11.0 is one break.
function Get-PublicApiFinding([string[]]$Lines) {
    $seen = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($line in $Lines) {
        if ($line -notmatch 'error ((?:CP|PKV)\d{3,4}): (.+?) \[([^\]]+)\]\s*$') { continue }
        $code = $Matches[1]
        $message = $Matches[2] -replace 'lib/net[0-9.]+/', 'lib/<tfm>/'
        $project = [IO.Path]::GetFileNameWithoutExtension($Matches[3])
        if ($seen.Add("$project|$code|$message")) {
            [pscustomobject]@{ Project = $project; Code = $code; Message = $message }
        }
    }
}

function Invoke-PublicApiComparison {
    $block = $env:API_BREAKS_BLOCK
    if ($block -notin 'true', 'false') {
        Write-Host "::error::API_BREAKS_BLOCK must be 'true' or 'false'; it is '$block'."
        return 2
    }

    $output = @(& dotnet pack --configuration Release --no-build --output $OutputDirectory "-p:Version=$Version" '-p:WhizbangApiCheck=true' 2>&1 |
            ForEach-Object { "$_" })
    $packExit = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }

    $findings = @(Get-PublicApiFinding -Lines $output)
    $otherErrors = @($output | Where-Object { $_ -match ' error [A-Z]+\d+:' -and $_ -notmatch ' error (?:CP|PKV)\d+:' })

    $summary = [System.Collections.Generic.List[string]]::new()
    $summary.Add('## Public API against the baseline')
    $summary.Add('')
    if ($findings.Count -eq 0) {
        $summary.Add('No public API break against the baseline.')
    } else {
        $summary.Add("$($findings.Count) public API break(s) against the baseline$(if ($block -eq 'true') { '' } else { ', reported but not blocking until they are triaged (#2605)' }).")
        $summary.Add('')
        $summary.Add('| Package | Rule | Break |')
        $summary.Add('| --- | --- | --- |')
        foreach ($f in $findings) { $summary.Add("| $($f.Project) | $($f.Code) | $($f.Message.Replace('|', '\|')) |") }
    }
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary }

    $level = if ($block -eq 'true') { 'error' } else { 'warning' }
    foreach ($f in $findings) { Write-Host "::$level title=Public API break $($f.Code) in $($f.Project)::$($f.Message)" }

    if ($packExit -ne 0 -and ($findings.Count -eq 0 -or $otherErrors.Count -gt 0)) {
        Write-Host "::error::The comparison pack failed (exit $packExit) for a reason other than a public API break; see the errors above."
        return 1
    }
    if ($findings.Count -gt 0 -and $block -eq 'true') { return 1 }
    return 0
}

if ($MyInvocation.InvocationName -ne '.') {
    exit (Invoke-PublicApiComparison)
}
