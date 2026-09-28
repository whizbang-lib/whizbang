#Requires -Modules Pester

# The release promotes exactly the packages its suites ran against, so the lookup must fail closed, and
# must not fail a publish just because one search answer was empty (#926). No test waits: the sleep and
# the GitHub API are injected.

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Find-TestedPackages.ps1') -Sha x -Version 0.0.0 -Repo o/r

  $script:Sha = 'abc123'
  $script:Branch = 'release/v0.2602.0'

  function New-Run([long]$id = 42, [string]$status = 'completed', [string]$conclusion = 'success', [string]$branch = $script:Branch) {
    [pscustomobject]@{ id = $id; status = $status; conclusion = $conclusion; head_branch = $branch; head_sha = $script:Sha
      html_url = "https://example.test/runs/$id" }
  }
  function New-Runs([object[]]$runs) { [pscustomobject]@{ workflow_runs = @($runs) } }
  function New-Artifacts([bool[]]$expired) {
    [pscustomobject]@{ artifacts = @($expired | ForEach-Object { [pscustomobject]@{ name = 'nuget-packages-42'; expired = $_ } }) }
  }

  # A fake API. Each route is a queue of answers, one per call; the last answer repeats. $null = a failed call.
  function New-FakeApi([hashtable]$routes) {
    $state = @{ Calls = [System.Collections.Generic.List[string]]::new(); Routes = $routes; Seen = @{} }
    $api = {
      param($path)
      $state.Calls.Add($path)
      $key = if ($path -like '*head_sha=*') { 'sha' } elseif ($path -like '*runs?branch=*') { 'branch' } else { 'artifacts' }
      $answers = @($state.Routes[$key])
      $i = [int]$state.Seen[$key]; $state.Seen[$key] = $i + 1
      if ($answers.Count -eq 0) { return $null }
      return $answers[[Math]::Min($i, $answers.Count - 1)]
    }.GetNewClosure()
    return [pscustomobject]@{ Api = $api; State = $state }
  }

  function Invoke-Find($fake, [int[]]$delays = @(0, 0, 0, 0)) {
    $script:Slept = [System.Collections.Generic.List[int]]::new()
    $slept = $script:Slept
    Find-TestedPackage -Sha $script:Sha -Version 0.2602.0 -Repo o/r -Delays $delays -GhApi $fake.Api `
      -Sleep { param($s) $slept.Add($s) }.GetNewClosure() -ThisRunId 7
  }

  $script:Empty = New-Runs @()
}

Describe 'found' {
  It 'promotes a green run with a live artifact on the first attempt' {
    $fake = New-FakeApi @{ sha = @(,(New-Runs @(New-Run))); artifacts = @(,(New-Artifacts @($false))) }
    $r = Invoke-Find $fake
    $r.Outcome | Should -Be 'found'
    $r.RunId | Should -Be '42'
    $r.ArtifactName | Should -Be 'nuget-packages-42'
    $r.Via | Should -Be 'head_sha'
    $r.Attempts | Should -Be 1
    $script:Slept.Count | Should -Be 0
  }

  It 'retries when the first lookup is empty and succeeds when a later one finds the run (#926)' {
    $fake = New-FakeApi @{ sha = @($Empty, $Empty, (New-Runs @(New-Run))); branch = @(,$Empty); artifacts = @(,(New-Artifacts @($false))) }
    $r = Invoke-Find $fake -delays @(10, 20, 30, 60)
    $r.Outcome | Should -Be 'found'
    $r.Attempts | Should -Be 3
    @($script:Slept) | Should -Be @(10, 20)
  }

  It 'treats a failed API call like an empty answer and retries' {
    $fake = New-FakeApi @{ sha = @($null, (New-Runs @(New-Run))); branch = @(,$null); artifacts = @(,(New-Artifacts @($false))) }
    (Invoke-Find $fake).Outcome | Should -Be 'found'
  }

  It 'uses the branch fallback when the head_sha query misses the run' {
    $other = New-Run -id 41
    $other.head_sha = 'someothercommit'
    $fake = New-FakeApi @{ sha = @(,$Empty); branch = @(,(New-Runs @($other, (New-Run)))); artifacts = @(,(New-Artifacts @($false))) }
    $r = Invoke-Find $fake
    $r.Outcome | Should -Be 'found'
    $r.Via | Should -Be 'branch'
    $r.RunId | Should -Be '42'
    $r.Attempts | Should -Be 1
    $fake.State.Calls | Should -Contain 'repos/o/r/actions/workflows/ci.yml/runs?branch=release%2Fv0.2602.0&event=push&per_page=50'
  }

  It 'ignores a head_sha hit on a non-release branch' {
    $fake = New-FakeApi @{ sha = @(,(New-Runs @(New-Run -branch develop))); branch = @(,$Empty) }
    (Invoke-Find $fake).Outcome | Should -Be 'not-found'
  }

  It 'waits out a run that is still in progress' {
    $fake = New-FakeApi @{ sha = @((New-Runs @(New-Run -status in_progress -conclusion '')), (New-Runs @(New-Run))); artifacts = @(,(New-Artifacts @($false))) }
    $r = Invoke-Find $fake
    $r.Outcome | Should -Be 'found'
    $r.Attempts | Should -Be 2
  }
}

Describe 'not found' {
  It 'fails after every attempt with the not-found recovery' {
    $fake = New-FakeApi @{ sha = @(,$Empty); branch = @(,$Empty) }
    $r = Invoke-Find $fake -delays @(10, 20, 30, 60)
    $r.Outcome | Should -Be 'not-found'
    $r.RunId | Should -Be ''
    $r.ArtifactName | Should -Be ''
    @($script:Slept) | Should -Be @(10, 20, 30, 60)
    $r.Message | Should -BeLike '*No CI push run for abc123 on release/v0.2602.0 was found after 5 attempts over 120s*'
    $r.Message | Should -BeLike '*gh run list --workflow ci.yml --branch release/v0.2602.0 --event push*'
    $r.Message | Should -BeLike '*gh run rerun 7 --failed*'
    $r.Message | Should -BeLike '*do not publish*'
    $r.Message | Should -Not -BeLike '*Re-run the release-branch CI*'
  }
}

Describe 'not green' {
  It 'fails at once, without retrying, when the run completed red' {
    $fake = New-FakeApi @{ sha = @(,(New-Runs @(New-Run -conclusion failure))) }
    $r = Invoke-Find $fake
    $r.Outcome | Should -Be 'not-green'
    $r.RunId | Should -Be '42'
    $r.ArtifactName | Should -Be ''
    $r.Attempts | Should -Be 1
    $script:Slept.Count | Should -Be 0
    $r.Message | Should -BeLike "*run 42 for abc123 ended 'failure', not success*"
    $r.Message | Should -BeLike '*gh run rerun 42 --failed*'
    $fake.State.Calls | Should -Not -Contain 'repos/o/r/actions/runs/42/artifacts?name=nuget-packages-42'
  }

  It 'fails as not green when the run is still running after every attempt' {
    $fake = New-FakeApi @{ sha = @(,(New-Runs @(New-Run -status in_progress -conclusion ''))) }
    $r = Invoke-Find $fake
    $r.Outcome | Should -Be 'not-green'
    $r.Attempts | Should -Be 5
    $r.Message | Should -BeLike "*is still 'in_progress'*"
  }

  It 'judges the newest run, even when an older one for the same commit was green' {
    $fake = New-FakeApi @{ sha = @(,(New-Runs @((New-Run -id 43 -conclusion cancelled), (New-Run -id 42)))) }
    $r = Invoke-Find $fake
    $r.Outcome | Should -Be 'not-green'
    $r.RunId | Should -Be '43'
  }
}

Describe 'artifact missing' {
  It 'fails at once when the artifact has expired, with the rebuild recovery' {
    $fake = New-FakeApi @{ sha = @(,(New-Runs @(New-Run))); artifacts = @(,(New-Artifacts @($true))) }
    $r = Invoke-Find $fake
    $r.Outcome | Should -Be 'artifact-missing'
    $r.ArtifactName | Should -Be ''
    $r.Attempts | Should -Be 1
    $r.Message | Should -BeLike '*nuget-packages-42 on green run 42) have expired*'
    $r.Message | Should -BeLike '*gh run rerun 42)*'
  }

  It 'retries an artifact that is not listed yet, then fails as missing' {
    $fake = New-FakeApi @{ sha = @(,(New-Runs @(New-Run))); artifacts = @(,[pscustomobject]@{ artifacts = @() }) }
    $r = Invoke-Find $fake
    $r.Outcome | Should -Be 'artifact-missing'
    $r.Attempts | Should -Be 5
    $r.Message | Should -BeLike '*has no nuget-packages-42 artifact after 5 attempts*'
  }

  It 'finds an artifact that appears on a later attempt' {
    $fake = New-FakeApi @{ sha = @(,(New-Runs @(New-Run))); artifacts = @([pscustomobject]@{ artifacts = @() }, (New-Artifacts @($false))) }
    (Invoke-Find $fake).Outcome | Should -Be 'found'
  }
}
