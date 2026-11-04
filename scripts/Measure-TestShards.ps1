#!/usr/bin/env pwsh
# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT

#Requires -Version 7.0

<#
.SYNOPSIS
    Measures CI test-suite timings and test-shard balance, and proposes (or applies) a rebalance.

.DESCRIPTION
    The test suites run in parallel, so a pull request waits for the SLOWEST one. This script answers
    whether that is still a sharded suite, why, and what to move. It is the routine described in
    ai-docs/test-sharding.md, run monthly by .github/workflows/test-shard-report.yml.

    1. Suite timings. For the last -Runs successful CI runs that ran the full matrix (pull request
       and merge queue runs with at least ten test jobs), every "Test" job's total and test-step
       time: median, p90, max, coefficient of variation, which suite was slowest per run, and the
       spread between the sharded suite's shards.

    2. Class cost. For the last -TrxRuns (default 10) of those runs, each shard's TRX results. A shard's time is
       NOT the sum of its test durations: its tests run in parallel (#2069), and time can pass between test
       bodies while the next test's database is provisioned. Each moment is therefore shared among the tests
       running then, and an idle moment goes to the test that started last before it, which sums exactly to
       the shard's test window; a class's cost is the median of its sums.

    3. Rebalance. Starting from the current [Category("ShardN")] assignment, repeatedly move the
       class from the heaviest shard to the lightest that best closes the gap without overshooting,
       until the shards are within -ToleranceMinutes. Starting from the current assignment keeps
       each rebalance a small diff rather than a reshuffle. -ShardCount above the current count
       adds empty shards to fill.

    Prints a markdown report (and writes it to -OutFile). -Apply rewrites the moved classes' shard
    categories in source; a new shard also needs its matrix leg in ci.yml, which
    .github/scripts/tests/Test-ShardMatrix.Tests.ps1 enforces.

    Needs the gh CLI, authenticated, with read access to the repository's Actions.

.PARAMETER Runs
    Recent full-matrix CI runs to measure suite timings over. Default 25.

.PARAMETER TrxRuns
    Recent runs whose shard TRX results are analyzed for class cost. Default 10: two 5-run samples
    taken hours apart disagreed by about 3 minutes on one shard and proposed different moves.

.PARAMETER ShardCount
    Shards to balance across. 0 (default) keeps the current count.

.PARAMETER ToleranceMinutes
    Stop rebalancing once the heaviest and lightest shard are within this. Default 1.

.PARAMETER Apply
    Rewrite the moved classes' [Category("ShardN")] in source.

.PARAMETER OutFile
    Also write the markdown report here.

.EXAMPLE
    pwsh scripts/Measure-TestShards.ps1
    pwsh scripts/Measure-TestShards.ps1 -ShardCount 5 -Apply
#>

param(
    [Parameter()] [int]$Runs = 25,
    [Parameter()] [int]$TrxRuns = 10,
    [Parameter()] [int]$ShardCount = 0,
    [Parameter()] [double]$ToleranceMinutes = 1.0,
    [Parameter()] [switch]$Apply,
    [Parameter()] [string]$OutFile = '',
    [Parameter()] [string]$Repository = 'whizbang-lib/whizbang',
    [Parameter()] [string]$ProjectDirectory = 'tests/Whizbang.Data.EFCore.Postgres.Tests',
    [Parameter()] [string]$ShardArtifactPrefix = 'trx-postgres-efcore-'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# Pure functions (unit-tested in .github/scripts/tests/Measure-TestShards.Tests.ps1)
# ---------------------------------------------------------------------------------------------

function Get-Quantile([double[]]$Values, [double]$P) {
  $v = @($Values | Sort-Object)
  if ($v.Count -eq 0) { return 0.0 }
  $k = ($v.Count - 1) * $P
  $f = [math]::Floor($k)
  $c = [math]::Min($f + 1, $v.Count - 1)
  return $v[$f] + ($v[$c] - $v[$f]) * ($k - $f)
}

<#
    Charges each class the time its tests occupied the shard, and sums per class. Every moment between two test
    boundaries is shared equally among the tests running then, so a serial shard charges each test its whole
    duration and a parallel one (#2069) charges a test its share of the time it ran beside others. A moment when no
    test runs (the next test's database being provisioned) goes to the test that started last before it, as the gap
    to the next start did when shards ran one test at a time. The costs sum to the shard's test window.
    $Results: objects with Class, Start ([datetime]) and End ([datetime]).
#>
function Get-ClassCost([object[]]$Results) {
  $tests = @($Results | Sort-Object Start)
  $cost = @{}
  foreach ($t in $tests) { $cost[$t.Class] = 0.0 }
  # One sweep over the boundaries: ends sort before starts at the same instant, so a test that ends as the next
  # begins never shares that instant with it.
  $events = [System.Collections.Generic.List[object]]::new()
  for ($i = 0; $i -lt $tests.Count; $i++) {
    $end = $(if ($tests[$i].End -gt $tests[$i].Start) { $tests[$i].End } else { $tests[$i].Start })
    $events.Add([pscustomobject]@{ At = $tests[$i].Start; Kind = 1; Index = $i })
    $events.Add([pscustomobject]@{ At = $end; Kind = 0; Index = $i })
  }
  $events = @($events | Sort-Object At, Kind)
  $running = [System.Collections.Generic.HashSet[int]]::new()
  $lastStarted = -1
  for ($e = 0; $e -lt $events.Count; $e++) {
    $ev = $events[$e]
    if ($ev.Kind -eq 1) { [void]$running.Add($ev.Index); $lastStarted = $ev.Index } else { [void]$running.Remove($ev.Index) }
    if ($e + 1 -ge $events.Count) { break }
    $seconds = ($events[$e + 1].At - $ev.At).TotalSeconds
    if ($seconds -le 0) { continue }
    if ($running.Count -gt 0) {
      foreach ($i in $running) { $cost[$tests[$i].Class] += $seconds / $running.Count }
    } else {
      $cost[$tests[$lastStarted].Class] += $seconds
    }
  }
  return $cost
}

<#
    Minimal-move rebalance. $Assignment: class -> shard number. $Cost: class -> seconds (a class with
    no measurement costs 0). Returns the new assignment, the moves made and the per-shard loads.
#>
function Get-ShardPlan([hashtable]$Assignment, [hashtable]$Cost, [int]$ShardCount, [double]$ToleranceSeconds) {
  $plan = @{}
  foreach ($k in $Assignment.Keys) { $plan[$k] = $Assignment[$k] }
  $count = [math]::Max($ShardCount, (@($plan.Values) + 1 | Measure-Object -Maximum).Maximum)
  $costOf = { param($c) if ($Cost.ContainsKey($c)) { [double]$Cost[$c] } else { 0.0 } }
  $loads = @{}
  for ($s = 1; $s -le $count; $s++) { $loads[$s] = 0.0 }
  foreach ($c in $plan.Keys) { $loads[$plan[$c]] += & $costOf $c }
  $moves = [System.Collections.Generic.List[object]]::new()
  for ($guard = 0; $guard -lt 10000; $guard++) {
    $heavy = ($loads.GetEnumerator() | Sort-Object Value, Name -Descending | Select-Object -First 1).Name
    $light = ($loads.GetEnumerator() | Sort-Object Value, Name | Select-Object -First 1).Name
    $gap = $loads[$heavy] - $loads[$light]
    if ($gap -le $ToleranceSeconds) { break }
    # The class that best closes the gap: the largest one no bigger than half of it, so the move can
    # never make the pair further apart than it was. Ties go to the alphabetically first class.
    $candidate = $plan.Keys |
      Where-Object { $plan[$_] -eq $heavy -and (& $costOf $_) -gt 0 -and (& $costOf $_) -le $gap / 2 } |
      Sort-Object @{ Expression = { & $costOf $_ }; Descending = $true }, @{ Expression = { $_ } } |
      Select-Object -First 1
    if (-not $candidate) { break }
    $c = & $costOf $candidate
    $plan[$candidate] = $light
    $loads[$heavy] -= $c
    $loads[$light] += $c
    $moves.Add([pscustomobject]@{ Class = $candidate; From = $heavy; To = $light; Seconds = $c })
  }
  return [pscustomobject]@{ Assignment = $plan; Moves = @($moves); Loads = $loads }
}

<#
    Maps every test class in a project's sources to its [Category("ShardN")]: the last shard category
    between the previous class declaration and this one, which handles multi-line attribute lists.
    Returns class -> @{ File; Shard }.
#>
$script:ClassDeclaration = '(?m)^[ \t]*(?:(?:public|internal|private|protected|sealed|abstract|static|partial|file)[ \t]+)*class[ \t]+(\w+)'

function Get-ShardAssignment([string]$Directory) {
  $map = @{}
  foreach ($file in Get-ChildItem -Path $Directory -Recurse -Filter '*.cs' -File | Where-Object { $_.FullName -notmatch '[/\\](bin|obj)[/\\]' }) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    $previous = 0
    # A declaration starts its line (after modifiers), so prose such as "every class that touches"
    # in a comment is never mistaken for one.
    foreach ($m in [regex]::Matches($text, $script:ClassDeclaration)) {
      $window = $text.Substring($previous, $m.Index - $previous)
      $shards = [regex]::Matches($window, 'Category\("Shard(\d+)"\)')
      if ($shards.Count -gt 0) {
        $map[$m.Groups[1].Value] = @{ File = $file.FullName; Shard = [int]$shards[$shards.Count - 1].Groups[1].Value }
      }
      $previous = $m.Index + $m.Length
    }
  }
  return $map
}

# Dot-sourcing for tests loads the functions without calling GitHub.
if ($MyInvocation.InvocationName -eq '.') { return }

# ---------------------------------------------------------------------------------------------
# Collection
# ---------------------------------------------------------------------------------------------

$repoRoot = Split-Path -Parent $PSScriptRoot
# The middle dot in CI job names ("Test <dot> Unit"), kept ASCII-only in source.
$Dot = [char]0x00B7
$report = [System.Text.StringBuilder]::new()
function Add-Line([string]$Line = '') { [void]$report.AppendLine($Line) }

function Get-Duration($Item) { (([datetime]$Item.completed_at) - ([datetime]$Item.started_at)).TotalSeconds }

Write-Information "Reading recent CI runs..." -InformationAction Continue
$since = (Get-Date).ToUniversalTime().AddDays(-14).ToString('yyyy-MM-dd')
$candidates = @(gh api "repos/$Repository/actions/workflows/ci.yml/runs?status=success&per_page=100&created=%3E%3D$since" |
  ConvertFrom-Json | Select-Object -ExpandProperty workflow_runs |
  Where-Object { $_.event -in @('pull_request', 'merge_group') } |
  # Sorted here rather than trusted: the list API has been seen to return a stale page whose
  # newest entries were a day old, which picks runs whose artifacts have already expired.
  Sort-Object { [datetime]$_.created_at } -Descending)

# Named in the log, so a stale run list (seen twice: its newest entry a day old, every artifact
# already expired) identifies itself instead of surfacing later as "no TRX artifacts found".
if ($candidates.Count -gt 0) {
  Write-Information "Candidate runs since ${since}: $($candidates.Count); newest created $($candidates[0].created_at)" -InformationAction Continue
}
$jobsByRun = [ordered]@{}
foreach ($run in $candidates) {
  if ($jobsByRun.Count -ge $Runs) { break }
  $jobs = @((gh api "repos/$Repository/actions/runs/$($run.id)/jobs?per_page=100" | ConvertFrom-Json).jobs |
    Where-Object { $_.name -like "Test $Dot *" -and $_.name -notlike '*Pipeline scripts*' -and $_.conclusion -eq 'success' })
  if ($jobs.Count -ge 10) { $jobsByRun[[string]$run.id] = $jobs }
}
if ($jobsByRun.Count -eq 0) { throw 'No recent CI run ran the full test matrix.' }

$suites = @{}
$slowest = @{}
foreach ($runId in $jobsByRun.Keys) {
  $perRun = @{}
  foreach ($job in $jobsByRun[$runId]) {
    $name = $job.name -replace "^Test $Dot ", '' -replace ' / Run suite$', ''
    $total = Get-Duration $job
    $testStep = (@($job.steps | Where-Object { $_.name -match '^Run .*[Tt]ests' -and $_.started_at -and $_.completed_at }) |
      ForEach-Object { Get-Duration $_ } | Measure-Object -Sum).Sum
    if (-not $suites.ContainsKey($name)) { $suites[$name] = [System.Collections.Generic.List[object]]::new() }
    $suites[$name].Add([pscustomobject]@{ Total = $total; Tests = [double]$testStep })
    $perRun[$name] = $total
  }
  $top = ($perRun.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1)
  $slowest[$top.Name] = $(if ($slowest.ContainsKey($top.Name)) { $slowest[$top.Name] } else { 0 }) + 1
}

Add-Line "# Test shard balance report"
Add-Line
Add-Line "Generated $(Get-Date -Format 'yyyy-MM-dd') from the last $($jobsByRun.Count) full-matrix CI runs by ``scripts/Measure-TestShards.ps1`` (see ``ai-docs/test-sharding.md``)."
Add-Line
Add-Line "## Suites"
Add-Line
Add-Line "| Suite | Median | p90 | Max | CV | Test step (median) | Slowest in |"
Add-Line "|---|---|---|---|---|---|---|"
foreach ($name in ($suites.Keys | Sort-Object { -(Get-Quantile ([double[]]($suites[$_].Total)) 0.5) })) {
  $t = [double[]]($suites[$name].Total)
  $mean = ($t | Measure-Object -Average).Average
  $sd = [math]::Sqrt((($t | ForEach-Object { ($_ - $mean) * ($_ - $mean) } | Measure-Object -Sum).Sum) / $t.Count)
  $slow = $(if ($slowest.ContainsKey($name)) { "$($slowest[$name]) of $($jobsByRun.Count)" } else { '' })
  Add-Line ("| {0} | {1:n1}m | {2:n1}m | {3:n1}m | {4:n2} | {5:n1}m | {6} |" -f $name,
    ((Get-Quantile $t 0.5) / 60), ((Get-Quantile $t 0.9) / 60), (($t | Measure-Object -Maximum).Maximum / 60),
    ($sd / $mean), ((Get-Quantile ([double[]]($suites[$name].Tests)) 0.5) / 60), $slow)
}

# ---------------------------------------------------------------------------------------------
# Shard class costs
# ---------------------------------------------------------------------------------------------

$work = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath "shard-trx-$PID"
New-Item -ItemType Directory -Path $work -Force | Out-Null
$ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
$classRuns = @{}       # class -> list of per-run cost
$shardWindows = @{}    # shard -> list of test-window seconds
$used = 0
# Every candidate, newest first, not only the timing sample: TRX artifacts expire after a few days,
# so the runs that still have them are the newest few whatever the sample holds.
foreach ($runId in @($candidates | ForEach-Object { [string]$_.id } | Select-Object -First 40)) {
  if ($used -ge $TrxRuns) { break }
  $artifacts = @((gh api "repos/$Repository/actions/runs/$runId/artifacts?per_page=100" | ConvertFrom-Json).artifacts |
    Where-Object { $_.name -like "$ShardArtifactPrefix*" -and -not $_.expired })
  if ($artifacts.Count -eq 0) { continue }
  $used++
  foreach ($artifact in $artifacts) {
    $shard = [int]($artifact.name.Substring($ShardArtifactPrefix.Length))
    $dir = Join-Path -Path $work -ChildPath "$runId-$shard"
    # Reported, never discarded: a download that fails silently leaves nothing to measure, and the
    # only symptom is "no TRX artifacts found" with no hint why.
    $downloadOutput = gh run download $runId --repo $Repository --name $artifact.name --dir $dir 2>&1
    if ($LASTEXITCODE -ne 0) {
      Write-Warning "Could not download $($artifact.name) from run ${runId}: $($downloadOutput -join ' ')"
      continue
    }
    foreach ($trx in Get-ChildItem -Path $dir -Recurse -Filter '*.trx') {
      [xml]$xml = [System.IO.File]::ReadAllText($trx.FullName)
      $classOf = @{}
      foreach ($d in (Select-Xml -Xml $xml -XPath '//t:UnitTest' -Namespace $ns)) {
        $classOf[$d.Node.id] = (($d.Node.TestMethod.className -split '\.')[-1] -split '\+')[-1]
      }
      $results = @(Select-Xml -Xml $xml -XPath '//t:UnitTestResult' -Namespace $ns | ForEach-Object { $_.Node } |
        Where-Object { $_.startTime -and $_.endTime } |
        ForEach-Object { [pscustomobject]@{ Class = $classOf[$_.testId]; Start = [datetime]$_.startTime; End = [datetime]$_.endTime } })
      if ($results.Count -eq 0) { continue }
      $cost = Get-ClassCost $results
      foreach ($c in $cost.Keys) {
        if (-not $classRuns.ContainsKey($c)) { $classRuns[$c] = [System.Collections.Generic.List[double]]::new() }
        $classRuns[$c].Add($cost[$c])
      }
      $window = (($cost.Values | Measure-Object -Sum).Sum)
      if (-not $shardWindows.ContainsKey($shard)) { $shardWindows[$shard] = [System.Collections.Generic.List[double]]::new() }
      $shardWindows[$shard].Add($window)
    }
  }
}
Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
if ($classRuns.Count -eq 0) { throw "No $ShardArtifactPrefix* TRX artifacts found in the measured runs (they expire after a few days)." }

$classCost = @{}
foreach ($c in $classRuns.Keys) { $classCost[$c] = Get-Quantile ([double[]]$classRuns[$c]) 0.5 }

$assignmentInfo = Get-ShardAssignment (Join-Path -Path $repoRoot -ChildPath $ProjectDirectory)
$assignment = @{}
foreach ($c in $assignmentInfo.Keys) { $assignment[$c] = $assignmentInfo[$c].Shard }
$unmapped = @($classCost.Keys | Where-Object { -not $assignment.ContainsKey($_) })
$currentCount = ($assignment.Values | Measure-Object -Maximum).Maximum
$target = $(if ($ShardCount -gt 0) { $ShardCount } else { $currentCount })
$plan = Get-ShardPlan -Assignment $assignment -Cost $classCost -ShardCount $target -ToleranceSeconds ($ToleranceMinutes * 60)

Add-Line
Add-Line "## Shards ($ProjectDirectory)"
Add-Line
Add-Line "Class cost is attributed from the last $used runs' TRX results (see the script for why it is not the sum of test durations)."
Add-Line
Add-Line "| Shard | Measured test window (median) | Load by attributed cost, now | Proposed |"
Add-Line "|---|---|---|---|"
for ($s = 1; $s -le [math]::Max($target, $currentCount); $s++) {
  $now = (@($assignment.Keys | Where-Object { $assignment[$_] -eq $s } | ForEach-Object { if ($classCost.ContainsKey($_)) { $classCost[$_] } else { 0 } }) | Measure-Object -Sum).Sum
  $measured = $(if ($shardWindows.ContainsKey($s)) { '{0:n1}m' -f ((Get-Quantile ([double[]]$shardWindows[$s]) 0.5) / 60) } else { '-' })
  $proposed = $(if ($plan.Loads.ContainsKey($s)) { '{0:n1}m' -f ($plan.Loads[$s] / 60) } else { '-' })
  Add-Line ("| Shard{0} | {1} | {2:n1}m | {3} |" -f $s, $measured, ($now / 60), $proposed)
}
Add-Line
if ($plan.Moves.Count -eq 0) {
  Add-Line "No moves needed: the shards are within $ToleranceMinutes minute(s) of each other."
} else {
  Add-Line "Proposed moves ($($plan.Moves.Count)):"
  Add-Line
  Add-Line "| Class | From | To | Cost |"
  Add-Line "|---|---|---|---|"
  foreach ($m in $plan.Moves) { Add-Line ("| {0} | Shard{1} | Shard{2} | {3:n0}s |" -f $m.Class, $m.From, $m.To, $m.Seconds) }
}
if ($unmapped.Count -gt 0) {
  Add-Line
  Add-Line "Measured classes with no shard category in source (renamed or removed since those runs): $($unmapped -join ', ')"
}

if ($Apply -and $plan.Moves.Count -gt 0) {
  foreach ($m in $plan.Moves) {
    $info = $assignmentInfo[$m.Class]
    $text = [System.IO.File]::ReadAllText($info.File)
    $decl = @([regex]::Matches($text, $script:ClassDeclaration) | Where-Object { $_.Groups[1].Value -eq $m.Class })[0]
    $previous = @([regex]::Matches($text.Substring(0, $decl.Index), $script:ClassDeclaration))
    $start = $(if ($previous.Count -gt 0) { $previous[$previous.Count - 1].Index + $previous[$previous.Count - 1].Length } else { 0 })
    $window = $text.Substring($start, $decl.Index - $start)
    $hits = [regex]::Matches($window, "Category\(""Shard$($m.From)""\)")
    if ($hits.Count -eq 0) { throw "Could not find Shard$($m.From) on $($m.Class) in $($info.File)." }
    $at = $start + $hits[$hits.Count - 1].Index
    $old = $hits[$hits.Count - 1].Value
    $text = $text.Substring(0, $at) + "Category(""Shard$($m.To)"")" + $text.Substring($at + $old.Length)
    [System.IO.File]::WriteAllText($info.File, $text)
  }
  Add-Line
  Add-Line "Applied: $($plan.Moves.Count) class(es) re-tagged. A new shard also needs its matrix leg in .github/workflows/ci.yml."
}

$markdown = $report.ToString()
if ($OutFile) { [System.IO.File]::WriteAllText($OutFile, $markdown) }
Write-Output $markdown
