# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The quality job's "Wait for test suites to publish coverage" step reads the Compile job's conclusion from the
# run's jobs list with build-conclusion.jq. It must read ONLY the Compile job ("Build · Compile / <child>", or the
# bare parent name when that reusable job is skipped). A prefix match also caught "Build · Compile net10.0", which
# is skipped on merge-queue runs: when that job came first in the list the step read the build as 'skipped' and
# slept until the job's 180-minute timeout with every suite green (#2385).

BeforeAll {
  $script:Filter = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'build-conclusion.jq'

  function Get-BuildConclusion {
    param([object[]]$Jobs)
    $json = @{ jobs = $Jobs } | ConvertTo-Json -Depth 5 -Compress
    $out = $json | jq -r -f $script:Filter
    if ($LASTEXITCODE -ne 0) { throw "jq exited $LASTEXITCODE" }
    $out
  }
}

# The step runs in bash on Linux runners, which carry jq. The pipeline tests also run on the Windows pool,
# which does not, so the filter's own cases skip there; the wiring check below runs everywhere.
BeforeDiscovery {
  $script:NoJq = -not (Get-Command -Name jq -ErrorAction SilentlyContinue)
}

Describe 'build-conclusion.jq' -Skip:$NoJq {
  It 'reads the Compile job when a skipped Compile net10.0 job is listed first (#2385)' {
    $jobs = @(
      @{ name = 'Build · Compile net10.0'; conclusion = 'skipped' },
      @{ name = 'Build · Compile / Compile and hash'; conclusion = 'success' }
    )
    Get-BuildConclusion $jobs | Should -Be 'success'
  }

  It 'reads the Compile job when it is listed first' {
    $jobs = @(
      @{ name = 'Build · Compile / Compile and hash'; conclusion = 'failure' },
      @{ name = 'Build · Compile net10.0'; conclusion = 'success' }
    )
    Get-BuildConclusion $jobs | Should -Be 'failure'
  }

  It 'reads a skipped Compile job by its bare parent name' {
    $jobs = @(
      @{ name = 'Build · Compile net10.0'; conclusion = 'success' },
      @{ name = 'Build · Compile'; conclusion = 'skipped' }
    )
    Get-BuildConclusion $jobs | Should -Be 'skipped'
  }

  It 'reports pending while the Compile job has no conclusion' {
    $jobs = @(
      @{ name = 'Build · Compile net10.0'; conclusion = 'skipped' },
      @{ name = 'Build · Compile / Compile and hash'; conclusion = $null }
    )
    Get-BuildConclusion $jobs | Should -Be 'pending'
  }

  It 'reports pending when no Compile job is listed yet' {
    $jobs = @(@{ name = 'Build · Compile net10.0'; conclusion = 'skipped' })
    Get-BuildConclusion $jobs | Should -Be 'pending'
  }
}

Describe 'The coverage wait step' {
  It 'uses build-conclusion.jq' {
    $workflow = Get-Content -Raw (Join-Path $PSScriptRoot '..' '..' 'workflows' 'reusable-quality.yml')
    $workflow | Should -Match 'jq -r -f \.github/scripts/build-conclusion\.jq'
    $workflow | Should -Not -Match 'startswith\("Build · Compile"\)'
  }
}
