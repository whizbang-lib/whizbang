#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    The pool's self-check: what a self-hosted machine can do, recorded where the runner picker reads it.

.DESCRIPTION
    A machine can be online and idle and still unable to do the job: one had no PowerShell, so the .NET setup
    failed, and uploaded so slowly that the compile's artifact uploads hit the job timeout (#1880).
    .github/workflows/runner-self-check.yml runs this script in three modes:

      Plan     On a GitHub-hosted runner: one slot per online pool machine (the RUNNER_POOL_TOKEN reads the
               runner group), written as slots=[1,2,...]. Never fails: no token, an API error or no machine
               writes slots=[] with a warning.
      Prepare  On the pool machine: writes -UploadMB of random data to -UploadFile and the time it finished
               (started-ms), just before the workflow uploads that file as an artifact.
      Record   On the pool machine, after the upload: the PowerShell version, the free disk under the
               workspace, whether Docker answers, and the upload speed (the file's size over the time since
               started-ms). Writes ok=true|false and key=<the record>, the key built by
               Select-CiRunner.ps1's ConvertTo-RunnerCheckKey; the workflow saves an Actions cache entry
               under that key, which is the record.

    A machine passes with PowerShell 7 or later and at least -MinDiskGB free. Upload speed and Docker are
    recorded and judged by the picker per job class (Select-CiRunner.ps1, Test-RunnerCheck). A machine where
    this script cannot run at all (no pwsh) writes no record, and a machine with no recent passing record gets
    no job, so a missing record is as safe as a failing one.

    Setup: .github/WORKFLOWS.md, "Runner selection".
#>

param(
    [ValidateSet('Plan', 'Prepare', 'Record', '')][string]$Mode = '',
    [string]$Organization = '',
    [string]$GroupName = 'Default',
    [string]$UploadFile = '',
    [int]$UploadMB = 200,
    [string]$StartedMs = '',
    [string]$UploadOutcome = '',
    [int]$MinDiskGB = 20,
    [string]$RunnerName = $env:RUNNER_NAME,
    [string]$Nonce = '',
    [string]$RecordFile = '',
    [string]$WorkPath = $env:GITHUB_WORKSPACE,
    [string]$OutputFile = $env:GITHUB_OUTPUT,
    [string]$SummaryFile = $env:GITHUB_STEP_SUMMARY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This script's own arguments, kept before the picker is dot-sourced: its param block shares this scope and resets
# the names the two scripts have in common (Organization, GroupName, OutputFile, SummaryFile).
$selfCheck = @{
  Mode = $Mode; Organization = $Organization; GroupName = $GroupName; UploadFile = $UploadFile; UploadMB = $UploadMB
  StartedMs = $StartedMs; UploadOutcome = $UploadOutcome; MinDiskGB = $MinDiskGB; RunnerName = $RunnerName; Nonce = $Nonce
  RecordFile = $RecordFile; WorkPath = $WorkPath; OutputFile = $OutputFile; SummaryFile = $SummaryFile
}

# The record key format and the API helpers live with the picker that reads them.
. (Join-Path $PSScriptRoot 'Select-CiRunner.ps1')

# Whether the machine can run the pool's jobs at all, and why not.
function Get-RunnerCapabilityResult {
  param([Parameter(Mandatory)][version]$PwshVersion, [AllowNull()][object]$FreeDiskGB, [int]$MinDiskGB = 20)
  $problems = @()
  if ($PwshVersion.Major -lt 7) { $problems += "PowerShell $PwshVersion (needs 7.0 or later)" }
  if ($null -eq $FreeDiskGB) { $problems += 'free disk space unknown' }
  elseif ($FreeDiskGB -lt $MinDiskGB) { $problems += "$([math]::Floor($FreeDiskGB)) GB free (needs $MinDiskGB)" }
  [pscustomobject]@{ Ok = ($problems.Count -eq 0); Problems = $problems }
}

<#
    MB/s of an upload of -UploadMB that started at -StartedMs and was seen finished at -FinishedMs (Unix
    milliseconds), rounded down; $null when the upload failed or the times cannot give a speed. The time includes
    the upload action's own start-up, so the speed is a lower bound.
#>
function Measure-UploadSpeed {
  param([int]$UploadMB, [AllowNull()][object]$StartedMs, [long]$FinishedMs, [bool]$Uploaded)
  if (-not $Uploaded -or $null -eq $StartedMs) { return $null }
  $seconds = ($FinishedMs - [long]$StartedMs) / 1000.0
  if ($seconds -le 0) { return $null }
  return [int][math]::Floor($UploadMB / $seconds)
}

# Free GB on the drive holding -Path: the drive with the longest root that is a prefix of it (case-insensitive).
function Get-FreeSpaceGB {
  param([string]$Path, [object[]]$Drives)
  $best = $null
  foreach ($drive in @($Drives)) {
    $root = [string]$drive.RootDirectory.FullName
    if (-not $Path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
    if ($null -eq $best -or $root.Length -gt ([string]$best.RootDirectory.FullName).Length) { $best = $drive }
  }
  if ($null -eq $best) { return $null }
  return [double]$best.AvailableFreeSpace / 1GB
}

# Whether Docker answers: the probe runs it, and a non-zero exit or a throw (no docker on the path) is a no.
function Test-DockerWork {
  param([scriptblock]$Probe = { & docker version --format '{{.Server.Version}}' *> $null })
  try {
    $global:LASTEXITCODE = 0
    $null = & $Probe
    return $global:LASTEXITCODE -eq 0
  }
  catch { return $false }
}

# One self-check slot per online pool machine (busy or idle: a busy one is checked once it is free).
function Get-SelfCheckSlot {
  param([object[]]$Runners)
  $online = @(Get-IdleRunner -Runners @($Runners | ForEach-Object {
          [pscustomobject]@{ name = $_.name; status = $_.status; busy = $false; labels = $_.labels }
        }) -Labels @('self-hosted', 'whizbang'))
  if ($online.Count -eq 0) { return @() }
  return @(1..$online.Count)
}

function Invoke-RunnerSelfCheck {
  param(
      [Parameter(Mandatory)][ValidateSet('Plan', 'Prepare', 'Record')][string]$Mode,
      [string]$OutputFile = '', [string]$SummaryFile = '',
      [scriptblock]$GetPoolState,
      [string]$UploadFile = '', [int]$UploadMB = 200, [scriptblock]$NowMs = { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() },
      [string]$RecordFile = '', [string]$RunnerName = '', [AllowNull()][object]$StartedMs, [string]$UploadOutcome = '',
      [int]$MinDiskGB = 20, [string]$Nonce = '', [version]$PwshVersion = $PSVersionTable.PSVersion,
      [scriptblock]$GetFreeSpaceGB, [scriptblock]$DockerProbe = { Test-DockerWork }
  )
  switch ($Mode) {
    'Plan' {
      $slots = @()
      try {
        $state = & $GetPoolState
        if ($null -eq $state.Group) { Write-Host '::warning title=Runner self-check::The runner group was not found; no machine is checked.' }
        else {
          $slots = @(Get-SelfCheckSlot -Runners @($state.Runners))
          if ($slots.Count -eq 0) { Write-Host '::warning title=Runner self-check::no pool runner is online; none is checked.' }
        }
      }
      catch { Write-Host "::warning title=Runner self-check::The pool could not be read ($($_.Exception.Message)); no machine is checked." }
      Write-RunnerFile -Path $OutputFile -Lines @("slots=$(ConvertTo-Json -InputObject @($slots) -Compress)")
    }
    'Prepare' {
      $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $UploadFile)
      # Random bytes, so no compression shortens the upload.
      $buffer = [byte[]]::new(1MB)
      $stream = [System.IO.File]::Create($UploadFile)
      try {
        for ($i = 0; $i -lt $UploadMB; $i++) { [System.Security.Cryptography.RandomNumberGenerator]::Fill($buffer); $stream.Write($buffer, 0, $buffer.Length) }
      }
      finally { $stream.Dispose() }
      Write-RunnerFile -Path $OutputFile -Lines @("started-ms=$(& $NowMs)")
    }
    'Record' {
      $result = Get-RunnerCapabilityResult -PwshVersion $PwshVersion -FreeDiskGB (& $GetFreeSpaceGB) -MinDiskGB $MinDiskGB
      $up = Measure-UploadSpeed -UploadMB $UploadMB -StartedMs $StartedMs -FinishedMs (& $NowMs) -Uploaded ($UploadOutcome -eq 'success')
      $docker = [bool](& $DockerProbe)
      $lines = @()
      try {
        $key = ConvertTo-RunnerCheckKey -RunnerName $RunnerName -Ok $result.Ok -UploadMBps $up -Docker $docker -Nonce $Nonce
        $lines += "key=$key"
        $ok = $result.Ok
      }
      catch {
        $ok = $false
        $result.Problems += $_.Exception.Message
      }
      $lines += "ok=$($ok.ToString().ToLowerInvariant())"
      Write-RunnerFile -Path $OutputFile -Lines $lines
      if ($RecordFile) {
        $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $RecordFile)
        Set-Content -LiteralPath $RecordFile -Value @($lines)
      }
      $speed = if ($null -eq $up) { 'not measured' } else { "$up MB/s" }
      $summary = @('### Runner self-check', '', "``$RunnerName``: $(if ($ok) { 'passed' } else { 'failed' })", '',
        "- Upload: $speed", "- Docker: $(if ($docker) { 'working' } else { 'not working' })")
      $summary += @($result.Problems | ForEach-Object { "- Problem: $_" })
      Write-RunnerFile -Path $SummaryFile -Lines $summary
      Write-Host ($summary -join [Environment]::NewLine)
    }
  }
}

# Dot-sourcing for tests loads the functions without checking anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$work = if ($selfCheck.WorkPath) { $selfCheck.WorkPath } else { (Get-Location).Path }
$invokeApi = New-GitHubApiInvoker
$poolToken = $env:RUNNER_POOL_TOKEN
$org = $selfCheck.Organization; $group = $selfCheck.GroupName
# The closures see only their captured variables, so the functions they call are captured too.
$readPool = ${function:Get-RunnerPoolState}; $freeSpace = ${function:Get-FreeSpaceGB}
Invoke-RunnerSelfCheck -Mode $selfCheck.Mode -OutputFile $selfCheck.OutputFile -SummaryFile $selfCheck.SummaryFile `
  -GetPoolState {
    if (-not $poolToken) { throw 'no runner-pool token (the RUNNER_POOL_APP_ID and RUNNER_POOL_APP_PRIVATE_KEY secrets)' }
    & $readPool -InvokeApi $invokeApi -Organization $org -GroupName $group -PoolToken $poolToken
  }.GetNewClosure() `
  -UploadFile $selfCheck.UploadFile -UploadMB $selfCheck.UploadMB -RecordFile $selfCheck.RecordFile -RunnerName $selfCheck.RunnerName `
  -StartedMs $(if ($selfCheck.StartedMs) { [long]$selfCheck.StartedMs } else { $null }) -UploadOutcome $selfCheck.UploadOutcome `
  -MinDiskGB $selfCheck.MinDiskGB -Nonce $selfCheck.Nonce `
  -GetFreeSpaceGB { & $freeSpace -Path $work -Drives @([System.IO.DriveInfo]::GetDrives() | Where-Object IsReady) }.GetNewClosure()
exit 0
