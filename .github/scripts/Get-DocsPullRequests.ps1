#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    The docs-site pull requests that document a code pull request, as JSON: [{number, state, referencesThis}].

.DESCRIPTION
    Code and docs live in two repositories, so a change and its documentation are two pull requests. One
    link is enough: when the docs pull request mentions this one ("Docs for whizbang-lib/whizbang#N"),
    GitHub records a cross-reference on this pull request's timeline, and this script finds it there. A
    docs pull request linked only from this description is read too, and counts as linked back only when
    its own description names this pull request.

    The review checks (scripts/lib/ReviewFindings.psm1, rule pr.docs-link) decide what a library change
    needs; this script only reads.

.PARAMETER Repository
    The code repository, owner/name.

.PARAMETER PullRequest
    The code pull request number.

.PARAMETER DocsRepository
    The docs-site repository, owner/name.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Repository,
  [Parameter(Mandatory)][int]$PullRequest,
  [string]$DocsRepository = 'whizbang-lib/whizbang-lib.github.io'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-DocsLinkNumbers {
  <# .SYNOPSIS The docs-site pull request numbers a description links, by URL or owner/name#N, once each. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Body, [Parameter(Mandatory)][string]$DocsRepository)
  $repo = [regex]::Escape($DocsRepository)
  $numbers = [regex]::Matches($Body, "(?:(?<=github\.com/)|(?<![\w./-]))$repo(?:/pull/|#)(\d+)\b") | ForEach-Object { [int]$_.Groups[1].Value }
  @($numbers | Select-Object -Unique)
}

function Test-ReferencesPullRequest {
  <# .SYNOPSIS Whether a description names the code pull request, by owner/name#N or its URL. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Body, [Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][int]$Number)
  $repo = [regex]::Escape($Repository)
  return [regex]::IsMatch($Body, "(?:(?<=github\.com/)|(?<![\w./-]))$repo(?:/pull/|#)$Number(?!\d)")
}

function ConvertFrom-DocsCrossReferences {
  <# .SYNOPSIS The docs-site pull requests that mention this one, from its timeline's cross-references. #>
  param([Parameter(Mandatory)][string]$Json, [Parameter(Mandatory)][string]$DocsRepository)
  $nodes = ($Json | ConvertFrom-Json).data.repository.pullRequest.timelineItems.nodes
  $seen = [System.Collections.Generic.HashSet[int]]::new()
  foreach ($n in $nodes) {
    $src = $n.source
    if (-not $src -or -not $src.PSObject.Properties['repository'] -or $src.repository.nameWithOwner -ne $DocsRepository) { continue }
    if ($seen.Add([int]$src.number)) {
      [pscustomobject]@{ Number = [int]$src.number; State = [string]$src.state; ReferencesThis = $true }
    }
  }
}

function Merge-DocsPullRequests {
  <#
  .SYNOPSIS
      The cross-referenced docs pull requests plus the ones only this description links, each of those
      read through LookupDocsPullRequest and checked for a link back.
  #>
  param(
    [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$CrossReferenced,
    [Parameter(Mandatory)][AllowEmptyCollection()][int[]]$LinkedNumbers,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][int]$Number,
    [Parameter(Mandatory)][scriptblock]$LookupDocsPullRequest
  )
  $CrossReferenced
  $known = @($CrossReferenced | ForEach-Object { $_.Number })
  foreach ($n in $LinkedNumbers | Where-Object { $_ -notin $known }) {
    $pr = & $LookupDocsPullRequest $n
    if ($pr) {
      [pscustomobject]@{ Number = [int]$pr.Number; State = [string]$pr.State
        ReferencesThis = Test-ReferencesPullRequest -Body ([string]$pr.Body) -Repository $Repository -Number $Number }
    }
  }
}

# Dot-sourcing for tests loads the functions without running anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$owner, $name = $Repository -split '/', 2
$query = 'query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){' +
  'body timelineItems(itemTypes:[CROSS_REFERENCED_EVENT],last:100){nodes{... on CrossReferencedEvent{source{... on PullRequest{number state repository{nameWithOwner}}}}}}}}}'
$json = (gh api graphql -f query=$query -f owner=$owner -f name=$name -F number=$PullRequest) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Reading pull request #$PullRequest failed (gh exit $LASTEXITCODE)." }

$body = [string](($json | ConvertFrom-Json).data.repository.pullRequest.body)
$docsOwner, $docsName = $DocsRepository -split '/', 2
$docsQuery = 'query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){number state body}}}'
$lookup = {
  param([int]$DocsNumber)
  # A number that is an issue, or nothing at all, reads as nothing.
  $found = gh api graphql -f query=$docsQuery -f owner=$docsOwner -f name=$docsName -F number=$DocsNumber 2>$null
  if ($LASTEXITCODE -ne 0) { return $null }
  $pr = (($found -join "`n") | ConvertFrom-Json).data.repository.pullRequest
  if (-not $pr) { return $null }
  [pscustomobject]@{ Number = [int]$pr.number; State = [string]$pr.state; Body = [string]$pr.body }
}

$all = @(Merge-DocsPullRequests -CrossReferenced @(ConvertFrom-DocsCrossReferences -Json $json -DocsRepository $DocsRepository) `
    -LinkedNumbers @(Get-DocsLinkNumbers -Body $body -DocsRepository $DocsRepository) -Repository $Repository -Number $PullRequest -LookupDocsPullRequest $lookup)
ConvertTo-Json -InputObject @($all | ForEach-Object { [ordered]@{ number = $_.Number; state = $_.State; referencesThis = [bool]$_.ReferencesThis } }) -Compress
exit 0
