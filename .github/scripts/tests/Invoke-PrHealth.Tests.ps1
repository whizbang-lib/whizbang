# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  . (Join-Path $PSScriptRoot '..' '..' '..' 'scripts' 'Invoke-PrHealth.ps1')
}

Describe 'Test-CoverageGateInQueue' {
  # A pull request into develop runs only the unit and component suites (#2070): the coverage gate runs in the
  # merge queue, and the PR run's partial coverage would list every line only an integration suite covers.
  It 'says the coverage gate of a pull request into develop runs in the merge queue' {
    Test-CoverageGateInQueue -BaseRefName 'develop' | Should -BeTrue
  }

  It 'keeps the PR run as the coverage source for a pull request into <base>' -ForEach @(@{ base = 'main' }, @{ base = 'release/v1.2.3' }, @{ base = '' }) {
    Test-CoverageGateInQueue -BaseRefName $base | Should -BeFalse
  }
}

Describe 'the script' {
  It 'refuses to run without a pull request number' {
    $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot '..' '..' '..' 'scripts' 'Invoke-PrHealth.ps1') 2>&1
    $LASTEXITCODE | Should -Not -Be 0
    "$out" | Should -BeLike '*-PullRequest*'
  }
}
