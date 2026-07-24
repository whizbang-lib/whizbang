using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="InheritScopeAnalyzer"/> diagnostics WB-AUTH-010 and WB-AUTH-011.
/// Verifies misuse of <c>[InheritScope]</c> is caught at compile time.
/// </summary>
/// <tests>Whizbang.Generators/InheritScopeAnalyzer.cs</tests>
[Category("Analyzers")]
public class InheritScopeAnalyzerTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeOnNonPerspective_ReportsAuth010Async() {
    const string source = """
        using Whizbang.Core.Lenses;

        namespace TestApp;

        // Plain class — does NOT implement IPerspectiveFor<>.
        [InheritScope(OnCreate = ScopeFields.Tenant | ScopeFields.User)]
        public class PlainClass {
          public int X { get; set; }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ400").ToArray();
    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("PlainClass");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeOnPerspective_NoAuth010Async() {
    const string source = """
        using Whizbang.Core.Lenses;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class FooModel { public System.Guid Id { get; set; } }
        public class FooEvent { public System.Guid StreamId { get; set; } }

        [InheritScope(OnCreate = ScopeFields.Tenant)]
        public class FooProjection : IPerspectiveFor<FooModel, FooEvent> {
          public FooModel Apply(FooModel current, FooEvent e) => new();
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ400").ToArray();
    await Assert.That(found.Length).IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeAllNone_ReportsAuth011Async() {
    const string source = """
        using Whizbang.Core.Lenses;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class FooModel { public System.Guid Id { get; set; } }
        public class FooEvent { public System.Guid StreamId { get; set; } }

        [InheritScope(OnCreate = ScopeFields.None, Always = ScopeFields.None)]
        public class FooProjection : IPerspectiveFor<FooModel, FooEvent> {
          public FooModel Apply(FooModel current, FooEvent e) => new();
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ401").ToArray();
    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Info);
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("FooProjection");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeDefaultTenantOnly_NoAuth011Async() {
    // The default form [InheritScope] (no overrides) is OnCreate=Tenant, Always=None.
    // That has Tenant set on OnCreate so the all-None check should NOT fire.
    const string source = """
        using Whizbang.Core.Lenses;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class FooModel { public System.Guid Id { get; set; } }
        public class FooEvent { public System.Guid StreamId { get; set; } }

        [InheritScope]
        public class FooProjection : IPerspectiveFor<FooModel, FooEvent> {
          public FooModel Apply(FooModel current, FooEvent e) => new();
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ401").ToArray();
    await Assert.That(found.Length).IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NoInheritScopeAttribute_NoDiagnosticsAsync() {
    const string source = """
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class FooModel { public System.Guid Id { get; set; } }
        public class FooEvent { public System.Guid StreamId { get; set; } }

        public class FooProjection : IPerspectiveFor<FooModel, FooEvent> {
          public FooModel Apply(FooModel current, FooEvent e) => new();
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    var ours = diagnostics.Where(d => d.Id is "WHIZ400" or "WHIZ401").ToArray();
    // (no further assertions — already covered above)
    await Assert.That(ours.Length).IsEqualTo(0);
  }
}
