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
        [object[]]$Issues = @([pscustomobject]@{ Number = 12; CreatedAt = [datetimeoffset]'2026-10-18T11:00:00Z' }),
        [string[]]$Labels = @()
    )
    [pscustomobject]@{
      Number = $Number; Author = $Author; AuthorIsBot = $AuthorIsBot; CreatedAt = [datetimeoffset]$CreatedAt
      BaseRef = $BaseRef; HeadRef = $HeadRef; Issues = $Issues; Labels = $Labels
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

  It 'passes an issue opened after the pull request when a maintainer added the linked-issue:late label' {
    $late = [pscustomobject]@{ Number = 13; CreatedAt = [datetimeoffset]'2026-10-18T13:00:00Z' }
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @($late) -Labels @('bug', 'linked-issue:late'))
    $v.Pass | Should -BeTrue
    $v.Exempt | Should -BeFalse
    $v.Reason | Should -Match '#13'
    $v.Reason | Should -Match 'opened after the pull request'
    $v.Reason | Should -Match 'linked-issue:late'
  }

  It 'still fails a pull request that closes no issue when it has the linked-issue:late label' {
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @() -Labels @('linked-issue:late'))
    $v.Pass | Should -BeFalse
    $v.Problems[0] | Should -Match 'closes no issue'
  }

  It 'names the linked-issue:late label when every linked issue was opened after the pull request' {
    $late = [pscustomobject]@{ Number = 13; CreatedAt = [datetimeoffset]'2026-10-18T13:00:00Z' }
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @($late))
    $v.Problems[0] | Should -Match 'linked-issue:late'
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
    @($pr.Labels).Count | Should -Be 0
    @($pr.Issues).Number | Should -Be @(12)
    $pr.Issues[0].CreatedAt | Should -Be ([datetimeoffset]'2026-10-18T11:00:00Z')
  }

  It 'reads the pull request labels' {
    $json = '{"data":{"repository":{"pullRequest":{"number":9,"createdAt":"2026-10-18T12:00:00Z","baseRefName":"develop","headRefName":"fix/x","author":{"login":"dana","__typename":"User"},"labels":{"nodes":[{"name":"bug"},{"name":"linked-issue:late"}]},"closingIssuesReferences":{"nodes":[]}}}}}'
    @((ConvertFrom-PullRequestJson -Json $json -Repository 'o/r').Labels) | Should -Be @('bug', 'linked-issue:late')
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

Describe 'Get-ClosingKeywordNumbers' {
  It 'reads each closing keyword GitHub honors' {
    @(Get-ClosingKeywordNumbers -Body "Fixes #12`nThis also closes #3 and Resolved #4.") | Should -Be @(12, 3, 4)
  }

  It 'reads a keyword however it is cased, once per issue' {
    @(Get-ClosingKeywordNumbers -Body 'FIXES #8, fixed #8, Close #9') | Should -Be @(8, 9)
  }

  It 'ignores a reference with no closing keyword' {
    @(Get-ClosingKeywordNumbers -Body 'Refs #5, see #6') | Should -BeNullOrEmpty
  }

  It 'ignores a keyword inside inline code or a code block, as GitHub does' {
    $body = "Write ``Fixes #9`` to link.`n``````n Fixes #10`n```````nNothing else."
    @(Get-ClosingKeywordNumbers -Body $body) | Should -BeNullOrEmpty
  }

  It 'reads nothing from an empty description' {
    @(Get-ClosingKeywordNumbers -Body '') | Should -BeNullOrEmpty
  }
}

Describe 'Resolve-ClosingIssues' {
  BeforeAll {
    $script:lookedUp = @()
    $script:lookup = {
      param([int]$Number)
      $script:lookedUp += $Number
      if ($Number -eq 404) { return $null }
      [pscustomobject]@{ Number = $Number; CreatedAt = [datetimeoffset]'2026-10-18T11:00:00Z'; Labels = @('bug') }
    }
  }

  BeforeEach { $script:lookedUp = @() }

  It "keeps GitHub's own closing references when there are any" {
    $pr = New-TestPullRequest
    $issues = @(Resolve-ClosingIssues -PullRequest $pr -Body 'Fixes #99' -LookupIssue $script:lookup)
    $issues.Number | Should -Be @(12)
    $script:lookedUp | Should -BeNullOrEmpty
  }

  It 'reads the description of a pull request into a release branch, which GitHub never links' {
    $pr = New-TestPullRequest -BaseRef 'release/v0.2622.0' -Issues @()
    $issues = @(Resolve-ClosingIssues -PullRequest $pr -Body 'Fixes #31 and closes #404' -LookupIssue $script:lookup)
    $issues.Number | Should -Be @(31)
    $issues[0].Labels | Should -Be @('bug')
    $script:lookedUp | Should -Be @(31, 404)
  }

  It 'does not read the description of a pull request into the default branch' {
    $pr = New-TestPullRequest -BaseRef 'develop' -Issues @()
    @(Resolve-ClosingIssues -PullRequest $pr -Body 'Fixes #31' -LookupIssue $script:lookup) | Should -BeNullOrEmpty
    $script:lookedUp | Should -BeNullOrEmpty
  }

  It 'passes the gate for a release-branch fix whose description closes an earlier issue' {
    $pr = New-TestPullRequest -BaseRef 'release/v0.2622.0' -Issues @()
    $pr.Issues = @(Resolve-ClosingIssues -PullRequest $pr -Body 'Fixes #31' -LookupIssue $script:lookup)
    (Get-LinkedIssueVerdict -PullRequest $pr).Pass | Should -BeTrue
  }
}
