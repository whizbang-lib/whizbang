#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-LinkedIssue.ps1') -Repository o/r

  # An issue as ConvertFrom-PullRequestJson and Set-PullRequestAuthority leave it: accepted by a maintainer
  # unless a test says otherwise.
  function New-TestIssue {
    param([int]$Number = 12, [string]$CreatedAt = '2026-10-18T11:00:00Z', [string[]]$Labels = @('accepted'),
      [string]$AcceptedBy = 'maint', [bool]$AcceptedByMaintainer = $true)
    [pscustomobject]@{ Number = $Number; CreatedAt = [datetimeoffset]$CreatedAt; Labels = $Labels
      AcceptedBy = $(if ($AcceptedBy) { $AcceptedBy } else { $null }); AcceptedByMaintainer = $AcceptedByMaintainer }
  }

  # A pull request as ConvertFrom-PullRequestJson returns it. Tests override from here.
  function New-TestPullRequest {
    param(
        [int]$Number = 7,
        [string]$Author = 'dana',
        [bool]$AuthorIsBot = $false,
        [bool]$AuthorIsMaintainer = $false,
        [string]$AuthorRole = '',
        [string]$CreatedAt = '2026-10-18T12:00:00Z',
        [string]$BaseRef = 'develop',
        [string]$HeadRef = 'fix/thing',
        [object[]]$Issues = @(New-TestIssue),
        [string[]]$Labels = @()
    )
    [pscustomobject]@{
      Number = $Number; Author = $Author; AuthorIsBot = $AuthorIsBot; AuthorIsMaintainer = $AuthorIsMaintainer; AuthorRole = $AuthorRole; CreatedAt = [datetimeoffset]$CreatedAt
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
    $late = (New-TestIssue -Number 13 -CreatedAt '2026-10-18T13:00:00Z')
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @($late))
    $v.Pass | Should -BeFalse
    $v.Problems[0] | Should -Match '#13'
    $v.Problems[0] | Should -Match 'opened after'
  }

  It 'passes an issue opened after the pull request when a maintainer added the linked-issue:late label' {
    $late = (New-TestIssue -Number 13 -CreatedAt '2026-10-18T13:00:00Z')
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
    $late = (New-TestIssue -Number 13 -CreatedAt '2026-10-18T13:00:00Z')
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @($late))
    $v.Problems[0] | Should -Match 'linked-issue:late'
  }

  It 'fails an outside contributor whose linked issue is not accepted, saying how acceptance works' {
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @(New-TestIssue -Labels @('bug') -AcceptedBy ''))
    $v.Pass | Should -BeFalse
    $v.Reason | Should -Be 'issue not accepted'
    ($v.Problems -join "`n") | Should -Match '#12'
    ($v.Problems -join "`n") | Should -Match '`accepted`'
    ($v.Problems -join "`n") | Should -Match 'How a change gets in'
  }

  It 'does not count an accepted label added by someone without the maintainer role' {
    $issue = New-TestIssue -AcceptedBy 'helper' -AcceptedByMaintainer $false
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @($issue))
    $v.Pass | Should -BeFalse
    ($v.Problems -join "`n") | Should -Match 'maintainer'
  }

  It 'passes an author with the maintainer role whose issue is not accepted' {
    $issue = New-TestIssue -Labels @() -AcceptedBy ''
    (Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -AuthorIsMaintainer $true -Issues @($issue))).Pass | Should -BeTrue
  }

  It 'still fails an author with the maintainer role whose pull request closes no issue' {
    (Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -AuthorIsMaintainer $true -Issues @())).Pass | Should -BeFalse
  }

  It 'does not let linked-issue:late waive acceptance' {
    $late = New-TestIssue -Number 13 -CreatedAt '2026-10-18T13:00:00Z' -Labels @() -AcceptedBy ''
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @($late) -Labels @('linked-issue:late'))
    $v.Pass | Should -BeFalse
    $v.Reason | Should -Be 'issue not accepted'
  }

  It 'reports both problems when the only issue is newer than the pull request and not accepted' {
    $late = New-TestIssue -Number 13 -CreatedAt '2026-10-18T13:00:00Z' -Labels @() -AcceptedBy ''
    $v = Get-LinkedIssueVerdict -PullRequest (New-TestPullRequest -Issues @($late))
    $v.Problems.Count | Should -Be 2
  }

  It 'passes when one linked issue came first even if another came later' {
    $early = (New-TestIssue)
    $late = (New-TestIssue -Number 13 -CreatedAt '2026-10-18T13:00:00Z')
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

  It 'reads who last added the accepted label from each issue''s timeline' {
    $json = @'
{"data":{"repository":{"pullRequest":{"number":7,"createdAt":"2026-10-18T12:00:00Z","baseRefName":"develop","headRefName":"fix/thing",
 "author":{"login":"dana","__typename":"User"},
 "closingIssuesReferences":{"nodes":[
   {"number":12,"createdAt":"2026-10-18T11:00:00Z","repository":{"nameWithOwner":"o/r"},"labels":{"nodes":[{"name":"accepted"}]},
    "timelineItems":{"nodes":[{"label":{"name":"accepted"},"actor":{"login":"helper"}},{"label":{"name":"bug"},"actor":{"login":"x"}},{"label":{"name":"accepted"},"actor":{"login":"maint"}}]}},
   {"number":13,"createdAt":"2026-10-18T11:00:00Z","repository":{"nameWithOwner":"o/r"},"labels":{"nodes":[]},
    "timelineItems":{"nodes":[{"label":{"name":"accepted"},"actor":{"login":"maint"}}]}}]}}}}}
'@
    $pr = ConvertFrom-PullRequestJson -Json $json -Repository 'o/r'
    $pr.Issues[0].AcceptedBy | Should -Be 'maint'
    $pr.Issues[1].AcceptedBy | Should -BeNullOrEmpty
    $pr.Issues[0].AcceptedByMaintainer | Should -BeFalse
    $pr.AuthorIsMaintainer | Should -BeFalse
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

  It 'passes the gate for a maintainer''s release-branch fix whose description closes an earlier issue' {
    $pr = New-TestPullRequest -BaseRef 'release/v0.2622.0' -Issues @() -AuthorIsMaintainer $true
    $pr.Issues = @(Resolve-ClosingIssues -PullRequest $pr -Body 'Fixes #31' -LookupIssue $script:lookup)
    (Get-LinkedIssueVerdict -PullRequest $pr).Pass | Should -BeTrue
  }
}

Describe 'Set-PullRequestAuthority' {
  It 'asks once per login for the role of the author and of each accepter' {
    $script:asked = @()
    $roleOf = { param([string]$Login) $script:asked += $Login; @{ maint = 'maintain'; dana = 'write' }[$Login] }
    $pr = New-TestPullRequest -Author 'dana' -Issues @((New-TestIssue -Number 1 -AcceptedBy 'maint'), (New-TestIssue -Number 2 -AcceptedBy 'helper'),
      (New-TestIssue -Number 3 -AcceptedBy 'maint'), (New-TestIssue -Number 4 -Labels @() -AcceptedBy ''))
    Set-PullRequestAuthority -PullRequest $pr -RoleOf $roleOf
    $pr.AuthorRole | Should -Be 'write'
    $pr.AuthorIsMaintainer | Should -BeFalse
    $pr.Issues.AcceptedByMaintainer | Should -Be @($true, $false, $true, $false)
    $script:asked | Should -Be @('dana', 'maint', 'helper')
  }

  It 'marks a maintainer author' {
    $pr = New-TestPullRequest -Author 'maint'
    Set-PullRequestAuthority -PullRequest $pr -RoleOf { param([string]$Login) 'admin' }
    $pr.AuthorIsMaintainer | Should -BeTrue
  }

  It 'counts the workflow bot as an accepter, since it adds the label only for a maintainer''s /accept' {
    $pr = New-TestPullRequest -Issues @(New-TestIssue -AcceptedBy 'github-actions')
    Set-PullRequestAuthority -PullRequest $pr -RoleOf { param([string]$Login) '' }
    $pr.Issues[0].AcceptedByMaintainer | Should -BeTrue
  }
}

Describe 'Test-CommitterRole' {
  It 'counts write and above, which the committers team grants' -TestCases @(
    @{ Role = 'admin'; Expected = $true }
    @{ Role = 'maintain'; Expected = $true }
    @{ Role = 'write'; Expected = $true }
    @{ Role = 'triage'; Expected = $false }
    @{ Role = ''; Expected = $false }
  ) {
    Test-CommitterRole -RoleName $Role | Should -Be $Expected
  }
}

Describe 'Get-IssueIntakeState' {
  It 'names the state of the linked issue for the pull request label' -TestCases @(
    @{ Issues = @(@{}); Writer = $false; Expected = 'accepted' }
    @{ Issues = @(@{ Labels = @() ; AcceptedBy = '' }); Writer = $false; Expected = 'awaiting-acceptance' }
    @{ Issues = @(@{ Labels = @() ; AcceptedBy = '' }); Writer = $true; Expected = 'awaiting-acceptance' }
    @{ Issues = @(@{ AcceptedByMaintainer = $false }); Writer = $false; Expected = 'awaiting-acceptance' }
    @{ Issues = @(); Writer = $false; Expected = 'none' }
  ) {
    $issues = @($Issues | ForEach-Object { New-TestIssue @_ })
    Get-IssueIntakeState -PullRequest (New-TestPullRequest -Issues $issues -AuthorIsMaintainer $Writer) | Should -Be $Expected
  }

  It 'is exempt for a bot and for the release pull request into main' {
    Get-IssueIntakeState -PullRequest (New-TestPullRequest -AuthorIsBot $true -Issues @()) | Should -Be 'exempt'
    Get-IssueIntakeState -PullRequest (New-TestPullRequest -BaseRef main -HeadRef 'release/v1.0.0' -Issues @()) | Should -Be 'exempt'
  }
}

Describe 'Test-MaintainerRole' {
  It 'counts the maintain and admin roles, which the maintainers team grants' -TestCases @(
    @{ Role = 'admin'; Expected = $true }
    @{ Role = 'maintain'; Expected = $true }
    @{ Role = 'write'; Expected = $false }
    @{ Role = 'triage'; Expected = $false }
    @{ Role = 'read'; Expected = $false }
    @{ Role = ''; Expected = $false }
  ) {
    Test-MaintainerRole -RoleName $Role | Should -Be $Expected
  }
}
