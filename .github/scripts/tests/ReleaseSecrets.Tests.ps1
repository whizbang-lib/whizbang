#Requires -Modules Pester

# The release app's ID and key are environment secrets (#1466): a job reads them only by declaring the
# release-app environment (develop, main and release/v* only) or release-cut (the same, plus a build
# admin's approval), so a workflow pushed on any other branch cannot mint a release token. A reusable
# workflow cannot be handed an environment secret by its caller, so no job passes them along.

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

  It 'are not declared as workflow_call secrets' {
    $declared = @(Get-ChildItem (Join-Path $Root '.github' 'workflows') -Filter '*.yml' |
        Where-Object { (Get-Content -Raw $_.FullName) -match '(?ms)^on:.*?workflow_call:.*?secrets:.*?\n\s+RELEASE_APP_(ID|PRIVATE_KEY):\s*\n.*?^jobs:' } |
        ForEach-Object { $_.Name })
    $declared | Should -BeNullOrEmpty
  }

  It 'cut a release only after a build admin approves (release-cut)' {
    $cut = @($Jobs | Where-Object { $_.Workflow -eq 'start-release.yml' -and ($_.Lines -join "`n") -match 'secrets\.RELEASE_APP_' })
    $cut.Count | Should -Be 1
    ($cut[0].Lines -join "`n") | Should -Match '(?m)^    environment:\s*release-cut\s*$'
  }
}
