# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-PullRequestType.ps1') -Repository o/r

  function Get-Verdict([string]$Title, [string[]]$ChangelogAdded = @(), [bool]$Bot = $false, [string]$Base = 'develop', [string]$Head = 'fix/x') {
    Get-PullRequestTypeVerdict -Title $Title -AuthorIsBot $Bot -BaseRef $Base -HeadRef $Head -ChangelogAdded $ChangelogAdded
  }
}

Describe 'Get-PullRequestTypeVerdict' {
  It 'passes a Conventional Commit title' -TestCases @(
    @{ T = 'fix(scoping): apply [InheritScope] as documented' }
    @{ T = 'feat: a new capability' }
    @{ T = 'docs(review): add a skill' }
    @{ T = 'perf(claim): read less' }
    @{ T = 'refactor: tidy' }
    @{ T = 'test: cover it' }
    @{ T = 'ci: a gate' }
    @{ T = 'build: pin it' }
    @{ T = 'chore(deps): bump' }
    @{ T = 'revert: undo it' }
  ) {
    (Get-Verdict -Title $T).Pass | Should -BeTrue
  }

  It 'fails a title that is not a Conventional Commit, naming the allowed types' -TestCases @(
    @{ T = 'Fix the thing' }
    @{ T = 'fix:missing space' }
    @{ T = 'feature: not a type' }
    @{ T = 'fix(): empty scope' }
    @{ T = '' }
  ) {
    $v = Get-Verdict -Title $T
    $v.Pass | Should -BeFalse
    ($v.Problems -join "`n") | Should -Match 'Conventional Commit'
    ($v.Problems -join "`n") | Should -Match 'feat, fix, docs'
  }

  It 'fails a breaking change with no Upgrade note in CHANGELOG.md' {
    $v = Get-Verdict -Title 'feat(scoping)!: change a default' -ChangelogAdded @('- **Breaking: something.** It changed.')
    $v.Pass | Should -BeFalse
    ($v.Problems -join "`n") | Should -Match 'Upgrade'
  }

  It 'passes a breaking change whose CHANGELOG.md entry has an Upgrade note' {
    $added = @('- **Breaking (scope): x.** It changed. **Upgrade:** add OnCreate = ScopeFields.Tenant.')
    (Get-Verdict -Title 'fix!: change a default' -ChangelogAdded $added).Pass | Should -BeTrue
  }

  It 'does not ask a non-breaking change for an Upgrade note' {
    (Get-Verdict -Title 'fix: a bug').Pass | Should -BeTrue
  }

  It 'exempts bots and the release pull request into main' {
    $bot = Get-Verdict -Title 'Bump x from 1 to 2' -Bot $true
    $bot.Pass | Should -BeTrue
    $bot.Exempt | Should -BeTrue
    $release = Get-Verdict -Title 'Release v1.0.0' -Base main -Head 'release/v1.0.0'
    $release.Pass | Should -BeTrue
    $release.Exempt | Should -BeTrue
  }
}

Describe 'Get-PatchAddedLines' {
  It 'returns the lines a patch adds, without the diff markers' {
    $patch = "@@ -1,2 +1,3 @@`n context`n+added one`n-removed`n+added two"
    @(Get-PatchAddedLines -Patch $patch) | Should -Be @('added one', 'added two')
  }

  It 'returns nothing for an empty patch' {
    @(Get-PatchAddedLines -Patch '').Count | Should -Be 0
  }
}

Describe 'Test-PullRequestType.ps1 parameters' {
  It 'keeps the pull request and queue branch it was given after loading the shared helpers' {
    # Dot-sourcing Test-LinkedIssue.ps1 runs its param() block in this script's scope; its defaults
    # (PullRequest 0, HeadRef '') must not replace the values this script was called with.
    $script = Join-Path $PSScriptRoot '..' 'Test-PullRequestType.ps1'
    $seen = & { . $script -Repository o/r -PullRequest 1445 -HeadRef 'refs/heads/x'; "$PullRequest|$HeadRef" }
    $seen | Should -Be '1445|refs/heads/x'
  }
}

