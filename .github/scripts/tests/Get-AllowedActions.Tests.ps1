#Requires -Modules Pester

# Only allowed actions can run (#1467): the repository's Actions setting allows GitHub's own actions, verified
# Marketplace creators, and the patterns in .github/allowed-actions.txt. These tests keep that list and the
# workflows in step, so an action the setting would refuse fails here, in review, and not in a release run.

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  . (Join-Path $PSScriptRoot '..' 'Get-AllowedActions.ps1')
}

Describe 'Test-ActionAllowed' {
  It 'allows any action in the actions and github organizations, which GitHub owns' {
    Test-ActionAllowed -Reference 'actions/checkout@abc' -Patterns @() | Should -BeTrue
    Test-ActionAllowed -Reference 'github/codeql-action/init@abc' -Patterns @() | Should -BeTrue
  }

  It 'allows an action that matches a pattern, wildcards included' {
    Test-ActionAllowed -Reference 'codecov/codecov-action@abc' -Patterns @('codecov/codecov-action@*') | Should -BeTrue
    Test-ActionAllowed -Reference 'gittools/actions/gitversion/setup@abc' -Patterns @('gittools/actions/gitversion/setup@*') | Should -BeTrue
  }

  It 'refuses an action no pattern names, and one whose owner only looks like an allowed one' {
    Test-ActionAllowed -Reference 'evil/codecov-action@abc' -Patterns @('codecov/codecov-action@*') | Should -BeFalse
    Test-ActionAllowed -Reference 'actions-evil/checkout@abc' -Patterns @() | Should -BeFalse
    Test-ActionAllowed -Reference 'codecov/codecov-action-fork@abc' -Patterns @('codecov/codecov-action@*') | Should -BeFalse
  }

  It 'treats the bracket and question mark in a reference as text, not wildcards' {
    Test-ActionAllowed -Reference 'a/b@x' -Patterns @('a/?@x') | Should -BeFalse
  }
}

Describe 'Read-AllowedActionList' {
  It 'reads one pattern per line, skipping comments and blank lines' {
    $path = Join-Path ([IO.Path]::GetTempPath()) ("aa-" + [guid]::NewGuid().ToString('N') + '.txt')
    try {
      Set-Content -LiteralPath $path -Value @('# comment', '', 'a/b@*  # why', '  c/d@*  ')
      Read-AllowedActionList -Path $path | Should -Be @('a/b@*', 'c/d@*')
    } finally { Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue }
  }
}

Describe 'Get-ActionReference' {
  It 'finds every remote uses: in workflows and composite actions, skipping local and docker references' {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("aa-" + [guid]::NewGuid().ToString('N'))
    try {
      New-Item -ItemType Directory -Path (Join-Path $dir 'workflows'), (Join-Path $dir 'actions' 'x') | Out-Null
      Set-Content -LiteralPath (Join-Path $dir 'workflows' 'w.yml') -Value @(
        'jobs:', '  a:', '    steps:',
        '      - uses: actions/checkout@abc # v1',
        "      - uses: 'owner/repo/path@def'",
        '      - uses: ./.github/actions/x',
        '      - uses: docker://alpine:3',
        '  b:', '    uses: ./.github/workflows/reusable.yml')
      Set-Content -LiteralPath (Join-Path $dir 'actions' 'x' 'action.yml') -Value @('runs:', '  steps:', '    - name: n', '      uses: "nick/retry@123"')
      $refs = @(Get-ActionReference -GitHubDirectory $dir)
      $refs.Reference | Sort-Object | Should -Be @('actions/checkout@abc', 'nick/retry@123', 'owner/repo/path@def')
      ($refs | Where-Object Reference -EQ 'nick/retry@123').File | Should -BeLike '*action.yml'
    } finally { Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue }
  }
}

Describe 'This repository' {
  BeforeAll {
    $script:Patterns = @(Read-AllowedActionList -Path (Join-Path $Root '.github' 'allowed-actions.txt'))
    $script:Refs = @(Get-ActionReference -GitHubDirectory (Join-Path $Root '.github'))
  }

  It 'uses actions' {
    $Refs.Count | Should -BeGreaterThan 50
  }

  It 'uses only actions the allowed-actions setting lets run' {
    $refused = @($Refs | Where-Object { -not (Test-ActionAllowed -Reference $_.Reference -Patterns $Patterns) } |
        ForEach-Object { "$($_.File): $($_.Reference)" })
    $refused | Should -BeNullOrEmpty
  }

  It 'lists no pattern that nothing uses, so the list stays the set of actions in use' {
    $unused = @($Patterns | Where-Object { $p = $_; -not ($Refs | Where-Object { Test-ActionAllowed -Reference $_.Reference -Patterns @($p) }) })
    $unused | Should -BeNullOrEmpty
  }

  It 'lists no GitHub-owned action, which the setting already allows' {
    @($Patterns | Where-Object { $_ -match '^(actions|github)/' }) | Should -BeNullOrEmpty
  }
}
