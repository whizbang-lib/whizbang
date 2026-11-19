# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Select-PullRequestFinding.ps1')
}

Describe 'Select-PullRequestFindingLine' {
  It 'keeps the findings in files the pull request changed and drops the rest' {
    $lines = @(
      'src/A/One.cs:12: var x = y;'
      'src/B/Two.cs:3: (1/2 conditions) if (a) {'
      'src/A/One.cs:40: return z;'
    )
    Select-PullRequestFindingLine -Line $lines -File @('src/A/One.cs') | Should -Be @('src/A/One.cs:12: var x = y;', 'src/A/One.cs:40: return z;')
  }

  It 'keeps a Sonar finding about a whole file, which has no line number' {
    Select-PullRequestFindingLine -Line @('src/A/One.cs:: S1128 Remove this unused using.') -File @('src/A/One.cs') | Should -Be @('src/A/One.cs:: S1128 Remove this unused using.')
  }

  It 'matches the whole path, not a prefix or a suffix' {
    $lines = @('src/A/One.cs.bak:1: x', 'other/src/A/One.cs:1: x', 'src/A/One.c:1: x')
    @(Select-PullRequestFindingLine -Line $lines -File @('src/A/One.cs')) | Should -BeNullOrEmpty
  }

  It 'returns nothing for no findings, and drops blank lines' {
    @(Select-PullRequestFindingLine -Line @() -File @('src/A/One.cs')) | Should -BeNullOrEmpty
    @(Select-PullRequestFindingLine -Line @('', '  ') -File @('src/A/One.cs')) | Should -BeNullOrEmpty
  }

  It 'drops every finding when the pull request changed no file' {
    @(Select-PullRequestFindingLine -Line @('src/A/One.cs:1: x') -File @()) | Should -BeNullOrEmpty
  }
}

Describe 'the script' {
  It 'rewrites each findings file in place, keeps a missing one missing, and writes an empty one as empty' {
    $files = Join-Path $TestDrive 'files.txt'
    Set-Content -Path $files -Value @('src/A/One.cs', 'docs/x.md')
    $lines = Join-Path $TestDrive 'lines.txt'
    Set-Content -Path $lines -Value @('src/A/One.cs:1: a', 'src/B/Two.cs:2: b')
    $sonar = Join-Path $TestDrive 'sonar.txt'
    Set-Content -Path $sonar -Value @('src/B/Two.cs:2: S1 m')
    $missing = Join-Path $TestDrive 'missing.txt'
    & (Join-Path $PSScriptRoot '..' 'Select-PullRequestFinding.ps1') -FileList $files -Path @($lines, $sonar, $missing) | Out-Null
    $LASTEXITCODE | Should -Be 0
    @(Get-Content $lines) | Should -Be @('src/A/One.cs:1: a')
    (Test-Path $sonar) | Should -BeTrue -Because 'an empty list is the evidence of none, and a missing file reads as "the gate did not run"'
    @(Get-Content $sonar | Where-Object { $_ }) | Should -BeNullOrEmpty
    (Test-Path $missing) | Should -BeFalse
  }

  It 'fails when the list of the pull request''s files cannot be read' {
    & (Join-Path $PSScriptRoot '..' 'Select-PullRequestFinding.ps1') -FileList (Join-Path $TestDrive 'nope.txt') -Path @() 2>&1 | Out-Null
    $LASTEXITCODE | Should -Be 1
  }
}
