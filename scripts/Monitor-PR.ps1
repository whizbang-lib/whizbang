#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Monitors a GitHub PR's checks, quality gates, and review status until ready to merge.

.DESCRIPTION
    Polls GitHub API via `gh` CLI to track all CI checks, extract errors from failures,
    and report when the PR is mergeable or blocked. Designed for both interactive use
    and consumption by automation scripts (JSON output mode).

.PARAMETER PR
    PR number to monitor.

.PARAMETER Repo
    Repository in owner/name format (default: detected from current git remote).

.PARAMETER PollInterval
    Seconds between status polls (default: 30).

.PARAMETER Timeout
    Maximum seconds to wait before giving up (default: 1800 = 30 min).

.PARAMETER OutputFormat
    Output format: Text (human-readable) or Json (machine-readable). Default: Text.

.PARAMETER AiMode
    Sparse output optimized for AI token consumption.

.PARAMETER Once
    Check once and exit (no polling).

.EXAMPLE
    ./Monitor-PR.ps1 -PR 148
    Monitors PR #148 until all checks complete.

.EXAMPLE
    ./Monitor-PR.ps1 -PR 148 -Once -OutputFormat Json
    Single check, outputs JSON for script consumption.

.EXAMPLE
    ./Monitor-PR.ps1 -PR 148 -AiMode -Timeout 600
    AI-friendly monitoring with 10 min timeout.
#>

param(
    [Parameter(Mandatory)]
    [int]$PR,

    [string]$Repo,

    [int]$PollInterval = 30,

    [int]$Timeout = 1800,

    [ValidateSet("Text", "Json")]
    [string]$OutputFormat = "Text",

    [switch]$AiMode,

    [switch]$Once
)

# --- Helper: Detect repo from git remote ---
function Get-RepoFromGit {
    $remote = git remote get-url origin 2>$null
    if ($remote -match "github\.com[:/](.+?)(?:\.git)?$") {
        return $Matches[1]
    }
    throw "Cannot detect repo from git remote. Use -Repo owner/name."
}

# --- Helper: Fetch PR metadata ---
function Get-PRStatus {
    param([string]$Repo, [int]$PR)

    $json = gh pr view $PR --repo $Repo --json `
        state,title,mergeable,mergeStateStatus,reviewDecision,statusCheckRollup,labels `
        2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to fetch PR #$PR from $Repo : $json"
    }

    return $json | ConvertFrom-Json
}

# --- Helper: Fetch failed job logs ---
function Get-FailedJobLog {
    param([string]$Repo, [string]$DetailsUrl)

    # Extract run ID and job ID from URL
    # Format: https://github.com/owner/repo/actions/runs/{runId}/job/{jobId}
    if ($DetailsUrl -match "actions/runs/(\d+)/job/(\d+)") {
        $runId = $Matches[1]
        $jobId = $Matches[2]

        $log = gh run view $runId --repo $Repo --job $jobId --log-failed 2>&1
        if ($LASTEXITCODE -eq 0 -and $log) {
            # Trim to last 50 lines to keep output manageable
            $lines = $log -split "`n"
            if ($lines.Count -gt 50) {
                return ($lines | Select-Object -Last 50) -join "`n"
            }
            return $log
        }
    }
    return $null
}

# --- Helper: Categorize check status ---
function Get-CheckSummary {
    param($checks)

    $result = @{
        Passed    = @()
        Failed    = @()
        Pending   = @()
        Skipped   = @()
    }

    foreach ($check in $checks) {
        $name = $check.name
        $status = $check.status
        $conclusion = $check.conclusion

        if ($status -eq "COMPLETED") {
            switch ($conclusion) {
                "SUCCESS"   { $result.Passed += $check }
                "SKIPPED"   { $result.Skipped += $check }
                "NEUTRAL"   { $result.Skipped += $check }
                default     { $result.Failed += $check }
            }
        } else {
            $result.Pending += $check
        }
    }

    return $result
}

# --- Helper: Format duration ---
function Format-Duration {
    param([datetime]$Start, [datetime]$End)
    $span = $End - $Start
    if ($span.TotalMinutes -ge 1) {
        return "{0:F0}m {1:F0}s" -f [Math]::Floor($span.TotalMinutes), $span.Seconds
    }
    return "{0:F1}s" -f $span.TotalSeconds
}

# --- Helper: Build structured result ---
function Build-Result {
    param($prData, $checkSummary, [string]$overallStatus, $failureLogs)

    return @{
        pr             = $PR
        repo           = $Repo
        title          = $prData.title
        state          = $prData.state
        mergeable      = $prData.mergeable
        mergeState     = $prData.mergeStateStatus
        reviewDecision = $prData.reviewDecision
        overallStatus  = $overallStatus
        checks         = @{
            passed  = $checkSummary.Passed | ForEach-Object { @{ name = $_.name; conclusion = $_.conclusion; duration = (Format-Duration ([datetime]$_.startedAt) ([datetime]$_.completedAt)) } }
            failed  = $checkSummary.Failed | ForEach-Object { @{ name = $_.name; conclusion = $_.conclusion; detailsUrl = $_.detailsUrl } }
            pending = $checkSummary.Pending | ForEach-Object { @{ name = $_.name; status = $_.status } }
            skipped = $checkSummary.Skipped | ForEach-Object { @{ name = $_.name } }
        }
        failureLogs    = $failureLogs
        timestamp      = (Get-Date -Format "o")
    }
}

# --- Main ---

if (-not $Repo) {
    $Repo = Get-RepoFromGit
}

$startTime = Get-Date
$lastPendingCount = -1

if (-not $AiMode -and $OutputFormat -eq "Text") {
    Write-Host "Monitoring PR #$PR on $Repo" -ForegroundColor Cyan
    Write-Host "Poll interval: ${PollInterval}s | Timeout: ${Timeout}s" -ForegroundColor Gray
    Write-Host ""
}

while ($true) {
    $elapsed = ((Get-Date) - $startTime).TotalSeconds

    if ($elapsed -gt $Timeout -and -not $Once) {
        if ($OutputFormat -eq "Json") {
            @{ pr = $PR; overallStatus = "TIMEOUT"; elapsed = $elapsed } | ConvertTo-Json -Depth 10
        } else {
            Write-Host "Timeout after ${Timeout}s. Checks still pending." -ForegroundColor Red
        }
        exit 2
    }

    # Fetch PR data
    try {
        $prData = Get-PRStatus -Repo $Repo -PR $PR
    } catch {
        Write-Host "Error: $_" -ForegroundColor Red
        exit 1
    }

    $checks = $prData.statusCheckRollup
    $summary = Get-CheckSummary -checks $checks
    $totalChecks = $checks.Count
    $passedCount = $summary.Passed.Count
    $failedCount = $summary.Failed.Count
    $pendingCount = $summary.Pending.Count
    $skippedCount = $summary.Skipped.Count

    # Determine overall status
    $overallStatus = if ($failedCount -gt 0) {
        "FAILED"
    } elseif ($pendingCount -gt 0) {
        "PENDING"
    } else {
        "PASSED"
    }

    # Text output
    if ($OutputFormat -eq "Text") {
        if ($AiMode) {
            # Only print when status changes
            if ($pendingCount -ne $lastPendingCount -or $overallStatus -ne "PENDING") {
                $ts = Get-Date -Format "HH:mm:ss"
                Write-Host "[$ts] PR #$PR: $passedCount passed, $failedCount failed, $pendingCount pending ($overallStatus)" -ForegroundColor $(
                    if ($overallStatus -eq "PASSED") { "Green" }
                    elseif ($overallStatus -eq "FAILED") { "Red" }
                    else { "Yellow" }
                )
            }
        } else {
            Write-Host "--- $(Get-Date -Format 'HH:mm:ss') ---" -ForegroundColor Gray

            foreach ($c in ($summary.Passed | Sort-Object { $_.name })) {
                $dur = if ($c.completedAt -and $c.startedAt -and $c.completedAt -ne "0001-01-01T00:00:00Z") {
                    Format-Duration ([datetime]$c.startedAt) ([datetime]$c.completedAt)
                } else { "" }
                Write-Host "  ✅ $($c.name) ($dur)" -ForegroundColor Green
            }
            foreach ($c in ($summary.Pending | Sort-Object { $_.name })) {
                Write-Host "  ⏳ $($c.name)" -ForegroundColor Yellow
            }
            foreach ($c in ($summary.Failed | Sort-Object { $_.name })) {
                Write-Host "  ❌ $($c.name) — $($c.conclusion)" -ForegroundColor Red
                Write-Host "     $($c.detailsUrl)" -ForegroundColor DarkRed
            }
            foreach ($c in ($summary.Skipped | Sort-Object { $_.name })) {
                Write-Host "  ⏭️  $($c.name)" -ForegroundColor DarkGray
            }

            Write-Host "  Total: $passedCount/$totalChecks passed" -ForegroundColor $(if ($overallStatus -eq "PASSED") { "Green" } else { "Gray" })
            Write-Host ""
        }
    }

    $lastPendingCount = $pendingCount

    # Terminal states: all checks done (passed or failed)
    if ($overallStatus -ne "PENDING" -or $Once) {

        # Fetch failure logs
        $failureLogs = @{}
        foreach ($failed in $summary.Failed) {
            $log = Get-FailedJobLog -Repo $Repo -DetailsUrl $failed.detailsUrl
            if ($log) {
                $failureLogs[$failed.name] = $log
            }
        }

        # JSON output
        if ($OutputFormat -eq "Json") {
            $result = Build-Result -prData $prData -checkSummary $summary `
                -overallStatus $overallStatus -failureLogs $failureLogs
            $result | ConvertTo-Json -Depth 10
            exit $(if ($overallStatus -eq "PASSED") { 0 } elseif ($overallStatus -eq "FAILED") { 1 } else { 2 })
        }

        # Text terminal output
        if ($overallStatus -eq "PASSED") {
            Write-Host "=====================================" -ForegroundColor Green
            Write-Host "  All checks passed! PR is ready." -ForegroundColor Green
            Write-Host "=====================================" -ForegroundColor Green
            Write-Host ""
            Write-Host "Merge state: $($prData.mergeStateStatus)" -ForegroundColor $(if ($prData.mergeStateStatus -eq "CLEAN") { "Green" } else { "Yellow" })
            Write-Host "Review: $($prData.reviewDecision ?? 'No reviews')" -ForegroundColor $(if ($prData.reviewDecision -eq "APPROVED") { "Green" } else { "Yellow" })
            Write-Host ""
            Write-Host "https://github.com/$Repo/pull/$PR" -ForegroundColor Cyan
            exit 0
        }

        if ($overallStatus -eq "FAILED") {
            Write-Host "=====================================" -ForegroundColor Red
            Write-Host "  $failedCount check(s) failed" -ForegroundColor Red
            Write-Host "=====================================" -ForegroundColor Red

            foreach ($failed in $summary.Failed) {
                Write-Host ""
                Write-Host "❌ $($failed.name)" -ForegroundColor Red
                Write-Host "   $($failed.detailsUrl)" -ForegroundColor DarkRed

                if ($failureLogs.ContainsKey($failed.name)) {
                    Write-Host "   --- Log (last 50 lines) ---" -ForegroundColor DarkGray
                    $failureLogs[$failed.name] -split "`n" | ForEach-Object {
                        Write-Host "   $_" -ForegroundColor DarkGray
                    }
                }
            }

            if ($AiMode) {
                Write-Host ""
                Write-Host "=== AI INSTRUCTIONS ===" -ForegroundColor Magenta
                Write-Host "CI checks failed. For each failure:" -ForegroundColor Magenta
                Write-Host "1. Read the failed job log above to identify root cause" -ForegroundColor Magenta
                Write-Host "2. Fix ALL failures, including pre-existing ones (Boy Scout rule)" -ForegroundColor Magenta
                Write-Host "3. For flaky tests: remove race conditions, use proper sync, model after stable tests" -ForegroundColor Magenta
                Write-Host "4. Spin up an agent per independent fix for parallelism" -ForegroundColor Magenta
                Write-Host "5. Push fixes and re-run this script to verify" -ForegroundColor Magenta
                Write-Host "========================" -ForegroundColor Magenta
            }

            exit 1
        }

        # Still pending but -Once was set
        if ($Once) {
            Write-Host "$pendingCount check(s) still pending." -ForegroundColor Yellow
            exit 2
        }
    }

    # Poll wait
    Start-Sleep -Seconds $PollInterval
}
