# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Invoke-PrIntake.ps1') -Repository o/r -Mode PullRequest -Number 0
}

Describe 'Get-IntakeLabelChange' {
  It 'adds the label for the state and removes the other intake labels' {
    $c = Get-IntakeLabelChange -Current @('bug', 'issue:none') -State 'accepted'
    $c.Add | Should -Be @('issue:accepted')
    $c.Remove | Should -Be @('issue:none')
  }

  It 'changes nothing when the label already matches' {
    $c = Get-IntakeLabelChange -Current @('issue:awaiting-acceptance') -State 'awaiting-acceptance'
    @($c.Add).Count | Should -Be 0
    @($c.Remove).Count | Should -Be 0
  }

  It 'removes every intake label from an exempt pull request' {
    $c = Get-IntakeLabelChange -Current @('issue:accepted', 'issue:none') -State 'exempt'
    @($c.Add).Count | Should -Be 0
    $c.Remove | Should -Be @('issue:accepted', 'issue:none')
  }
}

Describe 'ConvertFrom-IntakeCommand' {
  It 'reads /accept on the first line' -TestCases @(
    @{ Body = '/accept' }
    @{ Body = "/accept`nThanks, this fits the 1.0 scope." }
    @{ Body = '  /Accept  ' }
  ) {
    (ConvertFrom-IntakeCommand -Body $Body).Command | Should -Be 'accept'
  }

  It 'reads the pull request commands' -TestCases @(
    @{ Body = '/late-issue'; Expected = 'late-issue' }
    @{ Body = '/docs-not-needed'; Expected = 'docs-not-needed' }
    @{ Body = '/recheck'; Expected = 'recheck' }
  ) {
    (ConvertFrom-IntakeCommand -Body $Body).Command | Should -Be $Expected
  }

  It 'reads /decline and its reason' {
    $c = ConvertFrom-IntakeCommand -Body "/decline Out of scope for 1.0; a plugin can do this.`nmore"
    $c.Command | Should -Be 'decline'
    $c.Reason | Should -Be 'Out of scope for 1.0; a plugin can do this.'
  }

  It 'reads /decline with no reason' {
    (ConvertFrom-IntakeCommand -Body '/decline').Reason | Should -Be ''
  }

  It 'ignores anything that is not a command on the first line' -TestCases @(
    @{ Body = 'please /accept this' }
    @{ Body = '/acceptable' }
    @{ Body = "looks good`n/accept" }
    @{ Body = '' }
  ) {
    ConvertFrom-IntakeCommand -Body $Body | Should -BeNullOrEmpty
  }
}

Describe 'Format-IntakeComment' {
  It 'explains how to get a pull request with no issue reviewed, with the closing date' {
    $t = Format-IntakeComment -State 'none' -IssueNumbers @() -CloseOn '2026-11-01'
    $t | Should -BeLike '<!-- pr-intake -->*'
    $t | Should -Match 'Fixes #N'
    $t | Should -Match '`accepted`'
    $t | Should -Match '2026-11-01'
    $t | Should -Match 'CONTRIBUTING.md#how-a-change-gets-in'
  }

  It 'names the issues waiting for acceptance' {
    $t = Format-IntakeComment -State 'awaiting-acceptance' -IssueNumbers @(12, 13) -CloseOn '2026-11-01'
    $t | Should -Match '#12, #13'
    $t | Should -Match 're-runs'
  }
}

Describe 'Format-IntakeCloseComment' {
  It 'explains the process again and how to get the change in' {
    $t = Format-IntakeCloseComment -Days 14
    $t | Should -BeLike '<!-- pr-intake-closed -->*'
    $t | Should -Match '14 days'
    $t | Should -Match '`accepted`'
    $t | Should -Match 'reopen'
    $t | Should -Match 'CONTRIBUTING.md#how-a-change-gets-in'
  }
}

Describe 'Select-StalePullRequest' {
  It 'selects outside pull requests (not committers) still without an accepted issue after the grace period' {
    $now = [datetimeoffset]'2026-11-01T12:00:00Z'
    $prs = @(
      [pscustomobject]@{ Number = 1; CreatedAt = $now.AddDays(-15); State = 'none'; AuthorIsMaintainer = $false }
      [pscustomobject]@{ Number = 2; CreatedAt = $now.AddDays(-14); State = 'awaiting-acceptance'; AuthorIsMaintainer = $false }
      [pscustomobject]@{ Number = 3; CreatedAt = $now.AddDays(-13); State = 'none'; AuthorIsMaintainer = $false }
      [pscustomobject]@{ Number = 4; CreatedAt = $now.AddDays(-30); State = 'accepted'; AuthorIsMaintainer = $false }
      [pscustomobject]@{ Number = 5; CreatedAt = $now.AddDays(-30); State = 'none'; AuthorIsMaintainer = $true }
      [pscustomobject]@{ Number = 6; CreatedAt = $now.AddDays(-30); State = 'exempt'; AuthorIsMaintainer = $false }
    )
    $prs | ForEach-Object { $_ | Add-Member -NotePropertyName AuthorRole -NotePropertyValue $(if ($_.AuthorIsMaintainer) { 'maintain' } else { '' }) }
    $prs += [pscustomobject]@{ Number = 7; CreatedAt = $now.AddDays(-30); State = 'none'; AuthorIsMaintainer = $false; AuthorRole = 'write' }
    @(Select-StalePullRequest -PullRequests $prs -Now $now -Days 14) | Should -Be @(1, 2)
  }
}

Describe 'Invoke-IntakeSync' {
  BeforeEach {
    $script:calls = [System.Collections.Generic.List[string]]::new()
    $script:state = 'none'
    $script:writer = $false
    $script:role = ''
    $script:existing = @()
    Mock Read-LinkedPullRequest {
      [pscustomobject]@{ Number = 7; CreatedAt = [datetimeoffset]'2026-10-18T12:00:00Z'; AuthorIsMaintainer = $script:writer
        AuthorRole = $(if ($script:writer) { 'maintain' } else { $script:role }); Labels = @('issue:none'); Issues = @() }
    }
    Mock Get-IssueIntakeState { $script:state }
    Mock Get-IntakeComments { $script:existing }
    Mock Invoke-Gh { $script:calls.Add($Arguments -join ' ') }
  }

  It 'labels an outside pull request with no issue and posts the explanation once' {
    Invoke-IntakeSync -Repository 'o/r' -Number 7
    $script:calls | Should -Not -Contain 'pr edit 7 -R o/r --add-label issue:none'
    @($script:calls | Where-Object { $_ -like 'pr comment 7*' }).Count | Should -Be 1
  }

  It 'does not post the explanation again' {
    $script:existing = @([pscustomobject]@{ Id = 'IC_1'; IsMinimized = $false })
    Invoke-IntakeSync -Repository 'o/r' -Number 7
    @($script:calls | Where-Object { $_ -like 'pr comment 7*' }).Count | Should -Be 0
  }

  It 'relabels an accepted pull request and collapses the explanation' {
    $script:state = 'accepted'
    $script:existing = @([pscustomobject]@{ Id = 'IC_1'; IsMinimized = $false })
    Invoke-IntakeSync -Repository 'o/r' -Number 7
    $script:calls | Should -Contain 'pr edit 7 -R o/r --add-label issue:accepted --remove-label issue:none'
    @($script:calls | Where-Object { $_ -like '*minimizeComment*' -and $_ -like '*id=IC_1*' }).Count | Should -Be 1
  }

  It 'posts no explanation to a committer' {
    $script:role = 'write'
    Invoke-IntakeSync -Repository 'o/r' -Number 7
    @($script:calls | Where-Object { $_ -like 'pr comment*' }).Count | Should -Be 0
  }

  It 'posts no explanation to an author with the maintainer role' {
    $script:writer = $true
    Invoke-IntakeSync -Repository 'o/r' -Number 7
    @($script:calls | Where-Object { $_ -like 'pr comment*' }).Count | Should -Be 0
  }
}

Describe 'Invoke-IntakeCommand' {
  BeforeEach {
    $script:calls = [System.Collections.Generic.List[string]]::new()
    $script:writer = $true
    $script:roles = @{}
    Mock Get-RepositoryRole { if ($script:roles.ContainsKey($Login)) { $script:roles[$Login] } elseif ($script:writer) { 'maintain' } else { '' } }
    Mock Invoke-Gh { $script:calls.Add($Arguments -join ' ') }
    Mock Update-PullRequestsClosing { $script:calls.Add("refresh $Issue") }
    Mock Update-PullRequest { $script:calls.Add("refresh-pr $Number") }
    Mock Get-PullRequestAuthor { 'dana' }
  }

  It 'accepts the issue for a maintainer, removes needs-triage and refreshes its pull requests' {
    Invoke-IntakeCommand -Repository 'o/r' -Issue 12 -Body '/accept' -Actor 'maint' -CommentId 99
    $script:calls | Should -Contain 'issue edit 12 -R o/r --add-label accepted --remove-label needs-triage'
    $script:calls | Should -Contain 'refresh 12'
  }

  It 'declines the issue with the reason and closes it as not planned' {
    Invoke-IntakeCommand -Repository 'o/r' -Issue 12 -Body '/decline Out of scope.' -Actor 'maint' -CommentId 99
    $script:calls | Should -Contain 'issue edit 12 -R o/r --add-label wontfix --remove-label needs-triage --remove-label accepted'
    @($script:calls | Where-Object { $_ -like 'issue close 12 -R o/r --reason not planned --comment *Out of scope.*' }).Count | Should -Be 1
  }

  It 'ignores a command from someone without the maintainer role, saying so' {
    $script:writer = $false
    Invoke-IntakeCommand -Repository 'o/r' -Issue 12 -Body '/accept' -Actor 'stranger' -CommentId 99
    @($script:calls | Where-Object { $_ -like 'issue edit*' }).Count | Should -Be 0
    @($script:calls | Where-Object { $_ -like 'issue comment 12*' }).Count | Should -Be 1
  }
}

Describe 'Invoke-IntakeCommand on a pull request' {
  BeforeEach {
    $script:calls = [System.Collections.Generic.List[string]]::new()
    $script:roles = @{ maint = 'maintain'; dana = ''; committer = 'write' }
    Mock Get-RepositoryRole { $script:roles[$Login] }
    Mock Invoke-Gh { $script:calls.Add($Arguments -join ' ') }
    Mock Update-PullRequest { $script:calls.Add("refresh-pr $Number") }
    Mock Get-PullRequestAuthor { 'dana' }
  }

  It 'adds linked-issue:late for a maintainer and re-checks the pull request' {
    Invoke-IntakeCommand -Repository 'o/r' -Issue 7 -IsPullRequest -Body '/late-issue' -Actor 'maint'
    $script:calls | Should -Contain 'pr edit 7 -R o/r --add-label linked-issue:late'
    $script:calls | Should -Contain 'refresh-pr 7'
  }

  It 'adds docs:not-needed for a maintainer and re-checks the pull request' {
    Invoke-IntakeCommand -Repository 'o/r' -Issue 7 -IsPullRequest -Body '/docs-not-needed' -Actor 'maint'
    $script:calls | Should -Contain 'pr edit 7 -R o/r --add-label docs:not-needed'
    $script:calls | Should -Contain 'refresh-pr 7'
  }

  It 'refuses a label command from a committer' {
    Invoke-IntakeCommand -Repository 'o/r' -Issue 7 -IsPullRequest -Body '/docs-not-needed' -Actor 'committer'
    @($script:calls | Where-Object { $_ -like 'pr edit*' }).Count | Should -Be 0
    @($script:calls | Where-Object { $_ -like 'pr comment 7*' }).Count | Should -Be 1
  }

  It 're-checks for the author or a committer' -TestCases @(@{ Actor = 'dana' }, @{ Actor = 'committer' }) {
    Invoke-IntakeCommand -Repository 'o/r' -Issue 7 -IsPullRequest -Body '/recheck' -Actor $Actor
    $script:calls | Should -Contain 'refresh-pr 7'
  }

  It 'refuses /recheck from anyone else' {
    $script:roles['stranger'] = ''
    Invoke-IntakeCommand -Repository 'o/r' -Issue 7 -IsPullRequest -Body '/recheck' -Actor 'stranger'
    $script:calls | Should -Not -Contain 'refresh-pr 7'
  }

  It 'ignores an issue command on a pull request, and a pull request command on an issue' {
    Invoke-IntakeCommand -Repository 'o/r' -Issue 7 -IsPullRequest -Body '/accept' -Actor 'maint'
    Invoke-IntakeCommand -Repository 'o/r' -Issue 7 -Body '/recheck' -Actor 'maint'
    $script:calls.Count | Should -Be 0
  }
}

Describe 'Select-FailedCiRun' {
  It 'picks the latest failed pull request run of CI for the head commit' {
    $runs = @(
      [pscustomobject]@{ databaseId = 3; conclusion = 'failure'; event = 'pull_request'; createdAt = '2026-10-18T10:00:00Z' }
      [pscustomobject]@{ databaseId = 5; conclusion = 'failure'; event = 'pull_request'; createdAt = '2026-10-18T12:00:00Z' }
      [pscustomobject]@{ databaseId = 6; conclusion = 'failure'; event = 'merge_group'; createdAt = '2026-10-18T13:00:00Z' }
    )
    Select-FailedCiRun -Runs $runs | Should -Be 5
  }

  It 'picks nothing when the latest run did not fail' {
    $runs = @(
      [pscustomobject]@{ databaseId = 3; conclusion = 'failure'; event = 'pull_request'; createdAt = '2026-10-18T10:00:00Z' }
      [pscustomobject]@{ databaseId = 5; conclusion = ''; event = 'pull_request'; createdAt = '2026-10-18T12:00:00Z' }
    )
    Select-FailedCiRun -Runs $runs | Should -BeNullOrEmpty
  }
}

Describe 'Invoke-IssueOpened' {
  BeforeEach {
    $script:calls = [System.Collections.Generic.List[string]]::new()
    $script:roles = @{ maint = 'maintain'; owner = 'admin'; committer = 'write'; stranger = '' }
    Mock Get-RepositoryRole { $script:roles[$Login] }
    Mock Invoke-Gh { $script:calls.Add($Arguments -join ' ') }
  }

  It 'accepts an issue a maintainer opened, removing needs-triage' -TestCases @(@{ Author = 'maint' }, @{ Author = 'owner' }) {
    Invoke-IssueOpened -Repository 'o/r' -Issue 12 -Author $Author
    $script:calls | Should -Be @('issue edit 12 -R o/r --add-label accepted --remove-label needs-triage')
  }

  It 'leaves an issue from anyone else waiting for a maintainer' -TestCases @(@{ Author = 'committer' }, @{ Author = 'stranger' }) {
    Invoke-IssueOpened -Repository 'o/r' -Issue 12 -Author $Author
    $script:calls.Count | Should -Be 0
  }
}

Describe 'Select-PipelineOwnerJob' {
  BeforeAll {
    $script:Gate = "Gate $([char]0x00B7) Pipeline owner"
    function New-TestJob { param([string]$Conclusion, [string]$Status = 'completed', [string]$Name = $script:Gate, [long]$Id = 41)
      [pscustomobject]@{ name = $Name; databaseId = $Id; status = $Status; conclusion = $Conclusion } }
  }

  It 'picks the failed gate when a review approves, since the approval may be the one it waits for' {
    $jobs = @((New-TestJob -Name "Gate $([char]0x00B7) CI result" -Id 40 -Conclusion 'failure'), (New-TestJob -Conclusion 'failure'))
    Select-PipelineOwnerJob -Jobs $jobs -ReviewState 'approved' | Should -Be 41
  }

  It 'picks the passed gate when a review requests changes or is dismissed, since it may have counted that approval' -TestCases @(
    @{ State = 'changes_requested' }, @{ State = 'dismissed' }
  ) {
    Select-PipelineOwnerJob -Jobs @(New-TestJob -Conclusion 'success') -ReviewState $State | Should -Be 41
  }

  It 'picks nothing when the review cannot change the verdict' -TestCases @(
    @{ State = 'approved'; Conclusion = 'success' }
    @{ State = 'changes_requested'; Conclusion = 'failure' }
    @{ State = 'dismissed'; Conclusion = 'failure' }
    @{ State = 'commented'; Conclusion = 'failure' }
    @{ State = 'commented'; Conclusion = 'success' }
  ) {
    Select-PipelineOwnerJob -Jobs @(New-TestJob -Conclusion $Conclusion) -ReviewState $State | Should -BeNullOrEmpty
  }

  It 'picks nothing when the gate has not finished, or is not in the run' {
    Select-PipelineOwnerJob -Jobs @(New-TestJob -Status 'in_progress' -Conclusion '') -ReviewState 'approved' | Should -BeNullOrEmpty
    Select-PipelineOwnerJob -Jobs @(New-TestJob -Name 'Build' -Conclusion 'failure') -ReviewState 'approved' | Should -BeNullOrEmpty
  }
}

Describe 'Update-PipelineOwnerGate' {
  BeforeEach {
    $script:calls = [System.Collections.Generic.List[string]]::new()
    $script:runStatus = 'completed'
    $script:gate = "Gate $([char]0x00B7) Pipeline owner"
    Mock Invoke-Gh {
      $line = $Arguments -join ' '
      $script:calls.Add($line)
      if ($line -like 'pr view 7 *') { return 'abc123' }
      if ($line -like 'run list *') {
        return (ConvertTo-Json -Compress -InputObject @(
            @{ databaseId = 5; status = $script:runStatus; conclusion = 'failure'; event = 'pull_request'; createdAt = '2026-10-20T12:00:00Z' },
            @{ databaseId = 3; status = 'completed'; conclusion = 'failure'; event = 'pull_request'; createdAt = '2026-10-20T10:00:00Z' }))
      }
      if ($line -like 'run view 5 *') {
        return (ConvertTo-Json -Compress -InputObject @(@{ name = $script:gate; databaseId = 41; status = 'completed'; conclusion = 'failure' }))
      }
    }
  }

  It 're-runs the gate job of the latest pull request run for the head commit (and with it the CI result)' {
    Update-PipelineOwnerGate -Repository 'o/r' -Number 7 -ReviewState 'approved'
    $script:calls | Should -Contain 'run rerun --job 41 -R o/r'
    @($script:calls | Where-Object { $_ -like 'run list * --commit abc123 *' }).Count | Should -Be 1
  }

  It 'leaves a run that is still going, which cannot be re-run yet' {
    $script:runStatus = 'in_progress'
    Update-PipelineOwnerGate -Repository 'o/r' -Number 7 -ReviewState 'approved'
    @($script:calls | Where-Object { $_ -like 'run rerun*' }).Count | Should -Be 0
    @($script:calls | Where-Object { $_ -like 'run view*' }).Count | Should -Be 0
  }

  It 'does nothing when the review cannot change the verdict' {
    Update-PipelineOwnerGate -Repository 'o/r' -Number 7 -ReviewState 'commented'
    @($script:calls | Where-Object { $_ -like 'run rerun*' }).Count | Should -Be 0
  }
}

Describe 'Invoke-QueueGuard' {
  BeforeEach {
    $script:calls = [System.Collections.Generic.List[string]]::new()
    $script:check = [pscustomobject]@{ Pass = $true; Problems = @() }
    $script:dequeueFails = $false
    Mock Invoke-PipelineOwnerCheck { $script:check }
    Mock Invoke-Gh {
      $line = $Arguments -join ' '
      $script:calls.Add($line)
      if ($line -like 'pr view 7 *') { return 'PR_node7' }
      if ($line -like 'api graphql *dequeuePullRequest*' -and $script:dequeueFails) { throw 'gh api graphql failed (exit 1)' }
    }
  }

  It 'leaves a pull request that passes the pipeline-owner check in the queue' {
    Invoke-QueueGuard -Repository 'o/r' -Number 7
    @($script:calls | Where-Object { $_ -like '*dequeuePullRequest*' }).Count | Should -Be 0
    @($script:calls | Where-Object { $_ -like 'pr comment*' }).Count | Should -Be 0
  }

  It 'takes a pull request that fails the check out of the queue, and says why on it' {
    $script:check = [pscustomobject]@{ Pass = $false; Problems = @('changes .github/workflows/ci.yml without a build admin') }
    Invoke-QueueGuard -Repository 'o/r' -Number 7
    @($script:calls | Where-Object { $_ -like 'api graphql *dequeuePullRequest* -f id=PR_node7' }).Count | Should -Be 1
    $comment = @($script:calls | Where-Object { $_ -like 'pr comment 7 -R o/r --body *' })
    $comment.Count | Should -Be 1
    $comment[0] | Should -BeLike '*changes .github/workflows/ci.yml without a build admin*'
  }

  It 'fails, so the run shows red, when the dequeue itself fails' {
    $script:check = [pscustomobject]@{ Pass = $false; Problems = @('no build admin approval') }
    $script:dequeueFails = $true
    { Invoke-QueueGuard -Repository 'o/r' -Number 7 } | Should -Throw
  }
}

Describe 'ConvertFrom-PipelineOwnerOutput' {
  It 'passes on exit 0' {
    $result = ConvertFrom-PipelineOwnerOutput -ExitCode 0 -Output @('Pipeline owner: pull request #7 changes no file build admins own.')
    $result.Pass | Should -BeTrue
  }

  It 'fails on any other exit, with the ::error:: lines as the problems' {
    $result = ConvertFrom-PipelineOwnerOutput -ExitCode 1 -Output @('Pipeline owner: failed, no build admin approval on the head commit.', '::error::Pull request #7 changes files ...')
    $result.Pass | Should -BeFalse
    $result.Problems | Should -Be @('Pull request #7 changes files ...')
  }

  It 'fails closed with a generic problem when the check could not say why' {
    $result = ConvertFrom-PipelineOwnerOutput -ExitCode 3 -Output @()
    $result.Pass | Should -BeFalse
    @($result.Problems).Count | Should -Be 1
    $result.Problems[0] | Should -BeLike '*exit 3*'
  }
}
