# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:Script = Join-Path $Root '.github' 'scripts' 'Get-ReviewChecks.ps1'
  Import-Module (Join-Path $Root 'scripts' 'lib' 'ReviewFindings.psm1') -Force

  function Invoke-Git([string]$Repo, [string[]]$GitArgs) {
    $out = & git -C $Repo -c user.name='Dana Doe' -c user.email='dana@example.com' -c commit.gpgsign=false @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed" }
    return $out
  }
}

Describe 'Get-ReviewChecks.ps1' {
  BeforeAll {
    $script:Repo = Join-Path $TestDrive 'repo'
    New-Item -ItemType Directory -Path (Join-Path $Repo 'src' 'Lib') -Force | Out-Null
    Invoke-Git $Repo @('init', '-q', '-b', 'develop')
    Set-Content (Join-Path $Repo 'src' 'Lib' 'A.cs') 'int a;'
    Set-Content (Join-Path $Repo 'src' 'Lib' 'Quoted.cs') 'int q;'
    Invoke-Git $Repo @('add', '-A')
    Invoke-Git $Repo @('commit', '-q', '-s', '-m', 'base')
    $script:Base = (Invoke-Git $Repo @('rev-parse', 'HEAD')).Trim()

    Add-Content (Join-Path $Repo 'src' 'Lib' 'A.cs') '// the behaviour changes'
    Add-Content (Join-Path $Repo 'src' 'Lib' 'Quoted.cs') '// colour'
    Invoke-Git $Repo @('add', '-A')
    # Authored after the DCO was adopted, and not signed off.
    $env:GIT_AUTHOR_DATE = '2026-10-15T12:00:00Z'
    try { Invoke-Git $Repo @('commit', '-q', '-m', 'feat: a change') } finally { Remove-Item Env:GIT_AUTHOR_DATE }
    $script:HeadSha = (Invoke-Git $Repo @('rev-parse', 'HEAD')).Trim()

    Set-Content (Join-Path $TestDrive 'ignore.txt') @('# comments and blank lines are skipped', '', 'src/Lib/Quoted.cs')
    Set-Content (Join-Path $TestDrive 'body.md') 'A change with `Fixes #12` in backticks.'
    $script:Out = Join-Path $TestDrive 'comment.md'

    Push-Location $Repo
    try {
      & pwsh -NoProfile -File $Script -Base $Base -Head $HeadSha -BaseRef develop -HeadRef feat/x -PullRequest 7 `
        -BodyFile (Join-Path $TestDrive 'body.md') -ClosingIssueCount 0 -RunUrl 'https://example/run' `
        -IgnoreFile (Join-Path $TestDrive 'ignore.txt') -OutFile $Out
      $script:Exit = $LASTEXITCODE
    } finally { Pop-Location }
    $script:Text = Get-Content $Out -Raw
    $script:Block = @(ConvertFrom-ReviewBlock -Text $Text)[0]
  }

  It 'succeeds and writes the review-checks comment' {
    $Exit | Should -Be 0
    $Text | Should -BeLike '<!-- pr-review-checks -->*'
    $Text | Should -BeLike '*pwsh scripts/Invoke-PrReviewDraft.ps1 -PullRequest 7*'
  }

  It 'records the head commit it checked' {
    $Block.Source | Should -Be 'review-checks'
    $Block.Sha | Should -Be $HeadSha
  }

  It 'finds the diff, pull request and DCO problems' {
    $ids = @($Block.Findings.Id)
    $ids | Should -Contain 'style.en-us:src/Lib/A.cs:2'
    $ids | Should -Contain 'pr.closing-keyword'
    $ids | Should -Contain "dco:$($HeadSha.Substring(0, 12))"
    ($Block.Findings | Where-Object Id -EQ 'pr.closing-keyword').Body | Should -BeLike '*backticks*'
  }

  It 'skips the paths in the ignore file' {
    # The same check found the line in A.cs, so an empty result for Quoted.cs means it was skipped.
    @($Block.Findings | Where-Object Path -EQ 'src/Lib/A.cs').Count | Should -BeGreaterThan 0
    @($Block.Findings | Where-Object Path -EQ 'src/Lib/Quoted.cs').Count | Should -Be 0
  }

  It 'does not ask a bot pull request for an issue' {
    $botOut = Join-Path $TestDrive 'bot-comment.md'
    Push-Location $Repo
    try {
      & pwsh -NoProfile -File $Script -Base $Base -Head $HeadSha -BaseRef develop -HeadRef dependabot/x -PullRequest 8 `
        -BodyFile (Join-Path $TestDrive 'body.md') -ClosingIssueCount 0 -RunUrl 'https://example/run' `
        -IgnoreFile (Join-Path $TestDrive 'ignore.txt') -OutFile $botOut -AuthorIsBot
      $LASTEXITCODE | Should -Be 0
    } finally { Pop-Location }
    $botBlock = @(ConvertFrom-ReviewBlock -Text (Get-Content $botOut -Raw))[0]
    @($botBlock.Findings.Id) | Should -Not -Contain 'pr.closing-keyword'
    @($botBlock.Findings.Id) | Should -Contain 'style.en-us:src/Lib/A.cs:2'
  }

  It 'reports what the title type requires, from the diff and the closing issues' {
    $typeOut = Join-Path $TestDrive 'type-comment.md'
    Push-Location $Repo
    try {
      & pwsh -NoProfile -File $Script -Base $Base -Head $HeadSha -BaseRef develop -HeadRef fix/x -PullRequest 9 `
        -BodyFile (Join-Path $TestDrive 'body.md') -ClosingIssueCount 1 -RunUrl 'https://example/run' `
        -IgnoreFile (Join-Path $TestDrive 'ignore.txt') -OutFile $typeOut -Title 'fix(lib): a change' -ClosingIssueLabels 'enhancement,question'
      $LASTEXITCODE | Should -Be 0
    } finally { Pop-Location }
    $ids = @(@(ConvertFrom-ReviewBlock -Text (Get-Content $typeOut -Raw))[0].Findings.Id)
    $ids | Should -Contain 'pr.fix-bug-issue'
    $ids | Should -Contain 'pr.fix-test'
    $ids | Should -Contain 'pr.changelog'
  }

  # PowerShell names are case-insensitive: a local $labels for the closing issues once overwrote the
  # -Labels parameter, so the rules saw the issue's labels and docs:not-needed never cleared pr.docs-link (#1767).
  It 'honors the pull request''s own labels when the closing issue has labels too' {
    $labelOut = Join-Path $TestDrive 'label-comment.md'
    Push-Location $Repo
    try {
      & pwsh -NoProfile -File $Script -Base $Base -Head $HeadSha -BaseRef develop -HeadRef fix/x -PullRequest 11 `
        -BodyFile (Join-Path $TestDrive 'body.md') -ClosingIssueCount 1 -RunUrl 'https://example/run' `
        -IgnoreFile (Join-Path $TestDrive 'ignore.txt') -OutFile $labelOut -Title 'fix(lib): a change' `
        -ClosingIssueLabels 'bug' -Labels 'issue:accepted,docs:not-needed'
      $LASTEXITCODE | Should -Be 0
    } finally { Pop-Location }
    $ids = @(@(ConvertFrom-ReviewBlock -Text (Get-Content $labelOut -Raw))[0].Findings.Id)
    $ids | Should -Not -Contain 'pr.docs-link'
    $ids | Should -Not -Contain 'pr.fix-bug-issue' -Because 'the closing issue''s bug label still reaches its own rule'
  }

  It 'reports how many findings it wrote, for the gate step' {
    $outputs = Join-Path $TestDrive 'github-output.txt'
    $countOut = Join-Path $TestDrive 'count-comment.md'
    Push-Location $Repo
    try {
      $env:GITHUB_OUTPUT = $outputs
      & pwsh -NoProfile -File $Script -Base $Base -Head $HeadSha -BaseRef develop -HeadRef feat/x -PullRequest 10 `
        -BodyFile (Join-Path $TestDrive 'body.md') -ClosingIssueCount 0 -RunUrl 'https://example/run' `
        -IgnoreFile (Join-Path $TestDrive 'ignore.txt') -OutFile $countOut
      $LASTEXITCODE | Should -Be 0 -Because 'the comment is posted before the gate step decides'
    } finally { Remove-Item Env:GITHUB_OUTPUT; Pop-Location }
    $count = @(@(ConvertFrom-ReviewBlock -Text (Get-Content $countOut -Raw))[0].Findings).Count
    $count | Should -BeGreaterThan 0
    Get-Content $outputs | Should -Contain "findings=$count"
  }
}

Describe 'Get-ReviewChecks.ps1 reads every file the diff shows (#1799)' {
  BeforeAll {
    $script:Repo2 = Join-Path $TestDrive 'repo2'
    New-Item -ItemType Directory -Path (Join-Path $Repo2 'src' 'Lib') -Force | Out-Null
    Invoke-Git $Repo2 @('init', '-q', '-b', 'develop')
    $carried = "// surviving carry-forward $([char]0x2014) a small, bounded distortion"
    Set-Content (Join-Path $Repo2 'src' 'Lib' 'Closer.cs') @($carried, 'int a;', 'int b;', 'int c;', 'int d;', 'int e;')
    Invoke-Git $Repo2 @('add', '-A')
    Invoke-Git $Repo2 @('commit', '-q', '-s', '-m', 'base')
    $script:Base2 = (Invoke-Git $Repo2 @('rev-parse', 'HEAD')).Trim()

    # Moved and mostly rewritten: under git's 50% rename threshold, so the diff shows a deleted file and a
    # new one. The carried line is not the pull request's; the new dash is.
    Remove-Item (Join-Path $Repo2 'src' 'Lib' 'Closer.cs')
    Set-Content (Join-Path $Repo2 'src' 'Lib' 'Truncator.cs') @($carried, 'long v;', 'long w;', 'long x;', 'long y;',
      "// a new line $([char]0x2014) with a new dash")
    # A path git quotes by default (core.quotePath): its added lines are checked like any other.
    Set-Content -LiteralPath (Join-Path $Repo2 'src' 'Lib' "Caf$([char]0x00E9).cs") '// colour'
    Invoke-Git $Repo2 @('add', '-A')
    Invoke-Git $Repo2 @('commit', '-q', '-s', '-m', 'feat: move the closer')
    $script:Head2 = (Invoke-Git $Repo2 @('rev-parse', 'HEAD')).Trim()

    Set-Content (Join-Path $TestDrive 'body2.md') 'Fixes #12'
    $script:Out2 = Join-Path $TestDrive 'comment2.md'
    Push-Location $Repo2
    try {
      & pwsh -NoProfile -File $Script -Base $Base2 -Head $Head2 -BaseRef develop -HeadRef feat/x -PullRequest 8 `
        -BodyFile (Join-Path $TestDrive 'body2.md') -ClosingIssueCount 1 -RunUrl 'https://example/run' `
        -IgnoreFile (Join-Path $TestDrive 'no-ignore.txt') -OutFile $Out2
      $script:Exit2 = $LASTEXITCODE
    } finally { Pop-Location }
    $script:Ids2 = @(@(ConvertFrom-ReviewBlock -Text (Get-Content $Out2 -Raw))[0].Findings | ForEach-Object Id)
  }

  It 'checks the added lines of a file whose path git would quote' {
    $Exit2 | Should -Be 0
    $Ids2 | Should -Contain "style.en-us:src/Lib/Caf$([char]0x00E9).cs:1"
  }

  It 'flags the line a moved file adds, and not the line it carried over' {
    $Ids2 | Should -Contain 'style.em-dash:src/Lib/Truncator.cs:6'
    $Ids2 | Should -Not -Contain 'style.em-dash:src/Lib/Truncator.cs:1'
  }
}

Describe 'the Gate · Review checks job in ci.yml' {
  BeforeAll {
    $ci = Get-Content -Raw (Join-Path $Root '.github' 'workflows' 'ci.yml')
    $script:Job = [regex]::Match($ci, '(?ms)^  review-checks:\s*$.*?(?=^  [\w-]+:\s*$)').Value
  }

  # Without issues: read the job's token reads a pull request's labels as an empty list rather than
  # failing, so docs:not-needed never reached the rules and pr.docs-link stayed open (#1659).
  It 'can read the pull request''s labels' {
    $Job | Should -Not -BeNullOrEmpty -Because 'the job must be found for this check to mean anything'
    $Job | Should -Match '(?m)^      issues: read\b'
  }

  It 'logs the labels it read, so an empty read is visible' {
    $Job | Should -Match 'echo "Labels read: \$PR_LABELS"'
  }
}
