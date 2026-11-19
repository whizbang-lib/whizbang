# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# GitHub skips a job when any job upstream of it, however far back, was skipped, unless the job's `if` uses a status
# function (always(), failure(), cancelled()). A job that carries one exists because something above it can be skipped,
# so every job below it inherits the same skip and needs one too. v0.4457.0 published from the tested packages, which
# skips repack-tested-build, and the attach job and the merge-back skipped with it: the release was left a draft and
# develop never got the release's changes back (#2443). Notify · Failure skipped exactly when its gate failed, because
# the implicit success() is false then. This reads every workflow and fails on any such job.
#
# Owner: docs/RELEASING.md, "Gotchas & invariants" (the implicit success() bullet).

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path

  # The jobs of a workflow, each with its needs and its if, read from the text: jobs sit at two spaces under `jobs:`,
  # their keys at four. `needs` is a flow list, a scalar or a block list; `if` is inline or a folded/literal block.
  function script:Get-WorkflowJobs([string[]]$Lines) {
    $jobs = [ordered]@{}
    $inJobs = $false
    $job = $null
    for ($i = 0; $i -lt $Lines.Count; $i++) {
      $line = $Lines[$i]
      if ($line -match '^jobs:\s*$') { $inJobs = $true; continue }
      if (-not $inJobs) { continue }
      if ($line -match '^\S') { break }
      if ($line -match '^  ([A-Za-z0-9_-]+):\s*(#.*)?$') {
        $job = [pscustomobject]@{ Name = $Matches[1]; Needs = @(); If = '' }
        $jobs[$job.Name] = $job
        continue
      }
      if ($null -eq $job) { continue }
      if ($line -match '^    needs:\s*(.*?)\s*(#.*)?$') {
        $value = $Matches[1]
        if ($value -match '^\[(.*)\]$') {
          $job.Needs = @($Matches[1] -split ',' | ForEach-Object { $_.Trim().Trim("'", '"') } | Where-Object { $_ })
        } elseif ($value) {
          $job.Needs = @($value.Trim("'", '"'))
        } else {
          $list = @()
          while ($i + 1 -lt $Lines.Count -and $Lines[$i + 1] -match '^      - ([A-Za-z0-9_-]+)') { $list += $Matches[1]; $i++ }
          $job.Needs = $list
        }
        continue
      }
      if ($line -match '^    if:\s*(.*)$') {
        $value = $Matches[1]
        if ($value -match '^[>|][-+]?\s*$') {
          $block = @()
          while ($i + 1 -lt $Lines.Count -and ($Lines[$i + 1] -match '^      ' -or $Lines[$i + 1] -match '^\s*$')) { $block += $Lines[$i + 1].Trim(); $i++ }
          $value = $block -join ' '
        }
        $job.If = $value
      }
    }
    return $jobs
  }

  function script:Test-HasStatusFunction([string]$Condition) {
    return $Condition -match '\b(always|failure|cancelled)\(\)'
  }

  # Each job with no status function that has a job with one anywhere above it.
  function script:Find-UnguardedBelowGuarded($Jobs) {
    foreach ($job in $Jobs.Values) {
      if (Test-HasStatusFunction $job.If) { continue }
      $seen = [System.Collections.Generic.HashSet[string]]::new()
      $queue = [System.Collections.Generic.Queue[string]]::new()
      foreach ($need in $job.Needs) { $queue.Enqueue($need) }
      $guarded = @()
      while ($queue.Count -gt 0) {
        $name = $queue.Dequeue()
        if (-not $seen.Add($name) -or -not $Jobs.Contains($name)) { continue }
        if (Test-HasStatusFunction $Jobs[$name].If) { $guarded += $name }
        foreach ($need in $Jobs[$name].Needs) { $queue.Enqueue($need) }
      }
      if ($guarded) { "$($job.Name) (below $($guarded -join ', '))" }
    }
  }
}

Describe 'A job below a job that survives a skipped need survives it too (#2443)' {
  # Positive control: the reader and the rule catch the v0.4457.0 shape, in each form a workflow writes needs and if.
  It 'reports a job left without a status function below one that has it' {
    $workflow = @(
      'on: push',
      'jobs:',
      '  build-and-pack:',
      '    if: false',
      '  repack:',
      '    runs-on: ubuntu-latest',
      '  publish:',
      '    needs: [build-and-pack, repack]',
      '    if: >-',
      '      always() &&',
      "      (needs.build-and-pack.result == 'success' || needs.repack.result == 'success')",
      '  attach:',
      '    needs:',
      '      - publish',
      '  sync:',
      '    needs: publish',
      "    if: `${{ !cancelled() && needs.publish.result == 'success' }}",
      '  notify:',
      '    needs: [attach]'
    )
    $found = @(Find-UnguardedBelowGuarded (Get-WorkflowJobs $workflow))
    $found | Should -Be @('attach (below publish)', 'notify (below publish)')
  }

  It 'finds jobs, needs and status functions in the real workflows, so the check below is not vacuous' {
    $release = Get-WorkflowJobs (Get-Content -LiteralPath (Join-Path $Root '.github' 'workflows' 'release.yml'))
    $release['publish'].Needs | Should -Contain 'repack-tested-build'
    Test-HasStatusFunction $release['publish'].If | Should -BeTrue
    $release['sync-develop'].Needs | Should -Contain 'publish'
    $release['upload-release-assets'].Needs | Should -Contain 'publish'
  }

  It 'has no such job in any workflow' {
    $problems = foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Root '.github' 'workflows') -Filter '*.yml') {
      foreach ($finding in Find-UnguardedBelowGuarded (Get-WorkflowJobs (Get-Content -LiteralPath $file.FullName))) {
        "$($file.Name): $finding"
      }
    }
    $problems | Should -BeNullOrEmpty -Because 'a job below one with a status function is skipped whenever that one''s skipped need is; give it its own (!cancelled() and the result it depends on)'
  }
}
