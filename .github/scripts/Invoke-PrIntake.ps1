#!/usr/bin/env pwsh
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
      Command      a comment on an issue starting /accept or /decline <reason>, from a maintainer (the
                   maintain or admin role): accept it (removing needs-triage), or decline it (wontfix,
                   closed as not planned, with the reason). Anyone else is told only maintainers can.
      Sweep        close each outside pull request still without an accepted issue Days after it was
                   opened, with a comment explaining the process again.

    The gate stays the authority; the labels describe the linked issue so a maintainer can filter. A
    maintainer's own pull request is never explained to or closed.

.PARAMETER Repository
    owner/name.

.PARAMETER Mode
    PullRequest, Issue, Command or Sweep.

.PARAMETER Number
    The pull request (PullRequest) or issue (Issue, Command) number.

.PARAMETER CommentId
    The comment holding the command (Command).

.PARAMETER Actor
    Who wrote the command (Command).

.PARAMETER Days
    How long a pull request may wait for an accepted issue before it is closed.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Repository,
  [Parameter(Mandatory)][ValidateSet('PullRequest', 'Issue', 'Command', 'Sweep')][string]$Mode,
  [int]$Number = 0,
  [long]$CommentId = 0,
  [string]$Actor = '',
  [int]$Days = 14
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
  <# .SYNOPSIS The /accept or /decline command on a comment's first line, or nothing. #>
  param([AllowEmptyString()][string]$Body = '')
  $first = (($Body -split "`n", 2)[0]).Trim()
  if ($first -match '^/(?<cmd>accept|decline)(?:\s+(?<reason>.*))?$') {
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
    $_.State -in @('none', 'awaiting-acceptance') -and -not $_.AuthorIsMaintainer -and ($Now - $_.CreatedAt).TotalDays -ge $Days
  } | ForEach-Object { $_.Number }
}

function Select-FailedCiRun {
  <# .SYNOPSIS The id of the latest pull request run of CI when it failed, or nothing. #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Runs)
  $latest = $Runs | Where-Object { $_.event -eq 'pull_request' } | Sort-Object { [datetimeoffset]$_.createdAt } | Select-Object -Last 1
  if ($latest -and $latest.conclusion -eq 'failure') { return $latest.databaseId }
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
  if ($state -in @('none', 'awaiting-acceptance') -and -not $pr.AuthorIsMaintainer -and $existing.Count -eq 0) {
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

function Update-PullRequestsClosing {
  <# .SYNOPSIS Refreshes every open pull request that closes the issue, re-running its failed CI jobs. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Issue)
  $owner, $name = $Repository -split '/', 2
  $query = 'query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){issue(number:$number){' +
    'closedByPullRequestsReferences(first:20,includeClosedPrs:false){nodes{number headRefOid}}}}}'
  $json = (Invoke-Gh -Arguments @('api', 'graphql', '-f', "query=$query", '-f', "owner=$owner", '-f', "name=$name", '-F', "number=$Issue")) -join "`n"
  foreach ($pr in ($json | ConvertFrom-Json).data.repository.issue.closedByPullRequestsReferences.nodes) {
    Invoke-IntakeSync -Repository $Repository -Number ([int]$pr.number)
    $runs = (Invoke-Gh -Arguments @('run', 'list', '-R', $Repository, '--workflow', 'ci.yml', '--commit', [string]$pr.headRefOid,
        '--json', 'databaseId,conclusion,event,createdAt')) -join "`n" | ConvertFrom-Json
    $failed = Select-FailedCiRun -Runs @($runs)
    if ($failed) {
      [void](Invoke-Gh -Arguments @('run', 'rerun', "$failed", '--failed', '-R', $Repository))
      Write-Host "Re-ran the failed jobs of CI run $failed for pull request #$($pr.number)."
    }
  }
}

function Invoke-IntakeCommand {
  <# .SYNOPSIS Applies /accept or /decline from a maintainer to an issue. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Issue, [AllowEmptyString()][string]$Body = '',
    [Parameter(Mandatory)][string]$Actor, [long]$CommentId = 0)
  $command = ConvertFrom-IntakeCommand -Body $Body
  if (-not $command) { return }
  if (-not (Test-MaintainerAccess -Repository $Repository -Login $Actor)) {
    $text = "Only a maintainer can /$($command.Command) an issue. A maintainer will review it here; thanks for your patience."
    [void](Invoke-Gh -Arguments @('issue', 'comment', "$Issue", '-R', $Repository, '--body', $text))
    return
  }
  if ($command.Command -eq 'accept') {
    [void](Invoke-Gh -Arguments @('issue', 'edit', "$Issue", '-R', $Repository, '--add-label', 'accepted', '--remove-label', 'needs-triage'))
    # A label the workflow's own token adds starts no other workflow, so the pull requests are refreshed here.
    Update-PullRequestsClosing -Repository $Repository -Issue $Issue
  } else {
    [void](Invoke-Gh -Arguments @('issue', 'edit', "$Issue", '-R', $Repository, '--add-label', 'wontfix', '--remove-label', 'needs-triage', '--remove-label', 'accepted'))
    $why = if ($command.Reason) { " $($command.Reason)" } else { '' }
    $text = "Declined by a maintainer.$why Thanks for raising it; this issue is closed as not planned."
    [void](Invoke-Gh -Arguments @('issue', 'close', "$Issue", '-R', $Repository, '--reason', 'not planned', '--comment', $text))
  }
  if ($CommentId -gt 0) {
    [void](Invoke-Gh -Arguments @('api', '-X', 'POST', "repos/$Repository/issues/comments/$CommentId/reactions", '-f', 'content=+1'))
  }
}

function Invoke-IntakeSweep {
  <# .SYNOPSIS Closes the outside pull requests still without an accepted issue after the grace period. #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Days)
  $open = (Invoke-Gh -Arguments @('pr', 'list', '-R', $Repository, '--state', 'open', '--limit', '500', '--json', 'number,labels')) -join "`n" | ConvertFrom-Json
  $candidates = @($open | Where-Object { @($_.labels.name) -contains 'issue:none' -or @($_.labels.name) -contains 'issue:awaiting-acceptance' })
  $read = foreach ($c in $candidates) {
    $pr = Read-LinkedPullRequest -Repository $Repository -Number ([int]$c.number)
    if ($pr) { [pscustomobject]@{ Number = $pr.Number; CreatedAt = $pr.CreatedAt; State = (Get-IssueIntakeState -PullRequest $pr); AuthorIsMaintainer = $pr.AuthorIsMaintainer } }
  }
  foreach ($n in Select-StalePullRequest -PullRequests @($read) -Now ([datetimeoffset]::UtcNow) -Days $Days) {
    [void](Invoke-Gh -Arguments @('pr', 'close', "$n", '-R', $Repository, '--comment', (Format-IntakeCloseComment -Days $Days)))
    Write-Host "Closed pull request #${n}: no accepted issue after $Days days."
  }
}

# Dot-sourcing for tests loads the functions without running anything.
if ($MyInvocation.InvocationName -eq '.') { return }

switch ($Mode) {
  'PullRequest' { Invoke-IntakeSync -Repository $Repository -Number $Number }
  'Issue' { Update-PullRequestsClosing -Repository $Repository -Issue $Number }
  'Command' {
    $body = (Invoke-Gh -Arguments @('api', "repos/$Repository/issues/comments/$CommentId", '--jq', '.body')) -join "`n"
    Invoke-IntakeCommand -Repository $Repository -Issue $Number -Body $body -Actor $Actor -CommentId $CommentId
  }
  'Sweep' { Invoke-IntakeSweep -Repository $Repository -Days $Days }
}
exit 0
