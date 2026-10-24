# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# A test project's build output carries a hidden .msCoverageExtensionSourceRootsMapping_<name> file that maps
# the deterministic /_/ source paths in its pdbs back to the checkout the BUILD ran in. The coverage collector
# reads it in the test process and skips every assembly whose sources it cannot find there, writing an empty
# report while the tests still pass. A test job on another machine (a self-hosted runner, a Windows runner)
# checks out somewhere else, so the build's mapping points at nothing. Update-CoverageSourceRoots.ps1 rewrites
# each mapping to this job's checkout before the tests run. Measured both ways: the Linux build's component
# tests, run in a Linux x64 container at /home/runner/work/whizbang/whizbang, wrote a 14 MB report; at any
# other path, an empty one.

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Update-CoverageSourceRoots.ps1'
  . $ScriptPath

  $script:BuildRoot = '/home/runner/work/whizbang/whizbang/'
  $script:BuildLines = @(
    '/home/runner/work/whizbang/whizbang/src/Whizbang.Core/Whizbang.Core.csproj|/home/runner/.nuget/packages/=/_1/'
    '/home/runner/work/whizbang/whizbang/src/Whizbang.Core/Whizbang.Core.csproj|/home/runner/work/whizbang/whizbang/=/_/'
    '/home/runner/work/whizbang/whizbang/tools/Whizbang.RepoAnalyzers/Whizbang.RepoAnalyzers.csproj|/home/runner/work/whizbang/whizbang/=/_/'
  )

  # A mapping file as the build writes it: UTF-16 little endian with a byte order mark.
  function New-MappingFile {
    param([string]$Directory, [string]$Name = 'My.Tests', [string[]]$Lines = $script:BuildLines)
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $path = Join-Path $Directory ".msCoverageExtensionSourceRootsMapping_$Name"
    [IO.File]::WriteAllText($path, (($Lines -join "`n") + "`n"), [Text.UnicodeEncoding]::new($false, $true))
    $path
  }

  function Read-MappingFile([string]$Path) {
    @([IO.File]::ReadAllText($Path, [Text.Encoding]::Unicode).Split("`n") | Where-Object { $_ })
  }

  function New-Repo {
    $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    $root
  }
}

Describe 'Convert-SourceRootLine' {
  It 'points the repository root and the project path at the new checkout' {
    Convert-SourceRootLine -Line $BuildLines[1] -OldRoot $BuildRoot -NewRoot 'C:\a\_work\whizbang\whizbang\' -NuGetRoot 'C:\Users\r\.nuget\packages\' -Separator '\' |
      Should -Be 'C:\a\_work\whizbang\whizbang\src\Whizbang.Core\Whizbang.Core.csproj|C:\a\_work\whizbang\whizbang\=/_/'
  }

  It 'points the NuGet root at the local package folder' {
    Convert-SourceRootLine -Line $BuildLines[0] -OldRoot $BuildRoot -NewRoot '/w/repo/' -NuGetRoot '/home/me/.nuget/packages/' -Separator '/' |
      Should -Be '/w/repo/src/Whizbang.Core/Whizbang.Core.csproj|/home/me/.nuget/packages/=/_1/'
  }

  It 'leaves a line of another shape alone (<Line>)' -ForEach @(
    @{ Line = 'no separators at all' }
    @{ Line = 'a|b' }
    @{ Line = '' }
  ) {
    Convert-SourceRootLine -Line $Line -OldRoot $BuildRoot -NewRoot '/w/' -NuGetRoot '/n/' -Separator '/' | Should -Be $Line
  }

  It 'leaves a project path outside the old root alone' {
    Convert-SourceRootLine -Line '/elsewhere/X.csproj|/home/runner/work/whizbang/whizbang/=/_/' -OldRoot $BuildRoot -NewRoot '/w/' -NuGetRoot '/n/' -Separator '/' |
      Should -Be '/elsewhere/X.csproj|/w/=/_/'
  }
}

Describe 'Get-BuildSourceRoot' {
  It 'is the root the build mapped to /_/' {
    Get-BuildSourceRoot -Lines $BuildLines | Should -Be $BuildRoot
  }

  It 'is empty when no line maps /_/' {
    Get-BuildSourceRoot -Lines @($BuildLines[0], 'junk') | Should -BeNullOrEmpty
  }
}

Describe 'Update-CoverageSourceRoots' {
  It 'rewrites every mapping under tests and samples to the checkout, as UTF-16 with a byte order mark' {
    $repo = New-Repo
    $a = New-MappingFile (Join-Path $repo 'tests' 'A.Tests' 'bin' 'Release' 'net10.0') 'A.Tests'
    $b = New-MappingFile (Join-Path $repo 'samples' 'S' 'S.Tests' 'bin' 'Release' 'net10.0') 'S.Tests'
    $result = Update-CoverageSourceRoots -Root $repo -NuGetRoot '/n' -Separator '/'
    $result.Rewritten | Should -Be 2
    $root = $repo.TrimEnd('/', '\') + '/'
    foreach ($file in $a, $b) {
      $lines = Read-MappingFile $file
      $lines[1] | Should -Be "${root}src/Whizbang.Core/Whizbang.Core.csproj|$root=/_/"
      $lines[0] | Should -Be "${root}src/Whizbang.Core/Whizbang.Core.csproj|/n/=/_1/"
      $bytes = [IO.File]::ReadAllBytes($file)
      $bytes[0] | Should -Be 0xFF
      $bytes[1] | Should -Be 0xFE
    }
  }

  It 'uses the platform separator by default' {
    $repo = New-Repo
    $file = New-MappingFile (Join-Path $repo 'tests' 'A.Tests' 'bin') 'A.Tests'
    $null = Update-CoverageSourceRoots -Root $repo -NuGetRoot (Join-Path $repo 'nuget')
    $sep = [IO.Path]::DirectorySeparatorChar
    (Read-MappingFile $file)[1] | Should -Be ("$($repo.TrimEnd('/', '\'))$sep" + "src${sep}Whizbang.Core${sep}Whizbang.Core.csproj|$($repo.TrimEnd('/', '\'))$sep=/_/")
  }

  It 'changes nothing when the checkout is where the build ran' {
    $repo = New-Repo
    $lines = $BuildLines | ForEach-Object { $_.Replace($BuildRoot, ($repo.TrimEnd('/', '\') + '/')).Replace('/home/runner/.nuget/packages/', '/n/') }
    $file = New-MappingFile (Join-Path $repo 'tests' 'A.Tests' 'bin') 'A.Tests' -Lines $lines
    $before = [IO.File]::ReadAllBytes($file)
    $result = Update-CoverageSourceRoots -Root $repo -NuGetRoot '/n' -Separator '/'
    $result.Rewritten | Should -Be 0
    $result.Unchanged | Should -Be 1
    [IO.File]::ReadAllBytes($file) | Should -Be $before
  }

  It 'skips, and names, a mapping with no /_/ root' {
    $repo = New-Repo
    $file = New-MappingFile (Join-Path $repo 'tests' 'A.Tests' 'bin') 'A.Tests' -Lines @('junk')
    $result = Update-CoverageSourceRoots -Root $repo -NuGetRoot '/n' -Separator '/'
    $result.Rewritten | Should -Be 0
    $result.Skipped | Should -Be @($file)
    Read-MappingFile $file | Should -Be @('junk')
  }

  It 'finds nothing in a tree with no build output' {
    $result = Update-CoverageSourceRoots -Root (New-Repo) -NuGetRoot '/n' -Separator '/'
    $result.Rewritten | Should -Be 0
    $result.Unchanged | Should -Be 0
  }
}

Describe 'Get-DefaultNuGetRoot' {
  It 'is NUGET_PACKAGES when set, else .nuget/packages under the home directory' {
    $saved = $env:NUGET_PACKAGES
    try {
      $env:NUGET_PACKAGES = '/custom/packages'
      Get-DefaultNuGetRoot | Should -Be '/custom/packages'
      $env:NUGET_PACKAGES = $null
      Get-DefaultNuGetRoot | Should -Be (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget' 'packages')
    } finally {
      $env:NUGET_PACKAGES = $saved
    }
  }
}

Describe 'Invoke-UpdateCoverageSourceRoots' {
  It 'reports the counts and names each skipped mapping' {
    $repo = New-Repo
    $null = New-MappingFile (Join-Path $repo 'tests' 'A.Tests' 'bin') 'A.Tests'
    $junk = New-MappingFile (Join-Path $repo 'tests' 'B.Tests' 'bin') 'B.Tests' -Lines @('junk')
    $out = @(Invoke-UpdateCoverageSourceRoots -Root $repo -NuGetRoot '/n')
    $out | Should -Contain "::warning::coverage source-root mapping with no /_/ root left as built: $junk"
    $out[-1] | Should -Be "Coverage source roots: 1 rewritten to $repo, 0 already here, 1 skipped."
  }

  It 'uses the default NuGet root when none is given' {
    $repo = New-Repo
    $file = New-MappingFile (Join-Path $repo 'tests' 'A.Tests' 'bin') 'A.Tests'
    $null = Invoke-UpdateCoverageSourceRoots -Root $repo -NuGetRoot ''
    (Read-MappingFile $file)[0] | Should -BeLike "*|$((Get-DefaultNuGetRoot).TrimEnd('/', '\'))?=/_1/"
  }
}

Describe 'The script run as CI runs it' {
  It 'rewrites the mappings under -Root and exits 0' {
    $repo = New-Repo
    $file = New-MappingFile (Join-Path $repo 'tests' 'A.Tests' 'bin') 'A.Tests'
    $output = pwsh -NoProfile -File $ScriptPath -Root $repo
    $LASTEXITCODE | Should -Be 0
    ($output -join "`n") | Should -BeLike '*1 rewritten*'
    (Read-MappingFile $file)[1] | Should -Not -BeLike '/home/runner/*'
  }
}
