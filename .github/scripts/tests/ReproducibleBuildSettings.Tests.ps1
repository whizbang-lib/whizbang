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

# Every generator assembly is merged once, in place, with its symbols embedded, and that one file is what the tests
# load, what coverage reads (the Whizbang.Generators.Shared code merged into it needs the symbols) and what the package
# ships. ILRepack writes the merged assembly to a per-run temp folder and records the symbol file's path from the stream
# it wrote; a separate symbol file therefore puts that temp path in the assembly, while an embedded one is written from
# memory, so the assembly records only the file name and is the same in two builds of one commit. A build of a generator
# that does not embed its symbols fails (WhizbangRequireEmbeddedSymbols, Directory.Build.targets), so a new generator
# cannot ship a temp path by leaving the setting out.
Describe 'every ILRepack merge' {
  BeforeAll {
    $script:Targets = @(Get-ChildItem -Path (Join-Path $script:RepoRoot 'src') -Filter 'ILRepack.targets' -Recurse -File)
    $script:Projects = @($script:Targets | ForEach-Object {
        Get-ChildItem -LiteralPath $_.DirectoryName -Filter '*.csproj' -File | Select-Object -First 1 })
  }

  It 'is found, so the checks below read something' {
    $script:Targets.Count | Should -BeGreaterThan 4
    $script:Projects.Count | Should -Be $script:Targets.Count
  }

  It 'merges once, in place, with symbols, so the tests, coverage and the package read one file' {
    foreach ($file in $script:Targets) {
      [xml]$xml = Get-Content -Raw -LiteralPath $file.FullName
      $tasks = @($xml.SelectNodes('//*[local-name()="ILRepack"]'))
      $tasks.Count | Should -Be 1 -Because "$($file.FullName) merges once, and that merge is the tested and the shipped one"
      $tasks[0].GetAttribute('OutputFile') | Should -Be "@(IntermediateAssembly->'%(FullPath)')" -Because "$($file.FullName) merges in place"
      $tasks[0].GetAttribute('DebugInfo') | Should -Be 'true' -Because 'coverage of the merged Shared code needs the symbols'
    }
  }

  It 'embeds the symbols, so the assembly records no temp path' {
    foreach ($project in $script:Projects) {
      $evaluated = Get-BuildProperty $project.FullName @('DebugType', 'ILRepackEnabled') @('CI=true', 'Configuration=Release')
      $evaluated.ILRepackEnabled | Should -Be 'true' -Because "$($project.Name) has an ILRepack.targets"
      $evaluated.DebugType | Should -Be 'embedded' -Because "$($project.Name) is merged by ILRepack"
    }
  }

  It 'fails the build of a generator that does not embed its symbols' {
    $output = & dotnet msbuild $script:Generators -nologo -t:WhizbangRequireEmbeddedSymbols -p:DebugType=portable 2>&1
    $LASTEXITCODE | Should -Not -Be 0
    ($output -join "`n") | Should -Match 'DebugType'
  }

  It 'passes the build of a generator that embeds them' {
    & dotnet msbuild $script:Generators -nologo -t:WhizbangRequireEmbeddedSymbols 2>&1 | Out-Null
    $LASTEXITCODE | Should -Be 0
  }

  It 'is what each generator package ships: the build output the tests load, not a copy of its own' {
    foreach ($project in $script:Projects) {
      [xml]$xml = Get-Content -Raw -LiteralPath $project.FullName
      $analyzers = @($xml.SelectNodes('//*[@PackagePath]') | Where-Object {
          $_.GetAttribute('PackagePath') -like 'analyzers/dotnet/cs*' -and $_.GetAttribute('Include') -notlike '*CodeFixes*' })
      $analyzers.Count | Should -Be 1 -Because "$($project.Name) packs its generator assembly once"
      $analyzers[0].GetAttribute('Include') | Should -BeIn @('$(TargetPath)', '$(OutputPath)/$(AssemblyName).dll') -Because "$($project.Name) must ship the assembly its build wrote"
    }
  }
}
