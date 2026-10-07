#Requires -Modules Pester

# The new-code coverage gate decides what merges: a new line no test ran, or a new hand-written decision
# some outcome of which no test took, fails the PR. These tests pin both lists and the rule that tells a
# hand-written decision from the branches the compiler adds (async state machines, initializers).

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/Find-UncoveredNewLines.ps1') -CoverageRoot x -BaseRef y

  # One Cobertura report: $lines is a list of @{ n; hits; cov } where cov is "covered/total" or $null,
  # and an optional conds, the collector's per-condition detail: a list of @{ type; pct }.
  function New-Report([string]$dir, [string]$name, [string]$file, [object[]]$lines) {
    $lineXml = ($lines | ForEach-Object {
      if ($_.cov) {
        $pct = [int](100 * [int]($_.cov -split '/')[0] / [int]($_.cov -split '/')[1])
        $condXml = ''
        if ($_.conds) {
          $i = 0
          $condXml = '<conditions>' + (($_.conds | ForEach-Object { "<condition number=`"$(($i++))`" type=`"$($_.type)`" coverage=`"$($_.pct)%`" />" }) -join '') + '</conditions>'
        }
        "<line number=`"$($_.n)`" hits=`"$($_.hits)`" branch=`"True`" condition-coverage=`"$pct% ($($_.cov))`">$condXml</line>"
      } else {
        "<line number=`"$($_.n)`" hits=`"$($_.hits)`" branch=`"False`" />"
      }
    }) -join ''
    $xml = "<?xml version=`"1.0`"?><coverage><sources><source>/_/</source></sources><packages><package name=`"P`"><classes>" +
      "<class name=`"C`" filename=`"/_/$file`"><lines>$lineXml</lines></class></classes></package></packages></coverage>"
    $path = Join-Path $dir $name
    Set-Content -Path $path -Value $xml
    return $path
  }

  # Real collector output (Microsoft.Testing.Extensions.CodeCoverage, the version CI uses) for a two-method
  # library, from two test processes: A calls Sign(1) and Pick(1), B calls Sign(-1) and Pick(-1). Each
  # process takes one outcome of Sign's `if`, so each report says 1/2 for that line although together they
  # take both. merged-blocks.xml is `dotnet-coverage merge A.coverage B.coverage -f xml`. Calc.cs.txt is
  # the library source the reports were collected from (line numbers matter).
  $script:Fixture = Join-Path -Path $PSScriptRoot -ChildPath 'fixtures/shard-union'
  $script:FixturePath = 'src/Whizbang.Exp/Calc.cs'

  function New-Added([string]$file, [int[]]$lineNumbers) {
    $set = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($n in $lineNumbers) { [void]$set.Add($n) }
    return @{ $file = $set }
  }
}

Describe 'Test-HandWrittenDecision' {
  It 'counts <line>' -ForEach @(
      @{ line = 'if (count > 0) {' }, @{ line = '} else if (count < 0) {' },
      @{ line = 'return count > 0 ? "some" : "none";' }, @{ line = '? perspectives[0]' }, @{ line = ': null;' },
      @{ line = 'var name = given ?? fallback;' }, @{ line = 'cache ??= new Cache();' },
      @{ line = 'logger?.LogDebug("x");' }, @{ line = 'var first = items?[0];' },
      @{ line = 'return a && b;' }, @{ line = 'return a || b;' },
      @{ line = 'switch (kind) {' }, @{ line = 'case Kind.A:' }, @{ line = 'var size = kind switch {' },
      @{ line = 'Kind.A => 1,' }, @{ line = '_ => throw new InvalidOperationException()' }, @{ line = '{ IsTerminal: true } => false,' },
      @{ line = '} catch (TimeoutException ex) when (ex.Data.Count > 0) {' }, @{ line = '} catch (Exception) {' },
      @{ line = 'while (reader.Read()) {' }, @{ line = 'for (var i = 0; i < n; i++) {' }, @{ line = 'foreach (var row in rows) {' },
      @{ line = 'return value is string s && s.Length > 0;' }, @{ line = 'if (value is not null) {' },
      @{ line = 'var text = $"{(ok ? "yes" : "no")} done";' }, @{ line = 'Log($"{name ?? "none"}");' }) {
    Test-HandWrittenDecision $line | Should -BeTrue
  }

  It 'excludes <line>' -ForEach @(
      @{ line = 'await store.SaveAsync(item, cancellationToken).ConfigureAwait(false);' },
      @{ line = 'var result = await reader.ReadAsync(cancellationToken);' },
      @{ line = 'return new OutboxMessage {' }, @{ line = 'var hop = new MessageHop {' },
      @{ line = 'await _emitter.PublishAsync(new TickEvent {' }, @{ line = '}' }, @{ line = '' },
      @{ line = 'string? name = null;' }, @{ line = 'List<int?> values = [];' },
      @{ line = 'Log("a ?? b && c || d ? e : f");' }, @{ line = 'var x = 1; // if (x) a ?? b' },
      @{ line = 'var c = '':'';' }, @{ line = 'var text = $"{count} items {{literal ?? braces}}";' },
      @{ line = 'public int Size => _size;' }, @{ line = 'get => _value;' }, @{ line = 'items.Select(x => x.Id)' },
      @{ line = 'public sealed class Worker : BackgroundService {' }) {
    Test-HandWrittenDecision $line | Should -BeFalse
  }
}

Describe 'Read-CoberturaCoverage' {
  It 'keeps the most hits and the best condition coverage a line has in any report' {
    $a = New-Report $TestDrive 'a.cobertura.xml' 'src/P/A.cs' @(@{ n = 10; hits = 0; cov = '1/2' }, @{ n = 11; hits = 3; cov = $null })
    $b = New-Report $TestDrive 'b.cobertura.xml' 'src/P/A.cs' @(@{ n = 10; hits = 2; cov = '2/2' }, @{ n = 11; hits = 1; cov = $null })

    $coverage = Read-CoberturaCoverage @($a, $b)

    $coverage['src/P/A.cs'].Hits[10] | Should -Be 2
    $coverage['src/P/A.cs'].Hits[11] | Should -Be 3
    $coverage['src/P/A.cs'].Conditions[10] | Should -Be @(2, 2)
    $coverage['src/P/A.cs'].Conditions.ContainsKey(11) | Should -BeFalse
  }
}

Describe 'Get-UncoveredNewCode' {
  BeforeAll {
    $script:File = 'src/P/A.cs'
    $script:Source = @{
      1 = 'if (x) {'; 2 = 'await store.SaveAsync(item);'; 3 = 'return a ?? b;'; 4 = 'return new Hop {'
      5 = 'Do();'; 6 = 'if (y) {'; 7 = 'if (z) {'
    }
    $script:ReadSource = { param($p) 1..7 | ForEach-Object { $script:Source[$_] } }
    $report = New-Report $TestDrive 'c.cobertura.xml' $script:File @(
      @{ n = 1; hits = 4; cov = '1/2' },  # a decision with an untaken outcome
      @{ n = 2; hits = 4; cov = '1/2' },  # an await's state-machine branch: compiler-generated
      @{ n = 3; hits = 4; cov = '2/2' },  # every outcome taken
      @{ n = 4; hits = 4; cov = '1/2' },  # an initializer's branch: compiler-generated
      @{ n = 5; hits = 0; cov = $null },  # never ran
      @{ n = 6; hits = 0; cov = '0/2' },  # never ran: reported as a line, not also as a branch
      @{ n = 7; hits = 4; cov = '1/2' })  # a decision, but not added by this branch
    $script:Coverage = Read-CoberturaCoverage @($report)
  }

  It 'lists every added line no test ran, once, as a line' {
    $result = Get-UncoveredNewCode -Added (New-Added $script:File (1..6)) -Coverage $script:Coverage -ReadSource $script:ReadSource
    $result.Lines | Should -Be @("${script:File}:5: Do();", "${script:File}:6: if (y) {")
  }

  It 'lists an added hand-written decision with an untaken outcome as a branch, and nothing compiler-generated' {
    $result = Get-UncoveredNewCode -Added (New-Added $script:File (1..6)) -Coverage $script:Coverage -ReadSource $script:ReadSource
    $result.Branches | Should -Be @("${script:File}:1: (1/2 conditions) if (x) {")
  }

  It 'ignores lines the branch did not add, and files without coverage data' {
    $added = New-Added $script:File @(3)
    $added['src/P/NotInstrumented.cs'] = [System.Collections.Generic.HashSet[int]]::new([int[]]@(1))
    $result = Get-UncoveredNewCode -Added $added -Coverage $script:Coverage -ReadSource $script:ReadSource
    $result.Lines.Count | Should -Be 0
    $result.Branches.Count | Should -Be 0
  }
}

# Each test process writes its own report, and a report records how many outcomes of a line were taken,
# not which. Taking the best count per line therefore reads a line whose true outcome ran in one process
# and whose false outcome ran in another as half covered. These tests pin how the merge recovers what it
# can prove, and that it never claims an outcome no report can vouch for.
Describe 'Read-CoberturaCoverage, per-condition union' {
  It 'unions the conditions of one line that different reports covered' {
    $a = New-Report $TestDrive 'pa.cobertura.xml' 'src/P/A.cs' @(@{ n = 7; hits = 1; cov = '2/4'; conds = @(@{ type = 'jump'; pct = 100 }, @{ type = 'jump'; pct = 0 }) })
    $b = New-Report $TestDrive 'pb.cobertura.xml' 'src/P/A.cs' @(@{ n = 7; hits = 1; cov = '2/4'; conds = @(@{ type = 'jump'; pct = 0 }, @{ type = 'jump'; pct = 100 }) })

    $coverage = Read-CoberturaCoverage @($a, $b)

    $coverage['src/P/A.cs'].Conditions[7] | Should -Be @(4, 4)
  }

  It 'never counts two half-covered reports of one condition as both of its outcomes' {
    $a = New-Report $TestDrive 'ha.cobertura.xml' 'src/P/A.cs' @(@{ n = 7; hits = 1; cov = '1/2'; conds = @(@{ type = 'jump'; pct = 50 }) })
    $b = New-Report $TestDrive 'hb.cobertura.xml' 'src/P/A.cs' @(@{ n = 7; hits = 1; cov = '1/2'; conds = @(@{ type = 'jump'; pct = 50 }) })

    $coverage = Read-CoberturaCoverage @($a, $b)

    $coverage['src/P/A.cs'].Conditions[7] | Should -Be @(1, 2)
  }

  It 'keeps the best line count when the detail is not all jumps, whose outcome count it cannot read' {
    $a = New-Report $TestDrive 'sa.cobertura.xml' 'src/P/A.cs' @(@{ n = 7; hits = 1; cov = '2/5'; conds = @(@{ type = 'switch'; pct = 25 }, @{ type = 'jump'; pct = 50 }) })
    $b = New-Report $TestDrive 'sb.cobertura.xml' 'src/P/A.cs' @(@{ n = 7; hits = 1; cov = '3/5'; conds = @(@{ type = 'switch'; pct = 50 }, @{ type = 'jump'; pct = 50 }) })

    $coverage = Read-CoberturaCoverage @($a, $b)

    $coverage['src/P/A.cs'].Conditions[7] | Should -Be @(3, 5)
  }
}

Describe 'Read-CoberturaCoverage, if-body evidence' {
  BeforeAll {
    $script:IfFile = 'src/P/If.cs'
    $script:IfSource = @(
      'void M(int x) {',          # 1
      '  if (x > 0) {',           # 2  the decision
      '    Positive();',          # 3  its body: entered only through the true outcome
      '  }',                      # 4
      '  After();',               # 5
      '  if (x > 9) { Big(); }',  # 6  body on the decision's own line: no evidence
      '  if (x < 0) {',           # 7
      '  }',                      # 8  empty body: no evidence
      '}')
    $script:IfRead = { param($p) if ($p -eq $script:IfFile) { $script:IfSource } else { $null } }
  }

  It 'counts both outcomes when one process ran the body and another ran the line without it' {
    $a = New-Report $TestDrive 'ifa.cobertura.xml' $script:IfFile @(@{ n = 2; hits = 1; cov = '1/2' }, @{ n = 3; hits = 1; cov = $null }, @{ n = 5; hits = 1; cov = $null })
    $b = New-Report $TestDrive 'ifb.cobertura.xml' $script:IfFile @(@{ n = 2; hits = 1; cov = '1/2' }, @{ n = 3; hits = 0; cov = $null }, @{ n = 5; hits = 1; cov = $null })

    $coverage = Read-CoberturaCoverage @($a, $b) -ReadSource $script:IfRead

    $coverage[$script:IfFile].Conditions[2] | Should -Be @(2, 2)
  }

  It 'claims nothing when every process took the same outcome' {
    $a = New-Report $TestDrive 'ifc.cobertura.xml' $script:IfFile @(@{ n = 2; hits = 1; cov = '1/2' }, @{ n = 3; hits = 1; cov = $null })
    $b = New-Report $TestDrive 'ifd.cobertura.xml' $script:IfFile @(@{ n = 2; hits = 1; cov = '1/2' }, @{ n = 3; hits = 1; cov = $null })

    $coverage = Read-CoberturaCoverage @($a, $b) -ReadSource $script:IfRead

    $coverage[$script:IfFile].Conditions[2] | Should -Be @(1, 2)
  }

  It 'takes no evidence from a body on the decision line or an empty body' {
    $a = New-Report $TestDrive 'ife.cobertura.xml' $script:IfFile @(@{ n = 6; hits = 1; cov = '1/2' }, @{ n = 7; hits = 1; cov = '1/2' }, @{ n = 9; hits = 1; cov = $null })
    $b = New-Report $TestDrive 'iff.cobertura.xml' $script:IfFile @(@{ n = 6; hits = 1; cov = '1/2' }, @{ n = 7; hits = 1; cov = '1/2' }, @{ n = 9; hits = 0; cov = $null })

    $coverage = Read-CoberturaCoverage @($a, $b) -ReadSource $script:IfRead

    $coverage[$script:IfFile].Conditions[6] | Should -Be @(1, 2)
    $coverage[$script:IfFile].Conditions[7] | Should -Be @(1, 2)
      # Line 9 is past the body's closing brace, so whether it ran says nothing about the decision.
  }

  It 'proves the real collector output: each process taking one outcome of the same if' {
    $reports = @((Join-Path $script:Fixture 'A/A.cobertura.xml'), (Join-Path $script:Fixture 'B/B.cobertura.xml'))
    $source = Get-Content (Join-Path $script:Fixture 'Calc.cs.txt')

    $coverage = Read-CoberturaCoverage $reports -ReadSource { param($p) $source }

    $coverage[$script:FixturePath].Conditions[5] | Should -Be @(2, 2)
    $coverage[$script:FixturePath].Conditions[12] | Should -Be @(2, 2)
    $coverage[$script:FixturePath].Conditions[15] | Should -Be @(1, 2)
  }
}

Describe 'Read-BlockCoverage' {
  It 'sorts each line into the functions every block of which ran, and the functions some block of which did not' {
    $blocks = Read-BlockCoverage (Join-Path $script:Fixture 'merged-blocks.xml')

    $entry = $blocks[$script:FixturePath]
    @($entry.Complete | Sort-Object) | Should -Be @(5, 6, 8)
    @($entry.Incomplete | Sort-Object) | Should -Be @(12, 13, 15, 16, 18)
  }
}

Describe 'Merge-BlockCoverage' {
  BeforeAll {
    $script:ShardReports = @(
      (Join-Path $script:Fixture 'A/A.cobertura.xml'),
      (Join-Path $script:Fixture 'B/B.cobertura.xml'))
  }

  It 'counts a decision whose outcomes ran in different processes as fully covered once every block of its function ran' {
    $coverage = Read-CoberturaCoverage $script:ShardReports
    $coverage[$script:FixturePath].Conditions[5] | Should -Be @(1, 2)

    Merge-BlockCoverage -Coverage $coverage -Blocks (Read-BlockCoverage (Join-Path $script:Fixture 'merged-blocks.xml')) | Should -Be 1

    $coverage[$script:FixturePath].Conditions[5] | Should -Be @(2, 2)
  }

  It 'leaves every line of a function with a block no test ran as the reports say, because which outcome is missing is unknowable' {
    $coverage = Read-CoberturaCoverage $script:ShardReports

    Merge-BlockCoverage -Coverage $coverage -Blocks (Read-BlockCoverage (Join-Path $script:Fixture 'merged-blocks.xml')) | Out-Null

    $coverage[$script:FixturePath].Conditions[12] | Should -Be @(1, 2)
    $coverage[$script:FixturePath].Conditions[15] | Should -Be @(1, 2)
  }

  It 'does not upgrade a line that a complete function and an incomplete one (a lambda on the same line) both touch' {
    $report = New-Report $TestDrive 'lam.cobertura.xml' 'src/P/A.cs' @(@{ n = 3; hits = 1; cov = '1/2' }, @{ n = 9; hits = 1; cov = '1/2' })
    $coverage = Read-CoberturaCoverage @($report)
    $complete = [System.Collections.Generic.HashSet[int]]::new([int[]]@(3, 9))
    $incomplete = [System.Collections.Generic.HashSet[int]]::new([int[]]@(3))
    $blocks = @{ 'src/P/A.cs' = @{ Complete = $complete; Incomplete = $incomplete } }

    Merge-BlockCoverage -Coverage $coverage -Blocks $blocks | Should -Be 1

    $coverage['src/P/A.cs'].Conditions[3] | Should -Be @(1, 2)
    $coverage['src/P/A.cs'].Conditions[9] | Should -Be @(2, 2)
  }
}

Describe 'Get-BlockCoverageXml' {
  It 'returns nothing when no test process wrote a binary report' {
    $empty = Join-Path $TestDrive 'nobinaries'
    New-Item -ItemType Directory -Path $empty | Out-Null

    Get-BlockCoverageXml -CoverageRoot $empty -OutFile (Join-Path $TestDrive 'none.xml') | Should -BeNullOrEmpty
  }

  It 'fails with the install command when binary reports exist but the merge tool does not' {
    { Get-BlockCoverageXml -CoverageRoot $script:Fixture -OutFile (Join-Path $TestDrive 'x.xml') -Tool 'no-such-coverage-tool' } |
      Should -Throw '*dotnet tool install*dotnet-coverage*'
  }

  It 'merges the binary reports of every process into block data that proves the cross-process outcome' {
    $xml = Get-BlockCoverageXml -CoverageRoot $script:Fixture -OutFile (Join-Path $TestDrive 'merged.xml')

    $entry = (Read-BlockCoverage $xml)[$script:FixturePath]
    @($entry.Complete | Sort-Object) | Should -Be @(5, 6, 8)
    @($entry.Incomplete | Sort-Object) | Should -Be @(12, 13, 15, 16, 18)
  }
}

Describe 'Write-MergedCobertura' {
  It 'writes one report that reads back as the merged coverage, conditions included' {
    $a = New-Report $TestDrive 'wa.cobertura.xml' 'src/P/A.cs' @(@{ n = 4; hits = 1; cov = '1/2' }, @{ n = 5; hits = 0; cov = $null })
    $b = New-Report $TestDrive 'wb.cobertura.xml' 'src/Q/B.cs' @(@{ n = 9; hits = 3; cov = '4/4' })
    $coverage = Read-CoberturaCoverage @($a, $b)
    $coverage['src/P/A.cs'].Conditions[4] = @(2, 2)
    $out = Join-Path $TestDrive 'merged/merged.cobertura.xml'

    Write-MergedCobertura -Coverage $coverage -OutFile $out -SourceRoot '/work/repo'

    $back = Read-CoberturaCoverage @($out)
    $back.Keys | Sort-Object | Should -Be @('src/P/A.cs', 'src/Q/B.cs')
    $back['src/P/A.cs'].Hits[4] | Should -Be 1
    $back['src/P/A.cs'].Hits[5] | Should -Be 0
    $back['src/P/A.cs'].Conditions[4] | Should -Be @(2, 2)
    $back['src/Q/B.cs'].Conditions[9] | Should -Be @(4, 4)
    ([xml](Get-Content $out -Raw)).coverage.sources.source | Should -Be '/work/repo'
  }
}

Describe 'Read-CoberturaCoverage, one source line compiled into several assemblies' {
  # Shared source compiled into several assemblies (the generators' shared code) reports the same line
  # once per copy, and the copies' IL can differ, so one line can arrive with different outcome totals.
  It 'never counts more outcomes covered than the line has, and keeps the copy with the larger gap' {
    $wide = New-Report $TestDrive 'wide.cobertura.xml' 'src/P/Shared.cs' @(@{ n = 7; hits = 1; cov = '4/4' })
    $narrow = New-Report $TestDrive 'narrow.cobertura.xml' 'src/P/Shared.cs' @(@{ n = 7; hits = 1; cov = '1/2' })

    foreach ($order in @(@($wide, $narrow), @($narrow, $wide))) {
      $coverage = Read-CoberturaCoverage $order
      $pair = $coverage['src/P/Shared.cs'].Conditions[7]

      $pair[0] | Should -BeLessOrEqual $pair[1]
      $pair | Should -Be @(1, 2)
    }
  }

  It 'gives a whole-library count that is never negative' {
    $wide = New-Report $TestDrive 'wide2.cobertura.xml' 'src/P/Shared.cs' @(@{ n = 1; hits = 1; cov = '4/4' })
    $narrow = New-Report $TestDrive 'narrow2.cobertura.xml' 'src/P/Shared.cs' @(@{ n = 1; hits = 1; cov = '1/2' })
    $source = @{ 'src/P/Shared.cs' = @('if (a && b) {') }

    $whole = Get-WholeLibraryCoverage -Coverage (Read-CoberturaCoverage @($wide, $narrow)) -ReadSource { param($p) $source[$p] }

    $whole.CoveredOutcomes | Should -BeLessOrEqual $whole.Outcomes
    (Get-WholeLibraryGateResult $whole).Untested | Should -Be 1
  }
}

Describe 'Get-WholeLibraryCoverage' {
  It 'counts every hand-written library line and decision outcome, and nothing else' {
    $report = New-Report $TestDrive 'whole.cobertura.xml' 'src/P/A.cs' @(
      @{ n = 1; hits = 1; cov = '1/2' },  # if: a hand-written decision, one outcome untested
      @{ n = 2; hits = 1; cov = '1/2' },  # await: the compiler's branch, not counted
      @{ n = 3; hits = 1; cov = '4/4' },  # switch: every outcome taken
      @{ n = 4; hits = 0; cov = $null })  # never ran
    $coverage = Read-CoberturaCoverage @($report)
    foreach ($other in @('src/Whizbang.Testing/T.cs', 'src/P/obj/Release/G.g.cs', 'src/P/.whizbang/cache/C.cs', 'tests/P.Tests/X.cs')) {
      $coverage[$other] = @{ Hits = @{ 1 = 0 }; Conditions = @{ 1 = @(0, 2) } }
    }
    $source = @{ 'src/P/A.cs' = @('if (x) {', 'await store.SaveAsync(item);', 'var size = kind switch {', 'Do();') }
    $read = { param($p) $source[$p] }

    $whole = Get-WholeLibraryCoverage -Coverage $coverage -ReadSource $read

    $whole.Lines | Should -Be 4
    $whole.CoveredLines | Should -Be 3
    $whole.Outcomes | Should -Be 6
    $whole.CoveredOutcomes | Should -Be 5
  }

  It 'lists every uncovered library line and every hand-written decision with an untaken outcome, as the gap' {
    $report = New-Report $TestDrive 'gap.cobertura.xml' 'src/P/A.cs' @(
      @{ n = 1; hits = 1; cov = '1/2' },  # if: one outcome untested, listed
      @{ n = 2; hits = 1; cov = '1/2' },  # await: the compiler's, not listed
      @{ n = 3; hits = 1; cov = '4/4' },  # complete, not listed
      @{ n = 4; hits = 0; cov = $null })  # never ran, listed
    $coverage = Read-CoberturaCoverage @($report)
    $coverage['src/Whizbang.Testing/T.cs'] = @{ Hits = @{ 1 = 0 }; Conditions = @{} }
    $source = @{ 'src/P/A.cs' = @('if (x) {', 'await store.SaveAsync(item);', 'var size = kind switch {', 'Do();') }

    $whole = Get-WholeLibraryCoverage -Coverage $coverage -ReadSource { param($p) $source[$p] }

    $whole.Gap | Should -Be @('src/P/A.cs:1: (1/2 conditions) if (x) {', 'src/P/A.cs:4: (never ran) Do();')
  }

  It 'skips a library file it cannot read, rather than counting lines it cannot classify' {
    $report = New-Report $TestDrive 'gone.cobertura.xml' 'src/P/Gone.cs' @(@{ n = 1; hits = 0; cov = '0/2' })

    $whole = Get-WholeLibraryCoverage -Coverage (Read-CoberturaCoverage @($report)) -ReadSource { param($p) $null }

    $whole.Lines | Should -Be 0
    $whole.Outcomes | Should -Be 0
  }
}

Describe 'Format-WholeLibraryLine' {
  It 'shows <expected>' -ForEach @(
      @{ l = 999; tl = 1000; o = 20988; to = 21500; u = $true; expected = 'Whole library: lines 99.9%, hand-written branches 97.6% (512 outcomes untested)' },
      @{ l = 9996; tl = 10000; o = 9; to = 10; u = $true; expected = 'Whole library: lines 99.9%, hand-written branches 90% (1 outcome untested)' },
      @{ l = 50; tl = 50; o = 7; to = 7; u = $true; expected = 'Whole library: lines 100%, hand-written branches 100%, every hand-written decision in the library is covered' },
      @{ l = 50; tl = 50; o = 6; to = 7; u = $false; expected = 'Whole library: lines 100%, hand-written branches 85.7% (1 outcome untested; no block data to union outcomes across test processes, so the gap may be overstated)' }) {
    $summary = [pscustomobject]@{ Lines = $tl; CoveredLines = $l; Outcomes = $to; CoveredOutcomes = $o; BlockUnion = $u }

    Format-WholeLibraryLine $summary | Should -Be $expected
  }

  It 'never rounds a gap up to 100%' {
    $summary = [pscustomobject]@{ Lines = 100000; CoveredLines = 99999; Outcomes = 100000; CoveredOutcomes = 99999; BlockUnion = $true }

    Format-WholeLibraryLine $summary | Should -Be 'Whole library: lines 99.9%, hand-written branches 99.9% (1 outcome untested)'
  }
}

Describe 'Get-WholeLibraryGateResult' {
  # The whole-library gate: every hand-written decision in the library is covered, or the job fails.
  It 'passes when no hand-written outcome is untested' {
    $summary = [pscustomobject]@{ Lines = 50; CoveredLines = 49; Outcomes = 7; CoveredOutcomes = 7; BlockUnion = $true }

    $gate = Get-WholeLibraryGateResult $summary

    $gate.Passed | Should -BeTrue
    $gate.Untested | Should -Be 0
  }

  It 'fails on a single untested hand-written outcome, and says how many' {
    $summary = [pscustomobject]@{ Lines = 50; CoveredLines = 50; Outcomes = 7; CoveredOutcomes = 6; BlockUnion = $true }

    $gate = Get-WholeLibraryGateResult $summary

    $gate.Passed | Should -BeFalse
    $gate.Untested | Should -Be 1
    $gate.Message | Should -Match '1 hand-written decision outcome'
  }

  It 'does not count a member excluded from coverage' {
    # [ExcludeFromCodeCoverage] keeps a member out of the collector's reports entirely, so its lines carry
    # no data here and its decisions are not outcomes: the untested `if` on line 4 is not counted.
    $report = New-Report $TestDrive 'excluded.cobertura.xml' 'src/P/A.cs' @(
      @{ n = 1; hits = 1; cov = '2/2' })
    $source = @{ 'src/P/A.cs' = @(
      'if (ready) {',
      '}',
      '[ExcludeFromCodeCoverage(Justification = "unreachable until the issue is fixed")]',
      'void Excluded() { if (never) { Do(); } }') }

    $whole = Get-WholeLibraryCoverage -Coverage (Read-CoberturaCoverage @($report)) -ReadSource { param($p) $source[$p] }
    $gate = Get-WholeLibraryGateResult $whole

    $whole.Outcomes | Should -Be 2
    $gate.Passed | Should -BeTrue
  }
}

Describe 'Find-UncoveredNewLines.ps1, merge only (no base ref)' {
  It 'merges the per-process reports and their block data, and summarizes the whole library' {
    $sourceRoot = Join-Path $TestDrive 'repo'
    New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'src/Whizbang.Exp') -Force | Out-Null
    Copy-Item (Join-Path $script:Fixture 'Calc.cs.txt') (Join-Path $sourceRoot $script:FixturePath)
    $summaryFile = Join-Path $TestDrive 'whole.json'
    $merged = Join-Path $TestDrive 'out/merged.cobertura.xml'
    $gapFile = Join-Path $TestDrive 'out/library-gap.txt'
    $scriptPath = Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/Find-UncoveredNewLines.ps1'

    $pwsh = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    & $pwsh -NoProfile -File $scriptPath -CoverageRoot $script:Fixture -SourceRoot $sourceRoot -SummaryOutFile $summaryFile -MergedOutFile $merged -LibraryGapOutFile $gapFile | Out-Null
    $LASTEXITCODE | Should -Be 0
    Get-Content $gapFile | Should -Be @(
      "${script:FixturePath}:15: (1/2 conditions) if (x < -5) {",
      "${script:FixturePath}:16: (never ran) return 2;")

    $summary = Get-Content $summaryFile -Raw | ConvertFrom-Json
    $summary.Lines | Should -Be 8
    $summary.CoveredLines | Should -Be 7
    $summary.Outcomes | Should -Be 6
    $summary.CoveredOutcomes | Should -Be 5
    $summary.BlockUnion | Should -BeTrue
    $summary.Text | Should -Be 'Whole library: lines 87.5%, hand-written branches 83.3% (1 outcome untested)'
    $summary.Untested | Should -Be 1
    (Read-CoberturaCoverage @($merged))[$script:FixturePath].Conditions[5] | Should -Be @(2, 2)
  }

  It 'fails with -FailOnWholeLibrary while any hand-written outcome in the library is untested' {
    $sourceRoot = Join-Path $TestDrive 'repo-gate'
    New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'src/Whizbang.Exp') -Force | Out-Null
    Copy-Item (Join-Path $script:Fixture 'Calc.cs.txt') (Join-Path $sourceRoot $script:FixturePath)
    $scriptPath = Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/Find-UncoveredNewLines.ps1'

    $pwsh = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $output = & $pwsh -NoProfile -File $scriptPath -CoverageRoot $script:Fixture -SourceRoot $sourceRoot -FailOnWholeLibrary

    $LASTEXITCODE | Should -Be 1
    ($output -join "`n") | Should -Match '1 hand-written decision outcome'
  }
}
