#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-CiResult.ps1') -Needs '{}' -EventName x

  # A run where every job the gate looks at succeeded, with no reuse. Tests override from here.
  function Get-GreenNeed {
    $needs = @{}
    foreach ($job in @('changes', 'format', 'build', 'unit-tests', 'component-tests', 'postgres-integration', 'inmemory-integration',
        'rabbitmq-integration', 'servicebus-integration', 'azureblob-integration', 'general-integration', 'quality', 'pack')) {
      $needs[$job] = @{ result = 'success'; outputs = @{} }
    }
    $needs['changes'].outputs = @{ code = 'true' }
    foreach ($job in @('release-pr', 'ff-validated', 'queue-validated', 'verify-rebuild', 'reupload-reports')) {
      $needs[$job] = @{ result = 'skipped'; outputs = @{} }
    }
    return $needs
  }

  function Skip-Job([hashtable]$needs, [string[]]$jobs) {
    foreach ($job in $jobs) { $needs[$job].result = 'skipped' }
  }

  $script:AllSuites = @('unit-tests', 'component-tests', 'postgres-integration', 'inmemory-integration', 'rabbitmq-integration',
    'servicebus-integration', 'azureblob-integration', 'general-integration')
}

Describe 'tested here' {
  It 'passes when build, every suite and Quality are green' {
    $v = Get-CiVerdict -NeedsTable (Get-GreenNeed) -EventName pull_request
    $v.Path | Should -Be 'tested here'
    $v.Pass | Should -BeTrue
  }

  It 'fails when <suite> is skipped with nothing proving the tree was tested elsewhere' -ForEach @(
      @{ suite = 'unit-tests' }, @{ suite = 'component-tests' }, @{ suite = 'postgres-integration' }, @{ suite = 'inmemory-integration' },
      @{ suite = 'rabbitmq-integration' }, @{ suite = 'servicebus-integration' }, @{ suite = 'azureblob-integration' }, @{ suite = 'general-integration' }) {
    $n = Get-GreenNeed; Skip-Job $n @($suite)
    $v = Get-CiVerdict -NeedsTable $n -EventName pull_request
    $v.Pass | Should -BeFalse
    $v.Problems | Should -Contain "$suite is skipped, but nothing proves this tree was tested anywhere else"
  }

  It 'fails when a suite is missing from the needs (renamed in the workflow only)' {
    $n = Get-GreenNeed; $n.Remove('azureblob-integration')
    (Get-CiVerdict -NeedsTable $n -EventName push).Pass | Should -BeFalse
  }

  It 'fails when build and every suite are skipped on a code change (the #905 shape)' {
    $n = Get-GreenNeed; Skip-Job $n (@('build') + $AllSuites)
    $v = Get-CiVerdict -NeedsTable $n -EventName pull_request
    $v.Pass | Should -BeFalse
    $v.Problems.Count | Should -Be (1 + $AllSuites.Count) -Because 'the build and every suite are each reported'
  }

  It 'fails when Quality is skipped on an ordinary PR' {
    $n = Get-GreenNeed; Skip-Job $n @('quality')
    (Get-CiVerdict -NeedsTable $n -EventName pull_request -Actor someone).Pass | Should -BeFalse
  }

  It 'passes a Dependabot PR whose Quality is skipped (no Sonar secrets by design)' {
    $n = Get-GreenNeed; Skip-Job $n @('quality')
    (Get-CiVerdict -NeedsTable $n -EventName pull_request -Actor 'dependabot[bot]').Pass | Should -BeTrue
  }

  It 'fails on any failed or canceled job, even one outside the evidence' {
    $n = Get-GreenNeed; $n['pack'].result = 'cancelled'
    $v = Get-CiVerdict -NeedsTable $n -EventName push
    $v.Pass | Should -BeFalse
    $v.Problems | Should -Contain 'pack cancelled'
  }

  It 'reports a failed suite once, as a failure' {
    $n = Get-GreenNeed; $n['unit-tests'].result = 'failure'
    $v = Get-CiVerdict -NeedsTable $n -EventName pull_request
    $v.Problems | Should -Be @('unit-tests failure')
  }

  It 'treats a manual dispatch as tested here even when the change detector says docs-only' {
    $n = Get-GreenNeed; $n['changes'].outputs.code = 'false'; Skip-Job $n $AllSuites
    $v = Get-CiVerdict -NeedsTable $n -EventName workflow_dispatch
    $v.Path | Should -Be 'tested here'
    $v.Pass | Should -BeFalse
  }
}

Describe 'docs-only' {
  It 'passes a <event> with only inert paths and nothing built' -ForEach @(@{ event = 'push' }, @{ event = 'pull_request' }) {
    $n = Get-GreenNeed; $n['changes'].outputs.code = 'false'; Skip-Job $n (@('build', 'quality', 'pack') + $AllSuites)
    $v = Get-CiVerdict -NeedsTable $n -EventName $event
    $v.Path | Should -Be 'docs-only'
    $v.Pass | Should -BeTrue
  }

  It 'is not claimed when the change detector itself did not succeed' {
    $n = Get-GreenNeed; $n['changes'].result = 'skipped'; $n['changes'].outputs = @{}; Skip-Job $n (@('build') + $AllSuites)
    $v = Get-CiVerdict -NeedsTable $n -EventName push
    $v.Path | Should -Be 'tested here'
    $v.Pass | Should -BeFalse
  }

  It 'is not claimed by a merge queue run' {
    $n = Get-GreenNeed; $n['changes'].outputs.code = 'false'; Skip-Job $n $AllSuites
    (Get-CiVerdict -NeedsTable $n -EventName merge_group).Path | Should -Be 'tested here'
  }
}

Describe 'reused (queue-validated)' {
  BeforeEach {
    $script:n = Get-GreenNeed
    $n['queue-validated'] = @{ result = 'success'; outputs = @{ validated = 'true' } }
    $n['verify-rebuild'].result = 'success'
    $n['reupload-reports'].result = 'success'
    Skip-Job $n (@('quality') + $AllSuites)
  }

  It 'passes when build, verify-rebuild and reupload-reports are green' {
    $v = Get-CiVerdict -NeedsTable $n -EventName push
    $v.Path | Should -Be 'reused'
    $v.Pass | Should -BeTrue
  }

  It 'fails when <job> did not run' -ForEach @(@{ job = 'build' }, @{ job = 'verify-rebuild' }, @{ job = 'reupload-reports' }) {
    Skip-Job $n @($job)
    (Get-CiVerdict -NeedsTable $n -EventName push).Pass | Should -BeFalse
  }

  # A docs-only merge reaches develop on this path too: the queue validated its tree, and the change
  # detector found no code, so the build is skipped by design. Requiring it failed every such merge.
  It 'passes a docs-only push whose build was skipped because nothing needed building' {
    $n['changes'].outputs.code = 'false'; Skip-Job $n @('build', 'pack')
    $v = Get-CiVerdict -NeedsTable $n -EventName push
    $v.Path | Should -Be 'reused'
    $v.Pass | Should -BeTrue
  }

  It 'still fails a docs-only push when <job> did not run' -ForEach @(@{ job = 'verify-rebuild' }, @{ job = 'reupload-reports' }) {
    $n['changes'].outputs.code = 'false'; Skip-Job $n @('build', $job)
    (Get-CiVerdict -NeedsTable $n -EventName push).Pass | Should -BeFalse
  }
}

Describe 'fast-forward (ff-validated)' {
  It 'passes a queue run whose tree the PR run already covered' {
    $n = Get-GreenNeed
    $n['ff-validated'] = @{ result = 'success'; outputs = @{ skip = 'true' } }
    Skip-Job $n (@('format', 'build', 'quality') + $AllSuites)
    $v = Get-CiVerdict -NeedsTable $n -EventName merge_group
    $v.Path | Should -Be 'fast-forward'
    $v.Pass | Should -BeTrue
  }

  It 'runs the full rules when ff-validated said skip=false' {
    $n = Get-GreenNeed
    $n['ff-validated'] = @{ result = 'success'; outputs = @{ skip = 'false' } }
    Skip-Job $n $AllSuites
    (Get-CiVerdict -NeedsTable $n -EventName merge_group).Pass | Should -BeFalse
  }
}

Describe 'yielded (release-pr)' {
  BeforeEach {
    $script:n = Get-GreenNeed
    $n['release-pr'] = @{ result = 'success'; outputs = @{ skip = 'true'; 'coverage-run-id' = '123' } }
    Skip-Job $n (@('format', 'build', 'pack') + $AllSuites)
  }

  It 'passes when the run it yielded to ended its gate green and Quality is green here' {
    $v = Get-CiVerdict -NeedsTable $n -EventName pull_request -CoveringGate success
    $v.Path | Should -Be 'yielded'
    $v.Pass | Should -BeTrue
  }

  It 'fails when the yielded-to run''s gate ended <gate>' -ForEach @(
      @{ gate = 'failure' }, @{ gate = 'cancelled' }, @{ gate = 'skipped' }, @{ gate = 'absent' },
      @{ gate = 'still running after 90 minutes' }, @{ gate = '' }) {
    $v = Get-CiVerdict -NeedsTable $n -EventName pull_request -CoveringGate $gate
    $v.Pass | Should -BeFalse
    $v.Problems[0] | Should -BeLike 'the suites ran in run 123, whose gate ended*'
  }

  It 'fails when Quality is skipped, because Quality never yields' {
    Skip-Job $n @('quality')
    (Get-CiVerdict -NeedsTable $n -EventName push -CoveringGate success).Pass | Should -BeFalse
  }
}

Describe 'release-branch push (#1208)' {
  BeforeEach {
    $script:ReleaseRef = 'refs/heads/release/v0.2613.0'
    # The shape that shipped no packages: an empty fix merged into the release branch, so the push run
    # reused the tested results (queue-validated) and, finding no code, built and packed nothing.
    $script:n = Get-GreenNeed
    $n['queue-validated'] = @{ result = 'success'; outputs = @{ validated = 'true' } }
    $n['verify-rebuild'].result = 'success'
    $n['reupload-reports'].result = 'success'
    $n['changes'].outputs.code = 'false'
    Skip-Job $n (@('build', 'pack', 'quality') + $AllSuites)
  }

  It 'fails a reused run that built and packed nothing' {
    $v = Get-CiVerdict -NeedsTable $n -EventName push -Ref $ReleaseRef
    $v.Path | Should -Be 'reused'
    $v.Pass | Should -BeFalse
    $v.Problems | Should -Contain 'build is skipped, but a release-branch run is what the stable publish promotes and the release PR takes coverage from'
    $v.Problems | Should -Contain 'pack is skipped, but a release-branch run is what the stable publish promotes and the release PR takes coverage from'
  }

  It 'passes a reused run that built and packed' {
    $n['changes'].outputs.code = 'true'; $n['build'].result = 'success'; $n['pack'].result = 'success'
    (Get-CiVerdict -NeedsTable $n -EventName push -Ref $ReleaseRef).Pass | Should -BeTrue
  }

  It 'fails a docs-only verdict on a release branch' {
    $n['queue-validated'] = @{ result = 'skipped'; outputs = @{} }
    $v = Get-CiVerdict -NeedsTable $n -EventName push -Ref $ReleaseRef
    $v.Path | Should -Be 'docs-only'
    $v.Pass | Should -BeFalse
  }

  It 'reports a skipped build once on the tested-here path' {
    $n['queue-validated'] = @{ result = 'skipped'; outputs = @{} }; $n['changes'].outputs.code = 'true'
    $v = Get-CiVerdict -NeedsTable $n -EventName push -Ref $ReleaseRef
    @($v.Problems | Where-Object { $_ -like 'build is *' }).Count | Should -Be 1
  }

  It 'still passes a docs-only push to <ref>' -ForEach @(@{ ref = 'refs/heads/develop' }, @{ ref = 'refs/heads/main' }, @{ ref = '' }) {
    $n['queue-validated'] = @{ result = 'skipped'; outputs = @{} }
    (Get-CiVerdict -NeedsTable $n -EventName push -Ref $ref).Pass | Should -BeTrue
  }

  It 'does not apply to a release PR, which yields its build to the push run' {
    $p = Get-GreenNeed
    $p['release-pr'] = @{ result = 'success'; outputs = @{ skip = 'true'; 'coverage-run-id' = '123' } }
    Skip-Job $p (@('format', 'build', 'pack') + $AllSuites)
    (Get-CiVerdict -NeedsTable $p -EventName pull_request -Ref 'refs/pull/1205/merge' -CoveringGate success).Pass | Should -BeTrue
  }
}
