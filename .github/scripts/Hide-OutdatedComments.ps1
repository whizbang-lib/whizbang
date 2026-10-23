#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Minimizes a pull request's earlier bot comments that carry a marker, as outdated, keeping the latest.

.DESCRIPTION
    The review-checks and PR quality gate comments are posted once per run. Every run's comment is kept,
    so the history of what was found and fixed stays on the pull request, but only the latest one is
    current: the earlier ones are minimized with the OUTDATED classifier (collapsed, one click to expand),
    never deleted. Only comments posted by the workflow's bot account are touched, and a comment already
    minimized is left alone.

    Run after the new comment is posted. Exit 0 always: a comment that could not be minimized is a
    warning, never a reason to fail the run.

.PARAMETER Repository
    owner/name.

.PARAMETER PullRequest
    The pull request number.

.PARAMETER Marker
    The hidden marker the comment starts with, for example <!-- pr-review-checks -->.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Repository,
  [Parameter(Mandatory)][int]$PullRequest,
  [Parameter(Mandatory)][string]$Marker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:BotLogin = 'github-actions[bot]'

function Select-OutdatedComment {
  <# .SYNOPSIS The node ids of the bot's comments with the marker, oldest first, except the latest; none already minimized. #>
  param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Comments, [Parameter(Mandatory)][string]$Marker)
  $ours = @($Comments | Where-Object { $_.Login -eq $script:BotLogin -and $_.Body.StartsWith($Marker) })
  if ($ours.Count -le 1) { return }
  $ours | Select-Object -First ($ours.Count - 1) | Where-Object { -not $_.IsMinimized } | ForEach-Object { $_.NodeId }
}

# Dot-sourcing for tests loads the functions without running anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$owner, $name = $Repository -split '/', 2
$query = 'query($owner:String!,$name:String!,$number:Int!,$after:String){repository(owner:$owner,name:$name){pullRequest(number:$number){' +
  'comments(first:100,after:$after){pageInfo{hasNextPage endCursor} nodes{id body isMinimized author{login}}}}}}'
$comments = [System.Collections.Generic.List[object]]::new()
$after = $null
do {
  $arguments = @('api', 'graphql', '-f', "query=$query", '-f', "owner=$owner", '-f', "name=$name", '-F', "number=$PullRequest")
  if ($after) { $arguments += @('-f', "after=$after") }
  $page = (gh @arguments | ConvertFrom-Json).data.repository.pullRequest.comments
  foreach ($node in $page.nodes) {
    # GraphQL names the Actions bot without the [bot] suffix.
    $login = if ($node.author.login -eq 'github-actions') { $script:BotLogin } else { [string]$node.author.login }
    $comments.Add([pscustomobject]@{ NodeId = $node.id; Body = [string]$node.body; Login = $login; IsMinimized = [bool]$node.isMinimized })
  }
  $after = if ($page.pageInfo.hasNextPage) { $page.pageInfo.endCursor } else { $null }
} while ($after)

$mutation = 'mutation($id:ID!){minimizeComment(input:{subjectId:$id,classifier:OUTDATED}){minimizedComment{isMinimized}}}'
foreach ($id in Select-OutdatedComment -Comments $comments.ToArray() -Marker $Marker) {
  gh api graphql -f "query=$mutation" -f "id=$id" > $null
  if ($LASTEXITCODE -ne 0) { Write-Output "::warning::Could not minimize the earlier comment $id." }
}
exit 0
