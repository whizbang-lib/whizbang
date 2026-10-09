#Requires -Modules Pester

# Tests for .github/scripts/Get-TestPurity.ps1 (#1264): the purity guard over Unit-type test projects and the
# test-separation inventory it writes. Every C# fixture is a single-quoted here-string, so no '$' is expanded.

BeforeAll {
  $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '../Get-TestPurity.ps1' -Resolve
  . $script:ScriptPath
  $script:RepoRoot = Join-Path -Path $PSScriptRoot -ChildPath '../../..' -Resolve

  # A throwaway repository: each project is @{ Dir; Type; Tags; Files = @{ 'relative.cs' = 'source' } }.
  # Type and Tags are optional, so a project can declare neither.
  function New-FakeRepo([hashtable[]]$Projects) {
    $repo = Join-Path -Path $TestDrive -ChildPath ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $repo -Force | Out-Null
    foreach ($p in $Projects) {
      $dir = Join-Path -Path $repo -ChildPath $p.Dir
      New-Item -ItemType Directory -Path $dir -Force | Out-Null
      $name = Split-Path -Path $p.Dir -Leaf
      $type = if ($p.ContainsKey('Type')) { "<WhizbangTestType>$($p.Type)</WhizbangTestType>" } else { '' }
      $tags = if ($p.ContainsKey('Tags')) { "<WhizbangTestTags>$($p.Tags)</WhizbangTestTags>" } else { '' }
      Set-Content -Path (Join-Path -Path $dir -ChildPath "$name.csproj") -Value "<Project><PropertyGroup>$type$tags</PropertyGroup></Project>"
      if ($p.ContainsKey('Files')) {
        foreach ($file in $p.Files.Keys) {
          $path = Join-Path -Path $dir -ChildPath $file
          New-Item -ItemType Directory -Path (Split-Path -Path $path -Parent) -Force | Out-Null
          Set-Content -Path $path -Value $p.Files[$file] -NoNewline
        }
      }
    }
    return $repo
  }

  function Get-Type($Inventory, [string]$Class) {
    return @($Inventory.Types | Where-Object { $_.Class -ceq $Class })[0]
  }

  # One Unit project holding a component test and a unit test; reused by the report and script tests.
  $script:MixedProject = @{
    Dir = 'tests/Mixed.Tests'; Type = 'Unit'; Tags = 'Unit'; Files = @{
      'WorkerTests.cs' = @'
namespace Mixed;
public class WorkerTests {
  [Test]
  public async Task RunsAsync() {
    await _worker.StartAsync(ct);
  }
}
'@
      'PureTests.cs'   = @'
namespace Mixed;
public class PureTests {
  [Test]
  public void Adds() { }
}
'@
    }
  }
}

Describe 'Get-UnitTestProject' {
  It 'selects the projects declaring the Unit type under tests/ and samples/, in ordinal path order, with their tags' {
    $repo = New-FakeRepo -Projects @(
      @{ Dir = 'tests/A.Tests'; Type = 'Unit'; Tags = 'Unit;Docker' },
      @{ Dir = 'tests/B.Integration.Tests'; Type = 'Integration'; Tags = 'Postgres' },
      @{ Dir = 'samples/S/C.Tests'; Type = 'Unit' },
      @{ Dir = 'tests/D.Tests' },
      @{ Dir = 'tests/A.Tests/bin/Release/Copy'; Type = 'Unit' },
      @{ Dir = 'src/E.Tests'; Type = 'Unit' }
    )
    $projects = @(Get-UnitTestProject -Root $repo)
    $projects.Directory | Should -Be @('samples/S/C.Tests', 'tests/A.Tests')
    $projects.Name | Should -Be @('C.Tests', 'A.Tests')
    $projects.Tags | Should -Be @('', 'Unit;Docker')
    $projects[1].FullPath | Should -Be (Get-Item -LiteralPath (Join-Path -Path $repo -ChildPath 'tests/A.Tests')).FullName
  }

  It 'returns nothing when neither tests/ nor samples/ exists' {
    $repo = New-FakeRepo -Projects @(@{ Dir = 'src/A.Tests'; Type = 'Unit' })
    @(Get-UnitTestProject -Root $repo).Count | Should -Be 0
  }
}

Describe 'Remove-CSharpNoise' {
  It 'blanks <Case> so nothing in it is matched, keeping length and line breaks' -TestCases @(
    @{ Case = 'a line comment'; Code = "// Thread.Sleep(1)`nint x;" }
    @{ Case = 'a doc comment'; Code = "/// <c>Task.Delay(5)</c>`nint x;" }
    @{ Case = 'a multi-line block comment'; Code = "/* Thread.Sleep(1)`n Task.Delay(5) */ int x;" }
    @{ Case = 'a regular string'; Code = 'var s = "Task.Delay(5)";' }
    @{ Case = 'a string with an escaped quote'; Code = 'var s = "a\"Task.Delay(5)";' }
    @{ Case = 'a verbatim string with a doubled quote'; Code = 'var s = @"a""Task.Delay(5)";' }
    @{ Case = 'an interpolated string'; Code = 'var s = $"{x} Task.Delay(5)";' }
    @{ Case = 'a multi-line raw string'; Code = "var s = `"`"`"`n  Thread.Sleep(1);`n  `"`"`";" }
    @{ Case = 'a using directive'; Code = "using Testcontainers.PostgreSql;`nusing Alias = System.Threading.Timer;`nint x;" }
  ) {
    $clean = Remove-CSharpNoise -Text $Code
    $clean.Length | Should -Be $Code.Length
    $clean.Split("`n").Count | Should -Be $Code.Split("`n").Count
    @(Find-PurityConstruct -Text $clean).Count | Should -Be 0
  }

  It 'keeps code, including a using declaration and the code after a quote character literal' {
    $code = "using var http = new HttpClient();`nvar q = '`"'; Thread.Sleep(1);"
    $clean = Remove-CSharpNoise -Text $code
    @(Find-PurityConstruct -Text $clean).Rule | Should -Be @('RealHttp', 'ThreadSleep')
  }

  It 'returns an empty text unchanged' {
    Remove-CSharpNoise -Text '' | Should -Be ''
  }
}

Describe 'Get-LineNumber' {
  It 'counts the line an index falls on, from 1' {
    $text = "ab`ncd`nef"
    Get-LineNumber -Text $text -Index 0 | Should -Be 1
    Get-LineNumber -Text $text -Index 3 | Should -Be 2
    Get-LineNumber -Text $text -Index 8 | Should -Be 3
  }
}

Describe 'Get-CSharpTopLevelType' {
  It 'splits a file-scoped namespace at its top-level types, skipping nested types and bodiless records' {
    $text = Remove-CSharpNoise -Text @'
namespace N;
[NotInParallel]
public class A {
  private sealed class Inner { void M() { } }
  public void T() { foreach (var record in rs) { } }
}
public sealed record B(int X);
public record struct C { }
internal static class D<T> where T : class {
}
public enum E { One, Two }
'@
    $types = @(Get-CSharpTopLevelType -Text $text)
    $types.Name | Should -Be @('A', 'C', 'D', 'E')
    $types.Line | Should -Be @(3, 8, 9, 11)
    $first = $text.Substring($types[0].Start, $types[0].End - $types[0].Start)
    $first | Should -Match 'NotInParallel' -Because 'attributes before a type belong to its segment'
    $first | Should -Not -Match 'namespace' -Because 'a segment starts after the namespace declaration'
    $text.Substring($types[1].Start, $types[1].End - $types[1].Start) | Should -Match 'record B' -Because 'a bodiless declaration falls into the next segment'
  }

  It 'splits a block namespace' {
    $types = @(Get-CSharpTopLevelType -Text "namespace N {`n  public class A { }`n  public interface IB { }`n}")
    $types.Name | Should -Be @('A', 'IB')
  }

  It 'ignores a stray closing brace and a top-level block, and ends an unclosed type at the end of the text' {
    $text = "}`n{ }`nclass A {`n  void M() {"
    $types = @(Get-CSharpTopLevelType -Text $text)
    $types.Name | Should -Be @('A')
    $types[0].End | Should -Be $text.Length
  }

  It 'records each type''s namespace and whether it is declared partial' {
    $types = @(Get-CSharpTopLevelType -Text "namespace A {`n  namespace B.C {`n    public partial class X { }`n  }`n  sealed class Y { }`n}`nclass Z { }")
    $types.Name | Should -Be @('X', 'Y', 'Z')
    $types.Namespace | Should -Be @('A.B.C', 'A', '')
    $types.Partial | Should -Be @($true, $false, $false)
    @(Get-CSharpTopLevelType -Text "namespace F.G;`npublic static partial class P { }")[0].Namespace | Should -Be 'F.G'
  }

  It 'returns nothing for a file with no type' {
    @(Get-CSharpTopLevelType -Text 'namespace N;').Count | Should -Be 0
  }
}

Describe 'Find-PurityConstruct' {
  It 'flags <Rule> in: <Code>' -TestCases @(
    @{ Rule = 'Container'; Code = 'var c = new PostgreSqlBuilder().Build();' }
    @{ Rule = 'Container'; Code = 'await SharedPostgresContainer.InitializeAsync();' }
    @{ Rule = 'Container'; Code = 'var db = await PerTestDatabaseFactory.CreateAsync(x);' }
    @{ Rule = 'Container'; Code = 'var b = builder.WithImage(x);' }
    @{ Rule = 'RealHttp'; Code = 'var http = new HttpClient();' }
    @{ Rule = 'RealHttp'; Code = 'var http = new HttpClient { BaseAddress = u };' }
    @{ Rule = 'StartAsync'; Code = 'await worker.StartAsync(ct);' }
    @{ Rule = 'HostedService'; Code = 'class W : BackgroundService { }' }
    @{ Rule = 'HostedService'; Code = 'class W : IDisposable, IHostedService { }' }
    @{ Rule = 'HostBuilder'; Code = 'var b = Host.CreateApplicationBuilder();' }
    @{ Rule = 'HostBuilder'; Code = 'var b = WebApplication.CreateBuilder();' }
    @{ Rule = 'HostBuilder'; Code = 'var b = new HostBuilder();' }
    @{ Rule = 'TestServer'; Code = 'web.UseTestServer();' }
    @{ Rule = 'TestServer'; Code = 'var f = new WebApplicationFactory<Program>();' }
    @{ Rule = 'ThreadSleep'; Code = 'Thread.Sleep(10);' }
    @{ Rule = 'TaskDelay'; Code = 'await Task.Delay(100);' }
    @{ Rule = 'TaskDelay'; Code = 'await Task.Delay(TimeSpan.FromSeconds(1), ct);' }
    @{ Rule = 'NewThread'; Code = 'var t = new Thread(Run);' }
    @{ Rule = 'TaskRun'; Code = 'await Task.Run(() => 1);' }
    @{ Rule = 'TaskRun'; Code = 'Task.Factory.StartNew(Run);' }
    @{ Rule = 'ThreadPool'; Code = 'ThreadPool.QueueUserWorkItem(Run);' }
    @{ Rule = 'Parallel'; Code = 'await Parallel.ForEachAsync(xs, Run);' }
    @{ Rule = 'Timer'; Code = 'using var t = new PeriodicTimer(p);' }
    @{ Rule = 'Timer'; Code = 'var t = new System.Threading.Timer(Run);' }
    @{ Rule = 'TimeoutWait'; Code = 'await done.WaitAsync(TimeSpan.FromSeconds(5));' }
    @{ Rule = 'TimeoutWait'; Code = 'await done.WaitAsync(_timeout);' }
    @{ Rule = 'TimeoutWait'; Code = 'await done.WaitAsync(TestTimeouts.Long, ct);' }
    @{ Rule = 'TimeoutWait'; Code = 'gate.Wait(500);' }
    @{ Rule = 'TimeoutWait'; Code = 'cts.CancelAfter(100);' }
    @{ Rule = 'TimeoutWait'; Code = 'using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));' }
    @{ Rule = 'CrossThreadSignal'; Code = 'var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);' }
    @{ Rule = 'RealClock'; Code = 'var w = new Worker(TimeProvider.System);' }
    @{ Rule = 'Stopwatch'; Code = 'var sw = Stopwatch.StartNew();' }
    @{ Rule = 'FileSystem'; Code = 'File.WriteAllText(p, s);' }
    @{ Rule = 'FileSystem'; Code = 'System.IO.Directory.CreateDirectory(p);' }
    @{ Rule = 'FileSystem'; Code = 'var t = Path.GetTempPath();' }
    @{ Rule = 'Process'; Code = 'using var p = new Process { StartInfo = s };' }
    @{ Rule = 'Process'; Code = 'Process.Start(info);' }
    @{ Rule = 'Socket'; Code = 'var l = new TcpListener(ip, 0);' }
    @{ Rule = 'NetworkClient'; Code = 'var c = new NpgsqlConnection(cs);' }
    @{ Rule = 'NetworkClient'; Code = 'var f = new ConnectionFactory { Uri = u };' }
    @{ Rule = 'NetworkClient'; Code = 'var d = NpgsqlDataSource.Create(cs);' }
  ) {
    @(Find-PurityConstruct -Text $Code).Rule | Should -Be @($Rule)
  }

  It 'allows: <Code>' -TestCases @(
    @{ Code = 'await Task.Delay(Timeout.Infinite, ct);' }
    @{ Code = 'await Task.Delay(Timeout.InfiniteTimeSpan, ct);' }
    @{ Code = 'await Task.Delay(System.Threading.Timeout.Infinite, ct);' }
    @{ Code = 'await Task.Delay(-1, ct);' }
    @{ Code = 'await done.WaitAsync(ct);' }
    @{ Code = 'await done.WaitAsync(cancellationToken);' }
    @{ Code = 'using var cts = new CancellationTokenSource();' }
    @{ Code = 'var http = new HttpClient(handler);' }
    @{ Code = 'decisionFile.Exists(x);' }
    @{ Code = 'if (File.Exists(path)) { }' }
    @{ Code = 'if (Directory.Exists(path)) { }' }
    @{ Code = 'var p = new ProcessPayment(1);' }
    @{ Code = 'var now = DateTimeOffset.UtcNow;' }
    @{ Code = 'var t = new FakeTimeProvider();' }
    @{ Code = 'var done = new TaskCompletionSource();' }
    @{ Code = 'services.AddSingleton(new ServiceBusClient(cs));' }
    @{ Code = 'services.AddSingleton<IHostedService, W>();' }
  ) {
    @(Find-PurityConstruct -Text $Code).Count | Should -Be 0
  }

  It 'gives every rule a positive case above, and a non-unit category' {
    $covered = @('Container', 'RealHttp', 'StartAsync', 'HostedService', 'HostBuilder', 'TestServer', 'ThreadSleep',
      'TaskDelay', 'NewThread', 'TaskRun', 'ThreadPool', 'Parallel', 'Timer', 'TimeoutWait', 'CrossThreadSignal', 'RealClock', 'Stopwatch',
      'FileSystem', 'Process', 'Socket', 'NetworkClient')
    $script:PurityRules.Id | Should -Be $covered
    foreach ($rule in $script:PurityRules) { $rule.Category | Should -BeIn @('Component', 'Integration', 'Other') }
  }

  It 'records the category and the line of each construct' {
    $found = @(Find-PurityConstruct -Text "int x;`nawait Task.Delay(5);`nThread.Sleep(1);")
    $found.Line | Should -Be @(3, 2)
    $found.Category | Should -Be @('Component', 'Component')
  }
}

Describe 'Get-PurityCategory' {
  It 'ranks integration over component over other over unit' {
    Get-PurityCategory -Categories @() | Should -Be 'Unit'
    Get-PurityCategory -Categories @('Other', 'Unit') | Should -Be 'Other'
    Get-PurityCategory -Categories @('Other', 'Component') | Should -Be 'Component'
    Get-PurityCategory -Categories @('Component', 'Integration', 'Other') | Should -Be 'Integration'
  }
}

Describe 'Get-TestFileType' {
  It 'attributes each construct to the top-level type it occurs in, ignoring comments, and counts tests' {
    $path = Join-Path -Path $TestDrive -ChildPath 'Two.cs'
    Set-Content -Path $path -NoNewline -Value @'
namespace N;
public class ATests {
  [Test]
  public async Task OneAsync() { await w.StartAsync(ct); }
  [Test, Arguments(1)]
  public void Two(int x) { }
}
internal sealed class SlowHelper {
  // Task.Delay(5) in a comment is not a construct
  public void M() { Thread.Sleep(1); }
}
'@
    $types = @(Get-TestFileType -Path $path -RelativePath 'tests/P/Two.cs' -Project 'P')
    $types.Class | Should -Be @('ATests', 'SlowHelper')
    $types.Tests | Should -Be @(2, 0)
    $types.Category | Should -Be @('Component', 'Component')
    $types[0].Findings.Rule | Should -Be @('StartAsync')
    $types[0].Findings.Line | Should -Be @(4)
    $types[1].Findings.Rule | Should -Be @('ThreadSleep')
    $types[0].File | Should -Be 'tests/P/Two.cs'
    $types[0].Project | Should -Be 'P'
    $types[0].Line | Should -Be 2
  }

  It 'counts a cross-thread signal only in a test class, not in a double that merely offers one' {
    $path = Join-Path -Path $TestDrive -ChildPath 'Signal.cs'
    Set-Content -Path $path -NoNewline -Value @'
namespace N;
public class SignalTests { [Test] public async Task T() { var s = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); await s.Task; } }
public class SignalingDouble { public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
'@
    $types = @(Get-TestFileType -Path $path -RelativePath 'tests/P/Signal.cs' -Project 'P')
    $types.Category | Should -Be @('Component', 'Unit')
    @($types[1].Findings).Count | Should -Be 0
  }
}

Describe 'Get-TestPurityInventory' {
  BeforeAll {
    $repo = New-FakeRepo -Projects @(@{
        Dir = 'tests/P.Tests'; Type = 'Unit'; Files = @{
          'Helpers.cs'      = @'
namespace P;
public class SlowFake { public void M() { Thread.Sleep(1); } }
public class DbFixture { public string C => SharedPostgresContainer.ConnectionString; }
public class Chain { private SlowFake _f = new(); }
'@
          'ATests.cs'       = @'
namespace P;
public class ATests { [Test] public void T() { var c = new Chain(); } }
'@
          'BTests.cs'       = @'
namespace P;
public class BTests { [Test] public void T() { var d = new DbFixture(); var s = new SlowFake(); } }
'@
          'Dup1.cs'         = 'namespace P; public class Dup { public void M() { Thread.Sleep(1); } }'
          'Dup2.cs'         = 'namespace P; public class Dup { }'
          'CTests.cs'       = 'namespace P; public class CTests { [Test] public void T() { var d = new Dup(); } }'
          'SelfTests.cs'    = 'namespace P; public class SelfTests { [Test] public void T() { SelfTests.X(); } public static void X() { } }'
          'NestedTests.cs'  = 'namespace P; public class NestedTests { [Test] public void T() { new SlowFake(); } private sealed class SlowFake { } }'
          'WorkerATests.cs' = 'namespace P; public class WorkerATests { [Test] public void T() { Thread.Sleep(1); } public sealed record Probe; }'
          'PeerTests.cs'    = 'namespace P; public class PeerTests { [Test] public void T() { var p = new WorkerATests.Probe(); } }'
          'Split1.cs'       = 'namespace P; public partial class SplitTests { [Test] public void A() { Thread.Sleep(1); } }'
          'Split2.cs'       = 'namespace P; public partial class SplitTests { [Test] public void B() { } }'
          'SplitOther.cs'   = 'namespace P.Other; public partial class SplitTests { [Test] public void C() { } }'
          'Whole1.cs'       = 'namespace P; public partial class WholeTests { [Test] public void A() { } }'
          'Whole2.cs'       = 'namespace P; public partial class WholeTests { [Test] public void B() { } }'
          'bin/Debug/Gen.cs' = 'namespace P; public class Gen { void M() { Thread.Sleep(1); } }'
          'obj/Gen2.cs'     = 'namespace P; public class Gen2 { void M() { Thread.Sleep(1); } }'
        }
      }, @{ Dir = 'tests/Q.Tests'; Type = 'Unit'; Files = @{ 'QTests.cs' = 'namespace Q; public class QTests { [Test] public void T() { } }' } })
    $script:Inv = Get-TestPurityInventory -Root $repo
  }

  It 'scans every .cs file of every Unit project except bin/ and obj/, in ordinal file order' {
    $script:Inv.Projects.Name | Should -Be @('P.Tests', 'Q.Tests')
    $script:Inv.Types.Class | Should -Be @('ATests', 'BTests', 'CTests', 'Dup', 'Dup', 'SlowFake', 'DbFixture', 'Chain', 'NestedTests', 'PeerTests', 'SelfTests', 'SplitTests', 'SplitTests', 'SplitTests', 'WholeTests', 'WholeTests', 'WorkerATests', 'QTests')
    $script:Inv.Types[0].File | Should -Be 'tests/P.Tests/ATests.cs'
  }

  It 'carries a non-unit category through referenced helper types, transitively, and names them in Via' {
    (Get-Type $script:Inv 'Chain').Category | Should -Be 'Component'
    (Get-Type $script:Inv 'Chain').Via | Should -Be @('SlowFake (Component)')
    (Get-Type $script:Inv 'ATests').Category | Should -Be 'Component'
    (Get-Type $script:Inv 'ATests').Via | Should -Be @('Chain (Component)')
    (Get-Type $script:Inv 'BTests').Category | Should -Be 'Integration'
    (Get-Type $script:Inv 'BTests').Via | Should -Be @('DbFixture (Integration)', 'SlowFake (Component)')
  }

  It 'does not follow a name that more than one top-level type in the project declares' {
    (Get-Type $script:Inv 'CTests').Category | Should -Be 'Unit'
    @((Get-Type $script:Inv 'CTests').Via).Count | Should -Be 0
  }

  It 'does not follow a name the type declares itself, such as a nested type shadowing a helper' {
    (Get-Type $script:Inv 'NestedTests').Category | Should -Be 'Unit'
    @((Get-Type $script:Inv 'NestedTests').Via).Count | Should -Be 0
  }

  It 'does not follow a reference to another test class, which shares data types rather than behavior' {
    (Get-Type $script:Inv 'WorkerATests').Category | Should -Be 'Component'
    (Get-Type $script:Inv 'PeerTests').Category | Should -Be 'Unit'
  }

  It 'gives every part of a partial class the category of the whole class, within one namespace' {
    $parts = @($script:Inv.Types | Where-Object { $_.Class -ceq 'SplitTests' })
    $parts.File | Should -Be @('tests/P.Tests/Split1.cs', 'tests/P.Tests/Split2.cs', 'tests/P.Tests/SplitOther.cs')
    $parts.Category | Should -Be @('Component', 'Component', 'Unit')
    $parts[1].Via | Should -Be @('partial SplitTests (Component)')
    @($parts[2].Via).Count | Should -Be 0 -Because 'a partial class of the same name in another namespace is another class'
    $whole = @($script:Inv.Types | Where-Object { $_.Class -ceq 'WholeTests' })
    $whole.Category | Should -Be @('Unit', 'Unit')
    @($whole | ForEach-Object { $_.Via }).Count | Should -Be 0 -Because 'a unit partial class carries nothing into its parts'
  }

  It 'ignores a reference to the type itself, and keeps a type with no non-unit construct as unit' {
    (Get-Type $script:Inv 'SelfTests').Category | Should -Be 'Unit'
    (Get-Type $script:Inv 'QTests').Category | Should -Be 'Unit'
  }
}

Describe 'Format-TestPurityReport and Assert-TestPurity' {
  BeforeAll {
    $script:MixedRepo = New-FakeRepo -Projects @($script:MixedProject, @{
        Dir = 'tests/Clean.Tests'; Type = 'Unit'; Files = @{ 'CleanTests.cs' = 'namespace C; public class CleanTests { [Test] public void T() { } }' } })
    $script:Mixed = Get-TestPurityInventory -Root $script:MixedRepo
  }

  It 'lists each non-unit class per project with its constructs and lines, and says when a project is clean' {
    $report = Format-TestPurityReport -Inventory $script:Mixed
    $report[0] | Should -Be 'Test purity: 2 Unit-type projects, 3 types (3 test classes); 1 not unit-pure.'
    $text = $report -join "`n"
    $text | Should -Match 'Clean\.Tests \(tests/Clean\.Tests\): clean, 1 types'
    $text | Should -Match 'Mixed\.Tests \(tests/Mixed\.Tests\): 1 of 2 types not unit-pure'
    $text | Should -Match 'WorkerTests \[Component\] tests/Mixed\.Tests/WorkerTests\.cs:2 \(1 tests\)'
    $text | Should -Match 'StartAsync \(Component\): lines 5'
    $text | Should -Not -Match 'PureTests'
  }

  It 'names the referenced types that carried a category into a class' {
    $repo = New-FakeRepo -Projects @(@{ Dir = 'tests/V.Tests'; Type = 'Unit'; Files = @{
          'V.cs' = "namespace V;`npublic class Slow { void M() { Thread.Sleep(1); } }`npublic class VTests { [Test] public void T() { new Slow(); } }" } })
    (Format-TestPurityReport -Inventory (Get-TestPurityInventory -Root $repo)) -join "`n" | Should -Match 'via Slow \(Component\)'
  }

  It 'reports without failing while the guard is in report mode' {
    $report = Assert-TestPurity -Inventory $script:Mixed -Enforced:$false
    $report[0] | Should -Match '1 not unit-pure'
  }

  It 'fails on any non-unit class once enforced, naming it' {
    { Assert-TestPurity -Inventory $script:Mixed -Enforced:$true } | Should -Throw -ExpectedMessage '*1 type(s) in Unit-type projects are not unit tests*WorkerTests*'
  }

  It 'passes when enforced and every class is unit' {
    $repo = New-FakeRepo -Projects @(@{ Dir = 'tests/Clean.Tests'; Type = 'Unit'; Files = @{ 'CleanTests.cs' = 'namespace C; public class CleanTests { [Test] public void T() { } }' } })
    $report = Assert-TestPurity -Inventory (Get-TestPurityInventory -Root $repo) -Enforced:$true
    $report[0] | Should -Match '0 not unit-pure'
  }

  It 'fails, in either mode, when the scan found no Unit project' {
    $empty = Get-TestPurityInventory -Root (New-FakeRepo -Projects @(@{ Dir = 'tests/I.Tests'; Type = 'Integration' }))
    { Assert-TestPurity -Inventory $empty -Enforced:$false } | Should -Throw -ExpectedMessage '*found no Unit-type test project*'
  }

  It 'fails, in either mode, when the scan found no test class' {
    $noTests = Get-TestPurityInventory -Root (New-FakeRepo -Projects @(@{ Dir = 'tests/H.Tests'; Type = 'Unit'; Files = @{ 'H.cs' = 'namespace H; public class Helper { }' } }))
    { Assert-TestPurity -Inventory $noTests -Enforced:$false } | Should -Throw -ExpectedMessage '*found no test class*'
  }
}

Describe 'Export-TestPurityInventory' {
  BeforeAll {
    $script:CoreRepo = New-FakeRepo -Projects @(@{
        Dir = 'tests/Whizbang.Core.Tests'; Type = 'Unit'; Files = @{
          'Workers/WorkerTests.cs' = 'namespace W; public class WorkerTests { [Test] public async Task T() { await w.StartAsync(ct); Thread.Sleep(1); Thread.Sleep(2); } }'
          'Workers/PureTests.cs'   = 'namespace W; public class PureTests { [Test] public void T() { } }'
          'Workers/SlowHelper.cs'  = 'namespace W; public class SlowHelper { void M() { Thread.Sleep(1); } }'
          'Other/DiskTests.cs'     = 'namespace W; public class DiskTests { [Test] public void T() { File.ReadAllText(p); } }'
        }
      })
    $script:CoreInv = Get-TestPurityInventory -Root $script:CoreRepo
  }

  It 'writes one CSV row per type with its kind, category, constructs, lines and the #1259 note for Core worker types' {
    $csv = Join-Path -Path $TestDrive -ChildPath 'out/inv.csv'
    $md = Join-Path -Path $TestDrive -ChildPath 'out/inv.md'
    Export-TestPurityInventory -Inventory $script:CoreInv -CsvPath $csv -MarkdownPath $md
    $rows = @(Import-Csv -Path $csv)
    $rows.Class | Should -Be @('DiskTests', 'PureTests', 'SlowHelper', 'WorkerTests')
    $rows[2].Kind | Should -Be 'Helper'
    $rows[2].Tests | Should -Be '0'
    $rows[2].Note | Should -Be 'phase 2 waits for #1259'
    $worker = $rows[3]
    $worker.Project | Should -Be 'Whizbang.Core.Tests'
    $worker.File | Should -Be 'tests/Whizbang.Core.Tests/Workers/WorkerTests.cs'
    $worker.Kind | Should -Be 'Test'
    $worker.Tests | Should -Be '1'
    $worker.Category | Should -Be 'Component'
    $worker.Constructs | Should -Be 'StartAsync ThreadSleep'
    $worker.Reasons | Should -Be 'StartAsync L1; ThreadSleep L1,L1'
    $worker.Note | Should -Be 'phase 2 waits for #1259'
    $rows[1].Note | Should -Be '' -Because 'a unit class needs no move'
    $rows[0].Note | Should -Be '' -Because 'only the Core worker tests wait for #1259'
    $rows[0].Category | Should -Be 'Other'
    (Get-Content -Path $csv -TotalCount 1) | Should -Be '"Project","File","Class","Line","Kind","Tests","Category","Constructs","Reasons","Via","Note"'
  }

  It 'creates the markdown with the generated counts and worklist when it does not exist' {
    $md = Join-Path -Path $TestDrive -ChildPath 'new/inv.md'
    Export-TestPurityInventory -Inventory $script:CoreInv -CsvPath (Join-Path -Path $TestDrive -ChildPath 'new/inv.csv') -MarkdownPath $md
    $text = Get-Content -Path $md -Raw
    $text | Should -Match ([regex]::Escape($script:GeneratedMarker))
    $text | Should -Match '\| Whizbang\.Core\.Tests \| 1 \| 1 \| 0 \| 1 \| 3 \| 1 \|'
    $text | Should -Match '\| \*\*Total\*\* \| 1 \| 1 \| 0 \| 1 \| 3 \| 1 \|'
    $text | Should -Match '3 of 4 types are not unit-pure\.'
    $text | Should -Match '\| WorkerTests \| `tests/Whizbang\.Core\.Tests/Workers/WorkerTests\.cs` \| 1 \| Component \| StartAsync, ThreadSleep \|  \| phase 2 waits for #1259 \|'
    $text | Should -Not -Match '\| PureTests \|'
  }

  It 'replaces only the generated part of an existing markdown, keeping the text above the marker' {
    $md = Join-Path -Path $TestDrive -ChildPath 'keep.md'
    Set-Content -Path $md -Value "# Hand written`n`nKeep me.`n$($script:GeneratedMarker)`nstale generated text"
    Export-TestPurityInventory -Inventory $script:CoreInv -CsvPath (Join-Path -Path $TestDrive -ChildPath 'keep.csv') -MarkdownPath $md
    $text = Get-Content -Path $md -Raw
    $text | Should -Match 'Keep me\.'
    $text | Should -Not -Match 'stale generated text'
    ([regex]::Matches($text, [regex]::Escape($script:GeneratedMarker))).Count | Should -Be 1
  }

  It 'appends the generated part to an existing markdown that has no marker' {
    $md = Join-Path -Path $TestDrive -ChildPath 'nomarker.md'
    Set-Content -Path $md -Value '# Notes'
    Export-TestPurityInventory -Inventory $script:CoreInv -CsvPath (Join-Path -Path $TestDrive -ChildPath 'nomarker.csv') -MarkdownPath $md
    $text = Get-Content -Path $md -Raw
    $text | Should -Match '^# Notes'
    $text | Should -Match 'Counts per project'
  }

  It 'says so when a project has no non-unit type' {
    $repo = New-FakeRepo -Projects @(@{ Dir = 'tests/Clean.Tests'; Type = 'Unit'; Files = @{ 'CleanTests.cs' = 'namespace C; public class CleanTests { [Test] public void T() { } }' } })
    $markdown = ConvertTo-TestPurityMarkdown -Inventory (Get-TestPurityInventory -Root $repo)
    $markdown | Should -Match 'All 1 types are unit-pure\.'
  }
}

Describe 'running the script' {
  It 'prints the report for a repository' {
    $repo = New-FakeRepo -Projects @($script:MixedProject)
    $out = @(& $script:ScriptPath -Root $repo)
    $out[0] | Should -Be 'Test purity: 1 Unit-type projects, 2 types (2 test classes); 1 not unit-pure.'
  }

  It 'writes the inventory to plans/ by default, or to the paths given' {
    $repo = New-FakeRepo -Projects @($script:MixedProject)
    $out = @(& $script:ScriptPath -Root $repo -Inventory)
    Test-Path -Path (Join-Path -Path $repo -ChildPath 'plans/test-separation-inventory.csv') | Should -BeTrue
    Test-Path -Path (Join-Path -Path $repo -ChildPath 'plans/test-separation-inventory.md') | Should -BeTrue
    $out[-1] | Should -Match 'Wrote .*test-separation-inventory\.csv and .*test-separation-inventory\.md'

    $csv = Join-Path -Path $TestDrive -ChildPath 'given.csv'
    $md = Join-Path -Path $TestDrive -ChildPath 'given.md'
    & $script:ScriptPath -Root $repo -Inventory -CsvPath $csv -MarkdownPath $md | Out-Null
    Test-Path -Path $csv | Should -BeTrue
    Test-Path -Path $md | Should -BeTrue
  }
}

Describe 'the Unit-type projects in this repository' {
  It 'are all scanned and reported; the report fails only once phase 3 of #1264 sets $PurityEnforced' {
    $inventory = Get-TestPurityInventory -Root $script:RepoRoot
    $directories = @($inventory.Projects.Directory)
    $directories.Count | Should -BeGreaterThan 30 -Because 'the scan must find the Unit projects for this check to mean anything'
    $directories | Should -Contain 'tests/Whizbang.Core.Tests'
    $directories | Should -Contain 'tests/Whizbang.LanguageServer.Tests' -Because 'every test project declares its type (#1264)'
    @($inventory.Types | Where-Object { $_.Tests -gt 0 }).Count | Should -BeGreaterThan 1000
    $report = Assert-TestPurity -Inventory $inventory -Enforced:$script:PurityEnforced
    Write-Host ($report -join [Environment]::NewLine)
  }
}
