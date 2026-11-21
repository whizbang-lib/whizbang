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
    $script:Ci = Get-BuildProperty $script:Generators @('ContinuousIntegrationBuild', 'Deterministic', 'DeterministicTimestamp') @('CI=true')
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
}

# Every generator assembly is merged twice from the same compiler output, in the same build: once in place with symbols,
# for the tests and for coverage of the Whizbang.Generators.Shared code merged into it (a merge without symbols left
# that code reported as never run), and once without symbols for the package. ILRepack writes the symbol file's path,
# under a per-run temp folder, into the assembly, so only the merge without symbols is the same in two builds. Pack
# ships that one whether or not it rebuilds (CI packs the tested build with --no-build), so no build setting, and no
# step that must remember a property, decides which merge ships.
Describe 'every ILRepack merge' {
  BeforeAll {
    $script:Targets = @(Get-ChildItem -Path (Join-Path $script:RepoRoot 'src') -Filter 'ILRepack.targets' -Recurse -File)
  }

  It 'is found, so the checks below read something' {
    $script:Targets.Count | Should -BeGreaterThan 4
  }

  It 'keeps symbols in the merge the tests run, and writes the packaged merge without them' {
    foreach ($file in $script:Targets) {
      [xml]$xml = Get-Content -Raw -LiteralPath $file.FullName
      $tasks = @($xml.SelectNodes('//*[local-name()="ILRepack"]'))
      $tasks.Count | Should -Be 2 -Because "$($file.FullName) merges once for the tests and once for the package"
      $tested = @($tasks | Where-Object { $_.GetAttribute('OutputFile') -eq "@(IntermediateAssembly->'%(FullPath)')" })
      $packaged = @($tasks | Where-Object { $_.GetAttribute('OutputFile') -eq '$(WhizbangPackagedAnalyzer)' })
      $tested.Count | Should -Be 1 -Because "$($file.FullName) merges in place for the tests"
      $packaged.Count | Should -Be 1 -Because "$($file.FullName) merges the package's copy"
      $tested[0].GetAttribute('DebugInfo') | Should -Be 'true' -Because 'coverage of the merged Shared code needs the symbols'
      $packaged[0].GetAttribute('DebugInfo') | Should -Be 'false' -Because 'the symbol path is a per-run temp folder'
      foreach ($attribute in 'InputAssemblies', 'Internalize', 'TargetKind', 'KeyFile', 'LibraryPath') {
        $packaged[0].GetAttribute($attribute) | Should -Be $tested[0].GetAttribute($attribute) -Because "both merges in $($file.FullName) take the same inputs"
      }
    }
  }

  It 'puts the packaged merge in the build output pack reads' {
    (Get-BuildProperty $script:Generators @('WhizbangPackagedAnalyzer') @('CI=true', 'Configuration=Release')).WhizbangPackagedAnalyzer |
      Should -Match '[\\/]obj[\\/]Release[\\/]netstandard2\.0[\\/]packaged[\\/]Whizbang\.Generators\.dll$'
  }

  It 'is what each generator package ships, never the merge the tests run' {
    foreach ($file in $script:Targets) {
      $project = Get-ChildItem -LiteralPath $file.DirectoryName -Filter '*.csproj' -File | Select-Object -First 1
      [xml]$xml = Get-Content -Raw -LiteralPath $project.FullName
      $analyzers = @($xml.SelectNodes('//*[@PackagePath]') | Where-Object {
          $_.GetAttribute('PackagePath') -like 'analyzers/dotnet/cs*' -and $_.GetAttribute('Include') -notlike '*CodeFixes*' })
      $analyzers.Count | Should -Be 1 -Because "$($project.Name) packs its generator assembly once"
      $analyzers[0].GetAttribute('Include') | Should -Be '$(WhizbangPackagedAnalyzer)' -Because "$($project.Name) must ship the merge without symbols"
    }
  }
}
