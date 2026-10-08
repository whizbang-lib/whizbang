#Requires -Modules Pester

# The inert list is the whole safety argument for skipping tests on a change (the change detector) and
# for keeping an earlier verdict when the base moved (ff-validated, find-tested-run). These tests fail
# if it ever matches a file the build, the tests, analysis or versioning reads.

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-InertDiff.ps1') -Base x -Head y
  $script:Patterns = Get-InertPattern
  function Test-Inert([string]$path) { return [bool]($script:Patterns | Where-Object { $_.IsMatch($path) }) }
}

Describe 'inert-paths.txt' {
  It 'never matches <path>' -ForEach @(
      # baselines, seeds, schema constants and fixtures that tests and gates read
      @{ path = 'scripts/migration-sql-lint-baseline.txt' }, @{ path = 'scripts/migration-sql-docs-baseline.txt' },
      @{ path = 'scripts/unbounded-fanout-baseline.txt' }, @{ path = 'src/Whizbang.Data.Postgres/Migrations/constants.txt' },
      @{ path = 'src/Whizbang.Data.Postgres/Migrations/001_Initial.sql' },
      @{ path = 'tests/Whizbang.Observability.Tests/Baselines/metrics.txt' },
      @{ path = 'tests/Whizbang.Documentation.Tests/Baselines/configuration-keys.txt' },
      @{ path = 'samples/ECommerce/tests/ECommerce.Integration.TestUtilities/Fixtures/seed.json' },
      @{ path = 'tests/Whizbang.Transports.HotChocolate.Tests/Fixtures/schema.graphql' },
      # build, version and analysis inputs
      @{ path = 'Directory.Build.props' }, @{ path = 'Directory.Packages.props' }, @{ path = 'global.json' },
      @{ path = 'GitVersion.yml' }, @{ path = '.editorconfig' }, @{ path = 'Whizbang.sln' },
      @{ path = 'src/Whizbang.Core/Whizbang.Core.csproj' }, @{ path = 'src/Whizbang.Core/Dispatcher.cs' },
      # a README inside a project can be packed (PackageReadmeFile); only root-level .md is inert
      @{ path = 'src/Whizbang.Core/README.md' }, @{ path = 'tests/Whizbang.Core.Tests/README.md' },
      # the pipeline and its own lists
      @{ path = '.github/workflows/ci.yml' }, @{ path = '.github/actions/find-tested-run/action.yml' },
      @{ path = '.github/scripts/Test-InertDiff.ps1' }, @{ path = '.github/inert-paths.txt' },
      @{ path = '.github/nuget-packages.txt' }) {
    Test-Inert $path | Should -BeFalse
  }

  It 'matches <path>' -ForEach @(
      @{ path = 'README.md' }, @{ path = 'docs/RELEASING.md' }, @{ path = 'plans/archive/x.md' },
      @{ path = 'ai-docs/flaky-tests.md' }, @{ path = '.claude/skills/release/SKILL.md' },
      @{ path = '.github/WORKFLOWS.md' }, @{ path = 'LICENSE' }) {
    Test-Inert $path | Should -BeTrue
  }

  It 'matches only inert kinds of file in the real tree' {
    $files = @(git -C (Join-Path -Path $PSScriptRoot -ChildPath '../../..') ls-files)
    $files.Count | Should -BeGreaterThan 1000 -Because 'the job needs a full checkout for this test to mean anything'
    # Known and checked: editor settings, the license, and the banner source, which the build never reads
    # (it reads the generated WhizbangBanner.Generated.cs under src/).
    $known = @('.vscode/extensions.json', '.vscode/launch.json', '.vscode/settings.json', '.vscode/tasks.json',
      'LICENSE', 'logo/whizbang-banner.txt')
    $unexpected = @($files | Where-Object { (Test-Inert $_) -and $_ -notmatch '\.(md|png|svg|jpe?g|gif|ico)$' -and $_ -notin $known })
    $unexpected | Should -BeNullOrEmpty -Because 'a new kind of file under an inert path must be checked by hand, then listed here'
  }
}
