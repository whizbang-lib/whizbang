# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# Since Testing Platform 2.5, `dotnet test` writes every module's results to TestResults under its own working
# directory, so a suite run from the repository root (Run-Tests.ps1) put every module's TRX and coverage in one
# directory and the CI uploads and the coverage gate, which look beside each module's DLL, found nothing. The run now
# asks for a per-module layout under a scratch directory and scripts/lib/ModuleTestResults.psm1 moves each module's
# results back beside its DLL. The first block pins the move; the last runs two real modules under `dotnet test`
# with those arguments, so a change in the platform's layout fails here rather than in CI.

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  Import-Module (Join-Path $Root 'scripts' 'lib' 'ModuleTestResults.psm1') -Force

  function New-File([string]$Path, [string]$Content = 'x') {
    New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($Path)) -Force | Out-Null
    Set-Content -LiteralPath $Path -Value $Content
  }
}

Describe 'Get-ModuleResultsArgument' {
  It 'asks for one directory per module under the results root' {
    (Get-ModuleResultsArgument -ResultsRoot '/r/TestResults/by-module') -join ' ' |
      Should -Be '--results-directory /r/TestResults/by-module --results-directory-layout per-module'
  }
}

Describe 'Move-ModuleTestResult' {
  BeforeEach {
    $script:Repo = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
    $script:Scratch = Join-Path $Repo 'TestResults' 'by-module'
    $script:DllA = Join-Path $Repo 'tests' 'A.Tests' 'bin' 'Release' 'net11.0' 'A.Tests.dll'
    $script:DllB = Join-Path $Repo 'samples' 'B' 'B.Tests' 'bin' 'Release' 'net11.0' 'B.Tests.dll'
    $script:DllA10 = Join-Path $Repo 'tests' 'A.Tests' 'bin' 'Release' 'net10.0' 'A.Tests.dll'
    foreach ($d in $DllA, $DllB, $DllA10) { New-File $d }
  }

  It 'moves each module''s results beside its own DLL, keeping their inner layout' {
    New-File (Join-Path $Scratch 'A.Tests' 'unknown_x64' 'A.Tests_net11.0_x64.trx')
    New-File (Join-Path $Scratch 'A.Tests' 'unknown_x64' 'g1.cobertura.xml')
    New-File (Join-Path $Scratch 'A.Tests' 'unknown_x64' 'g1.coverage')
    New-File (Join-Path $Scratch 'A.Tests' 'unknown_x64' '_host_2026' 'In' 'host' 'g1.coverage')
    New-File (Join-Path $Scratch 'B.Tests' 'unknown_arm64' 'B.Tests_net11.0_arm64.trx')

    $filled = Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA, $DllB

    $a = Join-Path ([System.IO.Path]::GetDirectoryName($DllA)) 'TestResults'
    $b = Join-Path ([System.IO.Path]::GetDirectoryName($DllB)) 'TestResults'
    foreach ($f in 'A.Tests_net11.0_x64.trx', 'g1.cobertura.xml', 'g1.coverage', (Join-Path '_host_2026' 'In' 'host' 'g1.coverage')) {
      Join-Path $a $f | Should -Exist
    }
    Join-Path $b 'B.Tests_net11.0_arm64.trx' | Should -Exist
    @($filled) | Should -Be @($a, $b)
    $Scratch | Should -Not -Exist -Because 'an emptied scratch directory is removed'
  }

  It 'tells a module''s target frameworks apart when the directories name them' {
    New-File (Join-Path $Scratch 'A.Tests' 'net10.0_x64' 'ten.trx')
    New-File (Join-Path $Scratch 'A.Tests' 'net11.0_x64' 'eleven.trx')
    Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA, $DllA10 | Out-Null
    Join-Path ([System.IO.Path]::GetDirectoryName($DllA10)) 'TestResults' 'ten.trx' | Should -Exist
    Join-Path ([System.IO.Path]::GetDirectoryName($DllA)) 'TestResults' 'eleven.trx' | Should -Exist
    Join-Path ([System.IO.Path]::GetDirectoryName($DllA)) 'TestResults' 'ten.trx' | Should -Not -Exist
  }

  # A module named by --test-modules has no framework the command knows, so both builds share unknown_<runtime>.
  It 'tells a module''s target frameworks apart by their TRX when the directory does not' {
    $dir = Join-Path $Scratch 'A.Tests' 'unknown_x64'
    foreach ($case in @(@{ Fw = 'net10.0'; Id = 'g10'; Run = '_host_10' }, @{ Fw = 'net11.0'; Id = 'g11'; Run = '_host_11' })) {
      New-File (Join-Path $dir "A.Tests_$($case.Fw)_x64.trx") (@"
<TestRun><TestSettings><Deployment runDeploymentRoot="$($case.Run)" /></TestSettings>
<ResultSummary><CollectorDataEntries><Collector><UriAttachments>
<UriAttachment><A href="host/$($case.Id).cobertura.xml" /></UriAttachment>
<UriAttachment><A href="host/$($case.Id).coverage" /></UriAttachment>
</UriAttachments></Collector></CollectorDataEntries></ResultSummary></TestRun>
"@)
      New-File (Join-Path $dir "$($case.Id).cobertura.xml")
      New-File (Join-Path $dir "$($case.Id).coverage")
      New-File (Join-Path $dir $case.Run 'In' 'host' "$($case.Id).coverage")
    }
    New-File (Join-Path $dir 'dotnet_7_hang.dmp')

    $filled = Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA, $DllA10 -WarningVariable warned -WarningAction SilentlyContinue

    $ten = Join-Path ([System.IO.Path]::GetDirectoryName($DllA10)) 'TestResults'
    $eleven = Join-Path ([System.IO.Path]::GetDirectoryName($DllA)) 'TestResults'
    foreach ($f in 'A.Tests_net10.0_x64.trx', 'g10.cobertura.xml', 'g10.coverage', (Join-Path '_host_10' 'In' 'host' 'g10.coverage')) { Join-Path $ten $f | Should -Exist }
    foreach ($f in 'A.Tests_net11.0_x64.trx', 'g11.cobertura.xml', 'g11.coverage', (Join-Path '_host_11' 'In' 'host' 'g11.coverage')) { Join-Path $eleven $f | Should -Exist }
    Join-Path $ten 'g11.coverage' | Should -Not -Exist
    Join-Path $eleven 'g10.coverage' | Should -Not -Exist
    @($filled) | Should -Be @($ten, $eleven)
    # A result no TRX claims cannot be placed; it goes where `dotnet test` writes it, which the suites upload.
    Join-Path $Repo 'TestResults' 'dotnet_7_hang.dmp' | Should -Exist
    ($warned -join "`n") | Should -Match 'more than one build of A\.Tests'
    $Scratch | Should -Not -Exist
  }

  It 'skips a TRX that names no module of the run' {
    $dir = Join-Path $Scratch 'A.Tests' 'unknown_x64'
    New-File (Join-Path $dir 'A.Tests_net9.0_x64.trx')
    Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA, $DllA10 -WarningAction SilentlyContinue | Out-Null
    Join-Path $Repo 'TestResults' 'A.Tests_net9.0_x64.trx' | Should -Exist
  }

  It 'moves a TRX with no deployment directory or attachments on its own' {
    $dir = Join-Path $Scratch 'A.Tests' 'unknown_x64'
    New-File (Join-Path $dir 'A.Tests_net11.0_x64.trx') '<TestRun />'
    New-File (Join-Path $dir 'A.Tests_net10.0_x64.trx') '<TestRun><Deployment runDeploymentRoot="_gone" /><A href="host/gone.coverage" /></TestRun>'
    Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA, $DllA10 | Out-Null
    Join-Path ([System.IO.Path]::GetDirectoryName($DllA)) 'TestResults' 'A.Tests_net11.0_x64.trx' | Should -Exist
    Join-Path ([System.IO.Path]::GetDirectoryName($DllA10)) 'TestResults' 'A.Tests_net10.0_x64.trx' | Should -Exist
    $Scratch | Should -Not -Exist
  }

  It 'accepts a target directory named for the framework alone' {
    New-File (Join-Path $Scratch 'A.Tests' 'net11.0' 'a.trx')
    Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA | Out-Null
    Join-Path ([System.IO.Path]::GetDirectoryName($DllA)) 'TestResults' 'a.trx' | Should -Exist
  }

  It 'replaces a file of the same name and keeps the module''s other results' {
    $dest = Join-Path ([System.IO.Path]::GetDirectoryName($DllA)) 'TestResults'
    New-File (Join-Path $dest 'A.Tests_net11.0_x64.trx') 'old'
    New-File (Join-Path $dest 'earlier.cobertura.xml') 'kept'
    New-File (Join-Path $Scratch 'A.Tests' 'unknown_x64' 'A.Tests_net11.0_x64.trx') 'new'
    Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA | Out-Null
    Get-Content -LiteralPath (Join-Path $dest 'A.Tests_net11.0_x64.trx') | Should -Be 'new'
    Get-Content -LiteralPath (Join-Path $dest 'earlier.cobertura.xml') | Should -Be 'kept'
  }

  It 'moves the platform''s merged reports, which belong to no module, to the parent' {
    New-File (Join-Path $Scratch 'merged' 'merged-1.trx')
    New-File (Join-Path $Scratch 'merged' 'Merged_TRX_report.1' 'In' '0' 'host' 'g.coverage')
    @(Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA).Count | Should -Be 0
    Join-Path $Repo 'TestResults' 'merged' 'merged-1.trx' | Should -Exist
    Join-Path $Repo 'TestResults' 'merged' 'Merged_TRX_report.1' 'In' '0' 'host' 'g.coverage' | Should -Exist
    $Scratch | Should -Not -Exist
  }

  It 'moves a file written outside any module''s directory to the parent, where `dotnet test` writes it' {
    New-File (Join-Path $Scratch 'dotnet_123_hang.dmp')
    Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA | Out-Null
    Join-Path $Repo 'TestResults' 'dotnet_123_hang.dmp' | Should -Exist
    $Scratch | Should -Not -Exist
  }

  It 'does nothing when the run wrote no results, and nothing more when called again' {
    @(Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA).Count | Should -Be 0
    New-File (Join-Path $Scratch 'A.Tests' 'unknown_x64' 'a.trx')
    Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA | Out-Null
    @(Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $DllA).Count | Should -Be 0
    Join-Path ([System.IO.Path]::GetDirectoryName($DllA)) 'TestResults' 'a.trx' | Should -Exist
  }

  It 'takes an empty module list' {
    New-File (Join-Path $Scratch 'A.Tests' 'unknown_x64' 'a.trx')
    @(Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath @()).Count | Should -Be 0
    Join-Path $Repo 'TestResults' 'A.Tests' 'unknown_x64' 'a.trx' | Should -Exist
  }
}

# The platform's own layout, not a copy of it: two real TUnit modules (the coverage shapes) run under one `dotnet test`
# with the arguments Run-Tests.ps1 passes, and each module's TRX, Cobertura and binary coverage must end up beside its
# own DLL, where the CI uploads and Test-CoverageCollected.ps1 look.
Describe 'Each module''s results land beside its DLL under dotnet test' {
  BeforeAll {
    $shapes = Join-Path $PSScriptRoot 'shapes'
    $script:Out = Join-Path ([System.IO.Path]::GetTempPath()) "whizbang-results-$([guid]::NewGuid().ToString('N'))"
    $script:Modules = @('CalcRunner', 'ShapesRunner' | ForEach-Object {
        $build = & dotnet build (Join-Path $shapes $_ "$_.csproj") -c Release "-p:ShapesOut=$Out/" -nologo -v q 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Building shapes/$_ failed:`n$($build -join "`n")" }
        Join-Path $Out 'bin' $_ 'Release' 'net11.0' "$_.dll"
      })
    $script:Scratch = Join-Path $Out 'TestResults' 'by-module'
    $script:Stray = Join-Path $shapes 'TestResults'
    if (Test-Path -LiteralPath $Stray) { Remove-Item -LiteralPath $Stray -Recurse -Force }

    # From the shapes directory, so the repository's global.json puts `dotnet test` on the testing platform.
    $psi = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    # --test-modules takes paths relative to the root directory.
    $relative = $Modules | ForEach-Object { [System.IO.Path]::GetRelativePath($Out, $_) }
    $arguments = @('test', '--root-directory', $Out, '--test-modules', ($relative -join ';'), '--report-trx', '--coverage', '--coverage-output-format', 'cobertura,coverage') +
      (Get-ModuleResultsArgument -ResultsRoot $Scratch)
    foreach ($a in $arguments) { $psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $shapes
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(300000)) { $process.Kill($true); $process.WaitForExit() }
    $script:ExitCode = $process.ExitCode
    $script:Output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    $script:Filled = Move-ModuleTestResult -ResultsRoot $Scratch -ModulePath $Modules
  }

  AfterAll {
    if ($Out -and (Test-Path -LiteralPath $Out)) { Remove-Item -LiteralPath $Out -Recurse -Force }
    if ($Stray -and (Test-Path -LiteralPath $Stray)) { Remove-Item -LiteralPath $Stray -Recurse -Force }
  }

  It 'runs both modules' {
    $ExitCode | Should -Be 0 -Because $Output
  }

  It 'puts <_>''s TRX, Cobertura and binary coverage directly in the TestResults beside its DLL' -ForEach 'CalcRunner', 'ShapesRunner' {
    $results = Join-Path $Out 'bin' $_ 'Release' 'net11.0' 'TestResults'
    @(Get-ChildItem -LiteralPath $results -Filter '*.trx' -File -ErrorAction SilentlyContinue).Count | Should -Be 1
    @(Get-ChildItem -LiteralPath $results -Filter '*.cobertura.xml' -File -ErrorAction SilentlyContinue).Count | Should -Be 1
    @(Get-ChildItem -LiteralPath $results -Filter '*.coverage' -File -ErrorAction SilentlyContinue).Count | Should -Be 1
  }

  It 'leaves nothing in the scratch directory and nothing in the working directory' {
    @($Filled).Count | Should -Be 2
    $Scratch | Should -Not -Exist
    @(Get-ChildItem -LiteralPath $Stray -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object FullName) | Should -BeNullOrEmpty
  }
}
