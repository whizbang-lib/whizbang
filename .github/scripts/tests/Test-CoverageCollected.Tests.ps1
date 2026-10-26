# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# An empty Cobertura report (`<packages />`) is what the collector writes when it skipped every assembly, and
# the tests still pass, so a suite's upload alone cannot tell it from real coverage. Test-CoverageCollected.ps1
# warns per empty report and fails when the whole suite measured nothing. It replaced a bash step so the
# suites can run on any runner OS.

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Test-CoverageCollected.ps1'
  . $ScriptPath

  $script:Measured = '<?xml version="1.0"?><coverage><packages><package name="Whizbang.Core" /></packages></coverage>'
  $script:Empty = '<?xml version="1.0"?><coverage><packages /></coverage>'

  function New-Repo {
    $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    $root
  }

  function New-Report {
    param([string]$Root, [string[]]$RelativeDirectory, [string]$Content, [string]$Name = "$([guid]::NewGuid().ToString('N')).cobertura.xml")
    $dir = Join-Path $Root @RelativeDirectory
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $path = Join-Path $dir $Name
    Set-Content -LiteralPath $path -Value $Content
    $path
  }
}

Describe 'Get-CoverageReportVerdict' {
  It 'counts the reports directly under a TestResults folder in tests and samples, and those that measured code' {
    $repo = New-Repo
    $null = New-Report $repo @('tests', 'A.Tests', 'bin', 'Release', 'net10.0', 'TestResults') $Measured
    $empty = New-Report $repo @('samples', 'S', 'S.Tests', 'bin', 'Release', 'net10.0', 'TestResults') $Empty
    $null = New-Report $repo @('tests', 'A.Tests', 'bin', 'Release', 'net10.0', 'TestResults', 'run', 'In', 'host') $Measured  # TRX's copy
    $null = New-Report $repo @('src', 'X', 'TestResults') $Measured                                                              # not a test tree
    $null = New-Report $repo @('tests', 'A.Tests', 'bin') $Measured                                                              # not under TestResults
    $v = Get-CoverageReportVerdict -Root $repo
    $v.Reports | Should -Be 2
    $v.Measured | Should -Be 1
    $v.Empty | Should -Be @($empty)
  }

  It 'finds nothing in a tree with no reports' {
    $v = Get-CoverageReportVerdict -Root (New-Repo)
    $v.Reports | Should -Be 0
    $v.Measured | Should -Be 0
  }
}

Describe 'Invoke-TestCoverageCollected' {
  It 'passes, warning for each empty report, when any report measured code' {
    $repo = New-Repo
    $null = New-Report $repo @('tests', 'A.Tests', 'TestResults') $Measured
    $empty = New-Report $repo @('tests', 'B.Tests', 'TestResults') $Empty
    $out = @(Invoke-TestCoverageCollected -Root $repo 6>&1)
    $out[-1] | Should -Be 0
    ($out -join "`n") | Should -BeLike "*::warning::empty coverage report: $empty*"
    ($out -join "`n") | Should -BeLike '*1 of 2 coverage reports measured library code*'
  }

  It 'fails when no report measured code (<Case>)' -ForEach @(
    @{ Case = 'all empty'; Reports = 1 }
    @{ Case = 'no reports'; Reports = 0 }
  ) {
    $repo = New-Repo
    if ($Reports) { $null = New-Report $repo @('tests', 'A.Tests', 'TestResults') $Empty }
    $out = @(Invoke-TestCoverageCollected -Root $repo 6>&1)
    $out[-1] | Should -Be 1
    ($out -join "`n") | Should -BeLike '*::error::No coverage report measured any library assembly*'
  }
}

Describe 'The script run as CI runs it' {
  It 'exits 0 when coverage was collected and 1 when it was not' {
    $good = New-Repo
    $null = New-Report $good @('tests', 'A.Tests', 'TestResults') $Measured
    pwsh -NoProfile -File $ScriptPath -Root $good | Out-Null
    $LASTEXITCODE | Should -Be 0
    pwsh -NoProfile -File $ScriptPath -Root (New-Repo) | Out-Null
    $LASTEXITCODE | Should -Be 1
  }
}
