# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The license gate on shipped NuGet dependencies (#1441). Each test names the case that would let a package
# ship a dependency under a license nobody reviewed, or block a release on one that is fine.

BeforeAll {
  $script:Script = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-PackageLicenses.ps1'
  . $script:Script -Directory x
  $script:RepoConfig = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath '..', 'dependency-review-config.yml'

  function New-Component([string]$Name, [string]$Version = '1.0.0', [object[]]$Licenses = $null, [string]$Scope = $null) {
    $c = [ordered]@{ name = $Name; version = $Version }
    if ($null -ne $Licenses) { $c.licenses = $Licenses }
    if ($Scope) { $c.scope = $Scope }
    [pscustomobject]$c
  }
  function New-Sbom([string]$Package, [object[]]$Components) {
    [pscustomobject]@{ metadata = [pscustomobject]@{ component = [pscustomobject]@{ name = $Package } }; components = $Components }
  }
  function Id([string]$Id) { [pscustomobject]@{ license = [pscustomobject]@{ id = $Id } } }
  function Named([string]$Name) { [pscustomobject]@{ license = [pscustomobject]@{ name = $Name } } }
  function Expr([string]$Expression) { [pscustomobject]@{ expression = $Expression } }

  $script:Policy = [pscustomobject]@{ Allowed = @('MIT', 'Apache-2.0', 'BSD-3-Clause'); Excepted = @('Reviewed.Package') }
}

Describe 'Read-YamlList' {
  It 'reads one list and stops at the next key, ignoring comments and quotes' {
    $lines = @('# header', 'fail-on-severity: moderate', 'allow-licenses:', '  - MIT  # the common one', "  - 'Apache-2.0'",
      '# between', 'allow-dependencies-licenses:', '  - pkg:nuget/X')
    @(Read-YamlList -Lines $lines -Key 'allow-licenses') | Should -Be @('MIT', 'Apache-2.0')
    @(Read-YamlList -Lines $lines -Key 'allow-dependencies-licenses') | Should -Be @('pkg:nuget/X')
  }

  It 'returns nothing for a key that is absent' {
    @(Read-YamlList -Lines @('a:', '  - b') -Key 'missing').Count | Should -Be 0
  }
}

Describe 'Get-LicensePolicy' {
  It 'reads the repository configuration: MIT allowed, and only NuGet exceptions' {
    $policy = Get-LicensePolicy -Path $script:RepoConfig
    $policy.Allowed | Should -Contain 'MIT'
    $policy.Allowed | Should -Contain 'Apache-2.0'
    @($policy.Excepted | Where-Object { $_ -like 'pkg:*' }).Count | Should -Be 0
  }

  It 'strips the purl prefix and version from a NuGet exception and ignores other ecosystems' {
    $file = Join-Path $TestDrive 'c.yml'
    Set-Content $file @('allow-licenses:', '  - MIT', 'allow-dependencies-licenses:', '  - pkg:npm/left-pad', '  - pkg:nuget/Some.Package@1.2.3')
    $policy = Get-LicensePolicy -Path $file
    @($policy.Excepted) | Should -Be @('Some.Package')
  }

  It 'refuses a configuration with no allow list, rather than allowing nothing silently' {
    $file = Join-Path $TestDrive 'empty.yml'
    Set-Content $file @('fail-on-severity: moderate')
    { Get-LicensePolicy -Path $file } | Should -Throw '*allow-licenses*'
  }
}

Describe 'Test-LicenseExpression' {
  It '<expression> is <expected>' -ForEach @(
      @{ expression = 'MIT'; expected = $true }
      @{ expression = 'GPL-3.0-only'; expected = $false }
      @{ expression = 'MIT OR GPL-3.0-only'; expected = $true }
      @{ expression = 'GPL-3.0-only OR LGPL-2.1-only'; expected = $false }
      @{ expression = 'MIT AND Apache-2.0'; expected = $true }
      @{ expression = 'MIT AND GPL-3.0-only'; expected = $false }
      @{ expression = '(MIT AND BSD-3-Clause) OR GPL-3.0-only'; expected = $true }
      @{ expression = 'Apache-2.0 WITH LLVM-exception'; expected = $true }
      @{ expression = 'name:Microsoft Software License'; expected = $false }) {
    Test-LicenseExpression -Expression $expression -Allowed $script:Policy.Allowed | Should -Be $expected
  }
}

Describe 'Get-ComponentLicense' {
  It 'returns $null for a component that declares no license' {
    Get-ComponentLicense -Component (New-Component 'A') | Should -BeNullOrEmpty
    Get-ComponentLicense -Component (New-Component 'A' -Licenses @()) | Should -BeNullOrEmpty
  }

  It 'prefers an SPDX id, marks a bare name, and keeps an expression whole' {
    Get-ComponentLicense -Component (New-Component 'A' -Licenses @(Id 'MIT')) | Should -Be 'MIT'
    Get-ComponentLicense -Component (New-Component 'A' -Licenses @(Named 'Custom EULA')) | Should -Be 'name:Custom EULA'
    Get-ComponentLicense -Component (New-Component 'A' -Licenses @(Expr 'MIT OR Apache-2.0')) | Should -Be '(MIT OR Apache-2.0)'
  }

  It 'requires every one of several license entries' {
    Get-ComponentLicense -Component (New-Component 'A' -Licenses @((Id 'MIT'), (Id 'GPL-3.0-only'))) | Should -Be 'MIT AND GPL-3.0-only'
  }
}

Describe 'Get-LicenseViolation' {
  It 'passes a graph of allowed licenses' {
    $sbom = New-Sbom 'SoftwareExtravaganza.Whizbang.Core' @((New-Component 'A' -Licenses @(Id 'MIT')), (New-Component 'B' -Licenses @(Expr 'MIT OR GPL-2.0-only')))
    @(Get-LicenseViolation -Sboms @($sbom) -Policy $script:Policy).Count | Should -Be 0
  }

  It 'fails a shipped dependency under a license outside the list, naming every package that brings it once' {
    $gpl = New-Component 'Copyleft.Lib' '2.0.0' -Licenses @(Id 'GPL-3.0-only')
    $sboms = @((New-Sbom 'Pkg.One' @($gpl, $gpl)), (New-Sbom 'Pkg.Two' @($gpl)))
    $v = @(Get-LicenseViolation -Sboms $sboms -Policy $script:Policy)
    $v.Count | Should -Be 1
    $v[0].Dependency | Should -Be 'Copyleft.Lib@2.0.0'
    $v[0].License | Should -Be 'GPL-3.0-only'
    @($v[0].Packages) | Should -Be @('Pkg.One', 'Pkg.Two')
  }

  It 'fails a shipped dependency that declares no license' {
    $v = @(Get-LicenseViolation -Sboms @(New-Sbom 'Pkg' @(New-Component 'Mystery')) -Policy $script:Policy)
    $v[0].License | Should -Be 'none declared'
  }

  It 'ignores development-only references, Whizbang packages and reviewed exceptions' {
    $sbom = New-Sbom 'Pkg' @(
      (New-Component 'Analyzer' -Licenses @(Named 'Proprietary') -Scope 'excluded'),
      (New-Component 'SoftwareExtravaganza.Whizbang.Core'),
      (New-Component 'Reviewed.Package' -Licenses @(Id 'GPL-3.0-only')))
    @(Get-LicenseViolation -Sboms @($sbom) -Policy $script:Policy).Count | Should -Be 0
  }

  It 'checks a dependency with an explicit required scope' {
    $sbom = New-Sbom 'Pkg' @(New-Component 'Lib' -Licenses @(Id 'GPL-3.0-only') -Scope 'required')
    @(Get-LicenseViolation -Sboms @($sbom) -Policy $script:Policy).Count | Should -Be 1
  }
}

Describe 'the script' {
  BeforeEach {
    $script:Dir = Join-Path $TestDrive ([guid]::NewGuid())
    New-Item -ItemType Directory -Path $script:Dir | Out-Null
    $script:Config = Join-Path $TestDrive 'policy.yml'
    Set-Content $script:Config @('allow-licenses:', '  - MIT')
  }

  It 'exits 0 when every dependency is allowed' {
    New-Sbom 'Pkg' @(New-Component 'A' -Licenses @(Id 'MIT')) | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $script:Dir 'Pkg.1.0.0.cdx.json')
    $out = pwsh -NoProfile -File $script:Script -Directory $script:Dir -Config $script:Config
    $LASTEXITCODE | Should -Be 0
    "$out" | Should -BeLike '*1 packages*'
  }

  It 'exits 1 and names the dependency when one is not allowed' {
    New-Sbom 'Pkg' @(New-Component 'Bad' '3.1.0' -Licenses @(Id 'AGPL-3.0-only')) | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $script:Dir 'Pkg.1.0.0.cdx.json')
    $out = pwsh -NoProfile -File $script:Script -Directory $script:Dir -Config $script:Config
    $LASTEXITCODE | Should -Be 1
    "$out" | Should -BeLike '*Bad@3.1.0*AGPL-3.0-only*Pkg*'
  }

  It 'exits 1 when there are no SBOMs, rather than passing on nothing' {
    pwsh -NoProfile -File $script:Script -Directory $script:Dir -Config $script:Config | Out-Null
    $LASTEXITCODE | Should -Be 1
  }
}
