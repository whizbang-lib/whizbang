#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Prints a pull request's SonarCloud quality gate and every open issue and hotspot on its new code.

.DESCRIPTION
    The standard for a pull request is zero open Sonar issues of any type and severity (bugs,
    vulnerabilities, code smells, analyzer suggestions surfaced as issues) and zero hotspots to review,
    not merely a passing gate. This script is the one source for that answer, for a person, for an AI
    assistant, and for CI:

    - It prints each quality gate condition with its actual value.
    - It lists every open issue with type, severity, rule, file, line and message, grouped by file.
    - With -WaitForAnalysis it first waits for the analysis the scanner just submitted (the
      report-task.txt the scanner writes), so CI reads the result of THIS run, not the previous one.
    - With -FailOnAny it exits 1 when any issue or hotspot is open, which is how CI gates on it.

    The public SonarCloud API answers all of this for this project without a token. SONAR_TOKEN or a
    .sonarcloud.token file is used when present (required for private projects and for the
    compute-engine task query).

.PARAMETER PullRequest
    The PR number.

.PARAMETER ProjectKey
    SonarCloud project key; defaults to the library's.

.PARAMETER WaitForAnalysis
    Path to the scanner's report-task.txt (usually .sonarqube/out/.sonar/report-task.txt). The
    compute-engine task named there is polled until it completes, up to -WaitMinutes.

.PARAMETER WaitMinutes
    Upper bound for -WaitForAnalysis; 15 by default.

.PARAMETER OutFile
    Optional path; every open finding is written there, one per line as path:line: rule message.
    The file is always written, empty when there is nothing, so a missing file means the script did not run.

.PARAMETER FailOnAny
    Exit 1 when any open issue or hotspot exists. CI passes this.

.PARAMETER Json
    Emit JSON instead of the human report.

.EXAMPLE
    pwsh scripts/Get-SonarPrFindings.ps1 -PullRequest 732
    pwsh scripts/Get-SonarPrFindings.ps1 -PullRequest 732 -WaitForAnalysis .sonarqube/out/.sonar/report-task.txt -OutFile sonar-findings.txt -FailOnAny
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][int]$PullRequest,
  [string]$ProjectKey = 'whizbang-lib_whizbang',
  [string]$WaitForAnalysis,
  [int]$WaitMinutes = 15,
  [string]$OutFile,
  [switch]$FailOnAny,
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

if ($WaitForAnalysis) {
  if (-not (Test-Path $WaitForAnalysis)) { Write-Error "report-task file not found: $WaitForAnalysis" }
  $task = (Get-Content $WaitForAnalysis | Where-Object { $_ -like 'ceTaskId=*' } | Select-Object -First 1) -replace '^ceTaskId=', ''
  if (-not $task) { Write-Error "No ceTaskId in $WaitForAnalysis" }
  $deadline = (Get-Date).AddMinutes($WaitMinutes)
  $status = 'PENDING'
  while ($status -in @('PENDING', 'IN_PROGRESS')) {
    if ((Get-Date) -gt $deadline) { Write-Error "Sonar analysis $task did not complete within $WaitMinutes minutes." }
    Start-Sleep -Seconds 10
    $status = (Invoke-Sonar "ce/task?id=$task").task.status
    Write-Host "Sonar analysis $task`: $status"
  }
  if ($status -ne 'SUCCESS') { Write-Error "Sonar analysis $task ended with status $status." }
}

$gate = Invoke-Sonar "qualitygates/project_status?projectKey=$ProjectKey&pullRequest=$PullRequest"
$issues = @()
$page = 1
do {
  $chunk = Invoke-Sonar "issues/search?componentKeys=$ProjectKey&pullRequest=$PullRequest&resolved=false&ps=500&p=$page"
  $issues += @($chunk.issues)
  $page++
} while ($issues.Count -lt $chunk.total -and $page -le 20)
$hotspots = Invoke-Sonar "hotspots/search?projectKey=$ProjectKey&pullRequest=$PullRequest&status=TO_REVIEW&ps=500"

$findings = @($issues | ForEach-Object {
  [pscustomobject]@{ type = $_.type; severity = $_.severity; rule = $_.rule; file = ($_.component -replace "^${ProjectKey}:", ''); line = $_.line; message = $_.message }
}) + @($hotspots.hotspots | ForEach-Object {
  [pscustomobject]@{ type = 'SECURITY_HOTSPOT'; severity = $_.vulnerabilityProbability; rule = $_.ruleKey; file = ($_.component -replace "^${ProjectKey}:", ''); line = $_.line; message = $_.message }
})
$findings = @($findings | Sort-Object file, line, rule)
$failing = @($gate.projectStatus.conditions | Where-Object { $_.status -ne 'OK' })

if ($OutFile) {
  [System.IO.File]::WriteAllLines($OutFile, [string[]]@($findings | ForEach-Object { "$($_.file):$($_.line): $($_.rule) $($_.message)" }))
}

if ($Json) {
  [pscustomobject]@{
    pullRequest = $PullRequest
    gate        = $gate.projectStatus.status
    failing     = @($failing | ForEach-Object { [pscustomobject]@{ metric = $_.metricKey; actual = $_.actualValue; threshold = $_.errorThreshold; comparator = $_.comparator } })
    findings    = $findings
  } | ConvertTo-Json -Depth 5
} else {
  Write-Host "Quality gate for PR #${PullRequest}: $($gate.projectStatus.status)"
  foreach ($c in $gate.projectStatus.conditions) {
    $mark = if ($c.status -eq 'OK') { 'ok  ' } else { 'FAIL' }
    Write-Host ("  {0} {1,-32} actual={2,-6} threshold {3} {4}" -f $mark, $c.metricKey, $c.actualValue, $c.comparator, $c.errorThreshold)
  }
  Write-Host ""
  Write-Host "Open findings on new code (issues of every type and severity, plus hotspots to review): $($findings.Count)"
  $byFile = $findings | Group-Object file
  foreach ($g in $byFile) {
    Write-Host "  $($g.Name)"
    foreach ($f in $g.Group) {
      Write-Host ("    {0,-6} {1,-8} {2,-24} line {3,-5} {4}" -f $f.type.Substring(0, [Math]::Min(6, $f.type.Length)), $f.severity, $f.rule, $f.line, $f.message)
    }
  }
}

if ($FailOnAny -and $findings.Count -gt 0) {
  Write-Host "::error::$($findings.Count) open Sonar finding(s) on new code. Every finding is fixed (or resolved with a written reason) before a PR merges."
  exit 1
}
exit ($(if ($gate.projectStatus.status -eq 'OK') { 0 } else { 1 }))
