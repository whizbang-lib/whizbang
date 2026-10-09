#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Lists, per sliced test suite (each integration suite, and the component suite), the build output
    that suite needs, so each suite downloads its own slice of the build instead of all of it.

.DESCRIPTION
    Every test job used to download the whole build (about 2.4 GB compressed), though an integration
    suite runs one or two projects whose output is a few hundred megabytes, and the download time
    varied from about 40 seconds to over 5 minutes. A test project's bin/Release already holds its
    whole dependency closure, so that is all a suite needs from the build; sources come from the job's
    own checkout.

    A suite's projects are chosen exactly as Run-Tests.ps1 chooses them for that suite: projects in
    Whizbang.slnx that declare the suite's <WhizbangTestType> and, for an integration suite, carry its
    tag in <WhizbangTestTags>. The component suite runs every Component project (-Mode Component, no
    tag), so its slice is every Component project. A new project joins its slice with no change here,
    and a slice that misses one fails the suite: Run-Tests.ps1 -NoBuild refuses a selected project
    with no build output.

    Every <WhizbangTestType> is either sliced here or listed in $UnslicedTypes with the reason;
    .github/scripts/Test-WhizbangTestType.ps1 fails on a type that is neither (#1264).

    Writes one marker file per slice at the repository root (test-slice-<suite>.txt, naming the
    projects). Uploaded inside the slice, it anchors the artifact's paths at the repository root, so a
    slice whose projects all sit under tests/ still downloads to tests/..., and a test job can print
    what it received. With -GitHubOutput, writes one multi-line output per suite: the upload paths.

.PARAMETER GitHubOutput
    Write the paths as step outputs (GITHUB_OUTPUT) instead of to the console.

.EXAMPLE
    pwsh .github/scripts/Get-TestSlice.ps1
#>

param(
    [Parameter()] [switch]$GitHubOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Suite -> the <WhizbangTestType> it runs, the tag its reusable workflow passes to Run-Tests.ps1 (-Tag;
# none for a suite that runs the whole type), and any further build output it needs. Service Bus runs
# the ECommerce suite through an Aspire app host, which starts the sample services from their own build
# output. .github/scripts/tests/Get-TestSlice.Tests.ps1 checks this table against the workflows.
$script:Suites = [ordered]@{
  postgres   = @{ Type = 'Integration'; Tag = 'Postgres';        Extra = @() }
  inmemory   = @{ Type = 'Integration'; Tag = 'InMemory';        Extra = @() }
  rabbitmq   = @{ Type = 'Integration'; Tag = 'RabbitMQ';        Extra = @() }
  servicebus = @{ Type = 'Integration'; Tag = 'AzureServiceBus'; Extra = @('samples/ECommerce/**/bin/Release') }
  azureblob  = @{ Type = 'Integration'; Tag = 'AzureBlob';       Extra = @() }
  # In-process integration projects whose only suite tag is the plain "Integration" (#1196).
  integration = @{ Type = 'Integration'; Tag = 'Integration';    Extra = @() }
  # Every Component project: real workers and threads, no infrastructure (#1264).
  component  = @{ Type = 'Component';   Tag = '';                Extra = @() }
}

# Types that get no slice, and why.
$script:UnslicedTypes = [ordered]@{
  Unit      = 'the unit suite runs most projects, so it keeps the full build'
  Benchmark = 'not run in CI (BenchmarkDotNet, on demand)'
  Soak      = 'not run in CI (scripts/Run-Soak.ps1, on demand)'
}

# The projects in the solution of a type (and carrying a tag, when one is given), as repository-relative
# directories.
function Get-TestSliceProject([string]$Root, [string]$Tag, [string]$Type = 'Integration') {
  $solution = Get-Content (Join-Path -Path $Root -ChildPath 'Whizbang.slnx') -Raw
  $projects = [regex]::Matches($solution, '<Project Path="([^"]+\.csproj)"') | ForEach-Object { $_.Groups[1].Value }
  foreach ($relative in $projects) {
    $path = Join-Path -Path $Root -ChildPath $relative
    if (-not (Test-Path $path)) { continue }
    $content = Get-Content $path -Raw
    if ($content -notmatch "<WhizbangTestType>$Type</WhizbangTestType>") { continue }
    if (-not $Tag) { Split-Path -Path $relative -Parent; continue }
    if ($content -notmatch '<WhizbangTestTags>([^<]+)</WhizbangTestTags>') { continue }
    $tags = $Matches[1] -split ';' | ForEach-Object { $_.Trim() }
    if ($tags -contains $Tag) { Split-Path -Path $relative -Parent }
  }
}

# Dot-sourcing for tests loads the functions without writing anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$root = Join-Path -Path $PSScriptRoot -ChildPath '../..' -Resolve
foreach ($suite in $script:Suites.Keys) {
  $definition = $script:Suites[$suite]
  $projects = @(Get-TestSliceProject -Root $root -Tag $definition.Tag -Type $definition.Type | Sort-Object)
  if ($projects.Count -eq 0) { throw "The $suite slice selects no $($definition.Type) project$(if ($definition.Tag) { " tagged '$($definition.Tag)'" })." }
  $marker = "test-slice-$suite.txt"
  $lines = @("Build slice for the $suite suite (.github/scripts/Get-TestSlice.ps1):") + ($projects | ForEach-Object { "  $_" })
  Set-Content -Path (Join-Path -Path $root -ChildPath $marker) -Value $lines
  $paths = @($marker) + ($projects | ForEach-Object { "$_/bin/Release" }) + $definition.Extra
  if ($GitHubOutput) {
    $delimiter = "SLICE_$([guid]::NewGuid().ToString('N'))"
    Add-Content -Path $env:GITHUB_OUTPUT -Value (@("$suite<<$delimiter") + $paths + @($delimiter))
  }
  Write-Output "${suite}: $($projects -join ', ')$(if ($definition.Extra) { " + $($definition.Extra -join ', ')" })"
}
