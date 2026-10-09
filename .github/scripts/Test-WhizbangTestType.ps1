#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Fails when a test project declares a <WhizbangTestType> that some tool reading the property does not
    know, or when a CI test suite is missing from a list that must name every suite.

.DESCRIPTION
    Test projects declare their kind in <WhizbangTestType> (Unit, Component, Integration, Benchmark,
    Soak). Several tools read it, and each one silently skips a type it does not know: the project
    builds, its tests never run, and every check stays green. #1196 found 214 integration tests that no
    suite had ever selected that way. So a new type must be taught to every reader, and this guard
    names each type and each reader that does not know it:

      scripts/Run-Tests.ps1                   its $WhizbangTestTypes table: the -Mode values that run the
                                              type (each one accepted by -Mode and -LogMode), or RunBy,
                                              naming what runs a type that Run-Tests never does
      Directory.Build.targets                 <WhizbangKnownTestTypes>, which fails the build of a project
                                              declaring any other type
      scripts/Test-SolutionTestProjects.ps1   $KnownTestTypes
      .github/scripts/Get-TestSlice.ps1       a suite slice with that Type, or an entry in $UnslicedTypes
      CI                                      a reusable-test-*.yml running Run-Tests.ps1 with one of the
                                              type's modes, called by a ci.yml job the gate counts
                                              (Test-CiResult.ps1's $script:Suites)
      scripts/Run-PR.ps1                      runs every type that "-Mode All" runs

    A test project (IsTestProject) that declares no type is reported too: no reader selects it.

    Get-CiSuiteListProblem checks the other half: every suite the gate counts is a ci.yml job, every
    job that runs a reusable-test-*.yml is counted, and every suite is in the needs of each job that
    stands for all of them (the gate, test reporting, notifications, both publishes) and in the
    success condition of both publishes. A suite left out of a publish condition would let a package
    ship without it.

    Exit 0 when nothing is wrong, 1 otherwise, listing every problem.

.PARAMETER Root
    The repository root. Defaults to this script's repository.

.EXAMPLE
    pwsh .github/scripts/Test-WhizbangTestType.ps1
#>

param(
    [Parameter()] [string]$Root = (Join-Path -Path $PSScriptRoot -ChildPath '../..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The ci.yml jobs that stand for every suite, and so must need each one. The two publishes must also
# require each suite's success in their condition (see the DESCRIPTION).
$script:SuiteConsumers = @('ci-result', 'test-results', 'publish-test-status', 'notify-cancelled', 'prerelease-publish', 'release-publish')
$script:SuiteGatedPublishes = @('prerelease-publish', 'release-publish')

<#
    The value of a constant PowerShell expression: strings, numbers, $true/$false/$null, arrays and
    (ordered) hashtables of those. The tools' tables are read this way rather than by running the
    tools, which would start builds and test runs.
#>
function ConvertFrom-ConstantAst {
  param([Parameter(Mandatory)] [System.Management.Automation.Language.Ast]$Ast)
  switch ($Ast) {
    { $_ -is [System.Management.Automation.Language.PipelineAst] } { return ConvertFrom-ConstantAst -Ast $Ast.PipelineElements[0] }
    { $_ -is [System.Management.Automation.Language.CommandExpressionAst] } { return ConvertFrom-ConstantAst -Ast $Ast.Expression }
    { $_ -is [System.Management.Automation.Language.ConvertExpressionAst] } { return ConvertFrom-ConstantAst -Ast $Ast.Child }
    { $_ -is [System.Management.Automation.Language.StringConstantExpressionAst] } { return $Ast.Value }
    { $_ -is [System.Management.Automation.Language.ExpandableStringExpressionAst] } { return $Ast.Value }
    { $_ -is [System.Management.Automation.Language.ConstantExpressionAst] } { return $Ast.Value }
    { $_ -is [System.Management.Automation.Language.VariableExpressionAst] -and $Ast.VariablePath.UserPath -in @('true', 'false', 'null') } {
      return @{ true = $true; false = $false; null = $null }[$Ast.VariablePath.UserPath]
    }
    # Arrays are returned whole (the leading comma), so one holding a single item, or none, stays an
    # array; @('a', 'b') is an array expression around an array literal, so its items are flattened.
    { $_ -is [System.Management.Automation.Language.ArrayLiteralAst] } {
      $items = [System.Collections.Generic.List[object]]::new()
      foreach ($element in $Ast.Elements) { $items.Add((ConvertFrom-ConstantAst -Ast $element)) }
      return , $items.ToArray()
    }
    { $_ -is [System.Management.Automation.Language.ArrayExpressionAst] } {
      $items = [System.Collections.Generic.List[object]]::new()
      foreach ($statement in $Ast.SubExpression.Statements) {
        $value = ConvertFrom-ConstantAst -Ast $statement
        if ($value -is [object[]]) { $items.AddRange($value) } else { $items.Add($value) }
      }
      return , $items.ToArray()
    }
    { $_ -is [System.Management.Automation.Language.HashtableAst] } {
      $table = [ordered]@{}
      foreach ($pair in $Ast.KeyValuePairs) { $table[[string](ConvertFrom-ConstantAst -Ast $pair.Item1)] = ConvertFrom-ConstantAst -Ast $pair.Item2 }
      return $table
    }
  }
  throw "'$($Ast.Extent.Text)' is not a constant expression"
}

# The constant assigned to $Name (or $script:Name) in a script, or $null when it assigns none.
function Get-AssignedValue([string]$Path, [string]$Name) {
  $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$null)
  $assignment = $ast.Find({
      param($node)
      $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
      $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
      ($node.Left.VariablePath.UserPath -replace '^script:', '') -eq $Name
    }, $true)
  if (-not $assignment) { return $null }
  return ConvertFrom-ConstantAst -Ast $assignment.Right
}

# The values a script's parameter accepts through [ValidateSet(...)].
function Get-ValidateSet([string]$Path, [string]$Parameter) {
  $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$null)
  $param = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.ParameterAst] -and $node.Name.VariablePath.UserPath -eq $Parameter }, $true)
  $set = $param.Attributes | Where-Object { $_.TypeName.Name -eq 'ValidateSet' }
  return @($set.PositionalArguments | ForEach-Object { $_.Value })
}

# Every test project in the repository (a .csproj that is a test project or declares a type), with its
# declared type ('' when it declares none). Paths are repository-relative, with forward slashes.
function Get-DeclaredTestProject([string]$Root) {
  $rootPath = (Resolve-Path -Path $Root).Path
  Get-ChildItem -Path $rootPath -Recurse -Filter '*.csproj' -File |
    # Build output, packages, and dot folders (a local checkout may hold other worktrees there).
    Where-Object { $_.FullName.Substring($rootPath.Length) -notmatch '[\\/](bin|obj|node_modules|\.[^\\/]+)[\\/]' } |
    ForEach-Object {
      $content = Get-Content -Path $_.FullName -Raw
      $type = if ($content -match '<WhizbangTestType>\s*([^<\s]+)\s*</WhizbangTestType>') { $Matches[1] } else { '' }
      if ($type -or $content -match '<IsTestProject>\s*true\s*</IsTestProject>') {
        [pscustomobject]@{ Path = [System.IO.Path]::GetRelativePath($rootPath, $_.FullName) -replace '\\', '/'; Type = $type }
      }
    } |
    Sort-Object -Property Path
}

# ci.yml's jobs: id -> the job's text (its lines up to the next job).
function Get-CiJob([string]$Root) {
  $text = Get-Content -Path (Join-Path -Path $Root -ChildPath '.github/workflows/ci.yml') -Raw
  $jobs = [ordered]@{}
  $start = [regex]::Match($text, '(?m)^jobs:[ \t]*\r?$')
  $body = $text.Substring($start.Index + $start.Length)
  foreach ($m in [regex]::Matches($body, '(?m)^  ([A-Za-z0-9_-]+):[ \t]*\r?\n((?:(?!  [A-Za-z0-9_-]+:[ \t]*\r?$).*(?:\r?\n|$))*)')) {
    $jobs[$m.Groups[1].Value] = $m.Groups[2].Value
  }
  return $jobs
}

# The jobs a job needs, from either "needs: [a, b]" or "needs: a".
function Get-JobNeed([string]$Job) {
  if ($Job -match '(?m)^    needs:\s*\[([^\]]*)\]') { return @($Matches[1] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
  if ($Job -match '(?m)^    needs:\s*([A-Za-z0-9_-]+)\s*$') { return @($Matches[1]) }
  return @()
}

# The -Mode values Run-Tests.ps1 is called with in a file's text.
function Get-CalledMode([string]$Text) {
  return @([regex]::Matches($Text, '-Mode\s+(\w+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
}

function Get-WhizbangTestTypeProblem {
  param([Parameter(Mandatory)] [string]$Root)
  $runTests = Join-Path -Path $Root -ChildPath 'scripts/Run-Tests.ps1'
  $types = Get-AssignedValue -Path $runTests -Name 'WhizbangTestTypes'
  if ($null -eq $types) { $types = [ordered]@{} }
  $modeSet = Get-ValidateSet -Path $runTests -Parameter 'Mode'
  $logModeSet = Get-ValidateSet -Path $runTests -Parameter 'LogMode'

  $targets = Get-Content -Path (Join-Path -Path $Root -ChildPath 'Directory.Build.targets') -Raw
  $targetTypes = if ($targets -match '<WhizbangKnownTestTypes>([^<]*)</WhizbangKnownTestTypes>') { @($Matches[1] -split ';' | ForEach-Object { $_.Trim() }) } else { @() }
  $solutionTypes = @(Get-AssignedValue -Path (Join-Path -Path $Root -ChildPath 'scripts/Test-SolutionTestProjects.ps1') -Name 'KnownTestTypes')
  $slice = Join-Path -Path $Root -ChildPath '.github/scripts/Get-TestSlice.ps1'
  $slicedTypes = @((Get-AssignedValue -Path $slice -Name 'Suites').Values | ForEach-Object { $_['Type'] })
  $unslicedTypes = @((Get-AssignedValue -Path $slice -Name 'UnslicedTypes').Keys)

  # The modes each gated CI suite job runs, through the reusable workflow it calls.
  $gated = @(Get-AssignedValue -Path (Join-Path -Path $Root -ChildPath '.github/scripts/Test-CiResult.ps1') -Name 'Suites')
  $jobs = Get-CiJob -Root $Root
  $ciModes = @(foreach ($job in $gated) {
      if (-not $jobs.Contains($job) -or $jobs[$job] -notmatch 'uses:\s*\./\.github/workflows/(reusable-test-[\w-]+\.yml)') { continue }
      $workflow = Join-Path -Path $Root -ChildPath ".github/workflows/$($Matches[1])"
      if (Test-Path -Path $workflow) { Get-CalledMode -Text (Get-Content -Path $workflow -Raw) }
    })
  $prModes = Get-CalledMode -Text (Get-Content -Path (Join-Path -Path $Root -ChildPath 'scripts/Run-PR.ps1') -Raw)

  $projects = @(Get-DeclaredTestProject -Root $Root)
  foreach ($project in $projects | Where-Object { -not $_.Type }) {
    "$($project.Path): a test project (IsTestProject) that declares no <WhizbangTestType>, so no tool selects it"
  }
  foreach ($type in @($projects | Where-Object { $_.Type } | ForEach-Object { $_.Type } | Sort-Object -Unique)) {
    if (-not $types.Contains($type)) {
      "${type}: scripts/Run-Tests.ps1 does not know it (no entry in `$WhizbangTestTypes)"
    } else {
      $entry = $types[$type]
      $modes = @($entry['Modes'])
      foreach ($mode in $modes) {
        if ($mode -notin $modeSet) { "${type}: scripts/Run-Tests.ps1 -Mode does not accept '$mode'" }
        if ($mode -notin $logModeSet) { "${type}: scripts/Run-Tests.ps1 -LogMode does not accept '$mode'" }
      }
      $modeList = $modes -join ' or '
      if ($modes.Count -eq 0) {
        if (-not ($entry.Contains('RunBy') -and $entry['RunBy'])) { "${type}: scripts/Run-Tests.ps1 has no -Mode for it and names no RunBy (what runs it)" }
      } else {
        if (-not ($modes | Where-Object { $_ -in $ciModes })) {
          "${type}: no CI suite runs it (no reusable-test-*.yml called by a job Test-CiResult.ps1 counts runs Run-Tests.ps1 -Mode $modeList)"
        }
        if ($entry['InAll'] -and -not ($modes | Where-Object { $_ -in $prModes })) {
          "${type}: scripts/Run-PR.ps1 does not run it (no Run-Tests.ps1 -Mode $modeList)"
        }
      }
    }
    if ($type -notin $targetTypes) { "${type}: Directory.Build.targets does not know it (not in <WhizbangKnownTestTypes>)" }
    if ($type -notin $solutionTypes) { "${type}: scripts/Test-SolutionTestProjects.ps1 does not know it (not in `$KnownTestTypes)" }
    if ($type -notin $slicedTypes -and $type -notin $unslicedTypes) {
      "${type}: .github/scripts/Get-TestSlice.ps1 does not know it (no suite slice of that Type, and not in `$UnslicedTypes)"
    }
  }
}

function Get-CiSuiteListProblem {
  param([Parameter(Mandatory)] [string]$Root)
  $gated = @(Get-AssignedValue -Path (Join-Path -Path $Root -ChildPath '.github/scripts/Test-CiResult.ps1') -Name 'Suites')
  $jobs = Get-CiJob -Root $Root
  foreach ($suite in $gated | Where-Object { -not $jobs.Contains($_) }) {
    "${suite}: .github/scripts/Test-CiResult.ps1 counts it, but ci.yml has no such job"
  }
  foreach ($job in $jobs.Keys) {
    if ($jobs[$job] -match 'uses:\s*\./\.github/workflows/reusable-test-' -and $job -notin $gated) {
      "${job}: runs a reusable-test-*.yml suite, but .github/scripts/Test-CiResult.ps1 does not count it"
    }
  }
  foreach ($consumer in $script:SuiteConsumers) {
    if (-not $jobs.Contains($consumer)) {
      "${consumer}: ci.yml has no such job, so no suite can be checked against its needs"
      continue
    }
    $needs = Get-JobNeed -Job $jobs[$consumer]
    foreach ($suite in $gated | Where-Object { $_ -notin $needs }) {
      "${suite}: missing from the needs of ci.yml job $consumer"
    }
    if ($consumer -in $script:SuiteGatedPublishes) {
      foreach ($suite in $gated | Where-Object { -not $jobs[$consumer].Contains("needs.$_.result == 'success'") }) {
        "${suite}: ci.yml job $consumer does not require needs.$suite.result == 'success'"
      }
    }
  }
}

# Dot-sourcing for tests loads the functions without running the check.
if ($MyInvocation.InvocationName -eq '.') { return }

$problems = @(Get-WhizbangTestTypeProblem -Root $Root) + @(Get-CiSuiteListProblem -Root $Root)
if ($problems.Count -gt 0) {
  Write-Host 'Test types or CI suites that a tool does not know:'
  $problems | ForEach-Object { Write-Host "  $_" }
  Write-Host 'Teach every reader the type (docs/TEST-PROJECTS.md, "Adding a test type"), or add the suite to every list.'
  exit 1
}
Write-Host 'Every declared test type is known to every reader, and every CI suite is in every list.'
