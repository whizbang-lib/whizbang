# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# Package validation can fail CI (#2605).
#
# Packing a library compares its public API with the published baseline (#1439, Directory.Build.targets). Three things
# kept that comparison from ever failing a build:
#   - the build artifact left out obj/Release/**/ref/**, so every packed library stopped on MSB4018 (the reference
#     assembly validation reads was not there);
#   - pull request and queue runs stamped 0.0.0-*, assembly version 0.0.0.0, below the baseline's, so every library
#     reported an assembly identity change (CP0003);
#   - the pack step ended in `|| true`, which hid both.
# These tests read the workflows themselves, so none of the three can come back unnoticed.
#
# Owner: the Upload Build Artifacts step of .github/workflows/reusable-build.yml, the Placeholder version step of the
# changes job in .github/workflows/ci.yml, and the pack steps of reusable-pack.yml and nuget-publish.yml.

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:Workflows = Join-Path $Root '.github' 'workflows'
  $script:CiLines = Get-Content -LiteralPath (Join-Path $Workflows 'ci.yml')

  # The baseline every library is compared with, read from the MSBuild file itself.
  $targets = [xml](Get-Content -Raw -LiteralPath (Join-Path $Root 'Directory.Build.targets'))
  $script:Baseline = @($targets.Project.PropertyGroup.PackageValidationBaselineVersion |
      Where-Object { $_ } | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.'#text' } })

  # The lines of one job of ci.yml: from its key at two spaces under `jobs:` to the next job's key.
  function script:Get-JobLines([string[]]$Lines, [string]$Job) {
    $start = [array]::IndexOf($Lines, "  ${Job}:")
    if ($start -lt 0) { throw "ci.yml has no job '$Job'." }
    $end = $start + 1
    while ($end -lt $Lines.Count -and $Lines[$end] -notmatch '^  [A-Za-z0-9_-]+:\s*$' -and $Lines[$end] -notmatch '^\S') { $end++ }
    return $Lines[$start..($end - 1)]
  }

  # One step of a list of lines, by name: its lines up to the next step.
  function script:Get-StepLines([string[]]$Lines, [string]$Name) {
    $start = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) { if ($Lines[$i] -match "^\s+- name: $([regex]::Escape($Name))\s*$") { $start = $i; break } }
    if ($start -lt 0) { throw "No step named '$Name'." }
    $indent = ($Lines[$start] -replace '^(\s+)-.*$', '$1')
    $end = $start + 1
    while ($end -lt $Lines.Count -and -not ($Lines[$end] -match "^$indent- " -or ($Lines[$end] -match '^\S'))) {
      if ($Lines[$end] -match '^\s*\S' -and ($Lines[$end] -replace '^(\s*).*$', '$1').Length -lt $indent.Length) { break }
      $end++
    }
    return $Lines[$start..($end - 1)]
  }

  # The `run: |` block of a step, dedented.
  function script:Get-StepScript([string[]]$StepLines) {
    $script = @()
    for ($i = 0; $i -lt $StepLines.Count; $i++) {
      if ($StepLines[$i] -match '^(\s+)run: \|') {
        $bodyIndent = $Matches[1] + '  '
        while ($i + 1 -lt $StepLines.Count -and ($StepLines[$i + 1].StartsWith($bodyIndent) -or $StepLines[$i + 1] -match '^\s*$')) {
          $script += $StepLines[$i + 1] -replace "^$bodyIndent", ''; $i++
        }
      }
    }
    return ($script -join "`n")
  }

  # Runs the Placeholder version step's script in a directory, with the given event, and returns its exit code and
  # the lines it wrote to GITHUB_OUTPUT.
  function script:Invoke-PlaceholderStep([string]$Directory, [string]$EventName, [string]$PrNumber) {
    $body = Get-StepScript (Get-StepLines (Get-JobLines $CiLines 'changes') 'Placeholder version')
    $scriptFile = Join-Path $TestDrive 'placeholder.sh'
    $outputFile = Join-Path $TestDrive 'placeholder-output.txt'
    Set-Content -LiteralPath $scriptFile -Value $body
    Set-Content -LiteralPath $outputFile -Value $null
    $env:EVENT = $EventName; $env:PR_NUMBER = $PrNumber; $env:RUN_NUMBER = '42'; $env:GITHUB_OUTPUT = $outputFile
    Push-Location $Directory
    try {
      bash $scriptFile 2>&1 | Out-Null
      $code = $LASTEXITCODE
    } finally {
      Pop-Location
      Remove-Item Env:EVENT, Env:PR_NUMBER, Env:RUN_NUMBER, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue
    }
    return [pscustomobject]@{ ExitCode = $code; Output = @(Get-Content -LiteralPath $outputFile | Where-Object { $_ }) }
  }
}

Describe 'The build artifact carries what pack validates (#2605)' {
  BeforeAll {
    $script:Upload = Get-StepLines (Get-Content -LiteralPath (Join-Path $Workflows 'reusable-build.yml')) 'Upload Build Artifacts'
  }

  It 'uploads the packable projects'' intermediates, so the rule below is not vacuous' {
    $Upload | Should -Contain '            src/**/obj/Release'
    $Upload | Should -Contain '            tools/**/obj/Release'
  }

  It 'keeps the reference assemblies, which package validation reads from obj/Release/<tfm>/ref' {
    ($Upload | Where-Object { $_ -match '^\s+!.*/ref/' }) | Should -BeNullOrEmpty -Because 'without them every library stops on MSB4018 and its API is never compared'
  }
}

Describe 'Pull request and queue runs stamp a version validation can compare (#2605)' {
  It 'reads exactly one baseline from Directory.Build.targets' {
    $Baseline.Count | Should -Be 1
    $Baseline[0] | Should -Match '^\d+\.\d+\.\d+$'
  }

  It 'gives Compile and Pack the placeholder from Plan · Detect changes, never a 0.0.0 one' {
    foreach ($job in 'build', 'pack') {
      $versionLine = @(Get-JobLines $CiLines $job | Where-Object { $_ -match '^      version:' })
      $versionLine.Count | Should -Be 1 -Because "$job passes one version"
      $versionLine[0] | Should -Not -Match '0\.0\.0' -Because 'assembly version 0.0.0.0 is below the baseline, which ApiCompat reports as CP0003 in every library'
      $versionLine[0] | Should -Match 'needs\.version\.outputs\.semver \|\| needs\.changes\.outputs\.placeholder-version'
    }
  }

  It 'makes Pack wait for the job that computes the placeholder' {
    $needs = @(Get-JobLines $CiLines 'pack' | Where-Object { $_ -match '^    needs:' })
    $needs[0] | Should -Match '\bchanges\b'
  }

  It 'exposes the placeholder as an output of Plan · Detect changes' {
    (Get-JobLines $CiLines 'changes') | Should -Contain '      placeholder-version: ${{ steps.placeholder.outputs.version }}'
  }

  It 'stamps <Event> runs with the baseline''s own version and a label' -Skip:$IsWindows -TestCases @(
    @{ Event = 'merge_group'; Pr = ''; Label = 'queue.42' }
    @{ Event = 'pull_request'; Pr = '2605'; Label = 'pr2605.42' }
    @{ Event = 'workflow_dispatch'; Pr = ''; Label = 'ci.42' }
  ) {
    param($Event, $Pr, $Label)
    $result = Invoke-PlaceholderStep $Root $Event $Pr
    $result.ExitCode | Should -Be 0
    $result.Output | Should -Be @("version=$($Baseline[0])-$Label")
  }

  It 'gives an assembly version at the baseline, so ApiCompat sees no identity change' -Skip:$IsWindows {
    $version = ((Invoke-PlaceholderStep $Root 'merge_group' '').Output[0] -replace '^version=', '')
    $assemblyVersion = [version](($version -split '-', 2)[0])
    $assemblyVersion | Should -BeGreaterOrEqual ([version]$Baseline[0])
  }

  It 'fails, rather than stamping a version of its own, when it finds no baseline' -Skip:$IsWindows {
    $empty = Join-Path $TestDrive 'no-baseline'
    New-Item -ItemType Directory -Path $empty -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $empty 'Directory.Build.targets') -Value '<Project></Project>'
    $result = Invoke-PlaceholderStep $empty 'merge_group' ''
    $result.ExitCode | Should -Not -Be 0
    $result.Output | Should -BeNullOrEmpty
  }
}

Describe 'A failed pack fails its step (#2605)' {
  BeforeAll {
    $script:PackLines = @(foreach ($file in Get-ChildItem -LiteralPath $Workflows -Filter '*.yml') {
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
          if ($line -match '^\s+dotnet pack\b') { [pscustomobject]@{ File = $file.Name; Line = $line.Trim() } }
        }
      })
  }

  It 'finds the pack commands of the pack and publish workflows, so the rule below is not vacuous' {
    $PackLines.File | Should -Contain 'reusable-pack.yml'
    $PackLines.File | Should -Contain 'nuget-publish.yml'
  }

  It 'never discards the exit code of dotnet pack' {
    $hidden = @($PackLines | Where-Object { $_.Line -match '\|\|\s*(true|:)\s*$' } | ForEach-Object { "$($_.File): $($_.Line)" })
    $hidden | Should -BeNullOrEmpty -Because 'package validation reports an API break as a pack error, and a discarded exit code turns it into a pass'
  }
}
