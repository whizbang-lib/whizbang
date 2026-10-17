#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Turns the findings CI posted on a pull request into your own draft review, ready to add to and submit.

.DESCRIPTION
    The PR quality gate and the review-checks job each post a comment with a hidden findings block
    (scripts/lib/ReviewFindings.psm1). This script, run with your own gh login:

      1. reads those blocks, only from comments the workflow's bot posted;
      2. skips a block checked against an older commit than the pull request's head, and says so;
      3. finds your pending review on the pull request, or starts one;
      4. puts each finding on its line when the line is in the diff, and the rest in the review summary;
      5. marks each comment with its finding, so running it again adds only what is new.

    It never submits the review. Open the pull request, add your own comments, and submit it as
    Comment, Approve or Request changes. Nothing here needs a token in CI: your credential stays on
    your machine.

.PARAMETER PullRequest
    The pull request number.

.PARAMETER Repository
    owner/name. Defaults to the repository of the current directory.

.EXAMPLE
    pwsh scripts/Invoke-PrReviewDraft.ps1 -PullRequest 1409

.EXAMPLE
    pwsh scripts/Invoke-PrReviewDraft.ps1 -PullRequest 1409 -WhatIf
    Shows what would be drafted and changes nothing.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [int]$PullRequest,
  [string]$Repository
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib' 'ReviewFindings.psm1') -Force

function Invoke-Gh {
  <# .SYNOPSIS Runs gh and returns its output; throws when gh fails. #>
  param([Parameter(Mandatory)][string[]]$Arguments, [string]$InputText)
  $output = if ($PSBoundParameters.ContainsKey('InputText')) { $InputText | & gh @Arguments } else { & gh @Arguments }
  if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments -join ' ') failed (exit $LASTEXITCODE)" }
  return $output
}

function Invoke-GraphQl {
  <# .SYNOPSIS Runs one GraphQL query or mutation as you. #>
  param([Parameter(Mandatory)][string]$Query, [hashtable]$Variables = @{})
  $request = ConvertTo-Json -InputObject @{ query = $Query; variables = $Variables } -Depth 10 -Compress
  $response = (Invoke-Gh -Arguments @('api', 'graphql', '--input', '-') -InputText $request) -join "`n"
  return $response | ConvertFrom-Json
}

function Get-PullRequestState {
  <#
  .SYNOPSIS
      The pull request's head commit, its comments, its diff, and your own pending review if you have one.
  #>
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$PullRequest)
  $owner, $name = $Repository -split '/', 2

  $pr = (Invoke-Gh -Arguments @('api', "repos/$Repository/pulls/$PullRequest", '--jq', '{head: .head.sha, node: .node_id, url: .html_url}')) -join "`n" | ConvertFrom-Json

  $comments = @(Invoke-Gh -Arguments @('api', '--paginate', "repos/$Repository/issues/$PullRequest/comments", '--jq', '.[] | {login: .user.login, type: .user.type, body: .body}') |
      Where-Object { $_ } | ForEach-Object {
        $c = $_ | ConvertFrom-Json
        [pscustomobject]@{ Login = $c.login; Type = $c.type; Body = [string]$c.body }
      })

  $diffText = ''
  try {
    $diffText = (Invoke-Gh -Arguments @('api', "repos/$Repository/pulls/$PullRequest", '-H', 'Accept: application/vnd.github.v3.diff')) -join "`n"
  } catch {
    Write-Warning "Could not read the diff ($($_.Exception.Message)); every finding goes in the review summary."
  }

  # reviews(states: PENDING) lists only the viewer's own pending review; viewerDidAuthor makes sure.
  $query = 'query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){reviews(states:PENDING,first:10){nodes{id viewerDidAuthor body comments(first:100){nodes{body}}}}}}}'
  $result = Invoke-GraphQl -Query $query -Variables @{ owner = $owner; name = $name; number = $PullRequest }
  $nodes = $result.data.repository.pullRequest.reviews.nodes
  $mine = @($nodes | Where-Object { $_.viewerDidAuthor }) | Select-Object -First 1
  $pending = $null
  if ($mine) {
    $texts = @([string]$mine.body) + @($mine.comments.nodes | ForEach-Object { [string]$_.body })
    $pending = [pscustomobject]@{ Id = $mine.id; Body = [string]$mine.body; Texts = $texts }
  }

  [pscustomobject]@{
    NodeId = $pr.node; HeadSha = $pr.head; Url = $pr.url
    Comments = $comments; DiffText = $diffText; PendingReview = $pending
  }
}

$script:SummaryHeader = 'Findings from the PR checks that are not on a line of this diff:'

function Invoke-PrReviewDraft {
  <#
  .SYNOPSIS
      Drafts the findings into your pending review on the pull request (starting one if needed).
  #>
  [CmdletBinding(SupportsShouldProcess)]
  param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$PullRequest)

  $state = Get-PullRequestState -Repository $Repository -PullRequest $PullRequest
  $drafted = if ($state.PendingReview) { @(Get-DraftedIds -Texts $state.PendingReview.Texts) } else { @() }
  $plan = Get-ReviewDraftPlan -Comments $state.Comments -HeadSha $state.HeadSha -DiffText $state.DiffText -DraftedIds $drafted

  foreach ($s in $plan.Stale) {
    Write-Warning "The $($s.Source) findings were checked against $($s.Sha), not the head $($state.HeadSha.Substring(0, 9)); wait for CI to finish on the head, then run this again."
  }
  if ($plan.Threads.Count -eq 0 -and $plan.Summary.Count -eq 0) {
    Write-Host "Nothing new to draft on PR #$PullRequest ($($plan.AlreadyDrafted) finding(s) already in your draft)."
    return
  }

  $items = ($plan.Summary | ForEach-Object { $_.Body }) -join "`n"
  $target = "PR #$PullRequest ($($plan.Threads.Count) line comment(s), $($plan.Summary.Count) summary item(s))"

  if ($null -eq $state.PendingReview) {
    if (-not $PSCmdlet.ShouldProcess($target, 'Start a pending review')) { return }
    $in = @{
      pullRequestId = $state.NodeId
      commitOID     = $state.HeadSha
      threads       = @($plan.Threads | ForEach-Object { @{ path = $_.Path; line = $_.Line; side = 'RIGHT'; body = $_.Body } })
    }
    if ($items) { $in.body = "$script:SummaryHeader`n`n$items" }
    [void](Invoke-GraphQl -Query 'mutation($input:AddPullRequestReviewInput!){addPullRequestReview(input:$input){pullRequestReview{id}}}' -Variables @{ input = $in })
  } else {
    if (-not $PSCmdlet.ShouldProcess($target, 'Add to your pending review')) { return }
    $reviewId = $state.PendingReview.Id
    foreach ($t in $plan.Threads) {
      [void](Invoke-GraphQl -Query 'mutation($input:AddPullRequestReviewThreadInput!){addPullRequestReviewThread(input:$input){thread{id}}}' -Variables @{
          input = @{ pullRequestReviewId = $reviewId; path = $t.Path; line = $t.Line; side = 'RIGHT'; body = $t.Body }
        })
    }
    if ($items) {
      $existing = $state.PendingReview.Body
      $header = if ($existing.Contains($script:SummaryHeader)) { '' } else { "$script:SummaryHeader`n`n" }
      $body = if ($existing) { "$existing`n`n$header$items" } else { "$header$items" }
      [void](Invoke-GraphQl -Query 'mutation($input:UpdatePullRequestReviewInput!){updatePullRequestReview(input:$input){pullRequestReview{id}}}' -Variables @{
          input = @{ pullRequestReviewId = $reviewId; body = $body }
        })
    }
  }

  Write-Host "Drafted $($plan.Threads.Count) line comment(s) and $($plan.Summary.Count) summary item(s) on PR #$PullRequest; $($plan.AlreadyDrafted) were already in your draft."
  Write-Host "Nothing is submitted. Review and submit: $($state.Url)/files"
}

# Dot-sourcing for tests loads the functions without running anything.
if ($MyInvocation.InvocationName -eq '.') { return }

if ($PullRequest -le 0) { throw 'Pass -PullRequest <number>.' }
if (-not $Repository) {
  $Repository = ((Invoke-Gh -Arguments @('repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner')) -join '').Trim()
}
Invoke-PrReviewDraft -Repository $Repository -PullRequest $PullRequest -WhatIf:$WhatIfPreference
