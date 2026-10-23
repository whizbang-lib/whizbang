# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The generators package writes the pinned-type ledger and the message registry from the built assembly, so a
# consumer with EmitCompilerGeneratedFiles off still gets them. scripts/build-cost/Test-GeneratedJsonFiles.ps1 builds
# such a consumer; these tests pin the consumer it writes.

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/build-cost/Test-GeneratedJsonFiles.ps1')
}

Describe 'New-ConsumerProjectXml' {
  BeforeAll {
    $script:Xml = [xml](New-ConsumerProjectXml -RepositoryRoot '/repo')
  }

  It 'imports the package props first and the package targets last, as a package reference does' {
    $imports = @($Xml.Project.Import.Project)
    $imports[0] | Should -Be '/repo/src/Whizbang.Generators/build/SoftwareExtravaganza.Whizbang.Generators.props'
    $imports[-1] | Should -Be '/repo/src/Whizbang.Generators/build/SoftwareExtravaganza.Whizbang.Generators.targets'
    $Xml.Project.ChildNodes[0].Name | Should -Be 'Import'
    $Xml.Project.ChildNodes[$Xml.Project.ChildNodes.Count - 1].Name | Should -Be 'Import'
  }

  It 'keeps generated source off disk, so the JSON can only come from the built assembly' {
    $Xml.Project.PropertyGroup.EmitCompilerGeneratedFiles | Should -Be 'false'
  }

  It 'loads the generators as an analyzer, not as a reference' {
    $generators = @($Xml.Project.ItemGroup.ProjectReference | Where-Object Include -Like '*Whizbang.Generators.csproj')[0]
    $generators.OutputItemType | Should -Be 'Analyzer'
    $generators.ReferenceOutputAssembly | Should -Be 'false'
  }
}

Describe 'New-PinnedCommandSource' {
  It 'declares a pinned command with the given name and id' {
    $source = New-PinnedCommandSource -Name 'ProbeCommand' -PinnedId 'abc'
    $source | Should -Match '\[PinnedId\("abc"\)\]'
    $source | Should -Match 'public sealed record ProbeCommand\(Guid Id\) : ICommand;'
  }

  It 'starts with the license header the repository build requires (IDE0073)' {
    $source = New-PinnedCommandSource -Name 'ProbeCommand' -PinnedId 'abc'
    $source | Should -BeLike "// Copyright (c) whizbang-lib contributors.*// SPDX-License-Identifier: MIT*"
    ($source -split "`r?`n")[0] | Should -Be '// Copyright (c) whizbang-lib contributors.'
  }
}
