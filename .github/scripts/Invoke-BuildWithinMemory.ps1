#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Runs a build, samples the memory of its process tree while it runs, and fails when the peak passes a budget.

.DESCRIPTION
    A GitHub-hosted runner has 16 GB. When a build needs more, the runner does not fail it: it hangs until the job
    times out and leaves no log (#2388). Compiler memory grows quietly with what one project compiles, so this
    measures it on every build and fails at a budget below the runner, while there is still room to see why.

    Every few seconds it adds up the resident memory of the build and every process under it (the build nodes), plus
    the compiler server (VBCSCompiler), and keeps the highest total. The compiler server is counted wherever it sits:
    every build of the same user and SDK shares one, so a server an earlier build started is not under this build
    but compiles for it. Other processes outside the build's tree are not counted, so another job on a shared
    self-hosted machine does not count against it. When the build ends it prints the peak, adds it
    to the job summary, and exits with the build's own exit code, or 1 when the build succeeded with a peak over
    -BudgetMb.

    The command runs as given, with its output streamed unchanged.

.PARAMETER BudgetMb
    The highest total, in megabytes, a successful build may reach.

.PARAMETER IntervalSeconds
    How often to sample.

.PARAMETER Command
    The build to run: the program, then its arguments.

.PARAMETER CommandFile
    A file holding the build to run, the program and then each argument on a line of its own. This is how a shell
    passes it: pwsh -File reads an argument such as -mt:false as two, so a command line cannot be handed over as
    arguments intact.

.PARAMETER RootProcessId
    The process whose tree is measured, when it is not the build itself. For the script's own tests.

.EXAMPLE
    printf '%s\n' dotnet build --no-restore -maxcpucount:1 > build-command
    pwsh .github/scripts/Invoke-BuildWithinMemory.ps1 -BudgetMb 12288 -CommandFile build-command
#>

param(
    [Parameter()] [long]$BudgetMb = 12288,
    [Parameter()] [double]$IntervalSeconds = 2,
    [Parameter()] [string[]]$Command = @(),
    [Parameter()] [string]$CommandFile = '',
    [Parameter()] [int]$RootProcessId = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
    The verdict on a finished build: its own exit code when it failed, 1 when it succeeded over the budget, else 0.
    Returns the exit code and the line to print.
#>
function Get-MemoryVerdict([int]$BuildExitCode, [long]$PeakMb, [long]$BudgetMb) {
  $measured = "peak memory of the build and its processes: $PeakMb MB (budget $BudgetMb MB)"
  if ($BuildExitCode -ne 0) {
    return [pscustomobject]@{ ExitCode = $BuildExitCode; Message = "The build failed (exit $BuildExitCode); $measured." }
  }
  if ($PeakMb -gt $BudgetMb) {
    return [pscustomobject]@{
      ExitCode = 1
      Message  = "::error::The build's $measured. A hosted runner has 16 GB and a build past it hangs until it times " +
        'out (#2388). Find what grew: build the largest test projects one at a time and compare their peaks, and ' +
        'look at their generated sources (-p:EmitCompilerGeneratedFiles=true).'
    }
  }
  return [pscustomobject]@{ ExitCode = 0; Message = "The build's $measured." }
}

<#
    The resident memory, in bytes, of a process and every process under it, and of the compiler server wherever it
    is, from a list of processes ($Processes: objects with Id, ParentId, Bytes and, optionally, Name). A parent that is
    not in the list ends the walk, and the walk is bounded, so a loop in the list (a reused id) cannot make it run for
    ever.
#>
function Measure-TreeBytes([object[]]$Processes, [int]$RootId) {
  $parentOf = @{}
  foreach ($p in $Processes) { $parentOf[$p.Id] = $p.ParentId }
  $total = 0L
  foreach ($p in $Processes) {
    if ($p.PSObject.Properties['Name'] -and $p.Name -eq 'VBCSCompiler') { $total += $p.Bytes; continue }
    $id = $p.Id
    for ($depth = 0; $depth -lt 64 -and $null -ne $id; $depth++) {
      if ($id -eq $RootId) { $total += $p.Bytes; break }
      $id = $parentOf[$id]
    }
  }
  return $total
}

# Every running process with its parent and resident memory. A process that exits while it is read is left out.
function Get-ProcessSnapshot {
  foreach ($process in @(Get-Process)) {
    try {
      $parent = $process.Parent
      [pscustomobject]@{
        Id = $process.Id; ParentId = $(if ($parent) { $parent.Id } else { $null }); Bytes = $process.WorkingSet64
        Name = $process.ProcessName
      }
    } catch [System.InvalidOperationException] {
      continue
    }
  }
}

if ($MyInvocation.InvocationName -eq '.') { return }

if ($CommandFile) {
  $Command = @(Get-Content -LiteralPath $CommandFile | Where-Object { $_ -ne '' })
}
if ($Command.Count -eq 0) {
  Write-Output '::error::Invoke-BuildWithinMemory: no -Command or -CommandFile to run.'
  exit 2
}

$start = [System.Diagnostics.ProcessStartInfo]::new($Command[0])
foreach ($argument in $Command | Select-Object -Skip 1) { $start.ArgumentList.Add($argument) }
$start.UseShellExecute = $false
$build = [System.Diagnostics.Process]::Start($start)
$root = $(if ($RootProcessId) { $RootProcessId } else { $build.Id })

# Sampled at the start and once more after the build ends, so even a build shorter than one interval is measured.
$peak = 0L
do {
  $peak = [Math]::Max($peak, (Measure-TreeBytes @(Get-ProcessSnapshot) $root))
} until ($build.WaitForExit([int]($IntervalSeconds * 1000)))
$build.WaitForExit()
$peak = [Math]::Max($peak, (Measure-TreeBytes @(Get-ProcessSnapshot) $root))

$verdict = Get-MemoryVerdict -BuildExitCode $build.ExitCode -PeakMb ([long]($peak / 1MB)) -BudgetMb $BudgetMb
Write-Output $verdict.Message
if ($env:GITHUB_STEP_SUMMARY) {
  Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($verdict.Message -replace '^::error::', '**Over budget:** ')
}
exit $verdict.ExitCode
