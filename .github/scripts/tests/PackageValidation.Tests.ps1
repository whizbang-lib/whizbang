# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# Package validation can fail CI (#2605).
#
# Packing a library compares its public API with the published baseline (#1439, Directory.Build.targets). Three things
# kept that comparison from ever failing a build:
#   - the build artifact left out obj/Release/**/ref/**, so every packed library stopped on MSB4018 (the reference
#     assembly validation reads was not there);
#   - pull request and queue runs stamped 0.0.0-*, assembly version 0.0.0.0, below the baseline's, so every library
#     reported an assembly identity change (CP0003);
#   - the pack step ended in `|| true`, which hid both.
# These tests read the workflows themselves, so none of the three can come back unnoticed.
#
# Owner: the Upload Build Artifacts step of .github/workflows/reusable-build.yml, the Placeholder version step of the
# changes job in .github/workflows/ci.yml, and the pack steps of reusable-pack.yml and nuget-publish.yml.

BeforeAll {
  $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
  $script:Workflows = Join-Path $Root '.github' 'workflows'
  $script:CiLines = Get-Content -LiteralPath (Join-Path $Workflows 'ci.yml')

  # Several tests put a stand-in dotnet first on PATH and start pwsh. Where pwsh is a .NET tool, its launcher would
  # find the stand-in and run it instead of pwsh, so pwsh is started the way this one was: by its own host.
  $hostPath = [Environment]::ProcessPath
  $script:PwshCommand = @(if ((Split-Path -Leaf $hostPath) -match '^dotnet(\.exe)?$') { $hostPath; Join-Path $PSHOME 'pwsh.dll' } else { $hostPath })
  function script:Invoke-Pwsh {
    & $PwshCommand[0] @($PwshCommand | Select-Object -Skip 1) @args
  }

  # The baseline every library is compared with, read from the MSBuild file itself.
  $targets = [xml](Get-Content -Raw -LiteralPath (Join-Path $Root 'Directory.Build.targets'))
  $script:Baseline = @($targets.Project.PropertyGroup.PackageValidationBaselineVersion |
      Where-Object { $_ } | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.'#text' } })

  # The lines of one job of ci.yml: from its key at two spaces under `jobs:` to the next job's key.
  function script:Get-JobLines([string[]]$Lines, [string]$Job) {
    $start = [array]::IndexOf($Lines, "  ${Job}:")
    if ($start -lt 0) { throw "ci.yml has no job '$Job'." }
    $end = $start + 1
    while ($end -lt $Lines.Count -and $Lines[$end] -notmatch '^  [A-Za-z0-9_-]+:\s*$' -and $Lines[$end] -notmatch '^\S') { $end++ }
    return $Lines[$start..($end - 1)]
  }

  # One step of a list of lines, by name: its lines up to the next step.
  function script:Get-StepLines([string[]]$Lines, [string]$Name) {
    $start = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) { if ($Lines[$i] -match "^\s+- name: $([regex]::Escape($Name))\s*$") { $start = $i; break } }
    if ($start -lt 0) { throw "No step named '$Name'." }
    $indent = ($Lines[$start] -replace '^(\s+)-.*$', '$1')
    $end = $start + 1
    while ($end -lt $Lines.Count -and -not ($Lines[$end] -match "^$indent- " -or ($Lines[$end] -match '^\S'))) {
      if ($Lines[$end] -match '^\s*\S' -and ($Lines[$end] -replace '^(\s*).*$', '$1').Length -lt $indent.Length) { break }
      $end++
    }
    return $Lines[$start..($end - 1)]
  }

  # The `run: |` block of a step, dedented.
  function script:Get-StepScript([string[]]$StepLines) {
    $script = @()
    for ($i = 0; $i -lt $StepLines.Count; $i++) {
      if ($StepLines[$i] -match '^(\s+)run: \|') {
        $bodyIndent = $Matches[1] + '  '
        while ($i + 1 -lt $StepLines.Count -and ($StepLines[$i + 1].StartsWith($bodyIndent) -or $StepLines[$i + 1] -match '^\s*$')) {
          $script += $StepLines[$i + 1] -replace "^$bodyIndent", ''; $i++
        }
      }
    }
    return ($script -join "`n")
  }

  # Runs the Placeholder version step's script in a directory, with the given event, and returns its exit code and
  # the lines it wrote to GITHUB_OUTPUT.
  function script:Invoke-PlaceholderStep([string]$Directory, [string]$EventName, [string]$PrNumber) {
    $body = Get-StepScript (Get-StepLines (Get-JobLines $CiLines 'changes') 'Placeholder version')
    $scriptFile = Join-Path $TestDrive 'placeholder.sh'
    $outputFile = Join-Path $TestDrive 'placeholder-output.txt'
    Set-Content -LiteralPath $scriptFile -Value $body
    Set-Content -LiteralPath $outputFile -Value $null
    $env:EVENT = $EventName; $env:PR_NUMBER = $PrNumber; $env:RUN_NUMBER = '42'; $env:GITHUB_OUTPUT = $outputFile
    Push-Location $Directory
    try {
      bash $scriptFile 2>&1 | Out-Null
      $code = $LASTEXITCODE
    } finally {
      Pop-Location
      Remove-Item Env:EVENT, Env:PR_NUMBER, Env:RUN_NUMBER, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue
    }
    return [pscustomobject]@{ ExitCode = $code; Output = @(Get-Content -LiteralPath $outputFile | Where-Object { $_ }) }
  }
}

Describe 'The build artifact carries what pack validates (#2605)' {
  BeforeAll {
    $script:Upload = Get-StepLines (Get-Content -LiteralPath (Join-Path $Workflows 'reusable-build.yml')) 'Upload Build Artifacts'
  }

  It 'uploads the packable projects'' intermediates, so the rule below is not vacuous' {
    $Upload | Should -Contain '            src/**/obj/Release'
    $Upload | Should -Contain '            tools/**/obj/Release'
  }

  It 'keeps the reference assemblies, which package validation reads from each framework''s obj/Release folder' {
    ($Upload | Where-Object { $_ -match '^\s+!.*/ref/' }) | Should -BeNullOrEmpty -Because 'without them every library stops on MSB4018 and its API is never compared'
  }
}

Describe 'Pull request and queue runs stamp a version validation can compare (#2605)' {
  It 'reads exactly one baseline from Directory.Build.targets' {
    $Baseline.Count | Should -Be 1
    $Baseline[0] | Should -Match '^\d+\.\d+\.\d+$'
  }

  It 'gives Compile and Pack the placeholder from Plan · Detect changes, never a 0.0.0 one' {
    foreach ($job in 'build', 'pack') {
      $versionLine = @(Get-JobLines $CiLines $job | Where-Object { $_ -match '^      version:' })
      $versionLine.Count | Should -Be 1 -Because "$job passes one version"
      $versionLine[0] | Should -Not -Match '0\.0\.0' -Because 'assembly version 0.0.0.0 is below the baseline, which ApiCompat reports as CP0003 in every library'
      $versionLine[0] | Should -Match 'needs\.version\.outputs\.semver \|\| needs\.changes\.outputs\.placeholder-version'
    }
  }

  It 'makes Pack wait for the job that computes the placeholder' {
    $needs = @(Get-JobLines $CiLines 'pack' | Where-Object { $_ -match '^    needs:' })
    $needs[0] | Should -Match '\bchanges\b'
  }

  It 'exposes the placeholder as an output of Plan · Detect changes' {
    (Get-JobLines $CiLines 'changes') | Should -Contain '      placeholder-version: ${{ steps.placeholder.outputs.version }}'
  }

  It 'stamps <Event> runs with the baseline''s own version and a label' -Skip:$IsWindows -TestCases @(
    @{ Event = 'merge_group'; Pr = ''; Label = 'queue.42' }
    @{ Event = 'pull_request'; Pr = '2605'; Label = 'pr2605.42' }
    @{ Event = 'workflow_dispatch'; Pr = ''; Label = 'ci.42' }
  ) {
    param($Event, $Pr, $Label)
    $result = Invoke-PlaceholderStep $Root $Event $Pr
    $result.ExitCode | Should -Be 0
    $result.Output | Should -Be @("version=$($Baseline[0])-$Label")
  }

  It 'gives an assembly version at the baseline, so ApiCompat sees no identity change' -Skip:$IsWindows {
    $version = ((Invoke-PlaceholderStep $Root 'merge_group' '').Output[0] -replace '^version=', '')
    $assemblyVersion = [version](($version -split '-', 2)[0])
    $assemblyVersion | Should -BeGreaterOrEqual ([version]$Baseline[0])
  }

  It 'fails, rather than stamping a version of its own, when it finds no baseline' -Skip:$IsWindows {
    $empty = Join-Path $TestDrive 'no-baseline'
    New-Item -ItemType Directory -Path $empty -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $empty 'Directory.Build.targets') -Value '<Project></Project>'
    $result = Invoke-PlaceholderStep $empty 'merge_group' ''
    $result.ExitCode | Should -Not -Be 0
    $result.Output | Should -BeNullOrEmpty
  }
}

Describe 'A failed pack fails its step (#2605)' {
  BeforeAll {
    $script:PackLines = @(foreach ($file in Get-ChildItem -LiteralPath $Workflows -Filter '*.yml') {
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
          if ($line -match '^\s+dotnet pack\b') { [pscustomobject]@{ File = $file.Name; Line = $line.Trim() } }
        }
      })
  }

  It 'finds the pack commands of the pack and publish workflows, so the rule below is not vacuous' {
    $PackLines.File | Should -Contain 'reusable-pack.yml'
    $PackLines.File | Should -Contain 'nuget-publish.yml'
  }

  It 'never discards the exit code of dotnet pack' {
    $hidden = @($PackLines | Where-Object { $_.Line -match '\|\|\s*(true|:)\s*$' } | ForEach-Object { "$($_.File): $($_.Line)" })
    $hidden | Should -BeNullOrEmpty -Because 'package validation reports an API break as a pack error, and a discarded exit code turns it into a pass'
  }
}

Describe 'Every pack is followed by a public API comparison that reports each break (#2605)' {
  BeforeAll {
    $script:PackWorkflows = @{
      'reusable-pack.yml' = 'Pack NuGet packages (no rebuild)'
      'nuget-publish.yml' = 'Pack NuGet packages (no rebuild)'
    }
  }

  It 'packs <_> with validation off, so a pack error fails on its own and an API break is reported by the comparison' -TestCases @('reusable-pack.yml', 'nuget-publish.yml') {
    $pack = Get-StepLines (Get-Content -LiteralPath (Join-Path $Workflows $_)) $PackWorkflows[$_]
    ($pack | Where-Object { $_ -match '^\s+dotnet pack\b' }) | Should -Match '-p:EnablePackageValidation=false'
  }

  It 'compares the public API in <_> with the script, saying whether a break blocks' -TestCases @('reusable-pack.yml', 'nuget-publish.yml') {
    $lines = Get-Content -LiteralPath (Join-Path $Workflows $_)
    $compare = Get-StepLines $lines 'Compare the public API with the baseline'
    ($compare -join "`n") | Should -Match 'Compare-PublicApi\.ps1'
    ($compare | Where-Object { $_ -match '^\s+API_BREAKS_BLOCK:' }) | Should -Not -BeNullOrEmpty -Because 'whether an API break fails the run is stated where the step runs'
  }

  It 'blocks on a break in every workflow that sets API_BREAKS_BLOCK, now the breaks on develop are recorded' {
    $settings = @(foreach ($file in Get-ChildItem -LiteralPath $Workflows -Filter '*.yml') {
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
          if ($line -match '^\s+API_BREAKS_BLOCK:\s*(.+?)\s*$') { [pscustomobject]@{ File = $file.Name; Value = $Matches[1] } }
        }
      })
    $settings.File | Should -Contain 'reusable-pack.yml' -Because 'the rule below is not vacuous'
    $settings.File | Should -Contain 'nuget-publish.yml' -Because 'the rule below is not vacuous'
    @($settings | Where-Object Value -NE "'true'" | ForEach-Object { "$($_.File): $($_.Value)" }) |
      Should -BeNullOrEmpty -Because 'the breaks on develop were accepted and recorded (#2605); any further break must fail the run until it is recorded'
  }
}

Describe 'Compare-PublicApi.ps1 reads the findings of package validation (#2605)' {
  BeforeAll {
    . (Join-Path $Root '.github' 'scripts' 'Compare-PublicApi.ps1') -Version 1.0.0
    $script:Sdk = '/sdk/Microsoft.NET.ApiCompat.ValidatePackage.targets(39,5)'
  }

  It 'returns one finding per break, the same break in both frameworks counted once' {
    $lines = @(
      "$Sdk`: error CP0002: Member 'void A.B.C()' exists on [Baseline] lib/net10.0/A.dll but not on lib/net10.0/A.dll [/repo/src/A/A.csproj]"
      "$Sdk`: error CP0002: Member 'void A.B.C()' exists on [Baseline] lib/net11.0/A.dll but not on lib/net11.0/A.dll [/repo/src/A/A.csproj]"
      "$Sdk`: error CP0006: Cannot add interface member 'void A.I.M()' to lib/net10.0/A.dll because it does not exist on [Baseline] lib/net10.0/A.dll [/repo/src/A/A.csproj]"
      "$Sdk`: error PKV006: The target framework 'net9.0' is no longer supported [/repo/src/B/B.csproj]"
      "$Sdk`: error : API breaking changes found. [/repo/src/A/A.csproj]"
      '  A -> /repo/src/A/bin/Release/net10.0/A.dll'
    )
    $findings = @(Get-PublicApiFinding -Lines $lines)
    $findings | Should -HaveCount 3
    $findings[0].Project | Should -Be 'A'
    $findings[0].Code | Should -Be 'CP0002'
    $findings[0].Message | Should -Be "Member 'void A.B.C()' exists on [Baseline] lib/<tfm>/A.dll but not on lib/<tfm>/A.dll"
    ($findings | Where-Object Code -EQ 'PKV006').Project | Should -Be 'B'
  }

  It 'returns nothing for a clean pack' {
    @(Get-PublicApiFinding -Lines @('  A -> /repo/src/A/bin/Release/net10.0/A.dll', 'Successfully created package.')) | Should -HaveCount 0
  }
}

Describe 'Compare-PublicApi.ps1 decides the step''s result (#2605)' -Skip:$IsWindows {
  BeforeAll {
    $script:ScriptPath = Join-Path $Root '.github' 'scripts' 'Compare-PublicApi.ps1'
    $script:Break = "/sdk/x.targets(39,5): error CP0002: Member 'void A.B.C()' exists on [Baseline] lib/net10.0/A.dll but not on lib/net10.0/A.dll [/repo/src/A/A.csproj]"

    # Runs the script with a stand-in dotnet that prints the given lines and exits with the given code.
    function script:Invoke-Compare([string[]]$PackOutput, [int]$PackExit, [string]$Block) {
      $bin = Join-Path $TestDrive "bin-$([guid]::NewGuid())"
      New-Item -ItemType Directory -Path $bin | Out-Null
      $outputFile = Join-Path $bin 'pack-output.txt'
      Set-Content -LiteralPath $outputFile -Value $PackOutput
      $argsFile = Join-Path $bin 'args.txt'
      Set-Content -LiteralPath (Join-Path $bin 'dotnet') -Value "#!/usr/bin/env bash`nprintf '%s\n' `"`$@`" > '$argsFile'`ncat '$outputFile'`nexit $PackExit"
      chmod +x (Join-Path $bin 'dotnet')
      $summary = Join-Path $bin 'summary.md'
      $oldPath = $env:PATH
      $env:PATH = "$bin$([IO.Path]::PathSeparator)$oldPath"
      $env:GITHUB_STEP_SUMMARY = $summary
      $env:API_BREAKS_BLOCK = $Block
      try {
        $out = Invoke-Pwsh -NoProfile -File $ScriptPath -Version '0.4521.0-queue.1' -OutputDirectory (Join-Path $bin 'out') 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
      } finally {
        $env:PATH = $oldPath
        Remove-Item Env:GITHUB_STEP_SUMMARY, Env:API_BREAKS_BLOCK -ErrorAction SilentlyContinue
      }
      return [pscustomobject]@{
        ExitCode = $code
        Output   = ($out -join "`n")
        Summary  = if (Test-Path $summary) { Get-Content -Raw $summary } else { '' }
        # A refused run never reaches pack, so it leaves no recorded arguments
        Args     = if (Test-Path -LiteralPath $argsFile) { @(Get-Content -LiteralPath $argsFile) } else { @() }
      }
    }
  }

  It 'packs at the given version, with validation on, without rebuilding' {
    $result = Invoke-Compare @('Successfully created package.') 0 'false'
    $result.Args | Should -Contain 'pack'
    $result.Args | Should -Contain '--no-build'
    $result.Args | Should -Contain '-p:Version=0.4521.0-queue.1'
    $result.Args | Should -Contain '-p:WhizbangApiCheck=true'
  }

  It 'passes a pack that reports no change' {
    $result = Invoke-Compare @('Successfully created package.') 0 'false'
    $result.ExitCode | Should -Be 0
    $result.Summary | Should -Match 'No public API break'
  }

  It 'reports each break and passes while breaks do not block' {
    $result = Invoke-Compare @($Break, $Break.Replace('net10.0', 'net11.0')) 1 'false'
    $result.ExitCode | Should -Be 0
    $result.Output | Should -Match '::warning title=Public API break CP0002 in A::'
    $result.Summary | Should -Match '\| A \| CP0002 \|'
    $result.Summary | Should -Match '1 public API break'
  }

  It 'fails on a break once breaks block' {
    $result = Invoke-Compare @($Break) 1 'true'
    $result.ExitCode | Should -Be 1
    $result.Output | Should -Match '::error title=Public API break CP0002 in A::'
  }

  It 'fails a pack that failed for any other reason, whether or not breaks block' {
    $result = Invoke-Compare @('/repo/src/A/A.csproj : error MSB4018: Could not find file ref/A.dll') 1 'false'
    $result.ExitCode | Should -Be 1
    $result.Output | Should -Match '::error'
  }

  It 'fails a failed pack that also reports a break, since the break does not explain the failure alone' {
    $result = Invoke-Compare @($Break, '/repo/src/A/A.csproj : error MSB4018: Could not find file ref/A.dll') 1 'false'
    $result.ExitCode | Should -Be 1
  }

  It 'refuses an unknown blocking value rather than guessing' {
    $result = Invoke-Compare @('Successfully created package.') 0 'maybe'
    $result.ExitCode | Should -Be 2
  }
}

Describe 'Compare-PublicApi.ps1 -UpdateSuppressions records every break in the projects'' suppression files (#2605)' -Skip:$IsWindows {
  BeforeAll {
    $script:ScriptPath = Join-Path $Root '.github' 'scripts' 'Compare-PublicApi.ps1'

    # A repository with one project and its suppression file, and a stand-in dotnet that records its arguments and,
    # like the SDK with ApiCompatGenerateSuppressionFile=true, rewrites the file without its comments.
    function script:New-FakeRepository([string]$Existing, [int]$PackExit = 0) {
      $repo = Join-Path $TestDrive "repo-$([guid]::NewGuid())"
      $project = Join-Path $repo 'src' 'A'
      New-Item -ItemType Directory -Path (Join-Path $project 'obj' 'Release' 'net10.0') -Force | Out-Null
      Set-Content -LiteralPath (Join-Path $repo 'Directory.Build.targets') -Value '<Project><PropertyGroup><PackageValidationBaselineVersion>9.8.0</PackageValidationBaselineVersion></PropertyGroup></Project>'
      Set-Content -LiteralPath (Join-Path $project 'obj' 'Release' 'net10.0' 'Microsoft.NET.ApiCompat.ValidatePackage.semaphore') -Value ''
      if ($Existing) { Set-Content -LiteralPath (Join-Path $project 'CompatibilitySuppressions.xml') -Value $Existing -NoNewline }
      $bin = Join-Path $repo '.bin'
      New-Item -ItemType Directory -Path $bin | Out-Null
      $generated = Join-Path $bin 'generated.xml'
      Set-Content -LiteralPath $generated -NoNewline -Value "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<Suppressions xmlns:xsi=`"http://www.w3.org/2001/XMLSchema-instance`" xmlns:xsd=`"http://www.w3.org/2001/XMLSchema`">`n  <Suppression>`n    <DiagnosticId>CP0002</DiagnosticId>`n    <Target>M:A.B.C</Target>`n  </Suppression>`n</Suppressions>"
      $fake = @(
        '#!/usr/bin/env bash'
        "printf '%s\n' `"`$@`" > '$bin/args.txt'"
        "pwd > '$bin/cwd.txt'"
        "if [ -e '$project/obj/Release/net10.0/Microsoft.NET.ApiCompat.ValidatePackage.semaphore' ]; then echo stale > '$bin/semaphore.txt'; fi"
        "cp '$generated' '$project/CompatibilitySuppressions.xml'"
        "exit $PackExit"
      ) -join "`n"
      Set-Content -LiteralPath (Join-Path $bin 'dotnet') -Value $fake
      chmod +x (Join-Path $bin 'dotnet')
      return [pscustomobject]@{ Root = $repo; Bin = $bin; File = (Join-Path $project 'CompatibilitySuppressions.xml') }
    }

    function script:Invoke-Update($Repo, [string[]]$Arguments = @()) {
      $oldPath = $env:PATH
      $env:PATH = "$($Repo.Bin)$([IO.Path]::PathSeparator)$oldPath"
      Remove-Item Env:API_BREAKS_BLOCK -ErrorAction SilentlyContinue
      try {
        $out = Invoke-Pwsh -NoProfile -File $ScriptPath -UpdateSuppressions -RepositoryRoot $Repo.Root -OutputDirectory (Join-Path $Repo.Bin 'out') @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
      } finally { $env:PATH = $oldPath }
      $argsFile = Join-Path $Repo.Bin 'args.txt'
      return [pscustomobject]@{
        ExitCode = $code
        Output   = ($out -join "`n")
        Args     = if (Test-Path -LiteralPath $argsFile) { @(Get-Content -LiteralPath $argsFile) } else { @() }
      }
    }
  }

  It 'packs with validation on and the SDK writing the suppression files, building first, without API_BREAKS_BLOCK' {
    $repo = New-FakeRepository
    $result = Invoke-Update $repo
    $result.ExitCode | Should -Be 0
    $result.Args | Should -Contain 'pack'
    $result.Args | Should -Contain '-p:WhizbangApiCheck=true'
    $result.Args | Should -Contain '-p:ApiCompatGenerateSuppressionFile=true'
    $result.Args | Should -Not -Contain '--no-build' -Because 'one command regenerates the files from the current source'
    (Get-Content -LiteralPath (Join-Path $repo.Bin 'cwd.txt')) | Should -Match "[/\\]$([regex]::Escape((Split-Path -Leaf $repo.Root)))$" -Because 'it packs the repository it is pointed at'

  }

  It 'stamps the baseline''s own version when none is given, so no library reports an identity change' {
    $result = Invoke-Update (New-FakeRepository)
    $result.Args | Should -Contain '-p:Version=9.8.0-local'
  }

  It 'stamps the version it is given' {
    $result = Invoke-Update (New-FakeRepository) @('-Version', '9.9.0-test.1')
    $result.Args | Should -Contain '-p:Version=9.9.0-test.1'
  }

  It 'refuses to guess a version when the repository names no baseline' {
    $repo = New-FakeRepository
    Set-Content -LiteralPath (Join-Path $repo.Root 'Directory.Build.targets') -Value '<Project></Project>'
    $result = Invoke-Update $repo
    $result.ExitCode | Should -Be 2
    $result.Args | Should -BeNullOrEmpty
  }

  It 'deletes the incremental validation markers first, so a packed tree is compared again' {
    $repo = New-FakeRepository
    Invoke-Update $repo | Out-Null
    Test-Path -LiteralPath (Join-Path $repo.Bin 'semaphore.txt') | Should -BeFalse -Because 'a leftover semaphore makes the SDK skip validation and write nothing'
  }

  It 'keeps the comment that says why the breaks are intended, which the SDK replaces with its own when it rewrites the file' {
    $header = "<!-- #1: why these breaks are intended. -->`n"
    $existing = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n$header<Suppressions>`n</Suppressions>"
    $repo = New-FakeRepository -Existing $existing
    $result = Invoke-Update $repo
    $result.ExitCode | Should -Be 0
    $text = Get-Content -Raw -LiteralPath $repo.File
    $text | Should -BeLike "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n$header<Suppressions *"
    $text | Should -Match '<Target>M:A\.B\.C</Target>' -Because 'the regenerated suppressions are kept'
  }

  It 'names each suppression file and how many breaks it records' {
    $result = Invoke-Update (New-FakeRepository)
    $result.Output | Should -Match 'src/A/CompatibilitySuppressions\.xml: 1 suppression'
  }

  It 'fails when the pack fails' {
    $result = Invoke-Update (New-FakeRepository -PackExit 1)
    $result.ExitCode | Should -Be 1
    $result.Output | Should -Match '::error::'
  }
}

# The SDK itself, not a stand-in: a library packed against a baseline package it removed two members from. Only this
# shows that a break recorded in CompatibilitySuppressions.xml passes the blocking comparison while one that is not
# recorded fails it, and that -UpdateSuppressions writes a file the comparison then accepts. Needs the .NET SDK, as the
# script-tests job has; it fails rather than skips without it.
Describe 'With breaks blocking, a recorded break passes and an unrecorded one fails (#2605)' -Skip:$IsWindows {
  BeforeAll {
    $script:ScriptPath = Join-Path $Root '.github' 'scripts' 'Compare-PublicApi.ps1'
    $script:Lib = Join-Path $TestDrive 'lib'
    New-Item -ItemType Directory -Path $Lib | Out-Null
    $script:Baseline = Join-Path $TestDrive 'baseline'

    function script:Write-Library([string]$Members) {
      Set-Content -LiteralPath (Join-Path $Lib 'Lib.cs') -Value "namespace Lib;`npublic static class Api {`n  public static int Kept() => 0;`n$Members`n}"
    }
    # Outside the repository, so none of its Directory.Build files apply; the baseline is a local package.
    Set-Content -LiteralPath (Join-Path $Lib 'Lib.csproj') -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Whizbang.PackageValidationProbe</PackageId>
    <EnablePackageValidation>true</EnablePackageValidation>
    <PackageValidationBaselinePath Condition="'`$(UseBaseline)' == 'true'">$Baseline/Whizbang.PackageValidationProbe.1.0.0.nupkg</PackageValidationBaselinePath>
  </PropertyGroup>
</Project>
"@
    Write-Library "  public static int First() => 1;`n  public static int Second() => 2;"
    & dotnet pack (Join-Path $Lib 'Lib.csproj') -c Release -o $Baseline -p:Version=1.0.0 2>&1 | Out-Null
    $script:BaselinePacked = $LASTEXITCODE

    # The current version removes both members; this suppression file records only the first.
    Write-Library ''
    $script:Suppressions = Join-Path $Lib 'CompatibilitySuppressions.xml'
    Set-Content -LiteralPath $Suppressions -Value @'
<?xml version="1.0" encoding="utf-8"?>
<!-- First() was removed on purpose. -->
<Suppressions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Suppression>
    <DiagnosticId>CP0002</DiagnosticId>
    <Target>M:Lib.Api.First</Target>
    <Left>lib/net10.0/Lib.dll</Left>
    <Right>lib/net10.0/Lib.dll</Right>
    <IsBaselineSuppression>true</IsBaselineSuppression>
  </Suppression>
</Suppressions>
'@

    function script:Invoke-Real([string[]]$Arguments) {
      $env:API_BREAKS_BLOCK = 'true'
      try {
        $out = Invoke-Pwsh -NoProfile -File $ScriptPath -RepositoryRoot $Lib -Version '1.1.0' -OutputDirectory (Join-Path $TestDrive "out-$([guid]::NewGuid())") @Arguments 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($out -join "`n") }
      } finally { Remove-Item Env:API_BREAKS_BLOCK -ErrorAction SilentlyContinue }
    }
  }

  It 'packs the baseline, so the comparisons below compare something' {
    $BaselinePacked | Should -Be 0
    Test-Path -LiteralPath (Join-Path $Baseline 'Whizbang.PackageValidationProbe.1.0.0.nupkg') | Should -BeTrue
  }

  It 'fails on the unrecorded break, reports it, and does not report the recorded one' {
    & dotnet build (Join-Path $Lib 'Lib.csproj') -c Release -p:Version=1.1.0 -p:UseBaseline=true 2>&1 | Out-Null
    $env:UseBaseline = 'true'
    try { $result = Invoke-Real @() } finally { Remove-Item Env:UseBaseline -ErrorAction SilentlyContinue }
    $result.ExitCode | Should -Be 1
    $result.Output | Should -Match '::error title=Public API break CP0002 in Lib::.*Lib\.Api\.Second\(\)'
    $result.Output | Should -Not -Match '::error title=[^\n]*Lib\.Api\.First\(\)'
  }

  It 'passes once -UpdateSuppressions has recorded the remaining break, keeping the file''s comment' {
    $env:UseBaseline = 'true'
    try {
      $update = Invoke-Real @('-UpdateSuppressions')
      $update.ExitCode | Should -Be 0
      $result = Invoke-Real @()
    } finally { Remove-Item Env:UseBaseline -ErrorAction SilentlyContinue }
    $text = Get-Content -Raw -LiteralPath $Suppressions
    $text | Should -Match 'M:Lib\.Api\.Second'
    $text | Should -Match '<!-- First\(\) was removed on purpose\. -->'
    $result.ExitCode | Should -Be 0
    $result.Output | Should -Not -Match '::error'
  }
}
