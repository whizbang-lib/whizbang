#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Points a begun SonarScanner analysis at another pull request, so one build of a merge-queue group is
    reported to SonarCloud once per pull request in the group, each under its own pull-request key.

.DESCRIPTION
    The merge queue builds a group of pull requests once (#2070), and SonarCloud's plan analyzes only pull
    requests and main, so each pull request in the group needs an analysis under its own key. A key is fixed
    at SonarScanner begin, and building the group once per pull request would multiply the most expensive
    job in the queue. The key and the head branch live in the begun analysis's configuration
    (.sonarqube/conf/SonarQubeAnalysisConfig.xml, LocalSettings), which SonarScanner end reads; nothing the
    build wrote depends on them (the base branch, which decides what is new code, is the same for every pull
    request in the group). So each queue leg restores the one begun state, rewrites those two settings and
    ends the scan.

    Refuses a configuration that has no pull-request key: that is a main-line analysis, and turning it into
    a pull-request analysis is not this script's decision.

.PARAMETER ConfigPath
    The analysis configuration, normally .sonarqube/conf/SonarQubeAnalysisConfig.xml.

.PARAMETER Key
    The pull request number.

.PARAMETER Branch
    The pull request's head branch.

.EXAMPLE
    pwsh .github/scripts/Set-SonarPullRequest.ps1 -ConfigPath .sonarqube/conf/SonarQubeAnalysisConfig.xml -Key 2070 -Branch ci/queue
#>

param(
    [string]$ConfigPath = '',
    [string]$Key = '',
    [string]$Branch = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Sets every sonar.pullrequest.key and sonar.pullrequest.branch property, whatever the document's namespace.
function Set-SonarPullRequestProperty {
  param(
      [Parameter(Mandatory)][xml]$Xml,
      [Parameter(Mandatory)][string]$Key,
      [Parameter(Mandatory)][string]$Branch
  )
  $keys = @($Xml.SelectNodes("//*[local-name()='Property' and @Name='sonar.pullrequest.key']"))
  if ($keys.Count -eq 0) { throw 'The analysis configuration is not a pull-request analysis (no sonar.pullrequest.key).' }
  foreach ($node in $keys) { $node.InnerText = $Key }
  foreach ($node in @($Xml.SelectNodes("//*[local-name()='Property' and @Name='sonar.pullrequest.branch']"))) { $node.InnerText = $Branch }
}

# Dot-sourcing for tests loads the function without touching a file.
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $ConfigPath -or -not (Test-Path -LiteralPath $ConfigPath)) {
  Write-Output "::error::No SonarScanner analysis configuration at '$ConfigPath'."
  exit 1
}
$full = (Resolve-Path -LiteralPath $ConfigPath).Path
$xml = [xml]::new()
$xml.PreserveWhitespace = $true
$xml.Load($full)
Set-SonarPullRequestProperty -Xml $xml -Key $Key -Branch $Branch
$xml.Save($full)
Write-Output "The analysis now reports to pull request #$Key ($Branch)."
exit 0
