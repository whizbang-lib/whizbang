#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-PipelineOwner.ps1') -Repository o/r

  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:Team = '@whizbang-lib/build-admins'
  $script:Head = 'a1b2c3d4e5f60718293a4b5c6d7e8f9012345678'
  $script:Older = '0123456789abcdef0123456789abcdef01234567'
  $script:Owners = ConvertFrom-CodeOwners -Lines @(
    '# a comment', '',
    '/.github/                   @whizbang-lib/build-admins',
    '/scripts/                   @whizbang-lib/build-admins',
    '/global.json                @whizbang-lib/build-admins'
  )

  # A review as Read-PipelineOwnerPullRequest and Set-PipelineOwnerAuthority leave it.
  function New-TestReview {
    param([string]$Author = 'boss', [string]$State = 'APPROVED', [string]$CommitId = $script:Head,
      [string]$SubmittedAt = '2026-10-20T12:00:00Z', [string]$AuthorRole = 'admin')
    [pscustomobject]@{ Author = $Author; State = $State; CommitId = $CommitId; SubmittedAt = [datetimeoffset]$SubmittedAt
      AuthorRole = $AuthorRole }
  }

  # A pull request as Read-PipelineOwnerPullRequest returns it, roles filled in. Tests override from here.
  function New-TestPullRequest {
    param(
        [int]$Number = 7,
        [string]$Author = 'dana',
        [bool]$AuthorIsBot = $false,
        [string]$AuthorRole = 'write',
        [string]$HeadSha = $script:Head,
        [string[]]$Files = @('.github/workflows/ci.yml'),
        [object[]]$Reviews = @()
    )
    [pscustomobject]@{ Number = $Number; Author = $Author; AuthorIsBot = $AuthorIsBot; AuthorRole = $AuthorRole
      HeadSha = $HeadSha; Files = $Files; Reviews = $Reviews }
  }

  function Get-TestVerdict { param($PullRequest) Get-PipelineOwnerVerdict -PullRequest $PullRequest -CodeOwners $script:Owners -Owner $script:Team }
}

Describe 'Get-PipelineOwnerVerdict' {
  It 'passes a pull request that changes no file build admins own, whoever opened it' {
    $v = Get-TestVerdict (New-TestPullRequest -AuthorRole 'read' -Files @('src/Whizbang.Core/Thing.cs', 'README.md'))
    $v.Pass | Should -BeTrue
    $v.Exempt | Should -BeFalse
    $v.OwnedFiles | Should -BeNullOrEmpty
    $v.Reason | Should -Match 'no file'
  }

  It 'passes a build admin''s own pipeline change with no review' {
    $v = Get-TestVerdict (New-TestPullRequest -Author 'boss' -AuthorRole 'admin')
    $v.Pass | Should -BeTrue
    $v.Reason | Should -Match 'build admin'
  }

  It 'fails a committer''s pipeline change with no review, naming the files and what it needs' {
    $v = Get-TestVerdict (New-TestPullRequest -Files @('.github/workflows/ci.yml', 'src/A.cs', 'global.json'))
    $v.Pass | Should -BeFalse
    $v.OwnedFiles | Should -Be @('.github/workflows/ci.yml', 'global.json')
    $v.Problems.Count | Should -Be 1
    $v.Problems[0] | Should -Match ([regex]::Escape('.github/workflows/ci.yml'))
    $v.Problems[0] | Should -Match ([regex]::Escape('global.json'))
    $v.Problems[0] | Should -Not -Match ([regex]::Escape('src/A.cs'))
    $v.Problems[0] | Should -Match 'build admin'
    $v.Problems[0] | Should -Match 'approv'
    $v.Problems[0] | Should -Match $script:Head.Substring(0, 7)
  }

  It 'fails a maintainer''s pipeline change with no review: maintaining the code is not owning the pipeline' {
    (Get-TestVerdict (New-TestPullRequest -AuthorRole 'maintain')).Pass | Should -BeFalse
  }

  It 'fails a pipeline change from someone with no role on the repository' {
    (Get-TestVerdict (New-TestPullRequest -AuthorRole '')).Pass | Should -BeFalse
  }

  It 'passes a committer''s pipeline change a build admin approved on the current head' {
    $v = Get-TestVerdict (New-TestPullRequest -Reviews @(New-TestReview))
    $v.Pass | Should -BeTrue
    $v.Reason | Should -Match 'boss'
  }

  It 'fails when the build admin approved an earlier commit only' {
    $v = Get-TestVerdict (New-TestPullRequest -Reviews @(New-TestReview -CommitId $script:Older))
    $v.Pass | Should -BeFalse
    $v.Problems[0] | Should -Match 'earlier commit'
  }

  It 'fails when the only approval is from someone without the admin role' -TestCases @(
    @{ Role = 'write' }, @{ Role = 'maintain' }, @{ Role = '' }
  ) {
    (Get-TestVerdict (New-TestPullRequest -Reviews @(New-TestReview -Author 'kim' -AuthorRole $Role))).Pass | Should -BeFalse
  }

  It 'fails when a build admin requested changes' {
    (Get-TestVerdict (New-TestPullRequest -Reviews @(New-TestReview -State 'CHANGES_REQUESTED'))).Pass | Should -BeFalse
  }

  It 'fails when a build admin approved and then requested changes on the same head' {
    $reviews = @(
      (New-TestReview -State 'APPROVED' -SubmittedAt '2026-10-20T12:00:00Z'),
      (New-TestReview -State 'CHANGES_REQUESTED' -SubmittedAt '2026-10-20T13:00:00Z'))
    (Get-TestVerdict (New-TestPullRequest -Reviews $reviews)).Pass | Should -BeFalse
  }

  It 'fails when the build admin''s approval was dismissed' {
    (Get-TestVerdict (New-TestPullRequest -Reviews @(New-TestReview -State 'DISMISSED'))).Pass | Should -BeFalse
  }

  It 'keeps an approval when the build admin later only commented' {
    $reviews = @(
      (New-TestReview -State 'COMMENTED' -SubmittedAt '2026-10-20T13:00:00Z'),
      (New-TestReview -State 'APPROVED' -SubmittedAt '2026-10-20T12:00:00Z'))
    (Get-TestVerdict (New-TestPullRequest -Reviews $reviews)).Pass | Should -BeTrue
  }

  It 'passes when a build admin re-approved on the current head after approving an earlier commit' {
    $reviews = @(
      (New-TestReview -CommitId $script:Older -SubmittedAt '2026-10-20T11:00:00Z'),
      (New-TestReview -SubmittedAt '2026-10-20T12:00:00Z'))
    (Get-TestVerdict (New-TestPullRequest -Reviews $reviews)).Pass | Should -BeTrue
  }

  It 'exempts a pull request opened by a bot, as the linked-issue gate does' {
    $v = Get-TestVerdict (New-TestPullRequest -Author 'dependabot[bot]' -AuthorIsBot $true -AuthorRole '')
    $v.Pass | Should -BeTrue
    $v.Exempt | Should -BeTrue
    $v.Reason | Should -Match 'bot'
  }

  It 'does not exempt a user whose login merely ends in [bot]' {
    (Get-TestVerdict (New-TestPullRequest -Author 'sneaky[bot]' -AuthorIsBot $false)).Pass | Should -BeFalse
  }
}

Describe 'Test-CodeOwnerMatch' {
  It 'matches <Path> against <Pattern>: <Expected>' -TestCases @(
    # A trailing slash is a directory, anchored by the leading slash: anything under it, at any depth.
    @{ Pattern = '/.github/'; Path = '.github/workflows/ci.yml'; Expected = $true }
    @{ Pattern = '/.github/'; Path = '.github/CODEOWNERS'; Expected = $true }
    @{ Pattern = '/scripts/'; Path = 'scripts/lib/ReviewFindings.psm1'; Expected = $true }
    # A file merely named like the directory is not in it.
    @{ Pattern = '/scripts/'; Path = 'scripts'; Expected = $false }
    @{ Pattern = '/scripts/'; Path = 'scripts.md'; Expected = $false }
    @{ Pattern = '/.github/'; Path = '.github-old/ci.yml'; Expected = $false }
    # Anchored at the root: the same directory deeper down is not the owned one.
    @{ Pattern = '/scripts/'; Path = 'src/scripts/Thing.ps1'; Expected = $false }
    @{ Pattern = '/.github/'; Path = 'samples/App/.github/x.yml'; Expected = $false }
    # An exact file, anchored at the root.
    @{ Pattern = '/global.json'; Path = 'global.json'; Expected = $true }
    @{ Pattern = '/global.json'; Path = 'samples/global.json'; Expected = $false }
    @{ Pattern = '/global.json'; Path = 'global.json.bak'; Expected = $false }
    @{ Pattern = '/Directory.Build.props'; Path = 'src/Directory.Build.props'; Expected = $false }
    # A file pattern also owns a directory of that name, as git and GitHub read it.
    @{ Pattern = '/global.json'; Path = 'global.json/x'; Expected = $true }
    # Case matters, as on GitHub.
    @{ Pattern = '/global.json'; Path = 'Global.json'; Expected = $false }
    # With no leading slash and no inner slash, the name matches at any depth.
    @{ Pattern = 'BannedSymbols.txt'; Path = 'src/BannedSymbols.txt'; Expected = $true }
    @{ Pattern = 'tools/'; Path = 'a/tools/b.txt'; Expected = $true }
    # An inner slash anchors it like a leading one.
    @{ Pattern = 'docs/api/'; Path = 'docs/api/x.md'; Expected = $true }
    @{ Pattern = 'docs/api/'; Path = 'src/docs/api/x.md'; Expected = $false }
  ) {
    Test-CodeOwnerMatch -Pattern $Pattern -Path $Path | Should -Be $Expected
  }

  It 'refuses a wildcard pattern it cannot read, rather than owning too few files' -TestCases @(
    @{ Pattern = '*.yml' }, @{ Pattern = '/docs/**/x.md' }, @{ Pattern = '/a?.txt' }, @{ Pattern = '/[ab].txt' }
  ) {
    { Test-CodeOwnerMatch -Pattern $Pattern -Path 'x.yml' } | Should -Throw '*not supported*'
  }
}

Describe 'Get-OwnedFile' {
  It 'lets the last matching pattern decide, as GitHub does' {
    $owners = ConvertFrom-CodeOwners -Lines @(
      '/.github/              @whizbang-lib/build-admins',
      '/.github/ISSUE_TEMPLATE/ @whizbang-lib/maintainers')
    $owned = Get-OwnedFile -CodeOwners $owners -Owner $script:Team -Files @('.github/ISSUE_TEMPLATE/bug.yml', '.github/workflows/ci.yml')
    $owned | Should -Be @('.github/workflows/ci.yml')
  }

  It 'treats a pattern with no owner as unowning what it matches' {
    $owners = ConvertFrom-CodeOwners -Lines @('/.github/ @whizbang-lib/build-admins', '/.github/README.md')
    Get-OwnedFile -CodeOwners $owners -Owner $script:Team -Files @('.github/README.md') | Should -BeNullOrEmpty
  }

  It 'returns each owned file once' {
    Get-OwnedFile -CodeOwners $script:Owners -Owner $script:Team -Files @('global.json', 'global.json') | Should -Be @('global.json')
  }
}

Describe 'ConvertFrom-CodeOwners' {
  It 'reads patterns and owners, skipping comments and blank lines' {
    $script:Owners.Count | Should -Be 3
    $script:Owners[0].Pattern | Should -Be '/.github/'
    $script:Owners[0].Owners | Should -Be @('@whizbang-lib/build-admins')
  }

  It 'drops a trailing comment' {
    (ConvertFrom-CodeOwners -Lines @('/global.json  @whizbang-lib/build-admins  # the SDK')).Owners | Should -Be @('@whizbang-lib/build-admins')
  }

  It 'reads the repository''s own CODEOWNERS, every pattern of which this gate can match' {
    $entries = ConvertFrom-CodeOwners -Lines (Get-Content (Join-Path $Root '.github' 'CODEOWNERS'))
    $entries.Count | Should -BeGreaterThan 0
    foreach ($e in $entries) { { Test-CodeOwnerMatch -Pattern $e.Pattern -Path 'x' } | Should -Not -Throw }
    Get-OwnedFile -CodeOwners $entries -Owner $script:Team -Files @('.github/workflows/ci.yml', 'scripts/Run-Tests.ps1', 'Directory.Packages.props', 'src/Whizbang.Core/Thing.cs') |
      Should -Be @('.github/workflows/ci.yml', 'scripts/Run-Tests.ps1', 'Directory.Packages.props')
  }
}

Describe 'Set-PipelineOwnerAuthority' {
  It 'fills in the author''s and each reviewer''s role, asking once per login' {
    $script:asked = [System.Collections.Generic.List[string]]::new()
    $pr = New-TestPullRequest -AuthorRole '' -Reviews @(
      (New-TestReview -Author 'boss' -AuthorRole ''), (New-TestReview -Author 'boss' -AuthorRole '' -State 'COMMENTED'),
      (New-TestReview -Author 'kim' -AuthorRole ''))
    $roles = @{ dana = 'write'; boss = 'admin'; kim = 'maintain' }
    Set-PipelineOwnerAuthority -PullRequest $pr -RoleOf { param([string]$Login) $script:asked.Add($Login); $roles[$Login] }

    $pr.AuthorRole | Should -Be 'write'
    @($pr.Reviews.AuthorRole) | Should -Be @('admin', 'admin', 'maintain')
    @($script:asked) | Should -Be @('dana', 'boss', 'kim')
  }
}

Describe 'ConvertFrom-PipelineOwnerJson' {
  It 'reads the author, the head commit, the changed files (renamed from too) and the reviews' {
    $pull = '{"number":7,"user":{"login":"dana","type":"User"},"head":{"sha":"' + $script:Head + '"}}'
    $files = @('.github/workflows/ci.yml', 'docs/x.md', 'scripts/Old.ps1')
    $reviews = '[[{"user":{"login":"boss"},"state":"APPROVED","commit_id":"' + $script:Head + '","submitted_at":"2026-10-20T12:00:00Z"},' +
      '{"user":null,"state":"COMMENTED","commit_id":"' + $script:Head + '","submitted_at":"2026-10-20T12:30:00Z"}]]'
    $pr = ConvertFrom-PipelineOwnerJson -PullJson $pull -Files $files -ReviewsJson $reviews

    $pr.Number | Should -Be 7
    $pr.Author | Should -Be 'dana'
    $pr.AuthorIsBot | Should -BeFalse
    $pr.HeadSha | Should -Be $script:Head
    $pr.Files | Should -Be $files
    @($pr.Reviews).Count | Should -Be 1
    $pr.Reviews[0].Author | Should -Be 'boss'
    $pr.Reviews[0].State | Should -Be 'APPROVED'
    $pr.Reviews[0].CommitId | Should -Be $script:Head
  }

  It 'marks a bot author' {
    $pull = '{"number":8,"user":{"login":"dependabot[bot]","type":"Bot"},"head":{"sha":"' + $script:Head + '"}}'
    (ConvertFrom-PipelineOwnerJson -PullJson $pull -Files @() -ReviewsJson '[[]]').AuthorIsBot | Should -BeTrue
  }
}

Describe 'Get-QueuedPullRequestNumber' {
  It 'reads the pull request number from a merge queue branch, as the linked-issue gate does' {
    Get-QueuedPullRequestNumber -HeadRef 'refs/heads/gh-readonly-queue/develop/pr-1479-94055d9fc0c1aa' | Should -Be 1479
  }
}
