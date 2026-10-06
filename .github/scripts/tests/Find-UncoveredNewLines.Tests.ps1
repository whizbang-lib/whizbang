#Requires -Modules Pester

# The new-code coverage gate decides what merges: a new line no test ran, or a new hand-written decision
# some outcome of which no test took, fails the PR. These tests pin both lists and the rule that tells a
# hand-written decision from the branches the compiler adds (async state machines, initializers).

BeforeAll {
  . (Join-Path -Path $PSScriptRoot -ChildPath '../../../scripts/Find-UncoveredNewLines.ps1') -CoverageRoot x -BaseRef y

  # One Cobertura report: $lines is a list of @{ n; hits; cov } where cov is "covered/total" or $null.
  function New-Report([string]$dir, [string]$name, [string]$file, [object[]]$lines) {
    $lineXml = ($lines | ForEach-Object {
      if ($_.cov) {
        $pct = [int](100 * [int]($_.cov -split '/')[0] / [int]($_.cov -split '/')[1])
        "<line number=`"$($_.n)`" hits=`"$($_.hits)`" branch=`"True`" condition-coverage=`"$pct% ($($_.cov))`" />"
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
