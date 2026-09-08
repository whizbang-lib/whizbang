#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Prints a pull request's SonarCloud quality gate conditions and its open security findings.

.DESCRIPTION
    The public SonarCloud API answers the quality gate and the issue search for this project without a
    token, so an AI assistant can read why "SonarCloud Code Analysis" failed and go fix it: each failing
    condition with its actual value, every open vulnerability and hotspot with file, line, rule and
    message. Per-file "new lines" measures need a token (SONAR_TOKEN or .sonarcloud.token) and are
    printed when one is available.

.PARAMETER PullRequest
    The PR number.

.PARAMETER ProjectKey
    SonarCloud project key; defaults to the library's.

.PARAMETER Json
    Emit JSON instead of the human report.

.EXAMPLE
    pwsh scripts/Get-SonarPrFindings.ps1 -PullRequest 732
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][int]$PullRequest,
  [string]$ProjectKey = 'whizbang-lib_whizbang',
  [switch]$Json
)

$ErrorActionPreference = 'Stop'
$base = 'https://sonarcloud.io/api'

$token = $env:SONAR_TOKEN
if (-not $token -and (Test-Path '.sonarcloud.token')) { $token = (Get-Content '.sonarcloud.token' -Raw).Trim() }
$headers = @{}
if ($token) { $headers['Authorization'] = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${token}:")) }

function Invoke-Sonar([string]$path) {
  return Invoke-RestMethod -Uri "$base/$path" -Headers $headers -Method Get
}

$gate = Invoke-Sonar "qualitygates/project_status?projectKey=$ProjectKey&pullRequest=$PullRequest"
$issues = Invoke-Sonar "issues/search?componentKeys=$ProjectKey&pullRequest=$PullRequest&types=VULNERABILITY,BUG&resolved=false&ps=100"
$hotspots = Invoke-Sonar "hotspots/search?projectKey=$ProjectKey&pullRequest=$PullRequest&status=TO_REVIEW&ps=100"

$failing = @($gate.projectStatus.conditions | Where-Object { $_.status -ne 'OK' })
$vulns = @($issues.issues | ForEach-Object {
  [pscustomobject]@{ type = $_.type; severity = $_.severity; rule = $_.rule; file = ($_.component -replace "^${ProjectKey}:", ''); line = $_.line; message = $_.message }
})
$spots = @($hotspots.hotspots | ForEach-Object {
  [pscustomobject]@{ type = 'SECURITY_HOTSPOT'; severity = $_.vulnerabilityProbability; rule = $_.ruleKey; file = ($_.component -replace "^${ProjectKey}:", ''); line = $_.line; message = $_.message }
})

if ($Json) {
  [pscustomobject]@{
    pullRequest = $PullRequest
    gate        = $gate.projectStatus.status
    failing     = @($failing | ForEach-Object { [pscustomobject]@{ metric = $_.metricKey; actual = $_.actualValue; threshold = $_.errorThreshold; comparator = $_.comparator } })
    findings    = @($vulns + $spots)
  } | ConvertTo-Json -Depth 5
  exit ($(if ($gate.projectStatus.status -eq 'OK') { 0 } else { 1 }))
}

Write-Host "Quality gate for PR #${PullRequest}: $($gate.projectStatus.status)"
foreach ($c in $gate.projectStatus.conditions) {
  $mark = if ($c.status -eq 'OK') { 'ok  ' } else { 'FAIL' }
  Write-Host ("  {0} {1,-32} actual={2,-6} threshold {3} {4}" -f $mark, $c.metricKey, $c.actualValue, $c.comparator, $c.errorThreshold)
}
Write-Host ""
Write-Host "Open security findings (vulnerabilities, bugs, hotspots to review): $(($vulns + $spots).Count)"
foreach ($f in ($vulns + $spots)) {
  Write-Host ("  {0,-17} {1,-8} {2,-22} {3}:{4}" -f $f.type, $f.severity, $f.rule, $f.file, $f.line)
  Write-Host ("      {0}" -f $f.message)
}
if (-not $token) {
  Write-Host ""
  Write-Host "No SONAR_TOKEN: per-file new-line coverage is not available here; use Find-UncoveredNewLines.ps1 -DownloadFromRun <run id> instead."
}
exit ($(if ($gate.projectStatus.status -eq 'OK') { 0 } else { 1 }))
