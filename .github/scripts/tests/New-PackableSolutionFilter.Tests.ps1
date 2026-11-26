# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The reproducible-build check (#2484) builds only what ships: the projects behind .github/nuget-packages.txt.
# Each test names a way the filter could build less than ships, or fail to notice it.

BeforeAll {
  $script:Script = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'New-PackableSolutionFilter.ps1'
  . $script:Script -OutputPath x
  $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path

  function New-Repo([string]$Name, [string[]]$Projects, [hashtable]$Content = @{}) {
    $root = Join-Path $TestDrive $Name
    foreach ($p in $Projects) {
      $path = Join-Path $root $p
      $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path)
      Set-Content -LiteralPath $path -Value ($Content.ContainsKey($p) ? $Content[$p] : '<Project />')
    }
    return $root
  }
}

Describe 'Get-PackableProject' {
  It 'maps each package id to its project under src/ or tools/, skipping comments and blank lines' {
    $root = New-Repo 'map' @('src/Whizbang.Core/Whizbang.Core.csproj', 'tools/Whizbang.CLI/Whizbang.CLI.csproj')
    $ids = @('# header', '', 'SoftwareExtravaganza.Whizbang.Core', 'SoftwareExtravaganza.Whizbang.CLI')
    @(Get-PackableProject -PackageIds $ids -RepoRoot $root) | Should -Be @('src/Whizbang.Core/Whizbang.Core.csproj', 'tools/Whizbang.CLI/Whizbang.CLI.csproj')
  }

  It 'fails on a package with no project, rather than silently building less than ships' {
    $root = New-Repo 'missing' @('src/Whizbang.Core/Whizbang.Core.csproj')
    { Get-PackableProject -PackageIds @('SoftwareExtravaganza.Whizbang.Gone') -RepoRoot $root } | Should -Throw '*Whizbang.Gone*'
  }

  It 'finds a project for every package this repository ships' {
    $ids = Get-Content (Join-Path $script:RepoRoot '.github/nuget-packages.txt')
    $expected = @($ids | Where-Object { $_ -and -not $_.StartsWith('#') }).Count
    @(Get-PackableProject -PackageIds $ids -RepoRoot $script:RepoRoot).Count | Should -Be $expected
  }
}

# A package can bundle the build output of a project that does not ship on its own: Whizbang.Generators packs
# Whizbang.Generators.CodeFixes.dll from that project's bin folder, and packs without it (a warning) when it was not
# built. A filter of the shipped projects alone never built it, so the check compared packages without the code fix
# while the released package carries it.
Describe 'Get-BundledProject' {
  It 'adds a project whose bin output a shipped project packs, with either path separator' {
    $content = @{
      'src/A/A.csproj' = '<Project><PropertyGroup><X>$(MSBuildProjectDirectory)\..\A.Fixes\bin\$(Configuration)\A.Fixes.dll</X></PropertyGroup></Project>'
      'src/B/B.csproj' = '<Project><PropertyGroup><X>$(MSBuildProjectDirectory)/../B.Extra/bin/$(Configuration)/B.Extra.dll</X></PropertyGroup></Project>'
    }
    $root = New-Repo 'bundled' @('src/A/A.csproj', 'src/A.Fixes/A.Fixes.csproj', 'src/B/B.csproj', 'src/B.Extra/B.Extra.csproj') $content
    @(Get-BundledProject -Projects @('src/A/A.csproj', 'src/B/B.csproj') -RepoRoot $root) |
      Should -Be @('src/A.Fixes/A.Fixes.csproj', 'src/B.Extra/B.Extra.csproj')
  }

  It 'adds nothing for a project that packs only its own output' {
    $root = New-Repo 'plain' @('src/A/A.csproj')
    @(Get-BundledProject -Projects @('src/A/A.csproj') -RepoRoot $root).Count | Should -Be 0
  }

  It 'fails on a bundled project it cannot find, rather than building less than ships' {
    $content = @{ 'src/A/A.csproj' = '<Project><PropertyGroup><X>..\Gone\bin\Gone.dll</X></PropertyGroup></Project>' }
    $root = New-Repo 'gone' @('src/A/A.csproj') $content
    { Get-BundledProject -Projects @('src/A/A.csproj') -RepoRoot $root } | Should -Throw '*Gone*'
  }
}

Describe 'the script' {
  It 'writes a solution filter over the repository solution naming every packable project' {
    $out = Join-Path $TestDrive 'packable.slnf'
    & pwsh -NoProfile -File $script:Script -OutputPath $out
    $LASTEXITCODE | Should -Be 0
    $filter = Get-Content -Raw $out | ConvertFrom-Json
    $filter.solution.path | Should -Be (Join-Path $script:RepoRoot 'Whizbang.slnx')
    $filter.solution.projects | Should -Contain 'src/Whizbang.Core/Whizbang.Core.csproj'
    $filter.solution.projects | Should -Contain 'tools/Whizbang.Migrate/Whizbang.Migrate.csproj'
    $filter.solution.projects | Should -Contain 'src/Whizbang.Generators.CodeFixes/Whizbang.Generators.CodeFixes.csproj' -Because 'Whizbang.Generators ships its build output'
    @($filter.solution.projects | Select-Object -Unique).Count | Should -Be @($filter.solution.projects).Count
  }
}
