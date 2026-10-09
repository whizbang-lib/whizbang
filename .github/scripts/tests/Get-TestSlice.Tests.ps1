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

  It 'selects every project of a type when no tag is given' {
    $repo = Build-FakeRepo -Projects @(
      @{ Dir = 'tests/A.Component.Tests'; Type = 'Component'; Tags = 'Component' },
      @{ Dir = 'tests/B.Component.Tests'; Type = 'Component'; Tags = 'Anything' },
      @{ Dir = 'tests/C.Tests'; Type = 'Unit'; Tags = 'Component' },
      @{ Dir = 'tests/D.Component.Tests'; Type = 'Component'; Tags = 'Component' }    # not in the solution
    ) -InSolution @('tests/A.Component.Tests', 'tests/B.Component.Tests', 'tests/C.Tests')
    @(Get-TestSliceProject -Root $repo -Tag '' -Type 'Component') | Should -Be @('tests/A.Component.Tests', 'tests/B.Component.Tests')
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
    $sliceTags = @($script:Suites.Values | Where-Object { $_.Type -eq 'Integration' } | ForEach-Object { $_.Tag } | Sort-Object -Unique)
    $sliceTags | Should -Be $tags -Because 'a suite without a slice would download nothing it can run'
    foreach ($suite in $script:Suites.Keys) {
      @(Get-TestSliceProject -Root $Root -Tag $script:Suites[$suite].Tag -Type $script:Suites[$suite].Type).Count | Should -BeGreaterThan 0 -Because "the $suite slice must hold the projects its suite runs"
    }
  }

  It 'slices each untagged suite by the type its workflow runs, with no tag' {
    # The component suite runs every Component project (-Mode Component, no -Tag), so its slice must be
    # every Component project; a -Tag in the workflow would make the suite run less than its slice holds.
    $workflow = Get-Content (Join-Path -Path $Root -ChildPath '.github/workflows/reusable-test-component.yml') -Raw
    $workflow | Should -Match 'Run-Tests\.ps1 -Mode Component '
    $workflow | Should -Not -Match '-Tag '
    $script:Suites.component.Type | Should -Be 'Component'
    $script:Suites.component.Tag | Should -BeNullOrEmpty
  }

  It 'leaves no integration project without a suite that runs it' {
    # #1196: five projects carried only a tag no suite selected, so 214 tests never ran in CI and
    # nothing said so. Every integration project in the solution must carry a suite's tag.
    $suiteTags = @($script:Suites.Values | ForEach-Object { $_.Tag })
    $solution = Get-Content (Join-Path -Path $Root -ChildPath 'Whizbang.slnx') -Raw
    $orphans = foreach ($relative in [regex]::Matches($solution, '<Project Path="([^"]+\.csproj)"') | ForEach-Object { $_.Groups[1].Value }) {
      $content = Get-Content (Join-Path -Path $Root -ChildPath $relative) -Raw -ErrorAction SilentlyContinue
      if ($content -notmatch '<WhizbangTestType>Integration</WhizbangTestType>') { continue }
      $tags = if ($content -match '<WhizbangTestTags>([^<]+)</WhizbangTestTags>') { $Matches[1] -split ';' | ForEach-Object { $_.Trim() } } else { @() }
      if (-not ($tags | Where-Object { $suiteTags -contains $_ })) { $relative }
    }
    @($orphans) | Should -BeNullOrEmpty -Because 'an integration project no suite selects never runs in CI'
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
