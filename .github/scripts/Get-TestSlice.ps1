#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Lists, per integration test suite, the build output that suite needs, so each suite downloads
    its own slice of the build instead of all of it.

.DESCRIPTION
    Every test job used to download the whole build (about 2.4 GB compressed), though an integration
    suite runs one or two projects whose output is a few hundred megabytes, and the download time
    varied from about 40 seconds to over 5 minutes. A test project's bin/Release already holds its
    whole dependency closure, so that is all a suite needs from the build; sources come from the job's
    own checkout.

    A suite's projects are chosen exactly as Run-Tests.ps1 chooses them for that suite: projects in
    Whizbang.slnx that declare <WhizbangTestType>Integration</WhizbangTestType> and carry the suite's
    tag in <WhizbangTestTags>. A new project with the tag joins the slice with no change here, and a
    slice that misses one fails the suite: Run-Tests.ps1 -NoBuild refuses a selected project with no
    build output.

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

# Suite -> the tag its reusable workflow passes to Run-Tests.ps1 (-Tag), and any further build output it
# needs. Service Bus runs the ECommerce suite through an Aspire app host, which starts the sample
# services from their own build output. The unit suite is absent on purpose: it runs most projects,
# so it keeps the full build. .github/scripts/tests/Get-TestSlice.Tests.ps1 checks this table against
# the workflows.
$script:Suites = [ordered]@{
  postgres   = @{ Tag = 'Postgres';        Extra = @() }
  inmemory   = @{ Tag = 'InMemory';        Extra = @() }
  rabbitmq   = @{ Tag = 'RabbitMQ';        Extra = @() }
  servicebus = @{ Tag = 'AzureServiceBus'; Extra = @('samples/ECommerce/**/bin/Release') }
  azureblob  = @{ Tag = 'AzureBlob';       Extra = @() }
  # In-process integration projects whose only suite tag is the plain "Integration" (#1196).
  integration = @{ Tag = 'Integration';    Extra = @() }
}

# The integration projects in the solution that carry a tag, as repository-relative directories.
function Get-TestSliceProject([string]$Root, [string]$Tag) {
  $solution = Get-Content (Join-Path -Path $Root -ChildPath 'Whizbang.slnx') -Raw
  $projects = [regex]::Matches($solution, '<Project Path="([^"]+\.csproj)"') | ForEach-Object { $_.Groups[1].Value }
  foreach ($relative in $projects) {
    $path = Join-Path -Path $Root -ChildPath $relative
    if (-not (Test-Path $path)) { continue }
    $content = Get-Content $path -Raw
    if ($content -notmatch '<WhizbangTestType>Integration</WhizbangTestType>') { continue }
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
  $projects = @(Get-TestSliceProject -Root $root -Tag $definition.Tag | Sort-Object)
  if ($projects.Count -eq 0) { throw "The $suite slice selects no project tagged '$($definition.Tag)'." }
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
