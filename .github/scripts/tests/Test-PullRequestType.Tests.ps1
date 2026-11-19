# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-PullRequestType.ps1') -Repository o/r

  function Get-Verdict([string]$Title, [string[]]$ChangelogAdded = @(), [bool]$Bot = $false, [string]$Base = 'develop', [string]$Head = 'fix/x',
    [string[]]$FragmentAdded = @(), [string[]]$UnreleasedAdded = @(), [string[]]$Fragments = @()) {
    Get-PullRequestTypeVerdict -Title $Title -AuthorIsBot $Bot -BaseRef $Base -HeadRef $Head -ChangelogAdded $ChangelogAdded `
      -FragmentAdded $FragmentAdded -UnreleasedAdded $UnreleasedAdded -FragmentPaths $Fragments
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

  It 'fails a breaking change into develop with no Upgrade note in a changelog fragment, pointing at changelog.d' {
    $v = Get-Verdict -Title 'feat(scoping)!: change a default' -FragmentAdded @('- **Breaking: something.** It changed.') -Fragments @('changelog.d/12.md')
    $v.Pass | Should -BeFalse
    $v.Reason | Should -Be 'no upgrade note'
    ($v.Problems -join "`n") | Should -Match 'Upgrade'
    ($v.Problems -join "`n") | Should -Match 'changelog\.d/'
  }

  It 'passes a breaking change into develop whose fragment has an Upgrade note' {
    $added = @('- **Breaking (scope): x.** It changed. **Upgrade:** add OnCreate = ScopeFields.Tenant.')
    (Get-Verdict -Title 'fix!: change a default' -FragmentAdded $added -Fragments @('changelog.d/12.md')).Pass | Should -BeTrue
  }

  It 'passes a breaking change into a release branch whose CHANGELOG.md entry has an Upgrade note' {
    $added = @('- **Breaking (scope): x.** It changed. **Upgrade:** add OnCreate = ScopeFields.Tenant.')
    (Get-Verdict -Title 'fix!: change a default' -ChangelogAdded $added -Base 'release/v1.2.0').Pass | Should -BeTrue
  }

  It 'fails a breaking change into a release branch with no Upgrade note, pointing at CHANGELOG.md' {
    $v = Get-Verdict -Title 'fix!: change a default' -Base 'release/v1.2.0'
    $v.Pass | Should -BeFalse
    ($v.Problems -join "`n") | Should -Match "CHANGELOG\.md's"
  }

  It 'refuses a pull request into develop that adds lines under [Unreleased], pointing at changelog.d' {
    $v = Get-Verdict -Title 'fix: a bug' -UnreleasedAdded @('- **A fix (#12).** It works.')
    $v.Pass | Should -BeFalse
    $v.Reason | Should -Be 'changelog'
    ($v.Problems -join "`n") | Should -Match '\[Unreleased\]'
    ($v.Problems -join "`n") | Should -Match 'changelog\.d/<issue>\.md'
  }

  It 'lets a release branch and the sync back from main add under [Unreleased]' -TestCases @(
    @{ Base = 'release/v1.2.0'; Head = 'fix/x' }, @{ Base = 'develop'; Head = 'sync/main-to-develop-v1.2.0' }
  ) {
    (Get-Verdict -Title 'fix: a bug' -UnreleasedAdded @('- x') -Base $Base -Head $Head).Pass | Should -BeTrue
  }

  It 'refuses a fragment in a pull request into a release branch, which no cut would gather for it' {
    $v = Get-Verdict -Title 'fix: a bug' -Base 'release/v1.2.0' -Fragments @('changelog.d/12.md')
    $v.Pass | Should -BeFalse
    ($v.Problems -join "`n") | Should -Match 'changelog\.d/12\.md'
    ($v.Problems -join "`n") | Should -Match "CHANGELOG\.md's"
  }

  It 'reports every changelog problem at once' {
    $v = Get-Verdict -Title 'feat!: x' -UnreleasedAdded @('- x')
    $v.Problems.Count | Should -Be 2
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

Describe 'Get-UnreleasedAddedLines' {
  BeforeAll {
    $script:Head = @(
      '# Changelog', '', '## [Unreleased]', '', '### Fixed', '- new entry', '- old entry', '',
      '## [1.0.0] - 2026-01-01', '', '### Fixed', '- a correction'
    ) -join "`n"
  }

  It 'returns the added lines that land under [Unreleased] in the head file' {
    $patch = "@@ -3,4 +3,5 @@`n ## [Unreleased]`n `n ### Fixed`n+- new entry`n - old entry"
    @(Get-UnreleasedAddedLines -Patch $patch -Content $Head) | Should -Be @('- new entry')
  }

  It 'ignores an addition to a released section and a blank line' {
    $patch = "@@ -7,5 +8,6 @@`n `n+`n ## [1.0.0] - 2026-01-01`n `n ### Fixed`n+- a correction"
    @(Get-UnreleasedAddedLines -Patch $patch -Content $Head).Count | Should -Be 0
  }

  It 'counts the new line numbers across several hunks and removed lines' {
    $patch = "@@ -1,2 +1,1 @@`n-# Old title`n # Changelog`n@@ -6,1 +6,2 @@`n+- new entry`n - old entry"
    $content = @('# Changelog', '', '## [Unreleased]', '', '### Fixed', '- new entry', '- old entry') -join "`n"
    @(Get-UnreleasedAddedLines -Patch $patch -Content $content) | Should -Be @('- new entry')
  }

  It 'finds nothing when the file has no [Unreleased] heading' {
    @(Get-UnreleasedAddedLines -Patch "@@ -1,1 +1,2 @@`n # Changelog`n+- x" -Content "# Changelog`n- x").Count | Should -Be 0
  }
}

Describe 'Test-ChangelogFragmentPath' {
  It 'treats <p> as a fragment: <expected>' -TestCases @(
    @{ p = 'changelog.d/2168.md'; expected = $true }, @{ p = 'changelog.d/2152-2154.md'; expected = $true }
    @{ p = 'changelog.d/README.md'; expected = $false }, @{ p = 'CHANGELOG.md'; expected = $false }
    @{ p = 'docs/changelog.d/1.md'; expected = $false }
  ) {
    Test-ChangelogFragmentPath -Path $p | Should -Be $expected
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

