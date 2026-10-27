#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Pull request intake: keeps each pull request's issue label current, explains the process to an
    outside contributor, handles /accept and /decline on issues, and closes pull requests left without
    an accepted issue.

.DESCRIPTION
    A pull request implements an accepted issue (CONTRIBUTING.md, "How a change gets in"); the required
    "Gate - Linked issue" check (Test-LinkedIssue.ps1) enforces it. This script is the workflow around
    that gate, run by .github/workflows/pr-intake.yml:

      PullRequest  label the pull request issue:accepted, issue:awaiting-acceptance or issue:none (one at
                   a time, for filtering); post one explanation to an outside contributor whose issue is
                   not accepted, giving the date it will be closed; collapse it once the issue is accepted.
      Issue        an issue's accepted label changed: refresh every open pull request that closes it, and
                   re-run the failed jobs of its CI run so the gate sees the acceptance.
      Command      a comment whose first line is a command. On an issue, from a maintainer (the maintain or
                   admin role): /accept (adds accepted, removes needs-triage) or /decline <reason> (wontfix,
                   closed as not planned, with the reason). On a pull request: /late-issue and
                   /docs-not-needed from a maintainer add linked-issue:late or docs:not-needed; /recheck
                   from the author or a committer (write or above) re-runs the failed CI jobs. Each
                   re-checks the pull request, since a label the workflow's token adds starts no
                   workflow. Anyone else is told who can.
      IssueOpened  an issue was opened: when its author is a maintainer, accept it (adds accepted, removes
                   needs-triage), since a maintainer filing work has agreed to it. Anyone else's issue
                   waits for a maintainer's /accept.
      Sweep        close each outside pull request still without an accepted issue Days after it was
                   opened, with a comment explaining the process again.
      Review       a review was submitted or dismissed: re-run the "Gate - Pipeline owner" job
                   (Test-PipelineOwner.ps1) of the pull request's latest CI run when the review can change
                   its verdict, an approval of a failed gate or a change request or dismissal of a passed
                   one. Re-running a job re-runs the jobs that need it, so the CI result follows. A run
                   still going is left alone: the gate has not read the reviews yet, or, if it has,
                   /recheck re-runs it once the run ends.
      Enqueued     a pull request entered the merge queue: run the pipeline-owner check
                   (Test-PipelineOwner.ps1, from this base-branch checkout with its CODEOWNERS) and take a
                   pull request that fails it out of the queue, with a comment saying why (#1566). The CI
                   gate runs from the pull request's own ci.yml, so a pull request could remove it; this
                   workflow runs from the base branch, so it cannot. A check that cannot read the pull
                   request fails, and the pull request leaves the queue (fail closed).
      DocsChanged  a docs-site pull request naming pull request Payload was opened, edited or reopened (a
                   repository_dispatch from the docs repository): re-run the "Gate - Review checks" job of
                   that pull request's latest CI run when it failed before a docs pull request that
                   satisfies its docs link last changed (#1806). Payload must be a pull request number and
                   the pull request open; anything else is ignored. The dispatch only says where to look;
                   whether anything is re-run is decided from what this repository reads.
      CiCompleted  a CI run for HeadSha ended in failure (workflow_run): the same re-check for the open
                   pull request with that head commit. A docs pull request opened while the run was going
                   could not re-run it then (a running run cannot be re-run), so the run's end does.
                   Re-running the job re-runs "Gate - CI result" after it. Only a review check that started
                   before the docs pull request last changed is re-run, so a re-run that fails again on
                   another finding ends the run without starting another.

    The gate stays the authority; the labels describe the linked issue so a maintainer can filter. A
    committer's (write or above) pull request is never explained to or closed; it still needs an
    accepted issue unless its author is a maintainer.

.PARAMETER Repository
    owner/name.

.PARAMETER Mode
    PullRequest, Issue, IssueOpened, Command, Sweep, Review, Enqueued, DocsChanged or CiCompleted.

.PARAMETER Number
    The pull request (PullRequest) or issue (Issue, Command) number.

.PARAMETER CommentId
    The comment holding the command (Command).

.PARAMETER Actor
    Who wrote the command (Command).

.PARAMETER Days
    How long a pull request may wait for an accepted issue before it is closed.

.PARAMETER ReviewState
    The submitted or dismissed review's state (Review): approved, changes_requested, commented or dismissed.

.PARAMETER Payload
    The pull request number a docs-site dispatch names (DocsChanged), as sent: it is validated here.

.PARAMETER HeadSha
    The head commit of the CI run that ended (CiCompleted).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Repository,
  [Parameter(Mandatory)][ValidateSet('PullRequest', 'Issue', 'IssueOpened', 'Command', 'Sweep', 'Review', 'Enqueued', 'DocsChanged', 'CiCompleted')][string]$Mode,
  [int]$Number = 0,
  [long]$CommentId = 0,
  [string]$Actor = '',
  [switch]$IsPullRequest,
  [int]$Days = 14,
  [string]$ReviewState = '',
  [string]$Payload = '',
  [string]$HeadSha = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The gate's own reading of a pull request and its issues; dot-sourcing loads it without running the gate.
. (Join-Path $PSScriptRoot 'Test-LinkedIssue.ps1') -Repository $Repository

$script:IntakeLabels = [ordered]@{
  'accepted'            = 'issue:accepted'
  'awaiting-acceptance' = 'issue:awaiting-acceptance'
  'none'                = 'issue:none'
}
$script:IntakeMarker = '<!-- pr-intake -->'
$script:ClosedMarker = '<!-- pr-intake-closed -->'
$script:IntakeDays = $Days
# The pipeline-owner gate's job name in ci.yml, with the middle dot kept ASCII-only in source.
$script:PipelineOwnerJob = "Gate $([char]0x00B7) Pipeline owner"
$script:ReviewChecksJob = "Gate $([char]0x00B7) Review checks"
# Commands, where they apply, and who may give them.
$script:Commands = @{
  'accept'          = @{ OnPullRequest = $false; Who = 'maintainer' }
  'decline'         = @{ OnPullRequest = $false; Who = 'maintainer' }
  'late-issue'      = @{ OnPullRequest = $true; Who = 'maintainer'; Label = 'linked-issue:late' }
  'docs-not-needed' = @{ OnPullRequest = $true; Who = 'maintainer'; Label = 'docs:not-needed' }
  'recheck'         = @{ OnPullRequest = $true; Who = 'author or committer' }
}

function Get-IntakeLabelChange {
  <# .SYNOPSIS The intake labels to add and remove so the pull request carries exactly the one for State. #>
  param([AllowEmptyCollection()][string[]]$Current = @(), [Parameter(Mandatory)][string]$State)
  $want = if ($script:IntakeLabels.Contains($State)) { $script:IntakeLabels[$State] } else { $null }
  [pscustomobject]@{
    Add    = @(if ($want -and $Current -notcontains $want) { $want })
    Remove = @($script:IntakeLabels.Values | Where-Object { $_ -ne $want -and $Current -contains $_ })
  }
}

function ConvertFrom-IntakeCommand {
  <# .SYNOPSIS The intake command on a comment's first line, with any reason after it, or nothing. #>
  param([AllowEmptyString()][string]$Body = '')
  $first = (($Body -split "`n", 2)[0]).Trim()
  if ($first -match '^/(?<cmd>accept|decline|late-issue|docs-not-needed|recheck)(?:\s+(?<reason>.*))?$') {
    return [pscustomobject]@{ Command = $Matches['cmd'].ToLowerInvariant(); Reason = [string]$Matches['reason'] }
  }
}

function Format-IntakeComment {
  <# .SYNOPSIS The one explanation an outside contributor gets while the pull request has no accepted issue. #>
  param([Parameter(Mandatory)][string]$State, [AllowEmptyCollection()][int[]]$IssueNumbers = @(), [Parameter(Mandatory)][string]$CloseOn)
  $now = if ($State -eq 'none') {
    "This pull request doesn't close an issue yet. Open one (or find one already marked ``accepted``) and add ``Fixes #N`` to the description, as plain text."
  } else {
    "The issue it closes ($((@($IssueNumbers) | ForEach-Object { "#$_" }) -join ', ')) isn't accepted yet. Once a maintainer accepts it, the check re-runs on its own."
  }
  @"
$script:IntakeMarker
Thanks for the pull request! Every change here starts as an issue, where its value and approach are agreed before code is reviewed. A maintainer marks the issue ``accepted`` when it is ready to be worked on, and a pull request implements an accepted issue.

$now

Until then the **Gate · Linked issue** check fails and the pull request isn't reviewed. If there is still no accepted issue on **$CloseOn**, it will be closed; you can reopen it, or open a new one, once the issue is accepted.

See [How a change gets in](https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#how-a-change-gets-in).
"@
}

function Format-IntakeCloseComment {
  <# .SYNOPSIS The final comment on a pull request closed for waiting too long for an accepted issue. #>
  param([Parameter(Mandatory)][int]$Days)
  @"
$script:ClosedMarker
Closing this pull request: $Days days after it was opened, it still doesn't implement an accepted issue. Thank you for the work, and here is how to get it in:

1. Open an issue describing the problem or the change (or comment on an existing one), and discuss the approach there.
2. A maintainer marks the issue ``accepted`` once its value and approach are agreed.
3. Reopen this pull request (or open a new one) with ``Fixes #N`` in the description. The **Gate · Linked issue** check then passes, and the change is reviewed.

Following those steps, a pull request that meets the checks gets reviewed. See [How a change gets in](https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#how-a-change-gets-in).
"@
}

function Select-StalePullRequest {
  <# .SYNOPSIS The outside pull requests still without an accepted issue Days after they were opened. #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$PullRequests, [Parameter(Mandatory)][datetimeoffset]$Now, [Parameter(Mandatory)][int]$Days)
  $PullRequests | Where-Object {
    $_.State -in @('none', 'awaiting-acceptance') -and -not (Test-CommitterRole -RoleName ([string]$_.AuthorRole)) -and ($Now - $_.CreatedAt).TotalDays -ge $Days
  } | ForEach-Object { $_.Number }
}

function Select-FailedCiRun {
  <# .SYNOPSIS The id of the latest pull request run of CI when it failed, or nothing. #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Runs)
  $latest = $Runs | Where-Object { $_.event -eq 'pull_request' } | Sort-Object { [datetimeoffset]$_.createdAt } | Select-Object -Last 1
  if ($latest -and $latest.conclusion -eq 'failure') { return $latest.databaseId }
}

function Select-PipelineOwnerJob {
  <#
  .SYNOPSIS
      The id of the finished "Gate - Pipeline owner" job when a review in ReviewState can change its
      verdict: an approval of a failed gate, or a change request or dismissal of a passed one. Nothing else.
  #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Jobs, [Parameter(Mandatory)][AllowEmptyString()][string]$ReviewState)
  $gate = $Jobs | Where-Object { $_.name -eq $script:PipelineOwnerJob -and $_.status -eq 'completed' } | Select-Object -First 1
  if (-not $gate) { return }
  $wanted = switch ($ReviewState) { 'approved' { 'failure' } { $_ -in @('changes_requested', 'dismissed') } { 'success' } default { '' } }
  if ($wanted -and $gate.conclusion -eq $wanted) { return $gate.databaseId }
}

function Invoke-Gh {
  <# .SYNOPSIS Runs gh; throws when it fails. #>
  param([Parameter(Mandatory)][string[]]$Arguments)
  $output = & gh @Arguments
  if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments[0..1] -join ' ') failed (exit $LASTEXITCODE)" }
  return $output
}

function Get-IntakeComments {
  <# .SYNOPSIS The workflow bot's intake explanations on a pull request: node id and whether collapsed. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number)
  $owner, $name = $Repository -split '/', 2
  $query = 'query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){' +
    'comments(last:100){nodes{id body isMinimized author{login}}}}}}'
  $json = (Invoke-Gh -Arguments @('api', 'graphql', '-f', "query=$query", '-f', "owner=$owner", '-f', "name=$name", '-F', "number=$Number")) -join "`n"
  ($json | ConvertFrom-Json).data.repository.pullRequest.comments.nodes |
    Where-Object { $_.author -and $_.author.login -eq 'github-actions' -and ([string]$_.body).StartsWith($script:IntakeMarker) } |
    ForEach-Object { [pscustomobject]@{ Id = $_.id; IsMinimized = [bool]$_.isMinimized } }
}

function Invoke-IntakeSync {
  <# .SYNOPSIS Brings one pull request's intake label and explanation in line with its linked issue. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number)
  $pr = Read-LinkedPullRequest -Repository $Repository -Number $Number
  if (-not $pr) { throw "Pull request #$Number could not be read." }
  $state = Get-IssueIntakeState -PullRequest $pr

  $change = Get-IntakeLabelChange -Current @($pr.Labels) -State $state
  if ($change.Add.Count + $change.Remove.Count -gt 0) {
    $edit = @('pr', 'edit', "$Number", '-R', $Repository)
    $change.Add | ForEach-Object { $edit += @('--add-label', $_) }
    $change.Remove | ForEach-Object { $edit += @('--remove-label', $_) }
    [void](Invoke-Gh -Arguments $edit)
  }

  $existing = @(Get-IntakeComments -Repository $Repository -Number $Number)
  if ($state -in @('none', 'awaiting-acceptance') -and -not (Test-CommitterRole -RoleName ([string]$pr.AuthorRole)) -and $existing.Count -eq 0) {
    $closeOn = $pr.CreatedAt.AddDays($script:IntakeDays).ToString('yyyy-MM-dd')
    $body = Format-IntakeComment -State $state -IssueNumbers @($pr.Issues | ForEach-Object { $_.Number }) -CloseOn $closeOn
    [void](Invoke-Gh -Arguments @('pr', 'comment', "$Number", '-R', $Repository, '--body', $body))
  }
  if ($state -eq 'accepted') {
    $mutation = 'mutation($id:ID!){minimizeComment(input:{subjectId:$id,classifier:RESOLVED}){minimizedComment{isMinimized}}}'
    foreach ($c in $existing | Where-Object { -not $_.IsMinimized }) {
      [void](Invoke-Gh -Arguments @('api', 'graphql', '-f', "query=$mutation", '-f', "id=$($c.Id)"))
    }
  }
  Write-Host "Pull request #${Number}: $state."
}

function Update-PullRequest {
  <# .SYNOPSIS Re-syncs one pull request's intake state and re-runs its failed CI jobs, so the gates re-read it. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number)
  Invoke-IntakeSync -Repository $Repository -Number $Number
  $head = ((Invoke-Gh -Arguments @('pr', 'view', "$Number", '-R', $Repository, '--json', 'headRefOid', '--jq', '.headRefOid')) -join '').Trim()
  $runs = (Invoke-Gh -Arguments @('run', 'list', '-R', $Repository, '--workflow', 'ci.yml', '--commit', $head,
      '--json', 'databaseId,conclusion,event,createdAt')) -join "`n" | ConvertFrom-Json
  $failed = Select-FailedCiRun -Runs @($runs)
  if ($failed) {
    [void](Invoke-Gh -Arguments @('run', 'rerun', "$failed", '--failed', '-R', $Repository))
    Write-Host "Re-ran the failed jobs of CI run $failed for pull request #$Number."
  }
}

function Update-PipelineOwnerGate {
  <#
  .SYNOPSIS
      Re-runs the pipeline-owner gate of the pull request's latest CI run, and the jobs that need it, when
      the review can change its verdict and the run has ended.
  #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number, [Parameter(Mandatory)][AllowEmptyString()][string]$ReviewState)
  $head = ((Invoke-Gh -Arguments @('pr', 'view', "$Number", '-R', $Repository, '--json', 'headRefOid', '--jq', '.headRefOid')) -join '').Trim()
  $runs = (Invoke-Gh -Arguments @('run', 'list', '-R', $Repository, '--workflow', 'ci.yml', '--commit', $head,
      '--json', 'databaseId,status,conclusion,event,createdAt')) -join "`n" | ConvertFrom-Json
  $latest = @($runs) | Where-Object { $_.event -eq 'pull_request' } | Sort-Object { [datetimeoffset]$_.createdAt } | Select-Object -Last 1
  if (-not $latest) { Write-Host "Pull request #${Number}: no CI run for $head yet."; return }
  if ($latest.status -ne 'completed') {
    Write-Host "Pull request #${Number}: CI run $($latest.databaseId) is still going; its gate reads the reviews when it runs."
    return
  }
  $jobs = (Invoke-Gh -Arguments @('run', 'view', "$($latest.databaseId)", '-R', $Repository, '--json', 'jobs', '--jq', '.jobs')) -join "`n" | ConvertFrom-Json
  $job = Select-PipelineOwnerJob -Jobs @($jobs) -ReviewState $ReviewState
  if (-not $job) { Write-Host "Pull request #${Number}: a $ReviewState review does not change the pipeline-owner gate."; return }
  [void](Invoke-Gh -Arguments @('run', 'rerun', '--job', "$job", '-R', $Repository))
  Write-Host "Re-ran the pipeline-owner gate (job $job) of CI run $($latest.databaseId) for pull request #$Number."
}

function ConvertFrom-PipelineOwnerOutput {
  <#
  .SYNOPSIS
      The pipeline-owner check's verdict from its exit code and output: Pass on exit 0; otherwise the
      ::error:: lines as the problems, or one naming the exit code when it printed none.
  #>
  param([Parameter(Mandatory)][int]$ExitCode, [Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Output)
  if ($ExitCode -eq 0) { return [pscustomobject]@{ Pass = $true; Problems = @() } }
  $problems = @($Output | Where-Object { $_ -like '::error::*' } | ForEach-Object { $_.Substring('::error::'.Length) })
  if ($problems.Count -eq 0) { $problems = @("The pipeline-owner check failed (exit $ExitCode) without saying why; see the PR intake run.") }
  return [pscustomobject]@{ Pass = $false; Problems = $problems }
}

function Invoke-PipelineOwnerCheck {
  <#
  .SYNOPSIS
      Runs Test-PipelineOwner.ps1 from this checkout, in its own process (it shares function names with
      Test-LinkedIssue.ps1, which this script dot-sources), and returns its verdict.
  #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number)
  $output = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-PipelineOwner.ps1') -Repository $Repository -PullRequest $Number 2>&1 |
      ForEach-Object { "$_" })
  $output | ForEach-Object { Write-Host $_ }
  ConvertFrom-PipelineOwnerOutput -ExitCode $LASTEXITCODE -Output $output
}

function Invoke-QueueGuard {
  <#
  .SYNOPSIS
      A pull request entered the merge queue: take it out, with a comment, when it fails the
      pipeline-owner check. Throws when it cannot be taken out, so the run fails where it is seen.
  #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number)
  $check = Invoke-PipelineOwnerCheck -Repository $Repository -Number $Number
  if ($check.Pass) { Write-Host "Pull request #${Number}: passes the pipeline-owner check; left in the queue."; return }

  $id = ((Invoke-Gh -Arguments @('pr', 'view', "$Number", '-R', $Repository, '--json', 'id', '--jq', '.id')) -join '').Trim()
  $mutation = 'mutation($id:ID!){dequeuePullRequest(input:{id:$id}){mergeQueueEntry{id}}}'
  [void](Invoke-Gh -Arguments @('api', 'graphql', '-f', "query=$mutation", '-f', "id=$id"))
  $body = "Taken out of the merge queue: the pipeline-owner check failed. It runs from the base branch when a pull " +
    "request is queued, so a change to ci.yml cannot skip it (#1566).`n`n" + (($check.Problems | ForEach-Object { "- $_" }) -join "`n")
  [void](Invoke-Gh -Arguments @('pr', 'comment', "$Number", '-R', $Repository, '--body', $body))
  Write-Host "Pull request #${Number}: failed the pipeline-owner check; taken out of the merge queue."
}

function Update-PullRequestsClosing {
  <# .SYNOPSIS Refreshes every open pull request that closes the issue. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Issue)
  $owner, $name = $Repository -split '/', 2
  $query = 'query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){issue(number:$number){' +
    'closedByPullRequestsReferences(first:20,includeClosedPrs:false){nodes{number}}}}}'
  $json = (Invoke-Gh -Arguments @('api', 'graphql', '-f', "query=$query", '-f', "owner=$owner", '-f', "name=$name", '-F', "number=$Issue")) -join "`n"
  foreach ($pr in ($json | ConvertFrom-Json).data.repository.issue.closedByPullRequestsReferences.nodes) {
    Update-PullRequest -Repository $Repository -Number ([int]$pr.number)
  }
}

function Get-PullRequestAuthor {
  <# .SYNOPSIS The login that opened the pull request. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number)
  ((Invoke-Gh -Arguments @('pr', 'view', "$Number", '-R', $Repository, '--json', 'author', '--jq', '.author.login')) -join '').Trim()
}

function Invoke-IntakeCommand {
  <# .SYNOPSIS Applies an intake command on an issue or a pull request, from someone allowed to give it. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Issue, [AllowEmptyString()][string]$Body = '',
    [Parameter(Mandatory)][string]$Actor, [long]$CommentId = 0, [switch]$IsPullRequest)
  $command = ConvertFrom-IntakeCommand -Body $Body
  if (-not $command) { return }
  $rule = $script:Commands[$command.Command]
  # An issue command on a pull request, or the other way round, is not a command here.
  if ($rule.OnPullRequest -ne [bool]$IsPullRequest) { return }
  $kind = if ($IsPullRequest) { 'pr' } else { 'issue' }

  $role = Get-RepositoryRole -Repository $Repository -Login $Actor
  $allowed = if ($rule.Who -eq 'maintainer') { Test-MaintainerRole -RoleName $role }
    else { (Test-CommitterRole -RoleName $role) -or $Actor -eq (Get-PullRequestAuthor -Repository $Repository -Number $Issue) }
  if (-not $allowed) {
    $text = "Only a $($rule.Who) can use /$($command.Command) here. A maintainer will take a look; thanks for your patience."
    [void](Invoke-Gh -Arguments @($kind, 'comment', "$Issue", '-R', $Repository, '--body', $text))
    return
  }

  switch ($command.Command) {
    'accept' {
      [void](Invoke-Gh -Arguments @('issue', 'edit', "$Issue", '-R', $Repository, '--add-label', 'accepted', '--remove-label', 'needs-triage'))
      # A label the workflow's own token adds starts no other workflow, so the pull requests are refreshed here.
      Update-PullRequestsClosing -Repository $Repository -Issue $Issue
    }
    'decline' {
      [void](Invoke-Gh -Arguments @('issue', 'edit', "$Issue", '-R', $Repository, '--add-label', 'wontfix', '--remove-label', 'needs-triage', '--remove-label', 'accepted'))
      $why = if ($command.Reason) { " $($command.Reason)" } else { '' }
      $text = "Declined by a maintainer.$why Thanks for raising it; this issue is closed as not planned."
      [void](Invoke-Gh -Arguments @('issue', 'close', "$Issue", '-R', $Repository, '--reason', 'not planned', '--comment', $text))
    }
    'recheck' { Update-PullRequest -Repository $Repository -Number $Issue }
    default {
      [void](Invoke-Gh -Arguments @('pr', 'edit', "$Issue", '-R', $Repository, '--add-label', $rule.Label))
      Update-PullRequest -Repository $Repository -Number $Issue
    }
  }
  if ($CommentId -gt 0) {
    [void](Invoke-Gh -Arguments @('api', '-X', 'POST', "repos/$Repository/issues/comments/$CommentId/reactions", '-f', 'content=+1'))
  }
}

function Invoke-IssueOpened {
  <# .SYNOPSIS Accepts an issue its maintainer author opened; leaves anyone else's for a maintainer. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Issue, [Parameter(Mandatory)][string]$Author)
  if (-not (Test-MaintainerRole -RoleName (Get-RepositoryRole -Repository $Repository -Login $Author))) { return }
  [void](Invoke-Gh -Arguments @('issue', 'edit', "$Issue", '-R', $Repository, '--add-label', 'accepted', '--remove-label', 'needs-triage'))
  Write-Host "Issue #${Issue}: opened by a maintainer, accepted."
}

function Invoke-IntakeSweep {
  <# .SYNOPSIS Closes the outside pull requests still without an accepted issue after the grace period. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Days)
  $open = (Invoke-Gh -Arguments @('pr', 'list', '-R', $Repository, '--state', 'open', '--limit', '500', '--json', 'number,labels')) -join "`n" | ConvertFrom-Json
  $candidates = @($open | Where-Object { @($_.labels.name) -contains 'issue:none' -or @($_.labels.name) -contains 'issue:awaiting-acceptance' })
  $read = foreach ($c in $candidates) {
    $pr = Read-LinkedPullRequest -Repository $Repository -Number ([int]$c.number)
    if ($pr) { [pscustomobject]@{ Number = $pr.Number; CreatedAt = $pr.CreatedAt; State = (Get-IssueIntakeState -PullRequest $pr); AuthorRole = $pr.AuthorRole } }
  }
  foreach ($n in Select-StalePullRequest -PullRequests @($read) -Now ([datetimeoffset]::UtcNow) -Days $Days) {
    [void](Invoke-Gh -Arguments @('pr', 'close', "$n", '-R', $Repository, '--comment', (Format-IntakeCloseComment -Days $Days)))
    Write-Host "Closed pull request #${n}: no accepted issue after $Days days."
  }
}

function ConvertTo-PullRequestNumber {
  <# .SYNOPSIS The pull request number in Text when it is a positive whole number of at most nine digits, or nothing. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
  if ($Text -match '^\s*([1-9]\d{0,8})\s*$') { return [int]$Matches[1] }
}

function ConvertFrom-DocsPullRequestsJson {
  <# .SYNOPSIS The docs pull requests Get-DocsPullRequests.ps1 writes: Number, State, ReferencesThis, UpdatedAt. #>
  param([Parameter(Mandatory)][string]$Json)
  ($Json | ConvertFrom-Json) | Where-Object { $_ } | ForEach-Object {
    [pscustomobject]@{ Number = [int]$_.number; State = [string]$_.state; ReferencesThis = [bool]$_.referencesThis
      UpdatedAt = $(if ($_.updatedAt) { [datetimeoffset]$_.updatedAt } else { $null }) }
  }
}

function Select-StaleReviewChecksJob {
  <#
  .SYNOPSIS
      The id of the failed "Gate - Review checks" job when a docs-site pull request that satisfies its docs
      link (open or merged, and naming this pull request) changed after the job started. Nothing else: a
      review check that already read the docs pull request as it is now would fail the same way again.
  #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Jobs, [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$DocsPullRequests)
  $job = $Jobs | Where-Object { $_.name -eq $script:ReviewChecksJob -and $_.status -eq 'completed' } | Select-Object -First 1
  if (-not $job -or $job.conclusion -ne 'failure') { return }
  $started = [datetimeoffset]$job.startedAt
  $changed = @($DocsPullRequests | Where-Object { $_.State -in @('OPEN', 'MERGED') -and $_.ReferencesThis -and $_.UpdatedAt -and [datetimeoffset]$_.UpdatedAt -gt $started })
  if ($changed.Count -gt 0) { return $job.databaseId }
}

function Get-LinkedDocsPullRequests {
  <#
  .SYNOPSIS
      The docs-site pull requests found for a pull request, read as the review check reads them: by running
      Get-DocsPullRequests.ps1 from this checkout, in its own process.
  #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number)
  $json = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Get-DocsPullRequests.ps1') -Repository $Repository -PullRequest $Number
  if ($LASTEXITCODE -ne 0) { throw "Reading the docs pull requests of #$Number failed (exit $LASTEXITCODE)." }
  ConvertFrom-DocsPullRequestsJson -Json ($json -join "`n")
}

function Get-OpenPullRequests {
  <# .SYNOPSIS The open pull requests: number and head commit. #>
  param([Parameter(Mandatory)][string]$Repository)
  (Invoke-Gh -Arguments @('pr', 'list', '-R', $Repository, '--state', 'open', '--limit', '500', '--json', 'number,headRefOid')) -join "`n" | ConvertFrom-Json
}

function Update-DocsLinkedReviewChecks {
  <#
  .SYNOPSIS
      Re-runs the review check of the pull request's latest CI run for its head commit, and the CI result
      after it, when the run has ended and the check failed before a docs pull request that satisfies its
      docs link last changed.
  #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number, [Parameter(Mandatory)][string]$Head)
  $runs = (Invoke-Gh -Arguments @('run', 'list', '-R', $Repository, '--workflow', 'ci.yml', '--commit', $Head,
      '--json', 'databaseId,status,conclusion,event,createdAt')) -join "`n" | ConvertFrom-Json
  $latest = @($runs) | Where-Object { $_.event -eq 'pull_request' } | Sort-Object { [datetimeoffset]$_.createdAt } | Select-Object -Last 1
  if (-not $latest) { Write-Host "Pull request #${Number}: no CI run for $Head yet."; return }
  if ($latest.status -ne 'completed') {
    Write-Host "Pull request #${Number}: CI run $($latest.databaseId) is still going; its review check is re-checked when it ends."
    return
  }
  $jobs = (Invoke-Gh -Arguments @('run', 'view', "$($latest.databaseId)", '-R', $Repository, '--json', 'jobs', '--jq', '.jobs')) -join "`n" | ConvertFrom-Json
  $docs = @(Get-LinkedDocsPullRequests -Repository $Repository -Number $Number)
  $job = Select-StaleReviewChecksJob -Jobs @($jobs) -DocsPullRequests $docs
  if (-not $job) { Write-Host "Pull request #${Number}: the review check of CI run $($latest.databaseId) is not failed and stale; nothing to re-run."; return }
  [void](Invoke-Gh -Arguments @('run', 'rerun', '--job', "$job", '-R', $Repository))
  Write-Host "Re-ran the review check (job $job) of CI run $($latest.databaseId) for pull request #$Number, and the CI result after it."
}

function Invoke-DocsChanged {
  <# .SYNOPSIS A docs-site pull request naming pull request Payload changed: re-check it when it is an open pull request. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][AllowEmptyString()][string]$Payload)
  $number = ConvertTo-PullRequestNumber -Text $Payload
  if (-not $number) { Write-Host 'Ignored a docs dispatch whose pull request is not a number.'; return }
  $pr = @(Get-OpenPullRequests -Repository $Repository) | Where-Object { [int]$_.number -eq $number } | Select-Object -First 1
  if (-not $pr) { Write-Host "Ignored a docs dispatch for #${number}: not an open pull request here."; return }
  Update-DocsLinkedReviewChecks -Repository $Repository -Number $number -Head ([string]$pr.headRefOid)
}

function Invoke-CiCompleted {
  <# .SYNOPSIS A CI run for HeadSha ended in failure: re-check the open pull request with that head commit. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][AllowEmptyString()][string]$HeadSha)
  if ($HeadSha -notmatch '^[0-9a-f]{40}$') { Write-Host 'Ignored a CI completion whose head is not a commit id.'; return }
  $prs = @(Get-OpenPullRequests -Repository $Repository) | Where-Object { [string]$_.headRefOid -eq $HeadSha }
  if (-not $prs) { Write-Host "No open pull request has head $HeadSha."; return }
  foreach ($pr in $prs) { Update-DocsLinkedReviewChecks -Repository $Repository -Number ([int]$pr.number) -Head $HeadSha }
}

# Dot-sourcing for tests loads the functions without running anything.
if ($MyInvocation.InvocationName -eq '.') { return }

switch ($Mode) {
  'PullRequest' { Invoke-IntakeSync -Repository $Repository -Number $Number }
  'Issue' { Update-PullRequestsClosing -Repository $Repository -Issue $Number }
  'IssueOpened' { Invoke-IssueOpened -Repository $Repository -Issue $Number -Author $Actor }
  'Command' {
    $body = (Invoke-Gh -Arguments @('api', "repos/$Repository/issues/comments/$CommentId", '--jq', '.body')) -join "`n"
    Invoke-IntakeCommand -Repository $Repository -Issue $Number -Body $body -Actor $Actor -CommentId $CommentId -IsPullRequest:$IsPullRequest
  }
  'Sweep' { Invoke-IntakeSweep -Repository $Repository -Days $Days }
  'Review' { Update-PipelineOwnerGate -Repository $Repository -Number $Number -ReviewState $ReviewState }
  'Enqueued' { Invoke-QueueGuard -Repository $Repository -Number $Number }
  'DocsChanged' { Invoke-DocsChanged -Repository $Repository -Payload $Payload }
  'CiCompleted' { Invoke-CiCompleted -Repository $Repository -HeadSha $HeadSha }
}
exit 0
