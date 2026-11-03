# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-DiagnosticIds.ps1')
  $script:Root = Join-Path $PSScriptRoot '..' '..' '..'
}

Describe 'Get-DiagnosticDescriptor' {
  It 'reads a descriptor with named arguments' {
    $text = @'
  public static readonly DiagnosticDescriptor Found = new(
      id: "WHIZ001",
      title: "Receptor Discovered",
      messageFormat: "x",
'@
    $d = @(Get-DiagnosticDescriptor -Text $text -Path 'src/A/D.cs')
    $d.Count | Should -Be 1
    $d[0].Id | Should -Be 'WHIZ001'
    $d[0].Title | Should -Be 'Receptor Discovered'
    $d[0].Path | Should -Be 'src/A/D.cs'
    $d[0].Line | Should -Be 1
  }

  It 'reads a descriptor with positional arguments built inline' {
    $text = @'
    var descriptor = new DiagnosticDescriptor(
        "EFCORE104",
        "Model discovered",
        "x",
'@
    $d = @(Get-DiagnosticDescriptor -Text $text -Path 'src/B/G.cs')
    $d.Count | Should -Be 1
    $d[0].Id | Should -Be 'EFCORE104'
    $d[0].Title | Should -Be 'Model discovered'
  }

  It 'reads a title that contains an escaped quote' {
    $text = 'static readonly DiagnosticDescriptor X = new(id: "WHIZ002", title: "Use \"this\"", messageFormat: "x");'
    (Get-DiagnosticDescriptor -Text $text -Path 'a.cs').Title | Should -Be 'Use \"this\"'
  }

  It 'reads a title taken from a named constant' {
    $text = @'
    var descriptor = new DiagnosticDescriptor(
        id: "EFCORE997",
        title: EFCORE_GENERATOR_ERROR_TITLE,
'@
    $d = @(Get-DiagnosticDescriptor -Text $text -Path 'a.cs')
    $d[0].Id | Should -Be 'EFCORE997'
    $d[0].Title | Should -Be 'EFCORE_GENERATOR_ERROR_TITLE'
  }

  It 'reports a construction site whose id and title it cannot read, rather than skipping it' {
    $text = @'
    var descriptor = new DiagnosticDescriptor(
        Ids.Something,
        Titles.Something,
'@
    $d = @(Get-DiagnosticDescriptor -Text $text -Path 'src/C/H.cs')
    $d.Count | Should -Be 1
    $d[0].Id | Should -BeNullOrEmpty
  }
}

Describe 'Find-DiagnosticIdConflict' {
  BeforeAll {
    function New-D([string]$Id, [string]$Title, [string]$Path = 'src/A/D.cs', [int]$Line = 1) {
      [pscustomobject]@{ Id = $Id; Title = $Title; Path = $Path; Line = $Line }
    }
  }

  It 'passes one ID with one meaning, even when it is defined twice' {
    $r = @(Find-DiagnosticIdConflict -Descriptors @((New-D 'WHIZ001' 'A' 'src/A/D.cs'), (New-D 'WHIZ001' 'A' 'src/B/D.cs')) -Allowed @{})
    $r.Count | Should -Be 0
  }

  It 'reports one ID with two meanings, naming both definitions' {
    $r = @(Find-DiagnosticIdConflict -Descriptors @((New-D 'WHIZ400' 'Lens' 'src/A/D.cs' 10), (New-D 'WHIZ400' 'Scope' 'src/B/S.cs' 20)) -Allowed @{})
    $r.Count | Should -Be 1
    $r[0] | Should -Match 'WHIZ400'
    $r[0] | Should -Match ([regex]::Escape('src/A/D.cs:10'))
    $r[0] | Should -Match ([regex]::Escape('src/B/S.cs:20'))
  }

  It 'reports one ID given two meanings by a shipped package and a repository analyzer under tools/' {
    $r = @(Find-DiagnosticIdConflict -Descriptors @((New-D 'WHIZ604' 'Undisposed JsonDocument' 'tools/Whizbang.RepoAnalyzers/U.cs' 30), (New-D 'WHIZ604' 'Lens' 'src/Whizbang.Generators/D.cs' 12)) -Allowed @{})
    $r.Count | Should -Be 1
    $r[0] | Should -Match ([regex]::Escape('tools/Whizbang.RepoAnalyzers/U.cs:30'))
  }

  It 'passes an ID allowed to share a meaning across packages' {
    $r = @(Find-DiagnosticIdConflict -Descriptors @((New-D 'WHIZ070' 'Needs package'), (New-D 'WHIZ070' 'Missing package' 'src/B/D.cs')) -Allowed @{ WHIZ070 = 'same meaning' })
    $r.Count | Should -Be 0
  }

  It 'reports a construction site it could not read' {
    $r = @(Find-DiagnosticIdConflict -Descriptors @(New-D $null $null 'src/C/H.cs' 7) -Allowed @{})
    $r.Count | Should -Be 1
    $r[0] | Should -Match ([regex]::Escape('src/C/H.cs:7'))
  }
}

Describe 'Get-MSBuildDiagnosticCode' {
  It 'reads each Error and Warning code with its target as the title' {
    $text = @'
<Project>
  <Target Name="ValidateTags" BeforeTargets="Build">
    <Error Code="WHIZ600"
           Text="no tags" />
  </Target>
  <Target Name="ValidateType">
    <Warning Condition="x" Code="WHIZ601" Text="y" />
  </Target>
</Project>
'@
    $d = @(Get-MSBuildDiagnosticCode -Text $text -Path 'Directory.Build.targets')
    $d.Count | Should -Be 2
    $d[0].Id | Should -Be 'WHIZ600'
    $d[0].Title | Should -Be 'MSBuild target ValidateTags'
    $d[0].Path | Should -Be 'Directory.Build.targets'
    $d[0].Line | Should -Be 3
    $d[1].Id | Should -Be 'WHIZ601'
    $d[1].Title | Should -Be 'MSBuild target ValidateType'
  }

  It 'reads nothing from a file without codes' {
    @(Get-MSBuildDiagnosticCode -Text '<Project><Target Name="A"><Message Text="x" /></Target></Project>' -Path 'a.targets').Count | Should -Be 0
  }
}

Describe 'Get-DiagnosticOwner' {
  It 'names the project for a file under src/ or tools/, and the file itself for a root MSBuild file' {
    Get-DiagnosticOwner -Path 'src/Whizbang.Generators/DiagnosticDescriptors.cs' | Should -Be 'Whizbang.Generators'
    Get-DiagnosticOwner -Path 'tools/Whizbang.RepoAnalyzers/A.cs' | Should -Be 'Whizbang.RepoAnalyzers'
    Get-DiagnosticOwner -Path 'Directory.Build.targets' | Should -Be 'Directory.Build.targets'
  }
}

Describe 'Get-RetiredDiagnosticId' {
  It 'reads the RETIRED_IDS list from the descriptor source' {
    $text = @'
  public static readonly System.Collections.Immutable.ImmutableArray<string> RETIRED_IDS = [
    "WHIZ003", "WHIZ005",
    "WHIZ803",
  ];
  private const string CATEGORY = "X";
'@
    @(Get-RetiredDiagnosticId -Text $text) | Should -Be @('WHIZ003', 'WHIZ005', 'WHIZ803')
  }

  It 'fails loudly when the list cannot be found, rather than reading it as empty' {
    { Get-RetiredDiagnosticId -Text 'public static class Nothing { }' } | Should -Throw '*RETIRED_IDS*'
  }
}

Describe 'Read-DiagnosticIdRegistry' {
  It 'reads id, status, owner and meaning, skipping comments and blank lines' {
    $text = @'
# id | status | owner | meaning

WHIZ001 | active | Whizbang.Generators | Receptor discovered
WHIZ070 | active | Whizbang.Data.EFCore.Postgres.Generators, Whizbang.Generators | Vector field needs Pgvector
WHIZ003 | retired | Whizbang.Generators | Never reported (#1427)
'@
    $r = @(Read-DiagnosticIdRegistry -Text $text -Path 'diagnostic-ids.txt')
    $r.Count | Should -Be 3
    $r[0].Id | Should -Be 'WHIZ001'
    $r[0].Status | Should -Be 'active'
    $r[0].Owner | Should -Be 'Whizbang.Generators'
    $r[0].Meaning | Should -Be 'Receptor discovered'
    $r[0].Line | Should -Be 3
    $r[1].Owner | Should -Be 'Whizbang.Data.EFCore.Postgres.Generators, Whizbang.Generators'
    $r[2].Status | Should -Be 'retired'
  }

  It 'returns a malformed row with no id so the check reports it' {
    $r = @(Read-DiagnosticIdRegistry -Text "WHIZ001 | active | only three" -Path 'diagnostic-ids.txt')
    $r.Count | Should -Be 1
    $r[0].Id | Should -BeNullOrEmpty
    $r[0].Line | Should -Be 1
  }

  It 'treats an unknown status as malformed' {
    $r = @(Read-DiagnosticIdRegistry -Text "WHIZ001 | maybe | Whizbang.Generators | x" -Path 'diagnostic-ids.txt')
    $r[0].Id | Should -BeNullOrEmpty
  }
}

Describe 'Find-DiagnosticIdRegistryProblem' {
  BeforeAll {
    function New-D([string]$Id, [string]$Path = 'src/Whizbang.Generators/D.cs', [string]$Title = 'T') {
      [pscustomobject]@{ Id = $Id; Title = $Title; Path = $Path; Line = 1 }
    }
    function New-R([string]$Id, [string]$Status = 'active', [string]$Owner = 'Whizbang.Generators', [int]$Line = 1) {
      [pscustomobject]@{ Id = $Id; Status = $Status; Owner = $Owner; Meaning = 'm'; Line = $Line }
    }
    function Invoke-Check($Descriptors, $Registry, $Retired = @()) {
      @(Find-DiagnosticIdRegistryProblem -Descriptors @($Descriptors) -Registry @($Registry) -RetiredInCode @($Retired))
    }
  }

  It 'passes when every defined id is registered under its owner' {
    Invoke-Check @((New-D 'WHIZ001'), (New-D 'WHIZ600' 'Directory.Build.targets')) @((New-R 'WHIZ001'), (New-R 'WHIZ600' -Owner 'Directory.Build.targets')) |
      Should -BeNullOrEmpty
  }

  It 'passes an id defined by two projects when the registry names both' {
    $d = @((New-D 'WHIZ070' 'src/Whizbang.Generators/A.cs'), (New-D 'WHIZ070' 'src/Whizbang.Data.EFCore.Postgres.Generators/B.cs'))
    Invoke-Check $d @(New-R 'WHIZ070' -Owner 'Whizbang.Data.EFCore.Postgres.Generators, Whizbang.Generators') | Should -BeNullOrEmpty
  }

  It 'reports an id that does not match WHIZ and three digits' {
    $r = @(Invoke-Check @(New-D 'WHZ0007') @(New-R 'WHZ0007'))
    $r.Count | Should -Be 1
    $r[0] | Should -Match 'WHZ0007'
    $r[0] | Should -Match ([regex]::Escape('WHIZ'))
  }

  It 'passes a frozen legacy id outside the pattern, and reports a legacy row that matches the pattern' {
    Invoke-Check @(New-D 'EFCORE104' 'src/Whizbang.Data.EFCore.Postgres.Generators/G.cs') @(New-R 'EFCORE104' 'legacy' 'Whizbang.Data.EFCore.Postgres.Generators') |
      Should -BeNullOrEmpty
    $r = @(Invoke-Check @(New-D 'WHIZ001') @(New-R 'WHIZ001' 'legacy'))
    $r.Count | Should -Be 1
    $r[0] | Should -Match 'legacy'
  }

  It 'reports an id defined in code but missing from the registry, naming where it is defined' {
    $r = @(Invoke-Check @((New-D 'WHIZ001'), (New-D 'WHIZ605' 'src/Whizbang.TestAnalyzers/S.cs')) @(New-R 'WHIZ001'))
    $r.Count | Should -Be 1
    $r[0] | Should -Match 'WHIZ605'
    $r[0] | Should -Match ([regex]::Escape('src/Whizbang.TestAnalyzers/S.cs'))
  }

  It 'reports an active id in the registry that nothing defines' {
    $r = @(Invoke-Check @(New-D 'WHIZ001') @((New-R 'WHIZ001'), (New-R 'WHIZ999' -Line 7)))
    $r.Count | Should -Be 1
    $r[0] | Should -Match 'WHIZ999'
    $r[0] | Should -Match 'line 7'
  }

  It 'reports a registry owner that is not where the id is defined' {
    $r = @(Invoke-Check @(New-D 'WHIZ604' 'tools/Whizbang.RepoAnalyzers/U.cs') @(New-R 'WHIZ604' -Owner 'Whizbang.TestAnalyzers'))
    $r.Count | Should -Be 1
    $r[0] | Should -Match 'Whizbang.RepoAnalyzers'
  }

  It 'passes a retired id nothing defines, and reports one that is defined again' {
    Invoke-Check @(New-D 'WHIZ001') @((New-R 'WHIZ001'), (New-R 'WHIZ003' 'retired')) @('WHIZ003') | Should -BeNullOrEmpty
    $r = @(Invoke-Check @((New-D 'WHIZ001'), (New-D 'WHIZ003')) @((New-R 'WHIZ001'), (New-R 'WHIZ003' 'retired')) @('WHIZ003'))
    $r.Count | Should -Be 1
    $r[0] | Should -Match 'WHIZ003'
    $r[0] | Should -Match 'retired'
  }

  It 'reports a retired id the registry and RETIRED_IDS disagree on, in either direction' {
    $r = @(Invoke-Check @(New-D 'WHIZ001') @((New-R 'WHIZ001'), (New-R 'WHIZ003' 'retired')) @('WHIZ005'))
    $r.Count | Should -Be 2
    ($r -join "`n") | Should -Match 'WHIZ003'
    ($r -join "`n") | Should -Match 'WHIZ005'
    ($r -join "`n") | Should -Match 'RETIRED_IDS'
  }

  It 'reports an id listed twice in the registry' {
    $r = @(Invoke-Check @(New-D 'WHIZ001') @((New-R 'WHIZ001' -Line 3), (New-R 'WHIZ001' -Line 9)))
    $r.Count | Should -Be 1
    $r[0] | Should -Match 'twice|more than once'
  }

  It 'reports a malformed registry row' {
    $r = @(Invoke-Check @(New-D 'WHIZ001') @((New-R 'WHIZ001'), [pscustomobject]@{ Id = $null; Status = $null; Owner = $null; Meaning = $null; Line = 4 }))
    $r.Count | Should -Be 1
    $r[0] | Should -Match 'line 4'
  }
}

Describe 'The repository' {
  BeforeAll {
    $script:Descriptors = @(Get-RepositoryDiagnosticDescriptor -Root $Root)
    $script:Registry = @(Read-DiagnosticIdRegistry -Text (Get-Content -Raw -Path (Join-Path $Root 'diagnostic-ids.txt')) -Path 'diagnostic-ids.txt')
    $script:Retired = @(Get-RetiredDiagnosticId -Text (Get-Content -Raw -Path (Join-Path $Root 'src/Whizbang.Generators/DiagnosticDescriptors.cs')))
  }

  It 'defines no diagnostic ID with two meanings' {
    # The independent fact the scan is checked against: the library has well over a hundred
    # diagnostics, so a scan that finds a handful has stopped matching, not found the code clean.
    $Descriptors.Count | Should -BeGreaterThan 100
    Find-DiagnosticIdConflict -Descriptors $Descriptors -Allowed $script:AllowedSharedIds | Should -BeNullOrEmpty
  }

  It 'reads the repository-only analyzers under tools/ and the MSBuild checks, as well as src/' {
    # One ID space covers the shipped generators, src/Whizbang.TestAnalyzers, tools/Whizbang.RepoAnalyzers and the
    # MSBuild checks; a scan of src/ alone would let one of the others reuse an ID unnoticed.
    @($Descriptors | Where-Object { $_.Path -like 'tools/Whizbang.RepoAnalyzers/*' -and $_.Id -eq 'WHIZ604' }).Count | Should -Be 1
    @($Descriptors | Where-Object { $_.Path -like 'src/Whizbang.TestAnalyzers/*' -and $_.Id -eq 'WHIZ603' }).Count | Should -Be 1
    @($Descriptors | Where-Object { $_.Path -eq 'Directory.Build.targets' } | Select-Object -ExpandProperty Id) | Should -Be @('WHIZ600', 'WHIZ601', 'WHIZ602')
  }

  It 'registers every ID it defines, and defines every ID it registers' {
    Find-DiagnosticIdRegistryProblem -Descriptors $Descriptors -Registry $Registry -RetiredInCode $Retired | Should -BeNullOrEmpty
  }

  It 'keeps the legacy IDs outside the WHIZ pattern to the frozen set that shipped before it' {
    # These shipped in packages before every ID took the WHIZ form, and consumers may suppress them by name. The
    # set only shrinks: a new diagnostic takes the next WHIZ number from diagnostic-ids.txt.
    $frozen = @('EFCORE000', 'EFCORE001', 'EFCORE100', 'EFCORE102', 'EFCORE103', 'EFCORE104', 'EFCORE105', 'EFCORE106',
      'EFCORE107', 'EFCORE108', 'EFCORE109', 'EFCORE206', 'EFCORE994', 'EFCORE995', 'EFCORE996', 'EFCORE997', 'EFCORE999',
      'WSAGA004')
    $legacy = @($Registry | Where-Object Status -eq 'legacy' | Select-Object -ExpandProperty Id)
    @($legacy | Where-Object { $_ -notin $frozen }) | Should -BeNullOrEmpty
  }
}
