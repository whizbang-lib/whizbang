# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
  Fails when a build with nothing changed still does work.

.DESCRIPTION
  Builds the project once to bring it up to date, builds it again with a binary log, replays that log as
  text, and reports every watched target that ran in the second build: the project, and the input that was
  newer than its output. A no-op rebuild should skip all of them. When one runs, every project above it
  recompiles too, so one file rewritten on every build costs every contributor and every CI run a full
  recompile of the graph above it.

  A target with no Inputs and Outputs never prints "Skipping target", so it counts as work whenever it
  runs. That is deliberate: such a target cannot be skipped, and the fix is to give it inputs and outputs.

.PARAMETER Project
  The project or solution to build. Defaults to a sample service that references every generator package.

.PARAMETER Configuration
  Build configuration. Default: Debug.

.PARAMETER Targets
  The targets that must be skipped on a no-op rebuild. Default: CoreCompile and MergeSharedAssembly.

.PARAMETER OutputDirectory
  Where the binary and text logs are written. Default: a new folder under the temp directory.

.EXAMPLE
  pwsh scripts/build-cost/Test-NoOpRebuild.ps1

.EXAMPLE
  pwsh scripts/build-cost/Test-NoOpRebuild.ps1 -Project Whizbang.slnx -Configuration Release
#>
[CmdletBinding()]
param(
  [Parameter()]
  [string]$Project = 'samples/ECommerce/ECommerce.OrderService.API/ECommerce.OrderService.API.csproj',

  [Parameter()]
  [string]$Configuration = 'Debug',

  [Parameter()]
  [string[]]$Targets = @('CoreCompile', 'MergeSharedAssembly'),

  [Parameter()]
  [string]$OutputDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) "noop-rebuild-$([guid]::NewGuid().ToString('N'))")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
  Reads a replayed build log (detailed verbosity) and returns each watched target that did work.
.OUTPUTS
  PSCustomObject with Target, Project and Reason.
#>
function Get-NoOpRebuildWork {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath,

    [Parameter()]
    [string[]]$Targets = @('CoreCompile', 'MergeSharedAssembly')
  )

  $startPattern = '^\s*(?:\d+:\d+>)?Target "(?<target>[^"]+)" in (?:file "[^"]*" from )?project "(?<project>[^"]+)"'
  $current = $null

  foreach ($line in [System.IO.File]::ReadLines($LogPath)) {
    if ($line -match $startPattern) {
      if ($null -ne $current -and $current.Pending) {
        [pscustomobject]@{ Target = $current.Target; Project = $current.Project; Reason = 'the target ran without an up-to-date check' }
      }
      $current = $null
      if ($Targets -contains $Matches.target) {
        $current = @{ Target = $Matches.target; Project = $Matches.project; Pending = $true; Building = $false }
      }
      continue
    }
    if ($null -eq $current) {
      continue
    }
    if ($line -match '^\s*Skipping target "') {
      $current = $null
      continue
    }
    if ($line -match '^\s*Building target "[^"]+" (completely|partially)') {
      $current.Building = $true
      continue
    }
    if ($current.Building -and $line -match '^\s*Input file "(?<in>[^"]+)" is newer than output file "(?<out>[^"]+)"') {
      [pscustomobject]@{ Target = $current.Target; Project = $current.Project; Reason = "$($Matches.in) is newer than $($Matches.out)" }
      $current = $null
      continue
    }
    if ($current.Building) {
      [pscustomobject]@{ Target = $current.Target; Project = $current.Project; Reason = 'some outputs were out of date' }
      $current = $null
    }
  }

  if ($null -ne $current -and $current.Pending) {
    $reason = if ($current.Building) { 'some outputs were out of date' } else { 'the target ran without an up-to-date check' }
    [pscustomobject]@{ Target = $current.Target; Project = $current.Project; Reason = $reason }
  }
}

if ($MyInvocation.InvocationName -eq '.') { return }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$binlog = Join-Path $OutputDirectory 'noop.binlog'
$textLog = Join-Path $OutputDirectory 'noop.log'

Write-Host "Bringing $Project up to date ($Configuration)"
dotnet build $Project -c $Configuration -nologo -v:q | Out-Host
if ($LASTEXITCODE -ne 0) { throw "The first build failed (exit $LASTEXITCODE)." }

# One node, so the detailed log is not interleaved: a parallel build's log can print one project's
# "Skipping target" under another project's header. The binary log is kept for a closer look.
Write-Host 'Building again with nothing changed'
dotnet build $Project -c $Configuration -nologo -v:q --no-restore -m:1 "-bl:$binlog" "-flp:logfile=$textLog;verbosity=detailed" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "The no-op build failed (exit $LASTEXITCODE)." }

$work = @(Get-NoOpRebuildWork -LogPath $textLog -Targets $Targets)
if ($work.Count -eq 0) {
  Write-Host "No-op rebuild did no work in: $($Targets -join ', ')"
  exit 0
}

Write-Host "No-op rebuild did work ($($work.Count)). Each one makes every project above it recompile:"
foreach ($w in $work) {
  Write-Host "  $($w.Target) in $($w.Project): $($w.Reason)"
}
Write-Host "Logs: $OutputDirectory"
exit 1
