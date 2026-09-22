#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Watches a pull request's checks and reports each one as it settles; exits by the final outcome.

.DESCRIPTION
    Built for an AI assistant (or a person) monitoring a PR without polling by hand. Prints one line per
    check the moment it leaves "pending", a final summary when every check has settled, and exits 0 when
    all passed, 1 when any failed, 2 on timeout. With -Once it prints the current state and exits.

    Pair with Get-SonarPrFindings.ps1 (quality gate and security findings) and
    Find-UncoveredNewLines.ps1 -DownloadFromRun (the lines the coverage gate wants covered).

.PARAMETER PullRequest
    The PR number.

.PARAMETER Repo
    owner/name; defaults to the current repository as gh resolves it.

.PARAMETER IntervalSeconds
    Poll interval against the GitHub API; 60 is polite and fast enough for CI.

.PARAMETER TimeoutMinutes
    Give up after this long with exit code 2.

.PARAMETER Once
    Print the current state and exit.

.PARAMETER Json
    Emit the final state as JSON on stdout (for an AI to parse) instead of the human summary.

.PARAMETER OutFile
    Optional path; the final state is written there as JSON whatever the console mode, so a recipe
    can keep the report.

.EXAMPLE
    pwsh scripts/Watch-PrChecks.ps1 -PullRequest 732
    pwsh scripts/Watch-PrChecks.ps1 -PullRequest 732 -Once -Json
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][int]$PullRequest,
  [string]$Repo,
  [int]$IntervalSeconds = 60,
  [int]$TimeoutMinutes = 90,
  [switch]$Once,
  [switch]$Json,
  [string]$OutFile
)

$ErrorActionPreference = 'Stop'
$repoArgs = if ($Repo) { @('--repo', $Repo) } else { @() }

function Get-Checks {
  $raw = gh pr checks $PullRequest @repoArgs --json name,bucket,state,link 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $raw) { return $null }
  return ($raw | ConvertFrom-Json)
}

$seen = @{}
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ($true) {
  $checks = Get-Checks
  if ($null -eq $checks) {
    if ($Once) { Write-Error "Could not read checks for PR #$PullRequest."; exit 2 }
    Start-Sleep -Seconds $IntervalSeconds
    continue
  }
  foreach ($c in ($checks | Where-Object { $_.bucket -ne 'pending' } | Sort-Object name)) {
    if (-not $seen.ContainsKey($c.name) -or $seen[$c.name] -ne $c.bucket) {
      $seen[$c.name] = $c.bucket
      if (-not $Json) { Write-Host ("{0,-9} {1}" -f $c.bucket, $c.name) }
    }
  }
  $pending = @($checks | Where-Object { $_.bucket -eq 'pending' })
  $settled = ($checks.Count -gt 0) -and ($pending.Count -eq 0)
  if ($settled -or $Once) {
    $fail = @($checks | Where-Object { $_.bucket -eq 'fail' })
    $pass = @($checks | Where-Object { $_.bucket -eq 'pass' })
    $skip = @($checks | Where-Object { $_.bucket -eq 'skipping' })
    $state = [pscustomobject]@{
      pullRequest = $PullRequest
      settled     = $settled
      pass        = $pass.Count
      fail        = $fail.Count
      skipping    = $skip.Count
      pending     = $pending.Count
      failed      = @($fail | ForEach-Object { [pscustomobject]@{ name = $_.name; link = $_.link } })
      checks      = @($checks | ForEach-Object { [pscustomobject]@{ name = $_.name; bucket = $_.bucket; link = $_.link } })
    }
    if ($OutFile) { $state | ConvertTo-Json -Depth 4 | Set-Content -Path $OutFile }
    if ($Json) {
      $state | ConvertTo-Json -Depth 4
    } else {
      Write-Host ""
      Write-Host ("{0}: pass={1} fail={2} skipping={3} pending={4}" -f ($(if ($settled) { 'ALL CHECKS SETTLED' } else { 'SNAPSHOT' }), $pass.Count, $fail.Count, $skip.Count, $pending.Count))
      foreach ($f in $fail) { Write-Host ("  FAILED  {0}  {1}" -f $f.name, $f.link) }
    }
    if ($fail.Count -gt 0) { exit 1 }
    if ($settled) { exit 0 }
    exit 0
  }
  if ((Get-Date) -gt $deadline) {
    Write-Host "TIMEOUT: $($pending.Count) check(s) still pending after $TimeoutMinutes minutes."
    exit 2
  }
  Start-Sleep -Seconds $IntervalSeconds
}
