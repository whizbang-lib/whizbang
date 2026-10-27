# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The release app's ID and key are environment secrets (#1466): a job reads them only by declaring the
# release-app environment (develop, main and release/v* only) or release-cut (the same, plus a build
# admin's approval), so a workflow pushed on any other branch cannot mint a release token. A reusable
# workflow cannot be handed an environment secret by its caller, so no job passes them along; it declares
# them as optional workflow_call secrets instead, which is what lets the environment's values reach its jobs.

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path

  # Each job of a workflow: its name and its lines, split at the two-space job keys under jobs:.
  function Get-WorkflowJob([string]$Path) {
    $lines = Get-Content -LiteralPath $Path
    $inJobs = $false; $current = $null
    foreach ($line in $lines) {
      if ($line -match '^jobs:\s*$') { $inJobs = $true; continue }
      if (-not $inJobs) { continue }
      if ($line -match '^\S') { break }
      if ($line -match '^  ([A-Za-z0-9_-]+):\s*$') {
        if ($current) { $current }
        $current = [pscustomobject]@{ Workflow = (Split-Path $Path -Leaf); Name = $Matches[1]; Lines = [System.Collections.Generic.List[string]]::new() }
        continue
      }
      if ($current) { $current.Lines.Add($line) }
    }
    if ($current) { $current }
  }

  $script:Jobs = @(Get-ChildItem (Join-Path $Root '.github' 'workflows') -Filter '*.yml' | ForEach-Object { Get-WorkflowJob $_.FullName })
  $script:SecretJobs = @($Jobs | Where-Object { ($_.Lines -join "`n") -match 'secrets\.RELEASE_APP_' })
}

Describe 'Release app secrets' {
  It 'are read by at least the jobs that publish and cut releases' {
    $SecretJobs.Count | Should -BeGreaterOrEqual 7
  }

  It 'are read only by a job in the release-app or release-cut environment' {
    $offenders = @($SecretJobs | Where-Object {
        $env = @($_.Lines | Where-Object { $_ -match '^    environment:\s*(\S+)\s*$' } | ForEach-Object { $Matches[1] })
        -not ($env | Where-Object { $_ -in @('release-app', 'release-cut') })
      } | ForEach-Object { "$($_.Workflow): $($_.Name)" })
    $offenders | Should -BeNullOrEmpty
  }

  It 'are never passed to a reusable workflow, which could not receive an environment secret' {
    $passing = @($Jobs | Where-Object { ($_.Lines -join "`n") -match 'RELEASE_APP_(ID|PRIVATE_KEY):\s*\$\{\{\s*secrets\.' } |
        ForEach-Object { "$($_.Workflow): $($_.Name)" })
    $passing | Should -BeNullOrEmpty
  }

  It 'are declared, if at all, only as optional workflow_call secrets, so no caller has to pass them' {
    $required = @(Get-ChildItem (Join-Path $Root '.github' 'workflows') -Filter '*.yml' |
        Where-Object { (Get-Content -Raw $_.FullName) -match '(?m)^      RELEASE_APP_(ID|PRIVATE_KEY):\s*\n        required:\s*true' } |
        ForEach-Object { $_.Name })
    $required | Should -BeNullOrEmpty
  }

  It 'are declared as workflow_call secrets by every reusable workflow that reads them, or its jobs never see them' {
    # A reusable workflow's job sees only the secrets its workflow_call trigger declares (or its caller
    # passes); declaring the name is what lets the release-app environment's value through (#1477).
    $undeclared = @(Get-ChildItem (Join-Path $Root '.github' 'workflows') -Filter '*.yml' |
        Where-Object {
          $raw = Get-Content -Raw $_.FullName
          $raw -match '(?m)^  workflow_call:' -and $raw -match 'secrets\.RELEASE_APP_' -and
            -not ($raw -match '(?m)^      RELEASE_APP_ID:\s*$' -and $raw -match '(?m)^      RELEASE_APP_PRIVATE_KEY:\s*$')
        } | ForEach-Object { $_.Name })
    $undeclared | Should -BeNullOrEmpty
  }

  It 'reach a job inside a reusable workflow only through callers that pass secrets: inherit (#1796)' {
    # GitHub hands an environment secret to a reusable workflow's job only when the CALLER passes it,
    # by name or with secrets: inherit, even though the value comes from the called job's environment.
    # A caller that passes nothing leaves the secret an empty string, with no error until the step that
    # reads it fails. Declaring it under workflow_call does not change that: #1477 tried, and three
    # stable releases still could not mint the merge-back's token.
    $offenders = @(foreach ($reusable in @($SecretJobs | ForEach-Object Workflow | Sort-Object -Unique)) {
        $raw = Get-Content -Raw (Join-Path $Root '.github' 'workflows' $reusable)
        if ($raw -notmatch '(?m)^  workflow_call:') { continue }
        $Jobs | Where-Object { ($_.Lines -join "`n") -match "(?m)^    uses:\s*\./\.github/workflows/$([regex]::Escape($reusable))\s*$" } |
          Where-Object { -not (($_.Lines -join "`n") -match '(?m)^    secrets:\s*inherit\s*$') } |
          ForEach-Object { "$($_.Workflow): $($_.Name) calls $reusable without secrets: inherit" }
      })
    $offenders | Should -BeNullOrEmpty
  }

  It 'are read by each merge-back job itself, which declares release-app and is not a reusable-workflow call (#1796)' {
    # The merge-back is shaped like the publish jobs that mint the token on every release: an ordinary
    # job of the workflow the event started, declaring the environment, with the shared steps in the
    # sync-develop composite action.
    $mergeBacks = @($Jobs | Where-Object { "$($_.Workflow): $($_.Name)" -in @('release.yml: sync-develop', 'ci.yml: release-sync-develop') })
    $mergeBacks.Count | Should -Be 2
    foreach ($job in $mergeBacks) {
      $text = $job.Lines -join "`n"
      $text | Should -Not -Match '(?m)^    uses:' -Because "$($job.Workflow): $($job.Name) must run its own steps"
      $text | Should -Match '(?m)^    environment:\s*release-app\s*$' -Because "$($job.Workflow): $($job.Name) reads the release app's secrets"
      $text | Should -Match 'client-id:\s*\$\{\{\s*secrets\.RELEASE_APP_ID\s*\}\}' -Because "$($job.Workflow): $($job.Name) mints the token"
      $text | Should -Match '(?m)^\s+(- )?uses:\s*\./\.github/actions/sync-develop\s*$' -Because "$($job.Workflow): $($job.Name) runs the one merge-back"
    }
  }

  It 'cut a release only after a build admin approves (release-cut)' {
    $cut = @($Jobs | Where-Object { $_.Workflow -eq 'start-release.yml' -and ($_.Lines -join "`n") -match 'secrets\.RELEASE_APP_' })
    $cut.Count | Should -Be 1
    ($cut[0].Lines -join "`n") | Should -Match '(?m)^    environment:\s*release-cut\s*$'
  }
}
