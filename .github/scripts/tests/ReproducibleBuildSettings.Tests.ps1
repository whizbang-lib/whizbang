# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The build settings that make two builds of one commit byte-identical (#2484). Each test fails if a setting the
# reproducible-build check depends on is flipped or dropped; the check itself (reusable-reproducible-build.yml)
# would then fail on the next release branch, far from the change that broke it.

BeforeAll {
  $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
  $script:Generators = Join-Path $script:RepoRoot 'src/Whizbang.Generators/Whizbang.Generators.csproj'

  # Evaluates properties the way the build does (no build, no restore), so the test reads what MSBuild resolves
  # rather than what a file happens to say.
  function Get-BuildProperty([string]$Project, [string[]]$Names, [string[]]$Properties = @()) {
    $arguments = @('msbuild', $Project, '-nologo') + @($Names | ForEach-Object { "-getProperty:$_" }) + @($Properties | ForEach-Object { "-p:$_" })
    $output = & dotnet @arguments 2>$null
    if ($LASTEXITCODE -ne 0) { throw "dotnet msbuild could not evaluate $Project ($LASTEXITCODE)." }
    $text = $output -join "`n"
    # One property prints its bare value; several print JSON. Read the JSON as text so a date stays a string.
    if ($Names.Count -eq 1) { return [pscustomobject]@{ $Names[0] = $text.Trim() } }
    $values = [ordered]@{}
    foreach ($p in [System.Text.Json.JsonDocument]::Parse($text).RootElement.GetProperty('Properties').EnumerateObject()) {
      $values[$p.Name] = $p.Value.GetString()
    }
    return [pscustomobject]$values
  }
}

Describe 'a CI build' {
  BeforeAll {
    $script:Ci = Get-BuildProperty $script:Generators @('ContinuousIntegrationBuild', 'Deterministic', 'DeterministicTimestamp', 'WhizbangILRepackDebugInfo') @('CI=true')
  }

  It 'is a ContinuousIntegrationBuild, which maps the source root to /_/ in every path the compiler writes' {
    $script:Ci.ContinuousIntegrationBuild | Should -Be 'true'
  }

  It 'is deterministic' {
    $script:Ci.Deterministic | Should -Be 'true'
  }

  It 'stamps every package entry with one fixed time, not the time of the pack or of each file' {
    # NuGet reads "false" (its default) as "use each file's time" and "true" as "use now"; only a timestamp is fixed.
    $script:Ci.DeterministicTimestamp | Should -Not -BeNullOrEmpty
    $script:Ci.DeterministicTimestamp | Should -Not -BeIn @('true', 'false')
    $script:Ci.DeterministicTimestamp | Should -Match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$'
  }

  It 'merges generators without a symbol file, whose path ILRepack writes into the assembly from a per-run temp folder' {
    $script:Ci.WhizbangILRepackDebugInfo | Should -Be 'false'
  }
}

Describe 'a local build' {
  It 'keeps the merged generator symbols, so a generator can still be debugged' {
    (Get-BuildProperty $script:Generators @('WhizbangILRepackDebugInfo') @('CI=false', 'ContinuousIntegrationBuild=false')).WhizbangILRepackDebugInfo | Should -Be 'true'
  }
}

Describe 'every ILRepack merge' {
  It 'takes its symbol setting from the one shared property' {
    $targets = @(Get-ChildItem -Path (Join-Path $script:RepoRoot 'src') -Filter 'ILRepack.targets' -Recurse -File)
    $targets.Count | Should -BeGreaterThan 0
    foreach ($file in $targets) {
      [xml]$xml = Get-Content -Raw -LiteralPath $file.FullName
      $tasks = @($xml.SelectNodes('//*[local-name()="ILRepack"]'))
      $tasks.Count | Should -BeGreaterThan 0 -Because "$($file.FullName) merges with ILRepack"
      foreach ($task in $tasks) {
        $task.GetAttribute('DebugInfo') | Should -Be '$(WhizbangILRepackDebugInfo)' -Because "$($file.FullName) must not write a machine path into the merged assembly"
      }
    }
  }
}
