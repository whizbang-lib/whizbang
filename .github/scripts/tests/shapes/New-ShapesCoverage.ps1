#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the coverage gate's shapes libraries and runs each as two test processes under the real
    collector, so Find-UncoveredNewLines.Tests.ps1 tests the gate against coverage produced now.

.DESCRIPTION
    No coverage report is ever committed: a stored report proves only what some collector once wrote, and
    the gate exists to read what the collector writes today. This script produces the inputs instead, with
    exactly what CI's test suites use: the repository's codecoverage.config and
    `--coverage --coverage-output-format cobertura,coverage`, from the TUnit and collector versions in the
    repository's Directory.Packages.props. It then merges each pair of binary reports with dotnet-coverage,
    the tool and version the quality job uses.

    Two runners, two processes each (A and B, selected by test class):
    - CalcRunner exercises src/Whizbang.Exp/Calc.cs: each process takes one outcome of the same if.
    - ShapesRunner exercises src/Whizbang.Shapes/Shapes.cs: the statement shapes listed in that file's
      runner (ShapeRuns.cs) and in the tests.

    Output under -OutDir:
        build/                          the projects' build output
        CalcRunner/A, CalcRunner/B      one Cobertura and one binary report per process
        ShapesRunner/A, ShapesRunner/B
        CalcRunner.blocks.xml           `dotnet-coverage merge <both binaries> -f xml`
        ShapesRunner.blocks.xml

    Anything missing fails loudly: no dotnet, a failed build or run, no dotnet-coverage, or a report that
    measured nothing. The last is what the collector writes where it cannot instrument (its dynamic
    instrumentation does not initialize on macOS): such a report would make every test here meaningless,
    so it is an error, not a skip. Run the tests on Linux, as CI does (a dotnet/sdk container works).

.PARAMETER OutDir
    An empty or missing directory for the output.

.PARAMETER DotnetCoverage
    The dotnet-coverage command; defaults to the one on PATH.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$OutDir,
  [string]$DotnetCoverage = 'dotnet-coverage'
)

$ErrorActionPreference = 'Stop'

foreach ($tool in @('dotnet', $DotnetCoverage)) {
  if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
    throw "'$tool' is not on PATH. The coverage gate's tests build and run their shapes under the real collector and merge its binary reports with dotnet-coverage (dotnet tool install --global dotnet-coverage --version 18.12.0)."
  }
}

$shapes = $PSScriptRoot
$settings = (Resolve-Path (Join-Path $shapes '../../../../codecoverage.config')).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$build = Join-Path $OutDir 'build'

$log = & dotnet build (Join-Path $shapes 'shapes.slnx') --configuration Release --nologo --verbosity quiet "-p:ShapesOut=$build/" 2>&1
if ($LASTEXITCODE -ne 0) { throw "Building the coverage shapes failed (exit $LASTEXITCODE):`n$($log -join "`n")" }

foreach ($runner in @('CalcRunner', 'ShapesRunner')) {
  $dll = Join-Path $build "bin/$runner/Release/net10.0/$runner.dll"
  $binaries = @()
  foreach ($process in @('A', 'B')) {
    $results = Join-Path $OutDir "$runner/$process"
    $log = & dotnet $dll --treenode-filter "/*/*/$process/*" --coverage --coverage-output-format 'cobertura,coverage' --coverage-settings $settings --results-directory $results 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$runner process $process failed (exit $LASTEXITCODE):`n$($log -join "`n")" }
    $report = @(Get-ChildItem -Path $results -Filter '*.cobertura.xml' -File)
    $binary = @(Get-ChildItem -Path $results -Filter '*.coverage' -File)
    if ($report.Count -ne 1 -or $binary.Count -ne 1) {
      throw "$runner process $process wrote $($report.Count) Cobertura and $($binary.Count) binary report(s) under '$results'; expected one of each."
    }
    if (-not (Select-String -Path $report[0].FullName -Pattern '<package ' -Quiet)) {
      throw "The collector measured nothing in $runner process $process ('$($report[0].FullName)' has no package). It cannot instrument on this platform (on macOS its dynamic instrumentation does not initialize); run these tests on Linux, as CI does."
    }
    $binaries += $binary[0].FullName
  }
  $blocks = Join-Path $OutDir "$runner.blocks.xml"
  $log = & $DotnetCoverage merge @binaries --output-format xml --output $blocks --nologo --disable-console-output 2>&1
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $blocks)) { throw "'$DotnetCoverage merge' failed for $runner (exit $LASTEXITCODE):`n$($log -join "`n")" }
}
