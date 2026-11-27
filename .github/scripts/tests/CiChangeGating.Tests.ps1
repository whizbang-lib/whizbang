# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# Which jobs of ci.yml run in the merge queue, read from the workflow itself (#2602).
#
# Plan · Detect changes answers one question for every job that builds or tests: does this change touch code? It used
# to skip its filter on a merge_group event, leaving `code` empty there, and every consumer had to remember to add
# `|| github.event_name == 'merge_group'` to its own condition. Build · Compile net10.0 did not, so it never ran in the
# merge queue at all, although its comment says it does: a combination of pull requests that breaks only the net10.0
# build was first seen on a release branch. Now the filter answers on every event (a queue run has no base to compare,
# so it answers code), and the jobs read the one answer.
#
# A fast-forward queue run (Plan · Reuse the PR's results) reuses the pull request run's verdict for the same tree, so
# every job gated on that answer also stands down on a fast-forward, as Compile, Analyzers and Quality do. Pack packs
# this run's build, so it runs only after that build succeeded: on a fast-forward Compile is skipped and Pack with it.
#
# Owner: the comments on the changes, ff-validated, compile-net10 and pack jobs in .github/workflows/ci.yml.

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:CiLines = Get-Content -LiteralPath (Join-Path $Root '.github' 'workflows' 'ci.yml')

  # The lines of one job: from its key at two spaces under `jobs:` to the next job's key.
  function script:Get-JobLines([string[]]$Lines, [string]$Job) {
    $start = [array]::IndexOf($Lines, "  ${Job}:")
    if ($start -lt 0) { throw "ci.yml has no job '$Job'." }
    $end = $start + 1
    while ($end -lt $Lines.Count -and $Lines[$end] -notmatch '^  [A-Za-z0-9_-]+:\s*$' -and $Lines[$end] -notmatch '^\S') { $end++ }
    return $Lines[$start..($end - 1)]
  }

  # A job's needs (flow list) and its if (inline or a folded block), joined into one line.
  function script:Get-JobGate([string[]]$JobLines) {
    $needs = @()
    $condition = ''
    for ($i = 0; $i -lt $JobLines.Count; $i++) {
      if ($JobLines[$i] -match '^    needs:\s*\[(.*)\]') {
        $needs = @($Matches[1] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
      }
      if ($JobLines[$i] -match '^    if:\s*(.*)$') {
        $condition = $Matches[1]
        if ($condition -match '^[>|][-+]?\s*$') {
          $block = @()
          while ($i + 1 -lt $JobLines.Count -and $JobLines[$i + 1] -match '^      ') { $block += $JobLines[$i + 1].Trim(); $i++ }
          $condition = $block -join ' '
        }
      }
    }
    return [pscustomobject]@{ Needs = $needs; If = $condition }
  }

  # Every job of ci.yml, by name.
  function script:Get-CiJobs {
    $jobs = [ordered]@{}
    foreach ($line in $CiLines) {
      if ($line -match '^  ([A-Za-z0-9_-]+):\s*$') { $jobs[$Matches[1]] = Get-JobGate (Get-JobLines $CiLines $Matches[1]) }
    }
    return $jobs
  }

  # The Detect changed paths step of the changes job: its own if, and its script.
  function script:Get-ChangeFilterStep {
    $lines = Get-JobLines $CiLines 'changes'
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^      - name: Detect changed paths') { $start = $i; break } }
    if ($start -lt 0) { throw 'The changes job has no "Detect changed paths" step.' }
    $condition = ''
    $script = @()
    for ($i = $start + 1; $i -lt $lines.Count -and $lines[$i] -notmatch '^      - '; $i++) {
      if ($lines[$i] -match '^        if:\s*(.*)$') { $condition = $Matches[1] }
      if ($lines[$i] -match '^        run: \|') {
        while ($i + 1 -lt $lines.Count -and ($lines[$i + 1] -match '^          ' -or $lines[$i + 1] -match '^\s*$')) {
          $script += ($lines[$i + 1] -replace '^          ', ''); $i++
        }
      }
    }
    return [pscustomobject]@{ If = $condition; Script = ($script -join "`n") }
  }
}

Describe 'Plan · Detect changes answers on every event (#2602)' {
  It 'runs its filter in the merge queue too' {
    (Get-ChangeFilterStep).If | Should -Not -Match 'merge_group' -Because 'a skipped filter leaves code empty, and a job that reads it alone then never runs in the queue'
  }

  It 'answers code for a merge_group event, which has no base to compare against' {
    $step = Get-ChangeFilterStep
    $step.Script | Should -Not -BeNullOrEmpty
    $scriptFile = Join-Path $TestDrive 'filter.sh'
    $outputFile = Join-Path $TestDrive 'output.txt'
    Set-Content -LiteralPath $scriptFile -Value $step.Script
    New-Item -ItemType File -Path $outputFile -Force | Out-Null
    $env:EVENT = 'merge_group'; $env:PR_BASE = ''; $env:PR_HEAD = ''; $env:BEFORE = ''
    $env:GITHUB_OUTPUT = $outputFile; $env:GITHUB_REF = 'refs/heads/gh-readonly-queue/develop/pr-1-abc'
    try {
      bash $scriptFile | Out-Null
      $LASTEXITCODE | Should -Be 0
    } finally {
      Remove-Item Env:EVENT, Env:PR_BASE, Env:PR_HEAD, Env:BEFORE, Env:GITHUB_OUTPUT, Env:GITHUB_REF -ErrorAction SilentlyContinue
    }
    Get-Content -LiteralPath $outputFile | Should -Be @('code=true')
  }
}

Describe 'Every job gated on the change filter honors a fast-forward (#2602)' {
  BeforeAll {
    $script:Jobs = Get-CiJobs
    $script:CodeGated = @($Jobs.Keys | Where-Object { $Jobs[$_].If -match 'needs\.changes\.outputs\.code' })
  }

  It 'finds the code-gated jobs, so the rule below is not vacuous' {
    $CodeGated | Should -Contain 'build'
    $CodeGated | Should -Contain 'analyzers'
    $CodeGated | Should -Contain 'compile-net10'
  }

  It 'stands each one down when the queue reuses the pull request run for the same tree' {
    $missing = foreach ($job in $CodeGated) {
      if ($Jobs[$job].Needs -notcontains 'ff-validated' -or $Jobs[$job].If -notmatch "needs\.ff-validated\.outputs\.skip != 'true'") { $job }
    }
    $missing | Should -BeNullOrEmpty -Because 'the pull request run already did this work for the tree a fast-forward merges; add ff-validated to its needs and its if'
  }
}

Describe 'Build · Pack packs only a build this run made (#2602)' {
  It 'runs only after Compile succeeded, so a fast-forward that skips Compile skips Pack' {
    $pack = (Get-CiJobs)['pack']
    $pack.Needs | Should -Contain 'build'
    $pack.If | Should -Match "needs\.build\.result == 'success'"
  }

  It 'reads the same skip as Compile, so the two cannot drift apart' {
    $build = (Get-CiJobs)['build']
    $build.If | Should -Match "needs\.ff-validated\.outputs\.skip != 'true'"
  }
}
