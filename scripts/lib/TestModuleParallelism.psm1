# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

# How many test modules scripts/Run-Tests.ps1 runs at once. Tested by .github/scripts/tests/Run-Tests.Tests.ps1.

function Get-TestModuleParallelism {
  <#
  .SYNOPSIS
    Returns the --max-parallel-test-modules value for a run.
  .DESCRIPTION
    A value the caller asked for is kept. Otherwise every processor runs a module, except in an integration run
    that tests more than one framework (a release build tests net10.0 and net11.0): there one module runs at a
    time. Each framework's copy of an integration project shares one broker and one database server with the
    other's, so two run at once read each other's dead letters and contend for the server's locks.
  #>
  param(
    [Parameter(Mandatory)][string]$Mode,
    [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Frameworks,
    [Parameter(Mandatory)][int]$Requested,
    [Parameter(Mandatory)][int]$ProcessorCount
  )
  if ($Requested -gt 0) { return $Requested }
  if ($Mode -in @('Integration', 'AiIntegrations') -and $Frameworks.Count -gt 1) { return 1 }
  return $ProcessorCount
}

Export-ModuleMember -Function Get-TestModuleParallelism
