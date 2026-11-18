# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Modules Pester

# The build's memory guard (#2388). A hosted runner that runs out of memory does not fail the build, it hangs until
# the job times out, so the guard has to fail a build that grew past the budget, and must never turn a failed build
# into a pass or a passing build into a failure for any other reason.

BeforeAll {
  $script:Script = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'Invoke-BuildWithinMemory.ps1'
  . $script:Script
  $script:Pwsh = (Get-Process -Id $PID).Path
}

Describe 'Get-MemoryVerdict' {
  It 'passes a successful build within the budget' {
    $v = Get-MemoryVerdict -BuildExitCode 0 -PeakMb 9000 -BudgetMb 12288
    $v.ExitCode | Should -Be 0
    $v.Message | Should -BeLike '*9000 MB (budget 12288 MB)*'
  }

  It 'passes a build exactly at the budget' {
    (Get-MemoryVerdict -BuildExitCode 0 -PeakMb 12288 -BudgetMb 12288).ExitCode | Should -Be 0
  }

  It 'fails a successful build over the budget, as an error annotation naming the cause' {
    $v = Get-MemoryVerdict -BuildExitCode 0 -PeakMb 12289 -BudgetMb 12288
    $v.ExitCode | Should -Be 1
    $v.Message | Should -BeLike '::error::*12289 MB (budget 12288 MB)*#2388*'
  }

  It 'keeps a failed build''s own exit code, whatever its memory' {
    (Get-MemoryVerdict -BuildExitCode 3 -PeakMb 20000 -BudgetMb 12288).ExitCode | Should -Be 3
    (Get-MemoryVerdict -BuildExitCode 3 -PeakMb 1 -BudgetMb 12288).ExitCode | Should -Be 3
  }
}

Describe 'Measure-TreeBytes' {
  BeforeAll {
    # 1 is the build; 2 a build node under it; 3 the compiler server the node started; 4 another job's process
    # beside the build; 5 a child of that job.
    $script:Tree = @(
      [pscustomobject]@{ Id = 1; ParentId = 100; Bytes = 10L }
      [pscustomobject]@{ Id = 2; ParentId = 1; Bytes = 200L }
      [pscustomobject]@{ Id = 3; ParentId = 2; Bytes = 3000L }
      [pscustomobject]@{ Id = 4; ParentId = 100; Bytes = 40000L }
      [pscustomobject]@{ Id = 5; ParentId = 4; Bytes = 500000L }
    )
  }

  It 'adds up the build and every process under it, at any depth' {
    Measure-TreeBytes $script:Tree 1 | Should -Be 3210
  }

  It 'counts nothing outside the build''s tree' {
    Measure-TreeBytes $script:Tree 4 | Should -Be 540000
    Measure-TreeBytes $script:Tree 99 | Should -Be 0
  }

  It 'counts the compiler server wherever it hangs, since every build of the same user and SDK shares it' {
    # An earlier build started it, so it is not under this one, but this build compiles in it.
    $tree = $script:Tree + @([pscustomobject]@{ Id = 9; ParentId = 1000; Bytes = 7000000L; Name = 'VBCSCompiler' })
    Measure-TreeBytes $tree 1 | Should -Be 7003210
  }

  It 'ends on a loop of parent ids instead of running for ever' {
    $loop = @([pscustomobject]@{ Id = 7; ParentId = 8; Bytes = 1L }, [pscustomobject]@{ Id = 8; ParentId = 7; Bytes = 1L })
    Measure-TreeBytes $loop 1 | Should -Be 0
  }
}

Describe 'Invoke-BuildWithinMemory.ps1' {
  # The script runs in this test's process, so -RootProcessId $PID measures a tree that is alive at every sample and
  # always holds some memory: a budget of zero is always passed, and a huge one never is.
  It 'returns the command''s own exit code when it fails' {
    & $script:Script -BudgetMb 1000000 -Command @($script:Pwsh, '-NoProfile', '-Command', 'exit 3') | Out-Null
    $LASTEXITCODE | Should -Be 3
  }

  It 'passes a command within the budget' {
    $out = & $script:Script -BudgetMb 1000000 -RootProcessId $PID -Command @($script:Pwsh, '-NoProfile', '-Command', 'exit 0')
    $LASTEXITCODE | Should -Be 0
    ($out -join "`n") | Should -BeLike '*peak memory of the build and its processes: * MB (budget 1000000 MB)*'
  }

  It 'fails a command over the budget, even one shorter than a sampling interval' {
    & $script:Script -BudgetMb 0 -IntervalSeconds 60 -RootProcessId $PID -Command @($script:Pwsh, '-NoProfile', '-Command', 'exit 0') | Out-Null
    $LASTEXITCODE | Should -Be 1
  }

  It 'reads the command from a file, one argument a line, with its switches intact' -Skip:$IsWindows {
    $file = Join-Path -Path $TestDrive -ChildPath 'build-command'
    # sh, not pwsh: pwsh -File itself splits -mt:false, which is the reason the file exists.
    Set-Content -LiteralPath $file -Value @('sh', '-c', 'test "$1" = "-mt:false" && exit 5; exit 4', 'probe', '-mt:false')
    $out = & $script:Pwsh -NoProfile -File $script:Script -BudgetMb 1000000 -CommandFile $file
    $LASTEXITCODE | Should -Be 5 -Because "the command and an argument such as -mt:false must reach it whole: $out"
  }

  It 'refuses to run without a command' {
    & $script:Script | Out-Null
    $LASTEXITCODE | Should -Be 2
  }
}

Describe 'the CI builds' {
  # Every full-solution build in the pipeline's build jobs runs under the guard; the test suites build nothing new.
  It 'runs every full-solution build in <file> under the memory guard' -ForEach @(
      @{ file = 'reusable-build.yml'; builds = 1 }, @{ file = 'ci.yml'; builds = 2 }) {
    $lines = Get-Content (Join-Path -Path $PSScriptRoot -ChildPath "../../workflows/$file")
    $direct = @($lines | Where-Object { $_ -match '^\s*run:\s*dotnet build --no-restore --configuration Release' })
    $guarded = @($lines | Where-Object { $_ -match "printf '%s\\n' dotnet build " })
    $runs = @($lines | Where-Object { $_ -match 'Invoke-BuildWithinMemory\.ps1 -BudgetMb \d+ -CommandFile "\$BUILD_COMMAND"' })
    $direct | Should -BeNullOrEmpty
    $guarded.Count | Should -Be $builds
    $runs.Count | Should -Be $builds
  }
}
