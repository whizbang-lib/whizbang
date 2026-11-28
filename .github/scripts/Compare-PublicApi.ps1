#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Compares every library's public API with the published baseline and reports each break, or records the breaks
    in the projects' suppression files.

.DESCRIPTION
    Packs the already-built tree again, with package validation on (WhizbangApiCheck=true, Directory.Build.targets),
    into a throwaway directory, and reads what package validation reports. Every break (an ApiCompat CPnnnn or package
    validation PKVnnn error) becomes one annotation and one row of the step summary, the same break in both target
    frameworks counted once.

    The pack step before this one packs with validation off, so a pack that fails for any other reason fails there,
    on its own. This script still fails (exit 1) on a pack error that is not an API break.

    Whether a break fails the run is API_BREAKS_BLOCK ('true' or 'false'), set by the workflow step. It is 'true': the
    breaks that reached develop behind the old `|| true` were accepted by the owner and recorded (#2605), so a new
    break fails the run until it is recorded in the project's CompatibilitySuppressions.xml (docs/RELEASING.md,
    "Public API baseline"), after which it is no longer reported.

    With -UpdateSuppressions it records instead of comparing: it builds and packs the tree with
    ApiCompatGenerateSuppressionFile=true, so the SDK rewrites each project's CompatibilitySuppressions.xml with every
    break against the baseline, the ones already recorded included. It deletes the incremental validation markers
    first, since a leftover one makes the SDK skip validation and write nothing, and it puts back the comments at the
    top of each file, which say why the breaks are intended and which the SDK replaces with its own. A new file gets
    only the SDK's: add one that says why. Review the diff; it is the API review.

    Exit 0: no break, breaks that do not block, or the files were written. Exit 1: a break that blocks, or a pack
    error that is not an API break. Exit 2: API_BREAKS_BLOCK is neither 'true' nor 'false', or no -Version was given
    and Directory.Build.targets names no baseline.

.PARAMETER Version
    The version to pack at. Its assembly version must be at or above the baseline's, or every library reports an
    identity change (CP0003). Without it, the baseline's own version with a '-local' label.

.PARAMETER OutputDirectory
    Where the comparison's packages go. They are not the packages a run uploads.

.PARAMETER RepositoryRoot
    The directory packed, and searched for suppression files. The repository by default.

.PARAMETER UpdateSuppressions
    Record every break in the projects' CompatibilitySuppressions.xml instead of comparing. Ignores API_BREAKS_BLOCK.

.EXAMPLE
    API_BREAKS_BLOCK=true pwsh .github/scripts/Compare-PublicApi.ps1 -Version 0.4521.0-queue.7

    Compares an already-built tree, as CI does.

.EXAMPLE
    pwsh .github/scripts/Compare-PublicApi.ps1 -UpdateSuppressions

    Builds the tree and rewrites every project's suppression file with the breaks it has now. Run it after making an
    intended break, and when assembling a bundle of changes that each made one, then commit the files.
#>

param(
    [string]$Version,

    [string]$OutputDirectory = (Join-Path ($env:RUNNER_TEMP ?? [IO.Path]::GetTempPath()) 'public-api-packages'),

    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..' '..'),

    [switch]$UpdateSuppressions
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

# The version to pack at: the one given, or the baseline's own with a label. Null when there is neither.
function Resolve-PackVersion([string]$Given, [string]$Root) {
    if ($Given) { return $Given }
    $targets = Join-Path $Root 'Directory.Build.targets'
    if (-not (Test-Path -LiteralPath $targets)) { return $null }
    $baseline = @(([xml](Get-Content -Raw -LiteralPath $targets)).SelectNodes('//PackageValidationBaselineVersion') |
            ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ })
    if ($baseline.Count -eq 0) { return $null }
    return "$($baseline[0])-local"
}

# Every project's suppression file, never a copy under bin or obj.
function Get-SuppressionFile([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter 'CompatibilitySuppressions.xml' |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
}

# The comments between the XML declaration and <Suppressions>, which say why the breaks are intended.
$script:HeaderPattern = '^(?s)(<\?xml[^>]*\?>\r?\n)((?:\s*<!--.*?-->)*\s*?\r?\n)?(<Suppressions\b.*)$'

function Invoke-SuppressionUpdate([string]$Root, [string]$PackVersion) {
    $headers = @{}
    foreach ($file in Get-SuppressionFile $Root) {
        $text = Get-Content -Raw -LiteralPath $file.FullName
        if ($text -match $HeaderPattern -and $Matches[2]) { $headers[$file.FullName] = $Matches[2] }
    }
    Get-ChildItem -LiteralPath $Root -Recurse -File -Filter 'Microsoft.NET.ApiCompat.ValidatePackage.semaphore' |
        Remove-Item -Force

    & dotnet pack --configuration Release --output $OutputDirectory "-p:Version=$PackVersion" '-p:WhizbangApiCheck=true' '-p:ApiCompatGenerateSuppressionFile=true' 2>&1 |
        ForEach-Object { Write-Host "$_" }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "::error::The pack that writes the suppression files failed (exit $LASTEXITCODE); see the errors above."
        return 1
    }

    foreach ($file in Get-SuppressionFile $Root) {
        $text = Get-Content -Raw -LiteralPath $file.FullName
        $header = $headers[$file.FullName]
        if ($header -and $text -match $HeaderPattern -and $Matches[2] -ne $header) {
            $text = $Matches[1] + $header + $Matches[3]
            Set-Content -LiteralPath $file.FullName -Value $text -NoNewline
        }
        $count = [regex]::Matches($text, '<Suppression>').Count
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        Write-Host "${relative}: $count suppression(s)$(if (-not $header) { ', new: add a comment saying why' })"
    }
    return 0
}

function Invoke-PublicApiComparison([string]$PackVersion) {
    $block = $env:API_BREAKS_BLOCK
    if ($block -notin 'true', 'false') {
        Write-Host "::error::API_BREAKS_BLOCK must be 'true' or 'false'; it is '$block'."
        return 2
    }

    $output = @(& dotnet pack --configuration Release --no-build --output $OutputDirectory "-p:Version=$PackVersion" '-p:WhizbangApiCheck=true' 2>&1 |
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

function Invoke-CompareScript {
    $root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    $packVersion = Resolve-PackVersion $Version $root
    if (-not $packVersion) {
        Write-Host "::error::No -Version was given and $root/Directory.Build.targets names no PackageValidationBaselineVersion."
        return 2
    }
    Push-Location -LiteralPath $root
    try {
        if ($UpdateSuppressions) { return Invoke-SuppressionUpdate $root $packVersion }
        return Invoke-PublicApiComparison $packVersion
    } finally {
        Pop-Location
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    exit (Invoke-CompareScript)
}
