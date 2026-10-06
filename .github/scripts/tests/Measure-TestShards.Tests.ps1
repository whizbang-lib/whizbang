#Requires -Modules Pester

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/Measure-TestShards.ps1')
  $script:T0 = [datetime]'2026-10-05T12:00:00Z'
  # Each entry is (class, start second, end second) relative to T0.
  function ConvertTo-Result([object[]]$Entries) {
    foreach ($e in $Entries) { [pscustomobject]@{ Class = $e[0]; Start = $T0.AddSeconds($e[1]); End = $T0.AddSeconds($e[2]) } }
  }
}

Describe 'Get-ClassCost' {
  It 'charges each test the gap to the next start, so the costs sum to the whole test window' {
    # A has short bodies but each one waits a second for the next to start (database provisioning);
    # durations would say A cost 0.3s, the window says 3s.
    $results = @(ConvertTo-Result -Entries @(@('A', 0, 0.1), @('A', 1, 1.1), @('A', 2, 2.1), @('B', 3, 3.5), @('B', 3.5, 5)))
    $cost = Get-ClassCost $results
    $cost['A'] | Should -Be 3
    $cost['B'] | Should -Be 2
    ($cost.Values | Measure-Object -Sum).Sum | Should -Be 5
  }

  It 'does not depend on the input order' {
    $results = @(ConvertTo-Result -Entries @(@('B', 3, 4), @('A', 0, 1), @('A', 1, 3)))
    $cost = Get-ClassCost $results
    $cost['A'] | Should -Be 3
    $cost['B'] | Should -Be 1
  }

  It 'never charges a negative gap for overlapping tests' {
    $cost = Get-ClassCost @(ConvertTo-Result -Entries @(@('A', 0, 5), @('B', 0, 5)))
    $cost['A'] | Should -BeGreaterOrEqual 0
    $cost['B'] | Should -BeGreaterOrEqual 0
  }
}

Describe 'Get-ShardPlan' {
  It 'moves nothing when the shards are already within tolerance' {
    $plan = Get-ShardPlan -Assignment @{ a = 1; b = 2 } -Cost @{ a = 100; b = 90 } -ShardCount 2 -ToleranceSeconds 30
    $plan.Moves.Count | Should -Be 0
  }

  It 'balances from the current assignment with few moves' {
    $assignment = @{ a = 1; b = 1; c = 1; d = 1; e = 2 }
    $cost = @{ a = 400; b = 300; c = 200; d = 100; e = 100 }
    $plan = Get-ShardPlan -Assignment $assignment -Cost $cost -ShardCount 2 -ToleranceSeconds 60
    $spread = ($plan.Loads.Values | Measure-Object -Maximum).Maximum - ($plan.Loads.Values | Measure-Object -Minimum).Minimum
    $spread | Should -BeLessOrEqual 200
    $plan.Moves.Count | Should -BeLessOrEqual 2
  }

  It 'fills a newly added shard' {
    $assignment = @{ a = 1; b = 1; c = 2; d = 2 }
    $cost = @{ a = 300; b = 300; c = 300; d = 300 }
    $plan = Get-ShardPlan -Assignment $assignment -Cost $cost -ShardCount 3 -ToleranceSeconds 10
    $plan.Loads[3] | Should -BeGreaterThan 0
  }

  It 'never moves a class larger than half the gap, so a move cannot widen it' {
    # The only class on the heavy shard is bigger than half the gap: moving it would just swap sides.
    $plan = Get-ShardPlan -Assignment @{ big = 1; small = 2 } -Cost @{ big = 1000; small = 100 } -ShardCount 2 -ToleranceSeconds 10
    $plan.Moves.Count | Should -Be 0
  }

  It 'keeps the total load and every class' {
    $assignment = @{ a = 1; b = 1; c = 1; d = 2; e = 3 }
    $cost = @{ a = 500; b = 250; c = 125; d = 60; e = 30 }
    $plan = Get-ShardPlan -Assignment $assignment -Cost $cost -ShardCount 3 -ToleranceSeconds 20
    ($plan.Loads.Values | Measure-Object -Sum).Sum | Should -Be 965
    @($plan.Assignment.Keys).Count | Should -Be 5
  }

  It 'counts an unmeasured class as costing nothing and never moves it' {
    $plan = Get-ShardPlan -Assignment @{ a = 1; b = 1; new = 1 } -Cost @{ a = 300; b = 300 } -ShardCount 2 -ToleranceSeconds 10
    $plan.Assignment['new'] | Should -Be 1
  }
}

Describe 'Get-ShardAssignment' {
  It 'reads the shard category of every class, including after a multi-line attribute' {
    $dir = Join-Path -Path $TestDrive -ChildPath 'proj'
    New-Item -ItemType Directory -Path $dir | Out-Null
    Set-Content -Path (Join-Path -Path $dir -ChildPath 'A.cs') -Value @'
[Category("Integration")]
[Category("Shard1")]
[SuppressMessage("x", "y",
    Justification = "spans lines ] with a bracket")]
public class FirstTests { }

[Category("Shard3")]
// Serialized against every other class that touches the shared switch.
public sealed class SecondTests { }
'@
    $map = Get-ShardAssignment $dir
    $map['FirstTests'].Shard | Should -Be 1
    $map['SecondTests'].Shard | Should -Be 3
  }
}

Describe 'the EFCore shard matrix' {
  It 'gives every shard category in the sources a CI matrix leg, and every leg some classes' {
    $root = Join-Path -Path $PSScriptRoot -ChildPath '../../..'
    $used = @((Get-ShardAssignment (Join-Path -Path $root -ChildPath 'tests/Whizbang.Data.EFCore.Postgres.Tests')).Values |
      ForEach-Object { $_.Shard } | Sort-Object -Unique)
    $ci = Get-Content (Join-Path -Path $root -ChildPath '.github/workflows/ci.yml') -Raw
    $legs = @([regex]::Matches($ci, "testFilter: '\[Category=Shard(\d+)\]'") | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Unique)
    $used.Count | Should -BeGreaterThan 1 -Because 'the scan must find the shard categories for this check to mean anything'
    $legs | Should -Be $used -Because 'a shard category with no matrix leg runs in no job, and a leg with no classes runs nothing'
  }
}
