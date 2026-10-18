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
    the pull request.

    Exempt, and reported as exempt:
      - pull requests opened by a bot (Dependabot, the release and sync automation);
      - the release pull request into main (from a release/ branch), whose changes reached the release
        through pull requests that each closed an issue.

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
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Repository,
  [int]$PullRequest = 0,
  [string]$HeadRef = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
      ForEach-Object { [pscustomobject]@{ Number = [int]$_.number; CreatedAt = [datetimeoffset]$_.createdAt } })
  [pscustomobject]@{
    Number      = [int]$pr.number
    Author      = [string]$pr.author.login
    AuthorIsBot = $pr.author.__typename -eq 'Bot'
    CreatedAt   = [datetimeoffset]$pr.createdAt
    BaseRef     = [string]$pr.baseRefName
    HeadRef     = [string]$pr.headRefName
    Issues      = $issues
  }
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
      'discussed before it is reviewed (CONTRIBUTING.md, "Open an issue first"). Open one, add "Fixes #N" to the ' +
      'description (not inside backticks), and re-run this job.'
    return [pscustomobject]@{ Pass = $false; Exempt = $false; Reason = 'no linked issue'; Problems = @($problem) }
  }
  if (-not ($issues | Where-Object { $_.CreatedAt -lt $pr.CreatedAt })) {
    $numbers = ($issues | ForEach-Object { "#$($_.Number)" }) -join ', '
    $problem = "Every issue pull request #$($pr.Number) closes ($numbers) was opened after the pull request. The " +
      'issue comes first, so the change is discussed before it is reviewed: link the issue that asked for this work.'
    return [pscustomobject]@{ Pass = $false; Exempt = $false; Reason = 'issue opened after the pull request'; Problems = @($problem) }
  }
  $numbers = ($issues | ForEach-Object { "#$($_.Number)" }) -join ', '
  return [pscustomobject]@{ Pass = $true; Exempt = $false; Reason = "closes $numbers"; Problems = @() }
}

# Dot-sourcing for tests loads the functions without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$number = if ($PullRequest -gt 0) { $PullRequest } else { Get-QueuedPullRequestNumber -HeadRef $HeadRef }
if ($number -le 0) {
  Write-Output "::error::No pull request to check: pass -PullRequest, or run in the merge queue (head ref '$HeadRef')."
  exit 1
}

$owner, $name = $Repository -split '/', 2
$query = 'query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){' +
  'number createdAt baseRefName headRefName author{login __typename} ' +
  'closingIssuesReferences(first:50){nodes{number createdAt repository{nameWithOwner}}}}}}'
$json = gh api graphql -f query=$query -f owner=$owner -f name=$name -F number=$number
if ($LASTEXITCODE -ne 0) { Write-Output "::error::Reading pull request #$number failed (gh exit $LASTEXITCODE)."; exit 1 }

$verdict = Get-LinkedIssueVerdict -PullRequest (ConvertFrom-PullRequestJson -Json ($json -join "`n") -Repository $Repository)
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
