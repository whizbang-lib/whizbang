# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# A test suite that downloads the build and collects coverage must point the build's hidden
# .msCoverageExtensionSourceRootsMapping_* files at its own checkout before its tests run
# (Update-CoverageSourceRoots.ps1). The mapping names the BUILD job's checkout, so when the build and the
# suite land on different runner kinds (a GitHub-hosted build, Blacksmith suites) the collector finds no
# sources, skips every assembly and the suite fails "No coverage report measured any library assembly"
# while every test passes (#1948). Only the unit and component suites did it; the six integration suites
# did not. The list of suites is derived from the files, so a new reusable-test-*.yml is checked as well.

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:WorkflowDir = Join-Path $Root '.github' 'workflows'

  # The steps of a workflow in order: each one's name, if, uses and run text, split at the
  # "      - " step markers of its jobs.
  function Get-WorkflowStep([string]$Path) {
    $steps = [System.Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($line in Get-Content -LiteralPath $Path) {
      if ($line -match '^      - (.*)$') {
        if ($current) { $steps.Add($current) }
        $current = [pscustomobject]@{ Index = $steps.Count; Name = ''; If = ''; Uses = ''; Text = [System.Collections.Generic.List[string]]::new() }
        $line = "        $($Matches[1])"
      }
      elseif ($line -match '^\s{0,5}\S') {
        if ($current) { $steps.Add($current); $current = $null }
        continue
      }
      if (-not $current) { continue }
      $current.Text.Add($line)
      if ($line -match '^        name:\s*(.+?)\s*$') { $current.Name = $Matches[1] }
      if ($line -match '^        if:\s*(.+?)\s*$') { $current.If = $Matches[1] }
      if ($line -match '^        uses:\s*(\S+)') { $current.Uses = $Matches[1] }
    }
    if ($current) { $steps.Add($current) }
    , $steps
  }

  # A suite that downloads the build and collects coverage from it: the steps that matter, or nothing.
  function Get-CoverageSuite([string]$Path) {
    $steps = Get-WorkflowStep $Path
    $download = $steps | Where-Object Uses -like 'actions/download-artifact@*' | Select-Object -First 1
    $firstRun = $steps | Where-Object { ($_.Text -join "`n") -match 'Run-Tests\.ps1[^\n]*-ModuleCoverage' } | Select-Object -First 1
    if (-not $download -or -not $firstRun) { return }
    [pscustomobject]@{
      Workflow = Split-Path $Path -Leaf
      Download = $download
      FirstRun = $firstRun
      Rewrite  = $steps | Where-Object { ($_.Text -join "`n") -match 'Update-CoverageSourceRoots\.ps1' } | Select-Object -First 1
    }
  }

  # Why a suite's rewrite is missing or misplaced, or nothing when it is right: present, after the build
  # download, before the first test run, and running whenever the download runs (no narrower condition).
  function Get-RewriteProblem($Suite) {
    if (-not $Suite.Rewrite) { return "$($Suite.Workflow): no step runs Update-CoverageSourceRoots.ps1" }
    if ($Suite.Rewrite.Index -lt $Suite.Download.Index) { return "$($Suite.Workflow): the rewrite runs before the build download" }
    if ($Suite.Rewrite.Index -gt $Suite.FirstRun.Index) { return "$($Suite.Workflow): the rewrite runs after the tests" }
    if ($Suite.Rewrite.If -and $Suite.Rewrite.If -ne $Suite.Download.If) {
      return "$($Suite.Workflow): the rewrite's condition ($($Suite.Rewrite.If)) differs from the download's ($($Suite.Download.If))"
    }
  }

  $script:Suites = @(Get-ChildItem -LiteralPath $WorkflowDir -Filter 'reusable-test-*.yml' | ForEach-Object { Get-CoverageSuite $_.FullName })
}

Describe 'Coverage source-root rewrite in the test suites' {
  It 'finds the suites that download the build and collect coverage, including the known eight' {
    $Suites.Count | Should -BeGreaterThan 0
    $names = $Suites.Workflow
    foreach ($known in 'unit', 'component', 'azureblob', 'inmemory', 'integration', 'postgres', 'rabbitmq', 'servicebus') {
      $names | Should -Contain "reusable-test-$known.yml"
    }
  }

  It 'points coverage at the checkout after the download and before the tests, in every suite' {
    $problems = @($Suites | ForEach-Object { Get-RewriteProblem $_ } | Where-Object { $_ })
    $problems | Should -BeNullOrEmpty -Because ($problems -join '; ')
  }
}

Describe 'Get-RewriteProblem' {
  BeforeAll {
    function New-Suite([string]$Text) {
      $path = Join-Path $TestDrive 'reusable-test-x.yml'
      Set-Content -LiteralPath $path -Value $Text
      Get-CoverageSuite $path
    }
    $script:Head = @'
jobs:
  t:
    steps:
      - name: Checkout
        uses: actions/checkout@abc
'@
    $script:Download = @'
      - name: Download Build Artifacts
        if: inputs.artifact-name != ''
        uses: actions/download-artifact@abc
        with:
          name: x
'@
    $script:RunTests = @'
      - name: Run
        run: pwsh scripts/Run-Tests.ps1 -Mode Integration -NoBuild -ModuleCoverage
'@
    function New-RewriteStep([string]$If) {
      $ifLine = if ($If) { "`n        if: $If" } else { '' }
      "      - name: Point coverage at this checkout$ifLine`n        shell: pwsh`n        run: ./.github/scripts/Update-CoverageSourceRoots.ps1"
    }
  }

  It 'accepts a rewrite between the download and the tests, under the download condition' {
    Get-RewriteProblem (New-Suite ($Head, $Download, (New-RewriteStep "inputs.artifact-name != ''"), $RunTests -join "`n")) | Should -BeNullOrEmpty
  }

  It 'accepts an unconditional rewrite' {
    Get-RewriteProblem (New-Suite ($Head, $Download, (New-RewriteStep ''), $RunTests -join "`n")) | Should -BeNullOrEmpty
  }

  It 'names a suite with no rewrite' {
    Get-RewriteProblem (New-Suite ($Head, $Download, $RunTests -join "`n")) | Should -Match 'no step runs Update-CoverageSourceRoots'
  }

  It 'names a rewrite before the download' {
    Get-RewriteProblem (New-Suite ($Head, (New-RewriteStep ''), $Download, $RunTests -join "`n")) | Should -Match 'before the build download'
  }

  It 'names a rewrite after the tests' {
    Get-RewriteProblem (New-Suite ($Head, $Download, $RunTests, (New-RewriteStep '') -join "`n")) | Should -Match 'after the tests'
  }

  It 'names a rewrite under a narrower condition than the download' {
    Get-RewriteProblem (New-Suite ($Head, $Download, (New-RewriteStep "runner.os == 'Windows'"), $RunTests -join "`n")) | Should -Match 'condition'
  }

  It 'ignores a workflow that collects no coverage' {
    New-Suite ($Head, $Download -join "`n") | Should -BeNullOrEmpty
  }
}
