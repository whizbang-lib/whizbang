#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '../Get-TestSlice.ps1')
  $script:Root = Join-Path -Path $PSScriptRoot -ChildPath '../../..' -Resolve

  function Build-FakeRepo([hashtable[]]$Projects, [string[]]$InSolution) {
    $repo = Join-Path -Path $TestDrive -ChildPath ([guid]::NewGuid().ToString('N'))
    foreach ($p in $Projects) {
      $dir = Join-Path -Path $repo -ChildPath $p.Dir
      New-Item -ItemType Directory -Path $dir -Force | Out-Null
      $name = Split-Path -Path $p.Dir -Leaf
      Set-Content -Path (Join-Path -Path $dir -ChildPath "$name.csproj") -Value @"
<Project><PropertyGroup><WhizbangTestType>$($p.Type)</WhizbangTestType><WhizbangTestTags>$($p.Tags)</WhizbangTestTags></PropertyGroup></Project>
"@
    }
    $entries = $InSolution | ForEach-Object { "<Project Path=""$_/$(Split-Path -Path $_ -Leaf).csproj"" />" }
    Set-Content -Path (Join-Path -Path $repo -ChildPath 'Whizbang.slnx') -Value "<Solution>$($entries -join '')</Solution>"
    return $repo
  }
}

Describe 'Get-TestSliceProject' {
  It 'selects the solution''s integration projects carrying the tag, and nothing else' {
    $repo = Build-FakeRepo -Projects @(
      @{ Dir = 'tests/A.Integration.Tests'; Type = 'Integration'; Tags = 'Postgres;Docker' },
      @{ Dir = 'tests/B.Tests'; Type = 'Unit'; Tags = 'Postgres' },               # unit: the unit suite runs it
      @{ Dir = 'tests/C.Soak.Tests'; Type = 'Soak'; Tags = 'Soak;Postgres' },     # not integration-mode
      @{ Dir = 'tests/D.Integration.Tests'; Type = 'Integration'; Tags = 'RabbitMQ' },
      @{ Dir = 'tests/E.Integration.Tests'; Type = 'Integration'; Tags = 'Postgres' }  # not in the solution
    ) -InSolution @('tests/A.Integration.Tests', 'tests/B.Tests', 'tests/C.Soak.Tests', 'tests/D.Integration.Tests')
    @(Get-TestSliceProject -Root $repo -Tag 'Postgres') | Should -Be @('tests/A.Integration.Tests')
  }

  It 'matches a whole tag, not a prefix' {
    $repo = Build-FakeRepo -Projects @(@{ Dir = 'tests/A.Integration.Tests'; Type = 'Integration'; Tags = 'PostgresExtra' }) -InSolution @('tests/A.Integration.Tests')
    @(Get-TestSliceProject -Root $repo -Tag 'Postgres').Count | Should -Be 0
  }
}

Describe 'the suite slices in this repository' {
  It 'gives every tagged integration suite a slice with its tag, selecting at least one project' {
    $tags = @(Get-ChildItem -Path (Join-Path -Path $Root -ChildPath '.github/workflows') -Filter 'reusable-test-*.yml' |
      ForEach-Object { [regex]::Matches((Get-Content $_.FullName -Raw), '-Mode Integration [^\n]*-Tag (\w+)') } |
      ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $tags.Count | Should -BeGreaterThan 3 -Because 'the scan must find the suites for this check to mean anything'
    $sliceTags = @($script:Suites.Values | ForEach-Object { $_.Tag } | Sort-Object -Unique)
    $sliceTags | Should -Be $tags -Because 'a suite without a slice would download nothing it can run'
    foreach ($suite in $script:Suites.Keys) {
      @(Get-TestSliceProject -Root $Root -Tag $script:Suites[$suite].Tag).Count | Should -BeGreaterThan 0 -Because "the $suite slice must hold the projects its suite runs"
    }
  }

  It 'points each integration suite in ci.yml at its own slice' {
    $ci = Get-Content (Join-Path -Path $Root -ChildPath '.github/workflows/ci.yml') -Raw
    foreach ($suite in $script:Suites.Keys) {
      $ci | Should -Match "uses: \./\.github/workflows/reusable-test-[a-z]+\.yml\s+with:\s+artifact-name: build-$suite-\$\{\{ github\.run_id \}\}" -Because "the $suite suite must download its slice"
    }
  }

  It 'uploads every slice from the build job' {
    $build = Get-Content (Join-Path -Path $Root -ChildPath '.github/workflows/reusable-build.yml') -Raw
    foreach ($suite in $script:Suites.Keys) {
      $build | Should -Match "name: build-$suite-\$\{\{ github\.run_id \}\}" -Because "a slice no step uploads cannot be downloaded"
    }
  }
}
