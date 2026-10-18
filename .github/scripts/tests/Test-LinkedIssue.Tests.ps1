#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-LinkedIssue.ps1') -Repository o/r

  # A pull request as ConvertFrom-PullRequestJson returns it. Tests override from here.
  function New-TestPullRequest {
    param(
        [int]$Number = 7,
        [string]$Author = 'dana',
        [bool]$AuthorIsBot = $false,
        [string]$CreatedAt = '2026-10-18T12:00:00Z',
        [string]$BaseRef = 'develop',
        [string]$HeadRef = 'fix/thing',
        [object[]]$Issues = @([pscustomobject]@{ Number = 12; CreatedAt = [datetimeoffset]'2026-10-18T11:00:00Z' })
    )
    [pscustomobject]@{
      Number = $Number; Author = $Author; AuthorIsBot = $AuthorIsBot; CreatedAt = [datetimeoffset]$CreatedAt
      BaseRef = $BaseRef; HeadRef = $HeadRef; Issues = $Issues
    }
  }
}

Describe 'Get-LinkedIssueVerdict' {
  It 'passes a pull request that closes an issue opened before it' {
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest)
    $v.Pass | Should -BeTrue
    $v.Exempt | Should -BeFalse
    $v.Problems | Should -BeNullOrEmpty
  }

  It 'fails a pull request that closes no issue, saying how to fix it' {
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @())
    $v.Pass | Should -BeFalse
    $v.Problems.Count | Should -Be 1
    $v.Problems[0] | Should -Match 'closes no issue'
    $v.Problems[0] | Should -Match 'Fixes #N'
  }

  It 'fails when every linked issue was opened after the pull request' {
    $late = [pscustomobject]@{ Number = 13; CreatedAt = [datetimeoffset]'2026-10-18T13:00:00Z' }
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @($late))
    $v.Pass | Should -BeFalse
    $v.Problems[0] | Should -Match '#13'
    $v.Problems[0] | Should -Match 'opened after'
  }

  It 'passes when one linked issue came first even if another came later' {
    $early = [pscustomobject]@{ Number = 12; CreatedAt = [datetimeoffset]'2026-10-18T11:00:00Z' }
    $late = [pscustomobject]@{ Number = 13; CreatedAt = [datetimeoffset]'2026-10-18T13:00:00Z' }
    (Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @($late, $early))).Pass | Should -BeTrue
  }

  It 'exempts a pull request opened by a bot' {
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Author 'dependabot[bot]' -AuthorIsBot $true -Issues @())
    $v.Pass | Should -BeTrue
    $v.Exempt | Should -BeTrue
    $v.Reason | Should -Match 'bot'
  }

  It 'exempts the release pull request into main' {
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -BaseRef main -HeadRef 'release/v1.0.0' -Issues @())
    $v.Pass | Should -BeTrue
    $v.Exempt | Should -BeTrue
    $v.Reason | Should -Match 'release'
  }

  It 'does not exempt a hotfix into main, which is a fix like any other' {
    (Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -BaseRef main -HeadRef 'hotfix/v1.0.1' -Issues @())).Pass | Should -BeFalse
  }
}

Describe 'Get-QueuedPullRequestNumber' {
  It 'reads the pull request number from a merge queue branch' {
    Get-QueuedPullRequestNumber -HeadRef 'refs/heads/gh-readonly-queue/develop/pr-1433-94055d9fc0c1aa' | Should -Be 1433
  }

  It 'returns zero for a branch that is not a merge queue branch' {
    Get-QueuedPullRequestNumber -HeadRef 'refs/heads/fix/thing' | Should -Be 0
  }
}

Describe 'ConvertFrom-PullRequestJson' {
  It 'reads the pull request and the issues it closes in this repository' {
    $json = @'
{"data":{"repository":{"pullRequest":{"number":7,"createdAt":"2026-10-18T12:00:00Z","baseRefName":"develop","headRefName":"fix/thing",
 "author":{"login":"dana","__typename":"User"},
 "closingIssuesReferences":{"nodes":[
   {"number":12,"createdAt":"2026-10-18T11:00:00Z","repository":{"nameWithOwner":"o/r"}},
   {"number":99,"createdAt":"2026-10-18T10:00:00Z","repository":{"nameWithOwner":"other/repo"}}]}}}}}
'@
    $pr = ConvertFrom-PullRequestJson -Json $json -Repository 'o/r'

    $pr.Number | Should -Be 7
    $pr.Author | Should -Be 'dana'
    $pr.AuthorIsBot | Should -BeFalse
    $pr.BaseRef | Should -Be 'develop'
    $pr.HeadRef | Should -Be 'fix/thing'
    @($pr.Issues).Number | Should -Be @(12)
    $pr.Issues[0].CreatedAt | Should -Be ([datetimeoffset]'2026-10-18T11:00:00Z')
  }

  It 'marks a bot author' {
    $json = '{"data":{"repository":{"pullRequest":{"number":8,"createdAt":"2026-10-18T12:00:00Z","baseRefName":"develop","headRefName":"dependabot/x","author":{"login":"dependabot","__typename":"Bot"},"closingIssuesReferences":{"nodes":[]}}}}}'
    $pr = ConvertFrom-PullRequestJson -Json $json -Repository 'o/r'
    $pr.AuthorIsBot | Should -BeTrue
    @($pr.Issues).Count | Should -Be 0
  }

  It 'fails loudly when the pull request could not be read' {
    { ConvertFrom-PullRequestJson -Json '{"data":{"repository":{"pullRequest":null}}}' -Repository 'o/r' } | Should -Throw '*could not be read*'
  }
}
