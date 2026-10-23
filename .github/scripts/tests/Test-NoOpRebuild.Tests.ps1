#Requires -Modules Pester

# A build with nothing changed must do no work. When it does, every contributor and every CI run pays for a
# full recompile of the projects above the one that did the work. These tests pin how the check reads a
# replayed build log: which targets ran, in which project, and the input that made them run.

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/build-cost/Test-NoOpRebuild.ps1') -Project x

  function New-Log([string[]]$lines) {
    $path = Join-Path ([System.IO.Path]::GetTempPath()) "noop-$([guid]::NewGuid()).log"
    Set-Content -Path $path -Value $lines
    return $path
  }

  function Get-TargetLine([string]$target, [string]$project) {
    "   2:2>Target `"$target`" in file `"/sdk/Microsoft.CSharp.Core.targets`" from project `"$project`" (target `"Compile`" depends on it):"
  }
}

Describe 'Get-NoOpRebuildWork' {
  It 'reports nothing when every watched target was skipped' {
    $log = New-Log @(
      (Get-TargetLine 'CoreCompile' '/repo/src/A/A.csproj')
      '       Skipping target "CoreCompile" because all output files are up-to-date with respect to the input files.'
      (Get-TargetLine 'MergeSharedAssembly' '/repo/src/G/G.csproj')
      '       Skipping target "MergeSharedAssembly" because all output files are up-to-date with respect to the input files.'
    )
    @(Get-NoOpRebuildWork -LogPath $log) | Should -HaveCount 0
  }

  It 'reports a target that built, with its project and the input that was newer' {
    $log = New-Log @(
      (Get-TargetLine 'CoreCompile' '/repo/src/Core/Core.csproj')
      '       Building target "CoreCompile" completely.'
      '       Input file "obj/Debug/net10.0/Version.g.cs" is newer than output file "obj/Debug/net10.0/Core.xml".'
    )
    $work = @(Get-NoOpRebuildWork -LogPath $log)
    $work | Should -HaveCount 1
    $work[0].Target | Should -Be 'CoreCompile'
    $work[0].Project | Should -Be '/repo/src/Core/Core.csproj'
    $work[0].Reason | Should -Be 'obj/Debug/net10.0/Version.g.cs is newer than obj/Debug/net10.0/Core.xml'
  }

  It 'reports a partial build as work' {
    $log = New-Log @(
      (Get-TargetLine 'CoreCompile' '/repo/src/A/A.csproj')
      '       Building target "CoreCompile" partially, because some output files are out of date with respect to their input files.'
    )
    @(Get-NoOpRebuildWork -LogPath $log) | Should -HaveCount 1
  }

  It 'reports a watched target with no inputs and outputs, which always runs' {
    $log = New-Log @(
      (Get-TargetLine 'MergeSharedAssembly' '/repo/src/G/G.csproj')
      '       ILRepack: Merging Shared.dll into G.dll'
    )
    $work = @(Get-NoOpRebuildWork -LogPath $log)
    $work | Should -HaveCount 1
    $work[0].Target | Should -Be 'MergeSharedAssembly'
    $work[0].Reason | Should -Be 'the target ran without an up-to-date check'
  }

  It 'ignores targets it was not asked to watch' {
    $log = New-Log @(
      (Get-TargetLine 'GenerateVersionInfo' '/repo/src/Core/Core.csproj')
      '       Building target "GenerateVersionInfo" completely.'
    )
    @(Get-NoOpRebuildWork -LogPath $log) | Should -HaveCount 0
  }

  It 'keeps the projects apart when several build' {
    $log = New-Log @(
      (Get-TargetLine 'CoreCompile' '/repo/src/A/A.csproj')
      '       Building target "CoreCompile" completely.'
      '       Input file "a.cs" is newer than output file "a.dll".'
      (Get-TargetLine 'CoreCompile' '/repo/src/B/B.csproj')
      '       Skipping target "CoreCompile" because all output files are up-to-date with respect to the input files.'
      (Get-TargetLine 'CoreCompile' '/repo/src/C/C.csproj')
      '       Building target "CoreCompile" completely.'
      '       Input file "c.cs" is newer than output file "c.dll".'
    )
    (Get-NoOpRebuildWork -LogPath $log).Project | Should -Be @('/repo/src/A/A.csproj', '/repo/src/C/C.csproj')
  }

  # The generators package's targets run in every consumer's build; one with no Inputs and Outputs runs on a
  # no-op rebuild too (#1665).
  It 'watches the shipped generator targets by default, in the check and in the script' {
    $shipped = @('WhizbangExtractMessageRegistry', 'WhizbangExtractPinnedTypeLedger')
    $log = New-Log @(
      foreach ($t in $shipped) {
        (Get-TargetLine $t '/repo/src/Core/Core.csproj')
        '       Whizbang: Message registry extracted'
      }
    )
    @(Get-NoOpRebuildWork -LogPath $log | ForEach-Object Target) | Should -Be $shipped

    $script = Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/build-cost/Test-NoOpRebuild.ps1'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
    $default = $ast.ParamBlock.Parameters.Where({ $_.Name.VariablePath.UserPath -eq 'Targets' })[0].DefaultValue.SafeGetValue()
    $default | Should -Contain $shipped[0]
    $default | Should -Contain $shipped[1]
  }
}
