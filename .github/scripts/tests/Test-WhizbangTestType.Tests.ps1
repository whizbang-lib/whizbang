#Requires -Modules Pester

# The type guard (#1264). A <WhizbangTestType> that one tool does not know is a project whose tests
# silently stop running; #1196 found 214 integration tests that no suite had ever selected. These tests
# build a small repository in which every tool knows every type, break one tool at a time, and check
# that the guard names the type and the tool. The last block runs the guard over this repository.

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '../Test-WhizbangTestType.ps1')
  $script:Root = Join-Path -Path $PSScriptRoot -ChildPath '../../..' -Resolve

  function Write-RepoFile([string]$Repo, [string]$Path, [string]$Content) {
    $full = Join-Path -Path $Repo -ChildPath $Path
    New-Item -ItemType Directory -Path (Split-Path -Path $full -Parent) -Force | Out-Null
    Set-Content -Path $full -Value $Content
  }

  # A repository in which every tool knows Unit, Component, Integration and Soak.
  function New-FakeRepo {
    $repo = Join-Path -Path $TestDrive -ChildPath ([guid]::NewGuid().ToString('N'))
    foreach ($p in @(
        @{ Dir = 'tests/A.Tests'; Type = 'Unit' },
        @{ Dir = 'tests/A.Component.Tests'; Type = 'Component' },
        @{ Dir = 'tests/A.Integration.Tests'; Type = 'Integration' },
        @{ Dir = 'tests/A.Soak.Tests'; Type = 'Soak' })) {
      $name = Split-Path -Path $p.Dir -Leaf
      Write-RepoFile $repo "$($p.Dir)/$name.csproj" "<Project><PropertyGroup><IsTestProject>true</IsTestProject><WhizbangTestType>$($p.Type)</WhizbangTestType></PropertyGroup></Project>"
    }
    Write-RepoFile $repo 'src/A/A.csproj' '<Project><PropertyGroup><IsPackable>true</IsPackable></PropertyGroup></Project>'
    Write-RepoFile $repo 'scripts/Run-Tests.ps1' @'
param(
    [ValidateSet("All", "Ai", "AiUnit", "AiComponent", "AiIntegrations", "Unit", "Component", "Integration")]
    [string]$Mode = "All",
    [ValidateSet("All", "Ai", "AiUnit", "AiComponent", "AiIntegrations", "Unit", "Component", "Integration")]
    [string]$LogMode = ""
)
$WhizbangTestTypes = [ordered]@{
    Unit        = @{ Modes = @('Unit', 'AiUnit'); InAll = $true }
    Component   = @{ Modes = @('Component', 'AiComponent'); InAll = $true }
    Integration = @{ Modes = @('Integration', 'AiIntegrations'); InAll = $true }
    Soak        = @{ Modes = @(); InAll = $false; RunBy = 'scripts/Run-Soak.ps1' }
}
'@
    Write-RepoFile $repo 'Directory.Build.targets' '<Project><PropertyGroup><WhizbangKnownTestTypes>Unit;Component;Integration;Soak</WhizbangKnownTestTypes></PropertyGroup></Project>'
    Write-RepoFile $repo 'scripts/Test-SolutionTestProjects.ps1' '$KnownTestTypes = @(''Unit'', ''Component'', ''Integration'', ''Soak'')'
    Write-RepoFile $repo '.github/scripts/Get-TestSlice.ps1' @'
$script:Suites = [ordered]@{
  component   = @{ Type = 'Component';   Tag = 'Component';   Extra = @() }
  integration = @{ Type = 'Integration'; Tag = 'Integration'; Extra = @('samples/**/bin/Release') }
}
$script:UnslicedTypes = [ordered]@{
  Unit = 'runs most projects, so it keeps the full build'
  Soak = 'not run in CI'
}
'@
    Write-RepoFile $repo '.github/scripts/Test-CiResult.ps1' '$script:Suites = @(''unit-tests'', ''component-tests'', ''general-integration'')'
    Write-RepoFile $repo 'scripts/Run-PR.ps1' @'
$a = "-Mode AiUnit -Coverage"
$b = "-Mode AiComponent -Coverage"
$c = "-Mode AiIntegrations -Coverage"
'@
    Write-RepoFile $repo '.github/workflows/reusable-test-unit.yml' 'run: pwsh scripts/Run-Tests.ps1 -Mode Unit -NoBuild'
    Write-RepoFile $repo '.github/workflows/reusable-test-component.yml' 'run: pwsh scripts/Run-Tests.ps1 -Mode Component -NoBuild'
    Write-RepoFile $repo '.github/workflows/reusable-test-integration.yml' 'run: pwsh scripts/Run-Tests.ps1 -Mode Integration -Tag Integration'
    $suites = 'unit-tests, component-tests, general-integration'
    $success = "needs.unit-tests.result == 'success' && needs.component-tests.result == 'success' && needs.general-integration.result == 'success'"
    Write-RepoFile $repo '.github/workflows/ci.yml' @"
jobs:
  build:
    runs-on: ubuntu-latest
  unit-tests:
    needs: [build]
    uses: ./.github/workflows/reusable-test-unit.yml
  component-tests:
    needs: [build]
    uses: ./.github/workflows/reusable-test-component.yml
  general-integration:
    needs: [build]
    uses: ./.github/workflows/reusable-test-integration.yml
  test-results:
    needs: [$suites]
  ci-result:
    needs: [build, $suites]
  prerelease-publish:
    needs: [$suites]
    if: >-
      !failure() &&
      ($success)
  release-publish:
    needs: [$suites, build]
    if: >-
      $success
  notify-cancelled:
    needs: [build, $suites]
  publish-test-status:
    needs: [$suites]
"@
    return $repo
  }

  function Edit-RepoFile([string]$Repo, [string]$Path, [string]$Find, [string]$Replace) {
    $full = Join-Path -Path $Repo -ChildPath $Path
    $text = Get-Content -Path $full -Raw
    if (-not $text.Contains($Find)) { throw "fixture edit found no '$Find' in $Path" }
    Set-Content -Path $full -Value $text.Replace($Find, $Replace)
  }
}

Describe 'Get-WhizbangTestTypeProblem' {
  It 'finds nothing when every tool knows every declared type' {
    $repo = New-FakeRepo
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -BeNullOrEmpty
  }

  It 'names a test project that declares no type' {
    $repo = New-FakeRepo
    Write-RepoFile $repo 'tests/B.Tests/B.Tests.csproj' '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>'
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -Be @('tests/B.Tests/B.Tests.csproj: a test project (IsTestProject) that declares no <WhizbangTestType>, so no tool selects it')
  }

  It 'ignores projects under bin, obj, node_modules and dot folders' {
    $repo = New-FakeRepo
    Write-RepoFile $repo '.claude/worktrees/w/tests/Z.Tests/Z.Tests.csproj' '<Project><PropertyGroup><WhizbangTestType>Mystery</WhizbangTestType></PropertyGroup></Project>'
    Write-RepoFile $repo 'tests/A.Tests/bin/Release/x/X.Tests.csproj' '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>'
    Write-RepoFile $repo 'docs/node_modules/y/Y.Tests.csproj' '<Project><PropertyGroup><WhizbangTestType>Mystery</WhizbangTestType></PropertyGroup></Project>'
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -BeNullOrEmpty
  }

  It 'names every tool that does not know a new type' {
    $repo = New-FakeRepo
    Write-RepoFile $repo 'tests/B.Contract.Tests/B.Contract.Tests.csproj' '<Project><PropertyGroup><WhizbangTestType>Contract</WhizbangTestType></PropertyGroup></Project>'
    $problems = @(Get-WhizbangTestTypeProblem -Root $repo)
    $problems | Should -Contain "Contract: scripts/Run-Tests.ps1 does not know it (no entry in `$WhizbangTestTypes)"
    $problems | Should -Contain 'Contract: Directory.Build.targets does not know it (not in <WhizbangKnownTestTypes>)'
    $problems | Should -Contain "Contract: scripts/Test-SolutionTestProjects.ps1 does not know it (not in `$KnownTestTypes)"
    $problems | Should -Contain "Contract: .github/scripts/Get-TestSlice.ps1 does not know it (no suite slice of that Type, and not in `$UnslicedTypes)"
    $problems.Count | Should -Be 4 -Because 'with no Run-Tests entry there are no modes, so no CI or Run-PR check applies yet'
  }

  It 'names a Run-Tests mode that -Mode does not accept' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo 'scripts/Run-Tests.ps1' '"All", "Ai", "AiUnit", "AiComponent", "AiIntegrations", "Unit", "Component", "Integration")]
    [string]$Mode' '"All", "Ai", "AiUnit", "AiIntegrations", "Unit", "Component", "Integration")]
    [string]$Mode'
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -Be @("Component: scripts/Run-Tests.ps1 -Mode does not accept 'AiComponent'")
  }

  It 'names a Run-Tests mode that -LogMode does not accept' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo 'scripts/Run-Tests.ps1' '"Component", "Integration")]
    [string]$LogMode' '"Integration")]
    [string]$LogMode'
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -Be @("Component: scripts/Run-Tests.ps1 -LogMode does not accept 'Component'")
  }

  It 'requires a type that Run-Tests never runs to name what does' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo 'scripts/Run-Tests.ps1' "; RunBy = 'scripts/Run-Soak.ps1'" ''
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -Be @('Soak: scripts/Run-Tests.ps1 has no -Mode for it and names no RunBy (what runs it)')
  }

  It 'names a type that Directory.Build.targets does not know' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo 'Directory.Build.targets' 'Unit;Component;' 'Unit;'
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -Be @('Component: Directory.Build.targets does not know it (not in <WhizbangKnownTestTypes>)')
  }

  It 'treats a Directory.Build.targets with no list as knowing no type' {
    $repo = New-FakeRepo
    Write-RepoFile $repo 'Directory.Build.targets' '<Project />'
    @(Get-WhizbangTestTypeProblem -Root $repo).Count | Should -Be 4
  }

  It 'names a type that Test-SolutionTestProjects does not know' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo 'scripts/Test-SolutionTestProjects.ps1' "'Soak'" "'Benchmark'"
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -Be @("Soak: scripts/Test-SolutionTestProjects.ps1 does not know it (not in `$KnownTestTypes)")
  }

  It 'names a type that the slice script does not know' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo '.github/scripts/Get-TestSlice.ps1' "Type = 'Component'" "Type = 'Integration'"
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -Be @("Component: .github/scripts/Get-TestSlice.ps1 does not know it (no suite slice of that Type, and not in `$UnslicedTypes)")
  }

  It 'names a type that no CI suite runs' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo '.github/workflows/reusable-test-component.yml' '-Mode Component' '-Mode Unit'
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -Be @('Component: no CI suite runs it (no reusable-test-*.yml called by a job Test-CiResult.ps1 counts runs Run-Tests.ps1 -Mode Component or AiComponent)')
  }

  It 'does not count a suite workflow that no gated job calls' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo '.github/workflows/ci.yml' 'reusable-test-component.yml' 'reusable-test-unit.yml'
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -Be @('Component: no CI suite runs it (no reusable-test-*.yml called by a job Test-CiResult.ps1 counts runs Run-Tests.ps1 -Mode Component or AiComponent)')
  }

  It 'names a type that Run-PR does not run' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo 'scripts/Run-PR.ps1' '-Mode AiComponent' '-Mode AiUnit'
    @(Get-WhizbangTestTypeProblem -Root $repo) | Should -Be @('Component: scripts/Run-PR.ps1 does not run it (no Run-Tests.ps1 -Mode Component or AiComponent)')
  }

  It 'reports an unreadable Run-Tests table as knowing no type' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo 'scripts/Run-Tests.ps1' '$WhizbangTestTypes =' '$SomethingElse ='
    $problems = @(Get-WhizbangTestTypeProblem -Root $repo)
    $problems | Should -Contain "Unit: scripts/Run-Tests.ps1 does not know it (no entry in `$WhizbangTestTypes)"
    $problems.Count | Should -Be 4
  }
}

Describe 'Get-CiSuiteListProblem' {
  It 'finds nothing when every suite is in every list' {
    @(Get-CiSuiteListProblem -Root (New-FakeRepo)) | Should -BeNullOrEmpty
  }

  It 'names a counted suite that ci.yml does not define' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo '.github/scripts/Test-CiResult.ps1' "'general-integration')" "'general-integration', 'ghost-tests')"
    @(Get-CiSuiteListProblem -Root $repo) | Should -Be @('ghost-tests: .github/scripts/Test-CiResult.ps1 counts it, but ci.yml has no such job')
  }

  It 'names a suite job that the gate does not count' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo '.github/scripts/Test-CiResult.ps1' "'component-tests', " ''
    $problems = @(Get-CiSuiteListProblem -Root $repo)
    $problems | Should -Contain 'component-tests: runs a reusable-test-*.yml suite, but .github/scripts/Test-CiResult.ps1 does not count it'
  }

  It 'names each job whose needs miss a suite' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo '.github/workflows/ci.yml' "  publish-test-status:`n    needs: [unit-tests, component-tests, general-integration]" "  publish-test-status:`n    needs: [unit-tests, general-integration]"
    @(Get-CiSuiteListProblem -Root $repo) | Should -Be @('component-tests: missing from the needs of ci.yml job publish-test-status')
  }

  It 'names a consumer job that ci.yml does not define' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo '.github/workflows/ci.yml' '  notify-cancelled:' '  notify-canceled:'
    @(Get-CiSuiteListProblem -Root $repo) | Should -Be @('notify-cancelled: ci.yml has no such job, so no suite can be checked against its needs')
  }

  It 'names a publish job that does not require a suite to succeed' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo '.github/workflows/ci.yml' "    if: >-`n      needs.unit-tests.result == 'success' && needs.component-tests.result == 'success' && " "    if: >-`n      needs.unit-tests.result == 'success' && "
    @(Get-CiSuiteListProblem -Root $repo) | Should -Be @("component-tests: ci.yml job release-publish does not require needs.component-tests.result == 'success'")
  }

  It 'reads a single-job needs written without brackets' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo '.github/workflows/ci.yml' "  test-results:`n    needs: [unit-tests, component-tests, general-integration]" "  test-results:`n    needs: unit-tests"
    $problems = @(Get-CiSuiteListProblem -Root $repo)
    $problems | Should -Be @('component-tests: missing from the needs of ci.yml job test-results', 'general-integration: missing from the needs of ci.yml job test-results')
  }

  It 'reads a job with no needs as needing nothing' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo '.github/workflows/ci.yml' "  test-results:`n    needs: [unit-tests, component-tests, general-integration]" "  test-results:`n    runs-on: ubuntu-latest"
    @(Get-CiSuiteListProblem -Root $repo).Count | Should -Be 3
  }
}

Describe 'ConvertFrom-ConstantAst' {
  It 'reads strings, numbers, booleans, null, arrays and nested hashtables' {
    $ast = [System.Management.Automation.Language.Parser]::ParseInput('$x = [ordered]@{ A = "s"; B = 2; C = $true; D = $false; E = $null; F = @(''p'', ''q''); G = ''r'', ''t''; H = @{ I = @() }; J = "$y" }', [ref]$null, [ref]$null)
    $assignment = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)
    $value = ConvertFrom-ConstantAst -Ast $assignment.Right
    $value.A | Should -Be 's'
    $value.B | Should -Be 2
    $value.C | Should -BeTrue
    $value.D | Should -BeFalse
    $value.E | Should -BeNullOrEmpty
    $value.F | Should -Be @('p', 'q')
    $value.G | Should -Be @('r', 't')
    @($value.H.I).Count | Should -Be 0
    $value.J | Should -Be '$y'
  }

  It 'refuses an expression that is not a constant' {
    $ast = [System.Management.Automation.Language.Parser]::ParseInput('$x = Get-Date', [ref]$null, [ref]$null)
    $assignment = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)
    { ConvertFrom-ConstantAst -Ast $assignment.Right } | Should -Throw '*not a constant*'
  }
}

Describe 'the script' {
  It 'exits 0 on a repository where every tool knows every type' {
    $repo = New-FakeRepo
    & pwsh -NoProfile -File (Join-Path -Path $PSScriptRoot -ChildPath '../Test-WhizbangTestType.ps1') -Root $repo | Out-Null
    $LASTEXITCODE | Should -Be 0
  }

  It 'exits 1 and names the problem otherwise' {
    $repo = New-FakeRepo
    Edit-RepoFile $repo 'Directory.Build.targets' 'Unit;Component;' 'Unit;'
    $output = & pwsh -NoProfile -File (Join-Path -Path $PSScriptRoot -ChildPath '../Test-WhizbangTestType.ps1') -Root $repo
    $LASTEXITCODE | Should -Be 1
    ($output -join "`n") | Should -Match 'Component: Directory\.Build\.targets does not know it'
  }
}

Describe 'this repository' {
  It 'declares test types, so the guard has something to check' {
    $types = @(Get-DeclaredTestProject -Root $Root | ForEach-Object { $_.Type } | Sort-Object -Unique)
    $types | Should -Contain 'Unit'
    $types | Should -Contain 'Component'
    $types | Should -Contain 'Integration'
  }

  It 'has every declared test type known to every tool that reads it' {
    @(Get-WhizbangTestTypeProblem -Root $Root) | Should -BeNullOrEmpty
  }

  It 'has every gated suite in every list that must name it' {
    @(Get-CiSuiteListProblem -Root $Root) | Should -BeNullOrEmpty
  }
}
