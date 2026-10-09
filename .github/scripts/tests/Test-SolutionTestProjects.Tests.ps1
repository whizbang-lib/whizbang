#Requires -Modules Pester

# scripts/Test-SolutionTestProjects.ps1 (run by the CI build job): a test project outside the solution,
# with no <WhizbangTestType>, or with a type no runner knows is a project whose tests never run.

BeforeAll {
  $script:Script = Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/Test-SolutionTestProjects.ps1' -Resolve

  function New-FakeRepo([hashtable[]]$Projects, [string[]]$InSolution) {
    $repo = Join-Path -Path $TestDrive -ChildPath ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $repo -Force | Out-Null
    foreach ($p in $Projects) {
      $dir = Join-Path -Path $repo -ChildPath $p.Dir
      New-Item -ItemType Directory -Path $dir -Force | Out-Null
      Set-Content -Path (Join-Path -Path $dir -ChildPath "$(Split-Path -Path $p.Dir -Leaf).csproj") -Value "<Project><PropertyGroup>$($p.Body)</PropertyGroup></Project>"
    }
    $entries = $InSolution | ForEach-Object { "<Project Path=""$_/$(Split-Path -Path $_ -Leaf).csproj"" />" }
    Set-Content -Path (Join-Path -Path $repo -ChildPath 'Whizbang.slnx') -Value "<Solution>$($entries -join '')</Solution>"
    # The script's two exclusions must exist outside the solution, or they are reported as stale.
    foreach ($excluded in @('tests/Whizbang.Soak.Tests', 'benchmarks/Whizbang.Benchmarks.Postgres')) {
      $dir = Join-Path -Path $repo -ChildPath $excluded
      New-Item -ItemType Directory -Path $dir -Force | Out-Null
      Set-Content -Path (Join-Path -Path $dir -ChildPath "$(Split-Path -Path $excluded -Leaf).csproj") -Value '<Project><PropertyGroup><WhizbangTestType>Soak</WhizbangTestType></PropertyGroup></Project>'
    }
    return $repo
  }

  function Invoke-Check([string]$Repo) {
    $output = & pwsh -NoProfile -File $script:Script -Root $Repo
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Text = ($output -join "`n") }
  }
}

Describe 'Test-SolutionTestProjects.ps1' {
  It 'passes when every typed test project is in the solution' {
    $repo = New-FakeRepo -Projects @(
      @{ Dir = 'tests/A.Tests'; Body = '<IsTestProject>true</IsTestProject><WhizbangTestType>Unit</WhizbangTestType>' },
      @{ Dir = 'tests/A.Component.Tests'; Body = '<WhizbangTestType>Component</WhizbangTestType>' },
      @{ Dir = 'src/A'; Body = '<IsTestProject>false</IsTestProject>' }
    ) -InSolution @('tests/A.Tests', 'tests/A.Component.Tests', 'src/A')
    $result = Invoke-Check $repo
    $result.Exit | Should -Be 0
    $result.Text | Should -Match 'Every test project is in Whizbang\.slnx'
  }

  It 'fails on a test project missing from the solution' {
    $repo = New-FakeRepo -Projects @(@{ Dir = 'tests/A.Tests'; Body = '<WhizbangTestType>Unit</WhizbangTestType>' }) -InSolution @()
    $result = Invoke-Check $repo
    $result.Exit | Should -Be 1
    $result.Text | Should -Match 'missing from Whizbang\.slnx[\s\S]*tests/A\.Tests/A\.Tests\.csproj'
  }

  It 'fails on a test project that declares no type' {
    $repo = New-FakeRepo -Projects @(@{ Dir = 'tests/A.Tests'; Body = '<IsTestProject>true</IsTestProject>' }) -InSolution @('tests/A.Tests')
    $result = Invoke-Check $repo
    $result.Exit | Should -Be 1
    $result.Text | Should -Match 'tests/A\.Tests/A\.Tests\.csproj \(declares none\)'
  }

  It 'fails on a type no runner knows' {
    $repo = New-FakeRepo -Projects @(@{ Dir = 'tests/A.Tests'; Body = '<WhizbangTestType>Contract</WhizbangTestType>' }) -InSolution @('tests/A.Tests')
    $result = Invoke-Check $repo
    $result.Exit | Should -Be 1
    $result.Text | Should -Match "tests/A\.Tests/A\.Tests\.csproj \(declares 'Contract'\)"
  }

  It 'ignores projects under build output and dot folders' {
    $repo = New-FakeRepo -Projects @(
      @{ Dir = 'tests/A.Tests/bin/Release/B.Tests'; Body = '<WhizbangTestType>Contract</WhizbangTestType>' },
      @{ Dir = '.claude/worktrees/w/tests/C.Tests'; Body = '<IsTestProject>true</IsTestProject>' }
    ) -InSolution @()
    (Invoke-Check $repo).Exit | Should -Be 0
  }

  It 'fails on an exclusion that no longer applies' {
    $repo = New-FakeRepo -Projects @() -InSolution @('tests/Whizbang.Soak.Tests')
    $result = Invoke-Check $repo
    $result.Exit | Should -Be 1
    $result.Text | Should -Match 'Exclusions that no longer apply[\s\S]*tests/Whizbang\.Soak\.Tests'
  }

  It 'passes on this repository' {
    (Invoke-Check (Join-Path -Path $PSScriptRoot -ChildPath '../../..' -Resolve)).Exit | Should -Be 0
  }
}
