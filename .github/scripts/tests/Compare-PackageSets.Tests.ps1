# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The reproducible-build check (#2484): two builds of one commit must produce byte-identical packages. Each
# test names a way two package sets can differ that the check must report, or a way they can match that it
# must not flag.

BeforeAll {
  $script:Script = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Compare-PackageSets.ps1'
  . $script:Script -Left x -Right y
  Add-Type -AssemblyName System.IO.Compression

  # Writes a zip whose entries are (name, text) pairs in the given order, each stamped with $Time.
  function New-Package([string]$Path, [object[]]$Entries, [datetimeoffset]$Time = [datetimeoffset]'2000-01-01T00:00:00Z') {
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path)
    $stream = [System.IO.File]::Create($Path)
    try {
      $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
      try {
        foreach ($e in $Entries) {
          $entry = $zip.CreateEntry($e[0])
          $entry.LastWriteTime = $Time
          $writer = [System.IO.StreamWriter]::new($entry.Open())
          try { $writer.Write($e[1]) } finally { $writer.Dispose() }
        }
      } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
  }
}

Describe 'Compare-Package' {
  It 'reports nothing for two packages with the same entries, bytes, order and times' {
    $a = Join-Path $TestDrive 'same/a/P.1.0.0.nupkg'; $b = Join-Path $TestDrive 'same/b/P.1.0.0.nupkg'
    New-Package $a @(@('lib/P.dll', 'il'), @('P.nuspec', 'spec'))
    New-Package $b @(@('lib/P.dll', 'il'), @('P.nuspec', 'spec'))
    @(Compare-Package -LeftPath $a -RightPath $b).Count | Should -Be 0
  }

  It 'names the entry whose bytes differ' {
    $a = Join-Path $TestDrive 'content/a/P.1.0.0.nupkg'; $b = Join-Path $TestDrive 'content/b/P.1.0.0.nupkg'
    New-Package $a @(@('lib/P.dll', 'il-1'), @('P.nuspec', 'spec'))
    New-Package $b @(@('lib/P.dll', 'il-2'), @('P.nuspec', 'spec'))
    $d = @(Compare-Package -LeftPath $a -RightPath $b)
    $d.Count | Should -Be 1
    $d[0].Package | Should -Be 'P.1.0.0.nupkg'
    $d[0].Entry | Should -Be 'lib/P.dll'
    $d[0].Difference | Should -Be 'content'
  }

  It 'reports an entry present in only one package, on the side that has it' {
    $a = Join-Path $TestDrive 'only/a/P.1.0.0.nupkg'; $b = Join-Path $TestDrive 'only/b/P.1.0.0.nupkg'
    New-Package $a @(@('lib/P.dll', 'il'), @('package/services/metadata/core-properties/aaa.psmdcp', 'x'))
    New-Package $b @(@('lib/P.dll', 'il'), @('package/services/metadata/core-properties/bbb.psmdcp', 'x'))
    $d = @(Compare-Package -LeftPath $a -RightPath $b)
    ($d | Where-Object Entry -eq 'package/services/metadata/core-properties/aaa.psmdcp').Difference | Should -Be 'only in left'
    ($d | Where-Object Entry -eq 'package/services/metadata/core-properties/bbb.psmdcp').Difference | Should -Be 'only in right'
  }

  It 'reports a timestamp difference even when every byte of content matches' {
    $a = Join-Path $TestDrive 'time/a/P.1.0.0.nupkg'; $b = Join-Path $TestDrive 'time/b/P.1.0.0.nupkg'
    New-Package $a @(, @('lib/P.dll', 'il')) -Time ([datetimeoffset]'2000-01-01T00:00:00Z')
    New-Package $b @(, @('lib/P.dll', 'il')) -Time ([datetimeoffset]'2024-06-01T12:00:00Z')
    $d = @(Compare-Package -LeftPath $a -RightPath $b)
    $d.Count | Should -Be 1
    $d[0].Entry | Should -Be 'lib/P.dll'
    $d[0].Difference | Should -Be 'timestamp'
  }

  It 'reports the order of entries when the same entries are written in a different order' {
    $a = Join-Path $TestDrive 'order/a/P.1.0.0.nupkg'; $b = Join-Path $TestDrive 'order/b/P.1.0.0.nupkg'
    New-Package $a @(@('lib/P.dll', 'il'), @('P.nuspec', 'spec'))
    New-Package $b @(@('P.nuspec', 'spec'), @('lib/P.dll', 'il'))
    $d = @(Compare-Package -LeftPath $a -RightPath $b)
    $d.Count | Should -Be 1
    $d[0].Entry | Should -Be ''
    $d[0].Difference | Should -Be 'entry order'
  }
}

Describe 'Compare-PackageSet' {
  It 'reports nothing when every package in both directories is identical' {
    New-Package (Join-Path $TestDrive 'set-same/a/P.1.0.0.nupkg') @(, @('lib/P.dll', 'il'))
    New-Package (Join-Path $TestDrive 'set-same/b/P.1.0.0.nupkg') @(, @('lib/P.dll', 'il'))
    New-Package (Join-Path $TestDrive 'set-same/a/P.1.0.0.snupkg') @(, @('lib/P.pdb', 'pdb'))
    New-Package (Join-Path $TestDrive 'set-same/b/P.1.0.0.snupkg') @(, @('lib/P.pdb', 'pdb'))
    @(Compare-PackageSet -Left (Join-Path $TestDrive 'set-same/a') -Right (Join-Path $TestDrive 'set-same/b')).Count | Should -Be 0
  }

  It 'compares symbol packages as well as packages' {
    New-Package (Join-Path $TestDrive 'set-sym/a/P.1.0.0.snupkg') @(, @('lib/P.pdb', 'pdb-1'))
    New-Package (Join-Path $TestDrive 'set-sym/b/P.1.0.0.snupkg') @(, @('lib/P.pdb', 'pdb-2'))
    $d = @(Compare-PackageSet -Left (Join-Path $TestDrive 'set-sym/a') -Right (Join-Path $TestDrive 'set-sym/b'))
    $d.Count | Should -Be 1
    $d[0].Package | Should -Be 'P.1.0.0.snupkg'
    $d[0].Entry | Should -Be 'lib/P.pdb'
  }

  It 'reports a package that only one build produced' {
    New-Package (Join-Path $TestDrive 'set-only/a/P.1.0.0.nupkg') @(, @('lib/P.dll', 'il'))
    New-Package (Join-Path $TestDrive 'set-only/b/P.1.0.0.nupkg') @(, @('lib/P.dll', 'il'))
    New-Package (Join-Path $TestDrive 'set-only/b/Q.1.0.0.nupkg') @(, @('lib/Q.dll', 'il'))
    $d = @(Compare-PackageSet -Left (Join-Path $TestDrive 'set-only/a') -Right (Join-Path $TestDrive 'set-only/b'))
    $d.Count | Should -Be 1
    $d[0].Package | Should -Be 'Q.1.0.0.nupkg'
    $d[0].Difference | Should -Be 'package only in right'
  }

  It 'refuses two empty directories, since comparing nothing proves nothing' {
    $null = New-Item -ItemType Directory -Force -Path (Join-Path $TestDrive 'empty/a'), (Join-Path $TestDrive 'empty/b')
    { Compare-PackageSet -Left (Join-Path $TestDrive 'empty/a') -Right (Join-Path $TestDrive 'empty/b') } | Should -Throw '*No packages*'
  }
}

Describe 'the script' {
  It 'exits 0 and says how many packages matched when the sets are identical' {
    New-Package (Join-Path $TestDrive 'run-same/a/P.1.0.0.nupkg') @(, @('lib/P.dll', 'il'))
    New-Package (Join-Path $TestDrive 'run-same/b/P.1.0.0.nupkg') @(, @('lib/P.dll', 'il'))
    $out = & pwsh -NoProfile -File $script:Script -Left (Join-Path $TestDrive 'run-same/a') -Right (Join-Path $TestDrive 'run-same/b')
    $LASTEXITCODE | Should -Be 0
    ($out -join "`n") | Should -BeLike '*1 package*identical*'
  }

  It 'exits 1 and names each differing entry as an error when the sets differ' {
    New-Package (Join-Path $TestDrive 'run-diff/a/P.1.0.0.nupkg') @(, @('lib/P.dll', 'il-1'))
    New-Package (Join-Path $TestDrive 'run-diff/b/P.1.0.0.nupkg') @(, @('lib/P.dll', 'il-2'))
    $out = & pwsh -NoProfile -File $script:Script -Left (Join-Path $TestDrive 'run-diff/a') -Right (Join-Path $TestDrive 'run-diff/b')
    $LASTEXITCODE | Should -Be 1
    ($out -join "`n") | Should -BeLike '*::error::*P.1.0.0.nupkg*lib/P.dll*content*'
  }

  It 'extracts both sides of each differing entry when asked, for a byte-level diff' {
    New-Package (Join-Path $TestDrive 'run-x/a/P.1.0.0.nupkg') @(, @('lib/P.dll', 'il-1'))
    New-Package (Join-Path $TestDrive 'run-x/b/P.1.0.0.nupkg') @(, @('lib/P.dll', 'il-2'))
    $x = Join-Path $TestDrive 'run-x/extract'
    $null = & pwsh -NoProfile -File $script:Script -Left (Join-Path $TestDrive 'run-x/a') -Right (Join-Path $TestDrive 'run-x/b') -ExtractTo $x
    Get-Content -Raw (Join-Path $x 'left/P.1.0.0.nupkg/lib/P.dll') | Should -Be 'il-1'
    Get-Content -Raw (Join-Path $x 'right/P.1.0.0.nupkg/lib/P.dll') | Should -Be 'il-2'
  }
}
