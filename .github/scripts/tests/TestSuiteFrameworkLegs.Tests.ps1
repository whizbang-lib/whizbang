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
#
# Coverage is measured on one framework only, the development one (#2135): Build · Compile names it
# (coverage-framework, WhizbangDevelopmentTargetFramework in Directory.Build.props), and only that framework's leg of
# each suite collects and uploads coverage. The other legs still run every test and upload their TRX. Merging the
# net10.0 legs' reports into the net11.0 ones mixed two compilations of every library method (state machines and
# runtime async), and the whole-library gate read a line of the net11.0 build as never run (release PR #2450).

BeforeDiscovery {
  $script:NoJq = -not (Get-Command -Name jq -ErrorAction SilentlyContinue)
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

  It 'has Build · Compile name the one framework coverage is measured on, from Directory.Build.props' {
    $build = Get-Content -LiteralPath (Join-Path $Workflows 'reusable-build.yml') -Raw
    $build | Should -Match 'value: \$\{\{ jobs\.build\.outputs\.coverage-framework \}\}'
    $build | Should -Match 'coverage-framework: \$\{\{ steps\.frameworks\.outputs\.coverage-framework \}\}'
    $build | Should -Match '-getProperty:WhizbangDevelopmentTargetFramework'
    Get-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Raw |
      Should -Match '<WhizbangDevelopmentTargetFramework>net\d+\.\d+</WhizbangDevelopmentTargetFramework>'
  }

  It 'passes every suite the framework coverage is measured on' {
    $ci = Get-Content -LiteralPath (Join-Path $Workflows 'ci.yml') -Raw
    $jobs = @([regex]::Matches($ci, '(?ms)^  ([\w-]+):\r?\n(.*?)(?=^  [\w-]+:\r?\n|\z)') |
        Where-Object { $_.Groups[2].Value -match '(?m)^    uses: \./\.github/workflows/reusable-test-[a-z]+\.yml' })
    $jobs.Count | Should -BeGreaterOrEqual 8
    foreach ($job in $jobs) {
      $job.Groups[2].Value | Should -Match '(?m)^      coverage-framework: \$\{\{ needs\.build\.outputs\.coverage-framework \}\}' -Because "$($job.Groups[1].Value) must collect coverage on one framework only"
    }
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

    It 'collects and uploads coverage on the coverage framework''s leg only' {
      $Text | Should -Match '(?m)^      coverage-framework:\r?\n'
      $Text | Should -Match '(?m)^      COVERAGE_LEG: \$\{\{ inputs\.coverage-framework == '''' \|\| matrix\.framework == '''' \|\| matrix\.framework == inputs\.coverage-framework \}\}'
      $runs = @([regex]::Matches($Text, '(?m)^\s+run: pwsh scripts/Run-Tests\.ps1 .*$') | ForEach-Object Value)
      foreach ($run in $runs) {
        $run | Should -Match ([regex]::Escape("-CoverageFramework '`${{ inputs.coverage-framework }}'")) -Because "a leg on another framework must not collect coverage: $run"
      }
      foreach ($step in 'Verify coverage was collected', 'Upload Coverage') {
        $Text | Should -Match "(?m)^      - name: $step\r?\n        if: success\(\) && env\.COVERAGE_LEG == 'true'\r?\n" -Because "$step must skip a leg that collected no coverage"
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

# Quality waits for one coverage-* artifact per suite (per shard), not per leg: a release build runs two legs of most
# suites and only the coverage framework's uploads coverage. The count is read from the run's jobs list by
# expected-coverage.jq, as the build's conclusion is by build-conclusion.jq. The step runs in bash on Linux runners,
# which carry jq; the pipeline tests also run on the Windows pool, which does not, so the filter's cases skip there
# (NoJq, set in BeforeDiscovery above).
Describe 'Quality expects one coverage artifact per suite, whatever its legs (#2135)' {
  BeforeAll {
    $script:Filter = Join-Path $PSScriptRoot '..' 'expected-coverage.jq'
    function Get-ExpectedCoverage([string[]]$Names) {
      $json = @{ jobs = @($Names | ForEach-Object { @{ name = $_; conclusion = 'success' } }) } | ConvertTo-Json -Depth 5 -Compress
      $out = $json | jq -r -f $script:Filter
      if ($LASTEXITCODE -ne 0) { throw "jq exited $LASTEXITCODE" }
      [int]$out
    }
  }

  It 'reads the count with expected-coverage.jq' {
    $quality = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' '..' 'workflows' 'reusable-quality.yml') -Raw
    $quality | Should -Match 'EXPECTED=\$\(jq -r -f \.github/scripts/expected-coverage\.jq <<< "\$JOBS"\)'
  }

  It 'expects one artifact for each suite of a release build, which runs it on <Count> framework(s)' -Skip:$NoJq -ForEach @(
    @{ Count = 1; Names = @('Test · Unit / Run suite', 'Test · InMemory / Run suite', 'Test · PostgreSQL (efcore-1, EFCore\.Postgres, [Category=Shard1]) / Run suite') },
    @{ Count = 2; Names = @(
        'Test · Unit / Run suite (net10.0)', 'Test · Unit / Run suite (net11.0)', 'Test · InMemory / Run suite',
        'Test · PostgreSQL (efcore-1, EFCore\.Postgres, [Category=Shard1]) / Run suite (net10.0)',
        'Test · PostgreSQL (efcore-1, EFCore\.Postgres, [Category=Shard1]) / Run suite (net11.0)') }) {
    Get-ExpectedCoverage ($Names + @('Build · Compile / Compile and hash', 'Analyze · Quality / Sonar and coverage', 'Test · RabbitMQ')) | Should -Be 3
  }

  It 'expects nothing before a suite leg is listed' -Skip:$NoJq {
    Get-ExpectedCoverage @('Build · Compile / Compile and hash') | Should -Be 0
  }
}
