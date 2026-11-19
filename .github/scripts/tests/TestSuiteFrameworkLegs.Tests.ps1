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
  It 'has Build · Compile name the frameworks the tests target' {
    $build = Get-Content -LiteralPath (Join-Path $Workflows 'reusable-build.yml') -Raw
    $build | Should -Match 'value: \$\{\{ jobs\.build\.outputs\.test-frameworks \}\}'
    $build | Should -Match 'test-frameworks: \$\{\{ steps\.frameworks\.outputs\.test-frameworks \}\}'
    $build | Should -Match '-getProperty:TargetFrameworks'
  }

  It 'passes those frameworks to every suite CI runs' {
    $ci = Get-Content -LiteralPath (Join-Path $Workflows 'ci.yml') -Raw
    $calls = [regex]::Matches($ci, '(?m)^    uses: \./\.github/workflows/(reusable-test-[a-z]+\.yml)\r?\n    with:\r?\n((?:      .*\r?\n)+)')
    $calls.Count | Should -BeGreaterThan 0
    foreach ($call in $calls) {
      $call.Groups[2].Value | Should -Match '(?m)^      frameworks: \$\{\{ needs\.build\.outputs\.test-frameworks \}\}' -Because "$($call.Groups[1].Value) must run one leg per framework"
    }
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
