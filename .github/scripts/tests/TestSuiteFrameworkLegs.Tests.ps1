# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# A release build tests net10.0 and net11.0. Both used to run in one job per suite, which ran the integration modules
# one at a time (their two copies share one broker and one database server) and nearly doubled every suite. Each
# framework now runs as its own leg (#2427): Build · Compile names the frameworks (test-frameworks), every suite
# runs one leg per framework with Run-Tests.ps1 -Framework, and on a release build each leg's name and artifacts
# carry its framework, so the legs never collide and Quality counts each one. A development build has one leg, with
# the name and artifact names it always had. The suites are derived from the files, so a new reusable-test-*.yml is
# checked as well.

BeforeDiscovery {
  $root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:Suites = @(Get-ChildItem -Path (Join-Path $root '.github' 'workflows') -Filter 'reusable-test-*.yml' |
      Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'Run-Tests\.ps1' } |
      ForEach-Object { @{ Workflow = $_.Name; Path = $_.FullName } })
}

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:Workflows = Join-Path $Root '.github' 'workflows'
}

Describe 'Each test suite runs one leg per target framework (#2427)' {
  It 'has Build · Compile name the frameworks the tests and the sample tests target' {
    $build = Get-Content -LiteralPath (Join-Path $Workflows 'reusable-build.yml') -Raw
    foreach ($output in 'test-frameworks', 'sample-test-frameworks') {
      $build | Should -Match "value: \`$\{\{ jobs\.build\.outputs\.$output \}\}"
      $build | Should -Match "$output\: \`$\{\{ steps\.frameworks\.outputs\.$output \}\}"
    }
    $build | Should -Match "Name = 'test-frameworks'; Props = 'Directory\.Build\.props'"
    $build | Should -Match "Name = 'sample-test-frameworks'; Props = 'samples/ECommerce/Directory\.Build\.props'"
    $build | Should -Match '-getProperty:TargetFramework -getProperty:TargetFrameworks'
  }

  # sample-test-frameworks reads the samples' props, so it holds only while no sample test project names its own.
  It 'leaves every sample test project''s frameworks to samples/ECommerce/Directory.Build.props' {
    $samples = @(Get-ChildItem -LiteralPath (Join-Path $Root 'samples') -Recurse -Filter '*.csproj' |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '<WhizbangTestType>' })
    $samples.Count | Should -BeGreaterThan 0
    foreach ($project in $samples) {
      $project.FullName | Should -BeLike "*$([IO.Path]::DirectorySeparatorChar)ECommerce$([IO.Path]::DirectorySeparatorChar)*"
      Get-Content -LiteralPath $project.FullName -Raw | Should -Not -Match '<TargetFrameworks?>' -Because "$($project.Name) must target the samples' frameworks"
    }
  }

  # A leg's frameworks follow what it selects: only sample tests (which target the samples' frameworks alone, also in a
  # release build), the samples' list; anything else, the build's. A sample-only leg given the build's list ran a
  # net10.0 leg with nothing to test; one given the samples' list for library tests would drop net10.0 silently. The
  # selection is worked out as Run-Tests.ps1 makes it: the suite's -Mode and -Tag, then the leg's project filter.
  It 'passes every suite leg the frameworks of the projects it selects' {
    $projects = @(Get-ChildItem -LiteralPath (Join-Path $Root 'tests'), (Join-Path $Root 'samples') -Recurse -Filter '*.csproj' |
        ForEach-Object {
          $text = Get-Content -LiteralPath $_.FullName -Raw
          if ($text -match '<WhizbangTestType>\s*(\w+)\s*</WhizbangTestType>') {
            $type = $Matches[1]
            $tags = if ($text -match '<WhizbangTestTags>([^<]+)</WhizbangTestTags>') { @($Matches[1] -split ';' | ForEach-Object Trim) } else { @() }
            [pscustomobject]@{ Name = $_.BaseName; Type = $type; Tags = $tags; Sample = $_.FullName.StartsWith((Join-Path $Root 'samples')) }
          }
        })
    # An expression as one leg sees it: a matrix.shard choice, matrix.projectFilter, a quoted literal, or itself.
    function Resolve-LegValue([string]$Value, [hashtable]$Leg) {
      $Value = ($Value -replace '\s+#.*$', '').Trim()
      if ($Value -match '^\$\{\{\s*(.+?)\s*\}\}$') { $Value = $Matches[1] }
      if ($Value -match "^matrix\.shard == '([^']+)' && (.+?) \|\| (.+)$") {
        return Resolve-LegValue $(if ($Leg.shard -eq $Matches[1]) { $Matches[2] } else { $Matches[3] }) $Leg
      }
      if ($Value -eq 'matrix.projectFilter') { return $Leg.projectFilter }
      if ($Value -match "^'(.*)'$") { return $Matches[1] }
      $Value
    }
    $ci = Get-Content -LiteralPath (Join-Path $Workflows 'ci.yml') -Raw
    $jobs = [regex]::Matches($ci, '(?ms)^  ([\w-]+):\r?\n(.*?)(?=^  [\w-]+:\r?\n|\z)')
    $checked = 0
    foreach ($job in $jobs) {
      $body = $job.Groups[2].Value
      if ($body -notmatch '(?m)^    uses: \./\.github/workflows/(reusable-test-[a-z]+\.yml)') { continue }
      $workflow = Get-Content -LiteralPath (Join-Path $Workflows $Matches[1]) -Raw
      $run = [regex]::Match($workflow, '(?m)^\s+run: pwsh scripts/Run-Tests\.ps1 .*$').Value
      $mode = [regex]::Match($run, '-Mode (\w+)').Groups[1].Value
      $tag = [regex]::Match($run, '-Tag (\w+)').Groups[1].Value
      $frameworks = [regex]::Match($body, '(?m)^      frameworks: (.+?)\s*$').Groups[1].Value
      $filter = [regex]::Match($body, '(?m)^      project-filter: (.+?)\s*$').Groups[1].Value
      $legs = @([regex]::Matches($body, "(?m)^          - shard: (\S+)\r?\n            projectFilter: '([^']*)'") |
          ForEach-Object { @{ shard = $_.Groups[1].Value; projectFilter = $_.Groups[2].Value } })
      if ($legs.Count -eq 0 -and $body -match '(?m)^        shard: \[(.+)\]') { $legs = @($Matches[1] -split ',' | ForEach-Object { @{ shard = $_.Trim() } }) }
      if ($legs.Count -eq 0) { $legs = @(@{ shard = '' }) }
      foreach ($leg in $legs) {
        $projectFilter = if ($filter) { Resolve-LegValue $filter $leg } else { '' }
        $selected = @($projects | Where-Object {
            $_.Type -eq $mode -and (-not $tag -or $_.Tags -contains $tag) -and (-not $projectFilter -or $_.Name -match $projectFilter) })
        $what = "$($job.Groups[1].Value)$(if ($leg.shard) { " ($($leg.shard))" })"
        $selected.Count | Should -BeGreaterThan 0 -Because "$what must select a test project"
        $expected = if (@($selected | Where-Object { -not $_.Sample }).Count -eq 0) { 'needs.build.outputs.sample-test-frameworks' } else { 'needs.build.outputs.test-frameworks' }
        Resolve-LegValue $frameworks $leg | Should -Be $expected -Because "$what selects $(($selected.Name) -join ', ')"
        $checked++
      }
    }
    $checked | Should -BeGreaterOrEqual 8
  }

  It 'counts a leg named for its framework among the suites Quality waits for' {
    $quality = Get-Content -LiteralPath (Join-Path $Workflows 'reusable-quality.yml') -Raw
    $line = [regex]::Match($quality, "(?m)^\s+SUITE='(.+)'\s*$").Groups[1].Value
    $pattern = [regex]::Match($line, 'test\("(.+)"\)').Groups[1].Value -replace '\\\\', '\'
    $pattern | Should -Not -BeNullOrEmpty
    foreach ($name in 'Test · Unit / Run suite', 'Test · Unit / Run suite (net10.0)', 'Test · PostgreSQL (efcore-1) / Run suite (net11.0)') {
      $name | Should -Match $pattern
    }
    'Test · Unit' | Should -Not -Match $pattern
  }

  Context '<Workflow>' -ForEach $Suites {
    BeforeAll {
      $script:Text = Get-Content -LiteralPath $Path -Raw
    }

    It 'takes the frameworks as an input and runs a leg for each' {
      $Text | Should -Match '(?m)^      frameworks:\r?\n'
      $Text | Should -Match '(?m)^        framework: \$\{\{ fromJSON\(inputs\.frameworks \|\| ''\[""\]''\) \}\}'
      $Text | Should -Match '(?m)^    name: \$\{\{ strategy\.job-total > 1 && format\(''Run suite \(\{0\}\)'', matrix\.framework\) \|\| ''Run suite'' \}\}'
      $Text | Should -Match '(?m)^      FRAMEWORK_SUFFIX: \$\{\{ strategy\.job-total > 1 && format\(''-\{0\}'', matrix\.framework\) \|\| '''' \}\}'
    }

    It 'runs only its leg''s framework' {
      $runs = @([regex]::Matches($Text, '(?m)^\s+run: pwsh scripts/Run-Tests\.ps1 .*$') | ForEach-Object Value)
      $runs.Count | Should -BeGreaterThan 0
      foreach ($run in $runs) {
        $run | Should -Match ([regex]::Escape("-Framework '`${{ matrix.framework }}'")) -Because "every Run-Tests.ps1 call must pass it: $run"
      }
    }

    It 'names every artifact it uploads for its leg''s framework' {
      $names = @([regex]::Matches($Text, '(?ms)uses: actions/upload-artifact@\S+.*?\r?\n\s+with:\r?\n\s+name: ([^\r\n]+)') | ForEach-Object { $_.Groups[1].Value })
      $names.Count | Should -BeGreaterOrEqual 3 -Because 'each suite uploads its TRX, its coverage and its hang dumps'
      foreach ($name in $names) {
        $name | Should -Match ([regex]::Escape('${{ env.FRAMEWORK_SUFFIX }}')) -Because "the legs of one suite must not upload under one name: $name"
      }
    }
  }
}
