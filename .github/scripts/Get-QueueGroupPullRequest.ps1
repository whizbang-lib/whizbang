#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Lists every pull request in a merge-queue group, so the queue's gates check, and its quality gate
    reports to, each of them.

.DESCRIPTION
    The queue tests a group of up to several pull requests in one run (#2070). Its ref names only the last
    of them (gh-readonly-queue/<base>/pr-<number>-<sha>), so a gate that read only the ref would check one
    pull request and let the others through. The group commit is a chain of the queue's merge commits on top
    of the base, one per pull request ("Merge pull request #N from ..."), each the first parent of the next.
    Walking that chain from the head back to the base gives every pull request, in queue order.

    Fails toward the ref: if the chain cannot be read, does not reach the base within -MaxGroupSize commits,
    or holds a commit that is not a queued merge, the result is the pull request the ref names, which is what
    the queue's gates checked before groups were batched.

    With -GitHubOutput, writes the step outputs `numbers` (space-separated), `matrix` (a JSON array of
    {"pr": N, "head": "<head branch>", "sha": "<head commit>"} for a job matrix) and `first` / `first-head` (the first pull request
    and its head branch). Exit 1 when no pull request can be named at all.

.PARAMETER Repository
    owner/name.

.PARAMETER HeadRef
    github.event.merge_group.head_ref.

.PARAMETER HeadSha
    github.event.merge_group.head_sha.

.PARAMETER BaseSha
    github.event.merge_group.base_sha.

.PARAMETER MaxGroupSize
    The most commits to walk; above the largest group the queue builds. Default 10.

.PARAMETER GitHubOutput
    Write the step outputs to $GITHUB_OUTPUT.

.EXAMPLE
    pwsh .github/scripts/Get-QueueGroupPullRequest.ps1 -Repository whizbang-lib/whizbang -HeadRef "$REF" -HeadSha "$HEAD" -BaseSha "$BASE" -GitHubOutput
#>

param(
    [string]$Repository = '',
    [string]$HeadRef = '',
    [string]$HeadSha = '',
    [string]$BaseSha = '',
    [int]$MaxGroupSize = 10,
    [switch]$GitHubOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
    The pull requests merged by the queue's commits from HeadSha back to BaseSha, oldest first, or nothing when
    the chain is not exactly that. GetCommit returns a commit as the commits API does, or $null.
#>
function Get-QueueGroupPullRequestNumber {
  param(
      [Parameter(Mandatory)][string]$HeadSha,
      [Parameter(Mandatory)][string]$BaseSha,
      [Parameter(Mandatory)][scriptblock]$GetCommit,
      [int]$MaxGroupSize = 10
  )
  $numbers = [System.Collections.Generic.List[int]]::new()
  $sha = $HeadSha
  for ($i = 0; $i -lt $MaxGroupSize; $i++) {
    if ($sha -eq $BaseSha) { break }
    $commit = & $GetCommit $sha
    if ($null -eq $commit) { return }
    $parents = @($commit.parents)
    if ($parents.Count -ne 2 -or [string]$commit.commit.message -notmatch '^Merge pull request #(\d+) from ') { return }
    $numbers.Insert(0, [int]$Matches[1])
    $sha = [string]$parents[0].sha
  }
  if ($sha -ne $BaseSha) { return }
  return $numbers.ToArray()
}

# The group's pull requests, or the one the queue ref names when the group walk finds none.
function Resolve-QueueGroupPullRequest {
  param(
      [Parameter(Mandatory)][AllowEmptyString()][string]$HeadRef,
      [Parameter(Mandatory)][string]$HeadSha,
      [Parameter(Mandatory)][string]$BaseSha,
      [Parameter(Mandatory)][scriptblock]$GetCommit,
      [int]$MaxGroupSize = 10
  )
  $numbers = @(Get-QueueGroupPullRequestNumber -HeadSha $HeadSha -BaseSha $BaseSha -GetCommit $GetCommit -MaxGroupSize $MaxGroupSize)
  if ($numbers.Count -gt 0) { return $numbers }
  if ($HeadRef -match 'gh-readonly-queue/.+/pr-(\d+)-') { return @([int]$Matches[1]) }
  return
}

# Dot-sourcing for tests loads the functions without calling the API.
if ($MyInvocation.InvocationName -eq '.') { return }

$getCommit = {
  param($sha)
  $json = gh api "repos/$Repository/commits/$sha" 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $json) { return $null }
  return ($json | ConvertFrom-Json)
}.GetNewClosure()

$numbers = @(Resolve-QueueGroupPullRequest -HeadRef $HeadRef -HeadSha $HeadSha -BaseSha $BaseSha -GetCommit $getCommit -MaxGroupSize $MaxGroupSize)
if ($numbers.Count -eq 0) {
  Write-Output "::error::No pull request found in this queue group (head ref '$HeadRef', $BaseSha..$HeadSha)."
  exit 1
}

$entries = foreach ($n in $numbers) {
  $head = gh api "repos/$Repository/pulls/$n" --jq '.head.ref + " " + .head.sha'
  if ($LASTEXITCODE -ne 0 -or -not $head) { Write-Output "::error::Could not read pull request #$n."; exit 1 }
  $ref, $sha = ([string]$head).Split(' ', 2)
  [ordered]@{ pr = $n; head = $ref; sha = $sha }
}
$entries = @($entries)
Write-Output "Pull requests in this queue group: $(($numbers | ForEach-Object { "#$_" }) -join ', ')"

if ($GitHubOutput) {
  @(
    "numbers=$($numbers -join ' ')"
    "matrix=$(ConvertTo-Json -InputObject $entries -Compress)"
    "first=$($entries[0].pr)"
    "first-head=$($entries[0].head)"
  ) | Add-Content -Path $env:GITHUB_OUTPUT
}
exit 0
