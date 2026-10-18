#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Decides the "Gate - Linked issue" check: a pull request closes an issue in this repository that was
    opened before it.

.DESCRIPTION
    Every change is tied to a work item, and the work item comes first, so a request or a fix is discussed
    before code is reviewed (CONTRIBUTING.md, "Open an issue first"). The link is GitHub's own: a closing
    keyword in the description (Fixes #N, Closes #N, Resolves #N), read back as the pull request's closing
    issue references. At least one of the issues it closes in this repository must have been opened before
    the pull request, and at least one must be accepted: carry the `accepted` label, added by a maintainer
    (the maintain or admin role, which the maintainers team grants), read from the issue's timeline. A
    maintainer's own pull request needs no acceptance, only the issue. The ordering rule is waived when a
    maintainer added the linked-issue:late label: it allows issues opened after
    the pull request (one opened before this gate existed, or a small fix whose issue is filed after the
    fact), and the summary says so. The pull request must still close an issue. A label, not a comment,
    because only people with triage access can add one.

    Exempt, and reported as exempt:
      - pull requests opened by a bot (Dependabot, the release and sync automation);
      - the release pull request into main (from a release/ branch), whose changes reached the release
        through pull requests that each closed an issue.

    GitHub links closing keywords only on pull requests into the default branch, so a fix into a release
    branch (RELEASING.md F4, F7, F8) has no closing references however its description reads (#1449). For
    a pull request into release/v**, the closing keywords are read from the description instead, and each
    issue is looked up for its opening date and labels.

    The pull request is read from the API at run time, not from the event that started the run, so adding
    the keyword to the description and re-running this job is enough. In the merge queue the pull request
    number is read from the queue branch, so the link cannot be removed after the pull request run passed.

    Exit 0 on pass, 1 on fail.

.PARAMETER Repository
    owner/name.

.PARAMETER PullRequest
    The pull request number (pull request runs).

.PARAMETER HeadRef
    The queue branch (merge queue runs), gh-readonly-queue/<base>/pr-<number>-<sha>.

.PARAMETER EmitClosingIssues
    Print the issues the pull request closes as JSON ([{number, labels}]) and exit, for the review-checks
    step, so both gates read the same closing issues.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Repository,
  [int]$PullRequest = 0,
  [string]$HeadRef = '',
  [switch]$EmitClosingIssues
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:LateIssueLabel = 'linked-issue:late'
$script:AcceptedLabel = 'accepted'
$script:ContributingGuide = 'https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#how-a-change-gets-in'
# The workflow bot adds the accepted label only for a maintainer's /accept (Invoke-PrIntake.ps1).
$script:WorkflowBots = @('github-actions', 'github-actions[bot]')

function Test-MaintainerRole {
  <# .SYNOPSIS Whether a repository role is a maintainer's: maintain or admin. #>
  param([AllowEmptyString()][string]$RoleName = '')
  return $RoleName -in @('maintain', 'admin')
}

function Test-CommitterRole {
  <# .SYNOPSIS Whether a repository role is a committer's or above: write, maintain or admin. #>
  param([AllowEmptyString()][string]$RoleName = '')
  return $RoleName -in @('write', 'maintain', 'admin')
}

function ConvertFrom-IssueNode {
  <#
  .SYNOPSIS
      An issue from a GraphQL node: number, opening date, labels, and who last added the accepted label
      while it is still on the issue.
  #>
  param([Parameter(Mandatory)]$Node)
  $labels = @(if ($Node.PSObject.Properties['labels']) { $Node.labels.nodes | ForEach-Object { [string]$_.name } })
  $acceptedBy = $null
  if ($labels -contains $script:AcceptedLabel -and $Node.PSObject.Properties['timelineItems']) {
    $events = @($Node.timelineItems.nodes | Where-Object { $_ -and $_.label -and $_.label.name -eq $script:AcceptedLabel -and $_.actor })
    if ($events.Count -gt 0) { $acceptedBy = [string]$events[-1].actor.login }
  }
  [pscustomobject]@{ Number = [int]$Node.number; CreatedAt = [datetimeoffset]$Node.createdAt; Labels = $labels
    AcceptedBy = $acceptedBy; AcceptedByMaintainer = $false }
}

function Set-PullRequestAuthority {
  <#
  .SYNOPSIS
      Fills in the author's repository role, whether the author is a maintainer, and whether each issue's
      accepter was, asking RoleOf once per login ('' for no role).
  #>
  param([Parameter(Mandatory)]$PullRequest, [Parameter(Mandatory)][scriptblock]$RoleOf)
  $known = @{}
  $ask = { param([string]$Login) if (-not $known.ContainsKey($Login)) { $known[$Login] = [string](& $RoleOf $Login) }; $known[$Login] }
  $PullRequest.AuthorRole = & $ask $PullRequest.Author
  $PullRequest.AuthorIsMaintainer = Test-MaintainerRole -RoleName $PullRequest.AuthorRole
  foreach ($issue in @($PullRequest.Issues)) {
    $issue.AcceptedByMaintainer = if (-not $issue.AcceptedBy) { $false }
      elseif ($issue.AcceptedBy -in $script:WorkflowBots) { $true }
      else { Test-MaintainerRole -RoleName (& $ask $issue.AcceptedBy) }
  }
}

function Test-IssueAccepted {
  <# .SYNOPSIS Whether an issue carries the accepted label, added by a maintainer. #>
  param([Parameter(Mandatory)]$Issue)
  $labels = if ($Issue.PSObject.Properties['Labels']) { @($Issue.Labels) } else { @() }
  $byMaintainer = $Issue.PSObject.Properties['AcceptedByMaintainer'] -and $Issue.AcceptedByMaintainer
  return ($labels -contains $script:AcceptedLabel) -and [bool]$byMaintainer
}

function Get-IssueIntakeState {
  <#
  .SYNOPSIS
      The pull request's intake label state: accepted, awaiting-acceptance, none, or exempt (a bot, the
      release pull request into main). It describes the linked issue; the gate decides.
  #>
  param([Parameter(Mandatory)]$PullRequest)
  $pr = $PullRequest
  if ($pr.AuthorIsBot -or ($pr.BaseRef -eq 'main' -and $pr.HeadRef -like 'release/*')) { return 'exempt' }
  $issues = @($pr.Issues)
  if ($issues.Count -eq 0) { return 'none' }
  if ($issues | Where-Object { Test-IssueAccepted -Issue $_ }) { return 'accepted' }
  return 'awaiting-acceptance'
}

function Get-QueuedPullRequestNumber {
  <# .SYNOPSIS The pull request number a merge queue branch was made for, or 0 for any other branch. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$HeadRef)
  if ($HeadRef -match 'gh-readonly-queue/.+/pr-(\d+)-') { return [int]$Matches[1] }
  return 0
}

function ConvertFrom-PullRequestJson {
  <#
  .SYNOPSIS
      The pull request and the issues it closes in this repository, from the GraphQL response.
  #>
  param([Parameter(Mandatory)][string]$Json, [Parameter(Mandatory)][string]$Repository)
  $pr = ($Json | ConvertFrom-Json).data.repository.pullRequest
  if (-not $pr) { throw "The pull request could not be read from $Repository." }
  $issues = @($pr.closingIssuesReferences.nodes |
      Where-Object { $_.repository.nameWithOwner -eq $Repository } |
      ForEach-Object { ConvertFrom-IssueNode -Node $_ })
  [pscustomobject]@{
    Number      = [int]$pr.number
    Author      = [string]$pr.author.login
    AuthorIsBot = $pr.author.__typename -eq 'Bot'
    AuthorIsMaintainer = $false
    AuthorRole  = ''
    CreatedAt   = [datetimeoffset]$pr.createdAt
    BaseRef     = [string]$pr.baseRefName
    HeadRef     = [string]$pr.headRefName
    Body        = if ($pr.PSObject.Properties['body']) { [string]$pr.body } else { '' }
    Labels      = @(if ($pr.PSObject.Properties['labels']) { $pr.labels.nodes | ForEach-Object { [string]$_.name } })
    Issues      = $issues
  }
}

function Get-ClosingKeywordNumbers {
  <#
  .SYNOPSIS
      The issue numbers a description closes with a keyword GitHub honors, in order and once each. Code
      spans and code blocks are skipped, as GitHub skips them.
  #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Body)
  $text = [regex]::Replace($Body, '(?s)```.*?```', ' ')
  $text = [regex]::Replace($text, '`[^`]*`', ' ')
  $numbers = [regex]::Matches($text, '(?i)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(\d+)\b') |
    ForEach-Object { [int]$_.Groups[1].Value }
  @($numbers | Select-Object -Unique)
}

function Resolve-ClosingIssues {
  <#
  .SYNOPSIS
      The issues the pull request closes: GitHub's own references, or, for a pull request into a release
      branch (which GitHub never links), the ones its description closes, each read through LookupIssue.
  #>
  param(
    [Parameter(Mandatory)]$PullRequest,
    [Parameter(Mandatory)][AllowEmptyString()][string]$Body,
    [Parameter(Mandatory)][scriptblock]$LookupIssue
  )
  $linked = @($PullRequest.Issues)
  if ($linked.Count -gt 0 -or $PullRequest.BaseRef -notlike 'release/v*') { return $linked }
  @(Get-ClosingKeywordNumbers -Body $Body | ForEach-Object { & $LookupIssue $_ } | Where-Object { $_ })
}

function Get-LinkedIssueVerdict {
  <#
  .SYNOPSIS
      The decision, with no I/O: Pass, Exempt, Reason and the problems found.
  #>
  param([Parameter(Mandatory)]$PullRequest)
  $pr = $PullRequest
  if ($pr.AuthorIsBot) {
    return [pscustomobject]@{ Pass = $true; Exempt = $true; Reason = "opened by a bot ($($pr.Author))"; Problems = @() }
  }
  if ($pr.BaseRef -eq 'main' -and $pr.HeadRef -like 'release/*') {
    return [pscustomobject]@{ Pass = $true; Exempt = $true; Reason = 'the release pull request into main'; Problems = @() }
  }
  $issues = @($pr.Issues)
  if ($issues.Count -eq 0) {
    $problem = "Pull request #$($pr.Number) closes no issue. Every change needs an issue opened first, so it can be " +
      'discussed and accepted before it is reviewed. Open one (or find an accepted one), add "Fixes #N" to the ' +
      "description (not inside backticks), and re-run this job. See ""How a change gets in"": $($script:ContributingGuide)"
    return [pscustomobject]@{ Pass = $false; Exempt = $false; Reason = 'no linked issue'; Problems = @($problem) }
  }
  $numbers = ($issues | ForEach-Object { "#$($_.Number)" }) -join ', '
  $labels = if ($pr.PSObject.Properties['Labels']) { @($pr.Labels) } else { @() }
  $late = -not ($issues | Where-Object { $_.CreatedAt -lt $pr.CreatedAt })
  $lateAllowed = $late -and $labels -contains $script:LateIssueLabel
  $maintainer = $pr.PSObject.Properties['AuthorIsMaintainer'] -and $pr.AuthorIsMaintainer
  $accepted = $maintainer -or ($issues | Where-Object { Test-IssueAccepted -Issue $_ })
  $problems = [System.Collections.Generic.List[string]]::new()
  $reason = ''
  if ($late -and -not $lateAllowed) {
    $problems.Add("Every issue pull request #$($pr.Number) closes ($numbers) was opened after the pull request. The " +
      'issue comes first, so the change is discussed before it is reviewed: link the issue that asked for this work. ' +
      "A maintainer can allow a later issue with the $($script:LateIssueLabel) label.")
    $reason = 'issue opened after the pull request'
  }
  if (-not $accepted) {
    $byOthers = @($issues | Where-Object { $_.PSObject.Properties['AcceptedBy'] -and $_.AcceptedBy -and -not (Test-IssueAccepted -Issue $_) })
    $who = if ($byOthers.Count -gt 0) { ' An `accepted` label added by someone without the maintainer role does not count.' } else { '' }
    $problems.Add("No issue pull request #$($pr.Number) closes ($numbers) is accepted yet. A maintainer accepts an issue " +
      "with the ``accepted`` label once its value and approach are agreed in the issue; this check re-runs on its own when " +
      "that happens.$who See ""How a change gets in"": $($script:ContributingGuide)")
    $reason = 'issue not accepted'
  }
  if ($problems.Count -gt 0) {
    return [pscustomobject]@{ Pass = $false; Exempt = $false; Reason = $reason; Problems = $problems.ToArray() }
  }
  $detail = if ($lateAllowed) { ", opened after the pull request, allowed by $($script:LateIssueLabel)" } else { '' }
  return [pscustomobject]@{ Pass = $true; Exempt = $false; Reason = "closes $numbers$detail"; Problems = @() }
}

function Get-RepositoryRole {
  <#
  .SYNOPSIS
      A user's role on the repository (read, triage, write, maintain, admin), or '' for someone who is not
      a collaborator. Team membership grants the role: committers write, maintainers maintain,
      build-admins admin (GOVERNANCE.md).
  #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Login)
  # A user who is not a collaborator reads as 404.
  $role = gh api "repos/$Repository/collaborators/$Login/permission" --jq '.role_name' 2>$null
  if ($LASTEXITCODE -ne 0) { return '' }
  return ($role -join '').Trim()
}

function Read-LinkedPullRequest {
  <#
  .SYNOPSIS
      The pull request, the issues it closes, who accepted each, and whether the author and each accepter
      are maintainers, read from the API. Null, with an ::error:: line, when it could not be read.
  #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number)
  $owner, $name = $Repository -split '/', 2
  $issueFields = 'number createdAt labels(first:20){nodes{name}} ' +
    'timelineItems(itemTypes:[LABELED_EVENT],last:50){nodes{... on LabeledEvent{label{name} actor{login}}}}'
  $query = 'query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){' +
    'number createdAt baseRefName headRefName body author{login __typename} labels(first:50){nodes{name}} ' +
    "closingIssuesReferences(first:50){nodes{repository{nameWithOwner} $issueFields}}}}}"
  $json = gh api graphql -f query=$query -f owner=$owner -f name=$name -F number=$Number
  if ($LASTEXITCODE -ne 0) { Write-Host "::error::Reading pull request #$Number failed (gh exit $LASTEXITCODE)."; return $null }

  $pr = ConvertFrom-PullRequestJson -Json ($json -join "`n") -Repository $Repository
  $issueQuery = 'query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){' +
    "issue(number:`$number){$issueFields}}}"
  $lookup = {
    param([int]$IssueNumber)
    # A number that is a pull request, or no issue at all, reads as nothing
    $found = gh api graphql -f query=$issueQuery -f owner=$owner -f name=$name -F number=$IssueNumber 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $issue = (($found -join "`n") | ConvertFrom-Json).data.repository.issue
    if (-not $issue) { return $null }
    ConvertFrom-IssueNode -Node $issue
  }
  $pr.Issues = @(Resolve-ClosingIssues -PullRequest $pr -Body $pr.Body -LookupIssue $lookup)
  Set-PullRequestAuthority -PullRequest $pr -RoleOf { param([string]$Login) Get-RepositoryRole -Repository $Repository -Login $Login }
  return $pr
}

# Dot-sourcing for tests loads the functions without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$number = if ($PullRequest -gt 0) { $PullRequest } else { Get-QueuedPullRequestNumber -HeadRef $HeadRef }
if ($number -le 0) {
  Write-Output "::error::No pull request to check: pass -PullRequest, or run in the merge queue (head ref '$HeadRef')."
  exit 1
}

$pr = Read-LinkedPullRequest -Repository $Repository -Number $number
if (-not $pr) { exit 1 }

if ($EmitClosingIssues) {
  ConvertTo-Json -InputObject @($pr.Issues | ForEach-Object { [ordered]@{ number = $_.Number; labels = @($_.Labels) } }) -Compress -Depth 4
  exit 0
}

$verdict = Get-LinkedIssueVerdict -PullRequest $pr
$summary = if ($verdict.Exempt) { "Linked issue: exempt, $($verdict.Reason)." }
  elseif ($verdict.Pass) { "Linked issue: pull request #$number $($verdict.Reason)." }
  else { "Linked issue: failed, $($verdict.Reason)." }
if ($env:GITHUB_STEP_SUMMARY) {
  $summary | Add-Content -Path $env:GITHUB_STEP_SUMMARY
  $verdict.Problems | ForEach-Object { "- $_" } | Add-Content -Path $env:GITHUB_STEP_SUMMARY
}
Write-Output $summary
if (-not $verdict.Pass) {
  $verdict.Problems | ForEach-Object { Write-Output "::error::$_" }
  exit 1
}
exit 0
