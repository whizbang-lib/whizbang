# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Get-QueueGroupPullRequest.ps1')

  # A commit as the commits API returns it, reduced to what the walk reads.
  function New-Commit([string]$Sha, [string[]]$Parents, [string]$Message) {
    [pscustomobject]@{ sha = $Sha; parents = @($Parents | ForEach-Object { [pscustomobject]@{ sha = $_ } }); commit = [pscustomobject]@{ message = $Message } }
  }

  # A group of queued merges onto base: each merge's first parent is the previous one, its second the PR head.
  function New-Group([int[]]$Numbers) {
    $commits = @{}
    $previous = 'base'
    foreach ($n in $Numbers) {
      $sha = "merge$n"
      $commits[$sha] = New-Commit $sha @($previous, "head$n") "Merge pull request #$n from whizbang-lib/feature-$n`n`nTitle of $n"
      $previous = $sha
    }
    return @{ Commits = $commits; Head = $previous }
  }
}

Describe 'Get-QueueGroupPullRequestNumber' {
  It 'finds every pull request of a batched group, in queue order' {
    $g = New-Group @(101, 102, 103)
    $numbers = Get-QueueGroupPullRequestNumber -HeadSha $g.Head -BaseSha 'base' -GetCommit { param($sha) $g.Commits[$sha] }
    $numbers | Should -Be @(101, 102, 103)
  }

  It 'finds the one pull request of a group of one' {
    $g = New-Group @(7)
    @(Get-QueueGroupPullRequestNumber -HeadSha $g.Head -BaseSha 'base' -GetCommit { param($sha) $g.Commits[$sha] }) | Should -Be @(7)
  }

  It 'returns nothing when the walk does not reach the base (so the caller falls back to the queue ref)' {
    $g = New-Group @(1, 2)
    @(Get-QueueGroupPullRequestNumber -HeadSha $g.Head -BaseSha 'elsewhere' -GetCommit { param($sha) $g.Commits[$sha] }) | Should -BeNullOrEmpty
  }

  It 'returns nothing when a commit on the first-parent chain cannot be read' {
    $g = New-Group @(1, 2)
    @(Get-QueueGroupPullRequestNumber -HeadSha $g.Head -BaseSha 'base' -GetCommit { param($sha) if ($sha -eq 'merge1') { $null } else { $g.Commits[$sha] } }) | Should -BeNullOrEmpty
  }

  It 'returns nothing when a commit on the chain is not a queued pull request merge' {
    $g = New-Group @(1, 2)
    $g.Commits['merge1'] = New-Commit 'merge1' @('base') 'a direct commit'
    @(Get-QueueGroupPullRequestNumber -HeadSha $g.Head -BaseSha 'base' -GetCommit { param($sha) $g.Commits[$sha] }) | Should -BeNullOrEmpty
  }

  It 'stops after the largest group the queue builds, rather than walking history' {
    $g = New-Group (1..12)
    $script:reads = 0
    @(Get-QueueGroupPullRequestNumber -HeadSha $g.Head -BaseSha 'base' -MaxGroupSize 10 -GetCommit { param($sha) $script:reads++; $g.Commits[$sha] }) | Should -BeNullOrEmpty
    $script:reads | Should -BeLessOrEqual 10
  }
}

Describe 'Resolve-QueueGroupPullRequest' {
  It 'uses the group walk when it succeeds' {
    $g = New-Group @(5, 6)
    $r = Resolve-QueueGroupPullRequest -HeadRef 'refs/heads/gh-readonly-queue/develop/pr-6-abc' -HeadSha $g.Head -BaseSha 'base' -GetCommit { param($sha) $g.Commits[$sha] }
    $r | Should -Be @(5, 6)
  }

  It 'falls back to the pull request the queue ref names when the walk finds nothing' {
    $r = Resolve-QueueGroupPullRequest -HeadRef 'refs/heads/gh-readonly-queue/develop/pr-44-0123abcd' -HeadSha 'x' -BaseSha 'base' -GetCommit { param($sha) $null }
    @($r) | Should -Be @(44)
  }

  It 'returns nothing when neither names a pull request' {
    @(Resolve-QueueGroupPullRequest -HeadRef 'refs/heads/develop' -HeadSha 'x' -BaseSha 'base' -GetCommit { param($sha) $null }) | Should -BeNullOrEmpty
  }
}
