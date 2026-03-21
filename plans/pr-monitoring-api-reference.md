# PR Monitoring — GitHub API Building Blocks

Reference for building PR monitoring into scripts. All use `gh` CLI.

---

## 1. Get PR State + All Check Statuses (single call)

```powershell
$json = gh pr view 148 --repo whizbang-lib/whizbang --json `
  state,title,mergeable,mergeStateStatus,reviewDecision,statusCheckRollup,labels

$pr = $json | ConvertFrom-Json
```

**Returns**: Everything you need in one call.
- `$pr.state` → `OPEN`, `CLOSED`, `MERGED`
- `$pr.mergeable` → `MERGEABLE`, `CONFLICTING`, `UNKNOWN`
- `$pr.mergeStateStatus` → `CLEAN` (ready), `BLOCKED`, `BEHIND`, `DIRTY`, `UNSTABLE`
- `$pr.reviewDecision` → `APPROVED`, `CHANGES_REQUESTED`, `REVIEW_REQUIRED`, or `$null`
- `$pr.statusCheckRollup` → Array of check objects (see below)

Each check in `statusCheckRollup`:
```json
{
  "name": "Unit Tests / Unit Tests",
  "status": "COMPLETED",        // or "IN_PROGRESS", "QUEUED"
  "conclusion": "SUCCESS",      // or "FAILURE", "SKIPPED", "NEUTRAL", ""
  "startedAt": "2026-03-21T12:36:57Z",
  "completedAt": "2026-03-21T12:45:25Z",
  "detailsUrl": "https://github.com/.../job/68017439982"
}
```

**Categorize checks**:
```powershell
$passed  = $pr.statusCheckRollup | Where-Object { $_.status -eq "COMPLETED" -and $_.conclusion -eq "SUCCESS" }
$failed  = $pr.statusCheckRollup | Where-Object { $_.status -eq "COMPLETED" -and $_.conclusion -notin @("SUCCESS","SKIPPED","NEUTRAL") }
$pending = $pr.statusCheckRollup | Where-Object { $_.status -ne "COMPLETED" }
```

---

## 2. Quick Pass/Fail Check (one-liner)

```powershell
gh pr checks 148 --repo whizbang-lib/whizbang
# Exit code: 0 = all passed, 8 = pending/failed
```

Returns tab-separated: `name`, `status`, `duration`, `url`. Simple but less structured than JSON.

---

## 3. Get Failed Job Logs

Extract `runId` and `jobId` from the `detailsUrl`, then:

```powershell
# From: https://github.com/owner/repo/actions/runs/23379637111/job/68017439982
$runId = "23379637111"
$jobId = "68017439982"

# Get only the failed step logs (compact)
gh run view $runId --repo whizbang-lib/whizbang --job $jobId --log-failed

# Get full logs (verbose — can be huge)
gh run view $runId --repo whizbang-lib/whizbang --job $jobId --log
```

**Parse URL to extract IDs**:
```powershell
if ($detailsUrl -match "actions/runs/(\d+)/job/(\d+)") {
    $runId = $Matches[1]
    $jobId = $Matches[2]
}
```

---

## 4. Get PR Review Comments / Threads

```powershell
# All reviews (approved, changes requested, etc.)
gh pr view 148 --repo whizbang-lib/whizbang --json reviews | ConvertFrom-Json

# All comments on the PR
gh api repos/whizbang-lib/whizbang/pulls/148/comments | ConvertFrom-Json

# Review threads (includes resolved/unresolved)
gh api repos/whizbang-lib/whizbang/pulls/148/reviews | ConvertFrom-Json
```

---

## 5. Get SonarCloud Quality Gate Status

SonarCloud posts a commit status, visible in `statusCheckRollup` as a check named like `Quality / Quality Analysis` or via the API:

```powershell
# Get commit statuses (includes SonarCloud)
$sha = (gh pr view 148 --repo whizbang-lib/whizbang --json headRefOid | ConvertFrom-Json).headRefOid
gh api repos/whizbang-lib/whizbang/commits/$sha/status | ConvertFrom-Json
```

---

## 6. Polling Pattern

```powershell
$timeout = 1800  # 30 min
$interval = 30   # seconds
$start = Get-Date

while (((Get-Date) - $start).TotalSeconds -lt $timeout) {
    $pr = gh pr view 148 --repo whizbang-lib/whizbang --json statusCheckRollup | ConvertFrom-Json
    $pending = $pr.statusCheckRollup | Where-Object { $_.status -ne "COMPLETED" }

    if ($pending.Count -eq 0) {
        # All done — check for failures
        $failed = $pr.statusCheckRollup | Where-Object { $_.conclusion -notin @("SUCCESS","SKIPPED","NEUTRAL") }
        if ($failed.Count -eq 0) { Write-Host "All passed!"; break }
        else { Write-Host "$($failed.Count) failed"; break }
    }

    Write-Host "$($pending.Count) checks pending..."
    Start-Sleep -Seconds $interval
}
```

---

## 7. Merge Readiness Summary

Combine multiple signals into a single readiness check:

```powershell
$pr = gh pr view 148 --repo whizbang-lib/whizbang --json `
    mergeable,mergeStateStatus,reviewDecision,statusCheckRollup | ConvertFrom-Json

$allChecksPassed = ($pr.statusCheckRollup | Where-Object {
    $_.status -eq "COMPLETED" -and $_.conclusion -notin @("SUCCESS","SKIPPED","NEUTRAL")
}).Count -eq 0

$noPending = ($pr.statusCheckRollup | Where-Object { $_.status -ne "COMPLETED" }).Count -eq 0

$readyToMerge = $allChecksPassed -and $noPending -and
    $pr.mergeable -eq "MERGEABLE" -and
    $pr.mergeStateStatus -eq "CLEAN"

# $readyToMerge → $true when PR can be merged
```

---

## Notes

- All `gh` commands require the GitHub CLI authenticated (`gh auth login`)
- `--repo owner/name` can be omitted if running from within the git repo
- `gh pr view --json` fields: see `gh pr view --help` for full list
- Rate limits: GitHub API allows 5000 req/hr for authenticated users — polling every 30s is ~120 req/hr, well within limits
