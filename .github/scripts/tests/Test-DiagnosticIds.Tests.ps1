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

Describe 'The repository' {
  It 'defines no diagnostic ID with two meanings' {
    $descriptors = @(Get-RepositoryDiagnosticDescriptor -Root $Root)
    # The independent fact the scan is checked against: the library has well over a hundred
    # diagnostics, so a scan that finds a handful has stopped matching, not found the code clean.
    $descriptors.Count | Should -BeGreaterThan 100
    Find-DiagnosticIdConflict -Descriptors $descriptors -Allowed $script:AllowedSharedIds | Should -BeNullOrEmpty
  }
}
