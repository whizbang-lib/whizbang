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

  # Real collector output, produced now: shapes/New-ShapesCoverage.ps1 builds the two small libraries in
  # shapes/ and runs each as two test processes (A and B) under the collector, settings and versions CI
  # uses, then merges each pair of binary reports with dotnet-coverage. No coverage data is committed; a
  # stored report would prove only what some collector once wrote. The sources are the real files.
  $script:ShapesDir = Join-Path -Path $PSScriptRoot -ChildPath 'shapes'
  $script:Generated = Join-Path ([System.IO.Path]::GetTempPath()) "whizbang-gate-shapes-$([guid]::NewGuid().ToString('N'))"
  & (Join-Path $script:ShapesDir 'New-ShapesCoverage.ps1') -OutDir $script:Generated
  function Get-ProcessReport([string]$Root, [string]$Process) {
    return @(Get-ChildItem -Path (Join-Path $Root $Process) -Filter '*.cobertura.xml' -File)[0].FullName
  }

  # Calc.cs: A calls Sign(1) and Pick(1), B calls Sign(-1) and Pick(-1). Each process takes one outcome
  # of Sign's `if`, so each report says 1/2 for that line although together they take both.
  $script:Fixture = Join-Path $script:Generated 'CalcRunner'
  $script:FixturePath = 'src/Whizbang.Exp/Calc.cs'
  $script:FixtureA = Get-ProcessReport $script:Fixture 'A'
  $script:FixtureB = Get-ProcessReport $script:Fixture 'B'
  $script:FixtureBlocks = Join-Path $script:Generated 'CalcRunner.blocks.xml'
  $script:CalcSource = [string[]]@(Get-Content (Join-Path $script:ShapesDir $script:FixturePath))

  # Shapes.cs: A takes every ?. statement's non-null outcome and B its null one, except where a shape
  # needs otherwise (ShapeRuns.cs). The shapes, by line:
  #   22 _sink?.Record(n): outcomes split across the processes, in a function with an unrun block
  #   31 the same in an async method (a hoisted local)
  #   39 _never?.Record(n): the non-null outcome never taken, in either process
  #   47 if (sink?.Total >= 0) {: the if's false outcome never taken, though every block on the line ran
  #   57 bomb?.Fail(): the call always throws, so the null outcome's target never ran
  #   64 get()\n?.Record(n): the ?. on a continuation line, its non-null outcome never taken
  #   70 a collection initializer whose ?. and ?? are on a continuation line
  #   77 a call chain whose first line carries only the compiler's lambda-cache condition
  #   94 metrics?.Record(r) in an async loop: the MaintenanceWorker.cs:249 shape that found #1305, its
  #      outcomes split across the processes while another statement of the method never ran
  #   96 lookup.Find<Sink>()\n?.Record(...): the MaintenanceWorker.cs:252 shape, a ?. on the
  #      continuation line, non-null in neither process
  $script:Shapes = Join-Path $script:Generated 'ShapesRunner'
  $script:ShapesPath = 'src/Whizbang.Shapes/Shapes.cs'
  $script:ShapesSource = [string[]]@(Get-Content (Join-Path $script:ShapesDir $script:ShapesPath))
  $script:ShapesReports = @((Get-ProcessReport $script:Shapes 'A'), (Get-ProcessReport $script:Shapes 'B'))
  $script:ShapesBlocks = Join-Path $script:Generated 'ShapesRunner.blocks.xml'
  $script:ShapesRead = { param($p) if ($p -eq $script:ShapesPath) { $script:ShapesSource } else { $null } }

  function New-Added([string]$file, [int[]]$lineNumbers) {
    $set = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($n in $lineNumbers) { [void]$set.Add($n) }
    return @{ $file = $set }
  }
}

AfterAll {
  if ($script:Generated -and (Test-Path -LiteralPath $script:Generated)) { Remove-Item -LiteralPath $script:Generated -Recurse -Force }
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

# The collector reports every condition of a statement on the statement's first line, so a decision
# written on a continuation line (a ?. starting the second line of a call chain, a ?? inside an
# initializer) is counted on a line whose own text has none. The statement, not the line, is classified.
Describe 'Get-StatementCode' {
  It 'reads a statement from its first line to the ; that ends it' {
    $code = Get-StatementCode $script:ShapesSource 96

    $code.Count | Should -Be 2
    $code[1] | Should -Match '\?\.Record\('
  }

  It 'reads an object or collection initializer to its end, nested ones included' {
    $source = @('var hop = new MessageHop {', '  Topic = destination ?? "x",', '  Inner = new Inner { A = 1 },', '};', 'Next();')

    (Get-StatementCode $source 1).Count | Should -Be 4
  }

  It 'reads the initializer of an object created with arguments' {
    $source = @('return new Envelope<T>(id) {', '  Scope = scope?.Value,', '};', 'Next();')

    (Get-StatementCode $source 1).Count | Should -Be 3
  }

  It 'ends at the { that opens a block, after a condition or a block keyword' {
    $source = @('if (a', '    && b) {', '  Do(x ?? y);', '}', 'else', '{', '  Do(z ?? w);', '}', 'try {', '  Do(q ?? r);', '}')

    (Get-StatementCode $source 1).Count | Should -Be 2
    (Get-StatementCode $source 5).Count | Should -Be 2
    (Get-StatementCode $source 9).Count | Should -Be 1
  }

  It 'reads "} else {" as the else, and a line that only closes a block as nothing more' {
    $source = @('} else if (x) {', '  Do(a ?? b);', '}', 'Do(c ?? d);')

    (Get-StatementCode $source 1).Count | Should -Be 1
    (Get-StatementCode $source 3).Count | Should -Be 1
  }

  It 'ends where a bracket opened before the statement closes, or a block it is inside closes' {
    $source = @('  first ?? second)', '  .Next(a ?? b);', 'x = 1 }', 'Next(c ?? d);')

    (Get-StatementCode $source 1) | Should -Be @('  first ?? second')
    (Get-StatementCode $source 3) | Should -Be @('x = 1 ')
  }

  It 'ends at a ; even inside an initializer it never saw close' {
    $source = @('return new Hop {', 'Do();', 'if (y) {')

    (Get-StatementCode $source 1).Count | Should -Be 2
  }

  It 'leaves out a lambda body that starts on a continuation line, and every block body after the first line' {
    $source = @(
      'var count = items',
      '  .Where(i => i?.Total > 0)',
      '  .Select((i, n) => new { i, n })',
      '  .Count();',
      'Run(() => {',
      '  if (ready) { Go(); }',
      '}, other ?? fallback);')

    (Get-StatementCode $source 1) | Should -Be @('var count = items', '  .Where(i  )', '  .Select((i, n)  )', '  .Count();')
    (Get-StatementCode $source 5) | Should -Be @('Run(() => {', '', ', other ?? fallback);')
  }

  It 'ends a lambda expression body that starts on a continuation line at the , or ; after it' {
    $source = @('Configure(', '  selector: x => x.Select(y => y.A),', '  other ?? fallback);', 'Func<int, int> f =', '  x => x ?? 0;')

    (Get-StatementCode $source 1) | Should -Be @('Configure(', '  selector: x  ,', '  other ?? fallback);')
    (Get-StatementCode $source 4) | Should -Be @('Func<int, int> f =', '  x  ;')
  }

  It 'keeps a lambda that starts on the first line, body and all' {
    $source = @('Run(() => { if (ready) { Go(); } });', 'var x = items.Select(i =>', '  i?.Total);')

    (Get-StatementCode $source 1) | Should -Be @('Run(() => { if (ready) { Go(); } });')
    (Get-StatementCode $source 2) | Should -Be @('var x = items.Select(i =>', '  i?.Total);')
  }

  It 'keeps an Allman lambda body on the first line out of nothing, and drops it after' {
    $source = @('Run(x =>', '{', '  if (x) { Go(); }', '});')

    (Get-StatementCode $source 1) | Should -Be @('Run(x =>', '', '', ');')
  }

  It 'stops at a line that opens a multi-line string or comment, whose text a per-line reader would take for code' {
    $source = @(
      'await using var cmd = new Command(',
      '  """',
      '  SELECT CASE WHEN a IS NULL OR b THEN 1 END',
      '  """, conn);',
      'Run(@"line one',
      '  if (x) ? a : b");',
      'Run(a, /* note',
      '  if (x) */ b);')

    (Get-StatementCode $source 1).Count | Should -Be 2
    (Get-StatementCode $source 5).Count | Should -Be 1
    (Get-StatementCode $source 7).Count | Should -Be 1
  }

  It 'reads to the end of the source when the statement never ends' {
    (Get-StatementCode @('Run(a,', '  b') 1).Count | Should -Be 2
  }
}

Describe 'Test-HandWrittenStatement' {
  It 'counts a decision on a continuation line of the statement whose first line carries the conditions (Shapes.cs:96, the MaintenanceWorker.cs:252 shape)' {
    Test-HandWrittenDecision $script:ShapesSource[95] | Should -BeFalse

    Test-HandWrittenStatement $script:ShapesSource 96 | Should -BeTrue
  }

  It 'counts <name>' -ForEach @(
      @{ name = 'a ?. starting a continuation line (Shapes.cs:64)'; line = 64 },
      @{ name = 'a ?. and ?? inside a collection initializer (Shapes.cs:70)'; line = 70 },
      @{ name = 'a decision on the first line, as before (Shapes.cs:22)'; line = 22 }) {
    Test-HandWrittenStatement $script:ShapesSource $line | Should -BeTrue
  }

  It 'counts <name>' -ForEach @(
      @{ name = 'a ?. split at the line break'; source = @('var parent = envelope.Hops?', '  .LastOrDefault();') },
      @{ name = 'a ?[ split at the line break'; source = @('var first = items?', '  [0];') },
      @{ name = 'a conditional split across lines'; source = @('var size = count > 0', '  ? count', '  : 1;') },
      @{ name = 'a conditional whose ? ends a line'; source = @('var size = count > 0 ?', '  count', '  : 1;') },
      @{ name = 'a decision in an interpolation hole on a continuation line'; source = @('throw new TimeoutException(', '  $"after {timeout ?? fallback}");') },
      @{ name = 'a switch expression inside an initializer'; source = @('var hop = new Hop {', '  Scope = For(record switch {', '    A a => a.Id,', '    _ => null,', '  }),', '};') }) {
    Test-HandWrittenStatement $source 1 | Should -BeTrue
  }

  It 'excludes <name>' -ForEach @(
      @{ name = 'a call chain whose decisions are all in lambdas starting on continuation lines (Shapes.cs:77)'; source = $null; line = 77 },
      @{ name = 'a multi-line await'; source = @('var r = await pending', '  .ConfigureAwait(false);'); line = 1 },
      @{ name = 'a multi-line initializer with no decision'; source = @('var sink = new Sink {', '  Total = 3,', '};'); line = 1 },
      @{ name = 'a line closing a block, followed by a decision'; source = @('}', 'Do(a ?? b);'); line = 1 },
      @{ name = 'a statement lambda whose body holds the decisions'; source = @('Run(() => {', '  if (ready) { Go(); }', '});'); line = 1 },
      @{ name = 'a whole statement on one line, before another with a decision'; source = @('Do(x);', 'Do(a ?? b);'); line = 1 },
      @{ name = 'an initializer closed on its own line'; source = @('var sink = new Sink { };', 'Do(a ?? b);'); line = 1 },
      @{ name = 'the end of an argument list begun on an earlier line'; source = @('  x);', 'Do(a ?? b);'); line = 1 },
      @{ name = 'a lambda assigned on a continuation line, whose decision is its own'; source = @('Func<int, int> f =', '  x => x ?? 0;'); line = 1 },
      @{ name = 'a raw SQL string whose text reads like decisions'; source = @('await using var cmd = new Command(', '  """', '  SELECT CASE WHEN a IS NULL OR b THEN 1 END', '  """, conn);'); line = 1 },
      @{ name = 'a line past the end of the source'; source = @('Do();'); line = 2 }) {
    $text = if ($null -eq $source) { $script:ShapesSource } else { [string[]]$source }

    Test-HandWrittenStatement $text $line | Should -BeFalse
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

  It 'lists an added statement whose decision is on its continuation line as a branch' {
    $coverage = Read-CoberturaCoverage $script:ShapesReports

    $result = Get-UncoveredNewCode -Added (New-Added $script:ShapesPath @(64, 65, 77, 78)) -Coverage $coverage -ReadSource $script:ShapesRead

    $result.Branches | Should -Be @("${script:ShapesPath}:64: (1/2 conditions) get()", "${script:ShapesPath}:78: (1/2 conditions) .Where(i => i?.Total > 0)")
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
    $reports = @($script:FixtureA, $script:FixtureB)
    $source = $script:CalcSource

    $coverage = Read-CoberturaCoverage $reports -ReadSource { param($p) $source }

    $coverage[$script:FixturePath].Conditions[5] | Should -Be @(2, 2)
    $coverage[$script:FixturePath].Conditions[12] | Should -Be @(2, 2)
    $coverage[$script:FixturePath].Conditions[15] | Should -Be @(1, 2)
  }
}

Describe 'Read-BlockCoverage' {
  It 'sorts each line into the functions every block of which ran, and the functions some block of which did not' {
    $blocks = Read-BlockCoverage $script:FixtureBlocks

    $entry = $blocks[$script:FixturePath]
    @($entry.Complete | Sort-Object) | Should -Be @(5, 6, 8)
    @($entry.Incomplete | Sort-Object) | Should -Be @(12, 13, 15, 16, 18)
  }
}

Describe 'Merge-BlockCoverage' {
  BeforeAll {
    $script:ShardReports = @(
      $script:FixtureA,
      $script:FixtureB)
  }

  It 'counts a decision whose outcomes ran in different processes as fully covered once every block of its function ran' {
    $coverage = Read-CoberturaCoverage $script:ShardReports
    $coverage[$script:FixturePath].Conditions[5] | Should -Be @(1, 2)

    Merge-BlockCoverage -Coverage $coverage -Blocks (Read-BlockCoverage $script:FixtureBlocks) | Should -Be 1

    $coverage[$script:FixturePath].Conditions[5] | Should -Be @(2, 2)
  }

  It 'leaves every line of a function with a block no test ran as the reports say, because which outcome is missing is unknowable' {
    $coverage = Read-CoberturaCoverage $script:ShardReports

    Merge-BlockCoverage -Coverage $coverage -Blocks (Read-BlockCoverage $script:FixtureBlocks) | Out-Null

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

# A function with any unrun block left every line in it as the per-process reports said, so a ?. whose
# two outcomes ran in two processes stayed half covered whenever anything else in its function was
# untested (#1305, MaintenanceWorker.cs:249). The block data can prove such a line on its own: when every
# block on the statement ran, the statement only branches within itself (no if, loop, switch, catch or
# await), and the code it falls through to ran, every outcome of its conditions was taken. Each case below
# is real collector output; the ones that must stay uncovered are as real as the ones that must not.
Describe 'Read-BlockCoverage, per-line evidence' {
  BeforeAll {
    $script:ShapeBlocks = (Read-BlockCoverage $script:ShapesBlocks)[$script:ShapesPath]
  }

  It 'records the lines some block that did not run, or ran only in part, touches' {
    $script:ShapeBlocks.NotRun.Contains(39) | Should -BeTrue
    $script:ShapeBlocks.NotRun.Contains(65) | Should -BeTrue
    $script:ShapeBlocks.NotRun.Contains(24) | Should -BeTrue
    $script:ShapeBlocks.NotRun.Contains(22) | Should -BeFalse
    $script:ShapeBlocks.NotRun.Contains(47) | Should -BeFalse
  }

  It 'records the lines a statement starts on' {
    $script:ShapeBlocks.Starts.Contains(64) | Should -BeTrue
    $script:ShapeBlocks.Starts.Contains(65) | Should -BeFalse
  }

  It 'records the statements the code that follows them never ran after' {
    $script:ShapeBlocks.Unreached.Contains(57) | Should -BeTrue
    $script:ShapeBlocks.Unreached.Contains(23) | Should -BeTrue
    $script:ShapeBlocks.Unreached.Contains(22) | Should -BeFalse
    $script:ShapeBlocks.Unreached.Contains(31) | Should -BeFalse
  }
}

Describe 'Merge-BlockCoverage, a line proven by its own blocks' {
  BeforeAll {
    function Get-ShapesMerged([scriptblock]$Read = $script:ShapesRead) {
      $coverage = Read-CoberturaCoverage $script:ShapesReports
      Merge-BlockCoverage -Coverage $coverage -Blocks (Read-BlockCoverage $script:ShapesBlocks) -ReadSource $Read | Out-Null
      return $coverage[$script:ShapesPath]
    }
  }

  It 'counts as covered a ?. in an async loop whose outcomes the processes split, though another statement in the method never ran (Shapes.cs:94, the MaintenanceWorker.cs:249 shape)' {
    $coverage = Read-CoberturaCoverage $script:ShapesReports
    $coverage[$script:ShapesPath].Conditions[94] | Should -Be @(1, 2)

    Merge-BlockCoverage -Coverage $coverage -Blocks (Read-BlockCoverage $script:ShapesBlocks) -ReadSource $script:ShapesRead | Out-Null

    $coverage[$script:ShapesPath].Conditions[94] | Should -Be @(2, 2)
  }

  It 'leaves the outcome no process took untested (Shapes.cs:96, the MaintenanceWorker.cs:252 shape)' {
    $coverage = Read-CoberturaCoverage $script:ShapesReports

    Merge-BlockCoverage -Coverage $coverage -Blocks (Read-BlockCoverage $script:ShapesBlocks) -ReadSource $script:ShapesRead | Out-Null

    $coverage[$script:ShapesPath].Conditions[96] | Should -Be @(1, 2)
  }

  It 'counts a ?. whose outcomes ran in different processes as covered, in a plain and an async method (Shapes.cs:<line>)' -ForEach @(@{ line = 22 }, @{ line = 31 }) {
    (Get-ShapesMerged).Conditions[$line] | Should -Be @(2, 2)
  }

  It 'leaves <name> untested (Shapes.cs:<line>)' -ForEach @(
      @{ name = 'a ?. no process took both ways, a block on its line unrun'; line = 39; expected = @(1, 2) },
      @{ name = 'an if whose line ran in full but whose false outcome leads off it and never ran'; line = 47; expected = @(2, 4) },
      @{ name = 'a ?. whose only path threw, so the code after it never ran'; line = 57; expected = @(1, 2) },
      @{ name = 'an if whose body never ran'; line = 23; expected = @(1, 2) },
      @{ name = 'a ?. on a continuation line no process took both ways'; line = 64; expected = @(1, 2) }) {
    (Get-ShapesMerged).Conditions[$line] | Should -Be $expected
  }

  It 'claims nothing for a line whose source it cannot read, or without a source reader' {
    (Get-ShapesMerged -Read { param($p) $null }).Conditions[22] | Should -Be @(1, 2)
    (Get-ShapesMerged -Read $null).Conditions[22] | Should -Be @(1, 2)
  }

  It 'claims nothing for a line whose conditions are the compiler''s, such as one that only closes a block' {
    $report = New-Report $TestDrive 'close.cobertura.xml' 'src/P/A.cs' @(@{ n = 2; hits = 1; cov = '1/2' }, @{ n = 3; hits = 1; cov = '1/2' }, @{ n = 4; hits = 1; cov = '1/2' })
    $coverage = Read-CoberturaCoverage @($report)
    $lines = { param([int[]]$n) , [System.Collections.Generic.HashSet[int]]::new($n) }
    $blocks = @{ 'src/P/A.cs' = @{
        Complete = (& $lines @()); Incomplete = (& $lines @(2, 3, 4)); NotRun = (& $lines @(9))
        Starts = (& $lines @(2, 3)); Unreached = (& $lines @()) } }
    $source = @('void M() {', '  }', '  x?.Go();', '  y?.Go(); // no statement starts here in the block data')

    Merge-BlockCoverage -Coverage $coverage -Blocks $blocks -ReadSource { param($p) $source } | Should -Be 1

    $coverage['src/P/A.cs'].Conditions[2] | Should -Be @(1, 2)
    $coverage['src/P/A.cs'].Conditions[3] | Should -Be @(2, 2)
    $coverage['src/P/A.cs'].Conditions[4] | Should -Be @(1, 2)
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

  It 'lists the continuation-line gap and not the outcomes split across processes (Shapes.cs:94 and :96, the MaintenanceWorker.cs:249 and :252 shapes)' {
    $coverage = Read-CoberturaCoverage $script:ShapesReports
    Merge-BlockCoverage -Coverage $coverage -Blocks (Read-BlockCoverage $script:ShapesBlocks) -ReadSource $script:ShapesRead | Out-Null

    $gap = (Get-WholeLibraryCoverage -Coverage $coverage -ReadSource $script:ShapesRead).Gap

    $gap | Should -Contain "${script:ShapesPath}:96: (1/2 conditions) lookup.Find<Sink>()"
    @($gap | Where-Object { $_.StartsWith("${script:ShapesPath}:94:") }) | Should -BeNullOrEmpty
  }

  It 'counts the outcomes of a decision on a continuation line, and still not the compiler''s' {
    $coverage = Read-CoberturaCoverage $script:ShapesReports
    $only = { param([int[]]$keep) foreach ($n in @($coverage[$script:ShapesPath].Conditions.Keys)) { if ($keep -notcontains $n) { $coverage[$script:ShapesPath].Conditions.Remove($n) } } }
    & $only @(64, 70, 77)

    $whole = Get-WholeLibraryCoverage -Coverage $coverage -ReadSource $script:ShapesRead

    # 64 (1/2) and 70 (1/2) are counted; 77 (2/2) carries only the compiler's lambda-cache condition.
    $whole.Outcomes | Should -Be 4
    $whole.CoveredOutcomes | Should -Be 2
    $whole.Gap | Should -Contain "${script:ShapesPath}:64: (1/2 conditions) get()"
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
      @{ l = 999; tl = 1000; o = 20988; to = 21500; u = $true; expected = 'Whole library: lines 99.9% (1 line never run), hand-written branches 97.6% (512 outcomes untested)' },
      @{ l = 9996; tl = 10000; o = 9; to = 10; u = $true; expected = 'Whole library: lines 99.9% (4 lines never run), hand-written branches 90% (1 outcome untested)' },
      @{ l = 50; tl = 50; o = 7; to = 7; u = $true; expected = 'Whole library: lines 100%, hand-written branches 100%, every line and every hand-written decision in the library is covered' },
      @{ l = 49; tl = 50; o = 7; to = 7; u = $true; expected = 'Whole library: lines 98% (1 line never run), hand-written branches 100%' },
      @{ l = 50; tl = 50; o = 6; to = 7; u = $false; expected = 'Whole library: lines 100%, hand-written branches 85.7% (1 outcome untested; no block data to union outcomes across test processes, so the gap may be overstated)' }) {
    $summary = [pscustomobject]@{ Lines = $tl; CoveredLines = $l; Outcomes = $to; CoveredOutcomes = $o; BlockUnion = $u }

    Format-WholeLibraryLine $summary | Should -Be $expected
  }

  It 'never rounds a gap up to 100%' {
    $summary = [pscustomobject]@{ Lines = 100000; CoveredLines = 99999; Outcomes = 100000; CoveredOutcomes = 99999; BlockUnion = $true }

    Format-WholeLibraryLine $summary | Should -Be 'Whole library: lines 99.9% (1 line never run), hand-written branches 99.9% (1 outcome untested)'
  }

  It 'counts the uncovered lines, because the truncated percentage cannot tell one gap from eighty' {
    $one = [pscustomobject]@{ Lines = 81305; CoveredLines = 81304; Outcomes = 10; CoveredOutcomes = 10; BlockUnion = $true }
    $many = [pscustomobject]@{ Lines = 81305; CoveredLines = 81225; Outcomes = 10; CoveredOutcomes = 10; BlockUnion = $true }

    Format-WholeLibraryLine $one | Should -Be 'Whole library: lines 99.9% (1 line never run), hand-written branches 100%'
    Format-WholeLibraryLine $many | Should -Be 'Whole library: lines 99.9% (80 lines never run), hand-written branches 100%'
  }
}

Describe 'Get-WholeLibraryGateResult' {
  # The whole-library gate: every line and every hand-written decision in the library is covered, or the
  # job fails.
  It 'passes when every line is run and no hand-written outcome is untested' {
    $summary = [pscustomobject]@{ Lines = 50; CoveredLines = 50; Outcomes = 7; CoveredOutcomes = 7; BlockUnion = $true }

    $gate = Get-WholeLibraryGateResult $summary

    $gate.Passed | Should -BeTrue
    $gate.Untested | Should -Be 0
    $gate.UncoveredLines | Should -Be 0
  }

  It 'fails on a line no test runs, even when every hand-written outcome is taken' {
    $summary = [pscustomobject]@{ Lines = 50; CoveredLines = 49; Outcomes = 7; CoveredOutcomes = 7; BlockUnion = $true }

    $gate = Get-WholeLibraryGateResult $summary

    $gate.Passed | Should -BeFalse
    $gate.UncoveredLines | Should -Be 1
    $gate.Untested | Should -Be 0
    $gate.Message | Should -Match '1 library line no test runs'
  }

  It 'names both counts when lines and outcomes are both short' {
    $summary = [pscustomobject]@{ Lines = 50; CoveredLines = 48; Outcomes = 7; CoveredOutcomes = 6; BlockUnion = $true }

    $gate = Get-WholeLibraryGateResult $summary

    $gate.Passed | Should -BeFalse
    $gate.Message | Should -Match '2 library lines no test runs, and 1 hand-written decision outcome no test takes'
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
    $sourceRoot = $script:ShapesDir
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
    $summary.Text | Should -Be 'Whole library: lines 87.5% (1 line never run), hand-written branches 83.3% (1 outcome untested)'
    $summary.Untested | Should -Be 1
    $summary.UncoveredLines | Should -Be 1
    (Read-CoberturaCoverage @($merged))[$script:FixturePath].Conditions[5] | Should -Be @(2, 2)
  }

  It 'lists a continuation-line gap and not the ?. outcomes split across processes, from the binary reports' {
    $sourceRoot = $script:ShapesDir
    $gapFile = Join-Path $TestDrive 'out-shapes/library-gap.txt'
    $scriptPath = Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/Find-UncoveredNewLines.ps1'

    $pwsh = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    & $pwsh -NoProfile -File $scriptPath -CoverageRoot $script:Shapes -SourceRoot $sourceRoot -LibraryGapOutFile $gapFile | Out-Null
    $LASTEXITCODE | Should -Be 0
    $gap = @(Get-Content $gapFile | Where-Object { $_ -match '\(\d+/\d+ conditions\)' })

    $gap | Should -Be @(
      "${script:ShapesPath}:23: (1/2 conditions) if (rare) {",
      "${script:ShapesPath}:32: (1/2 conditions) if (rare) {",
      "${script:ShapesPath}:39: (1/2 conditions) _never?.Record(n);",
      "${script:ShapesPath}:40: (1/2 conditions) if (rare) {",
      "${script:ShapesPath}:47: (2/4 conditions) if (sink?.Total >= 0) {",
      "${script:ShapesPath}:57: (1/2 conditions) bomb?.Fail();",
      "${script:ShapesPath}:64: (1/2 conditions) get()",
      "${script:ShapesPath}:96: (1/2 conditions) lookup.Find<Sink>()",
      "${script:ShapesPath}:98: (1/2 conditions) if (rare) {")
  }

  It 'fails with -FailOnWholeLibrary while any hand-written outcome in the library is untested' {
    $sourceRoot = $script:ShapesDir
    $scriptPath = Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/Find-UncoveredNewLines.ps1'

    $pwsh = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $output = & $pwsh -NoProfile -File $scriptPath -CoverageRoot $script:Fixture -SourceRoot $sourceRoot -FailOnWholeLibrary

    $LASTEXITCODE | Should -Be 1
    ($output -join "`n") | Should -Match '1 hand-written decision outcome'
  }
}
