using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage for <see cref="InheritScopeAnalyzer"/> paths the existing
/// <c>InheritScopeAnalyzerTests</c> never exercise: a non-class named type reaching the
/// symbol action, a class whose attribute list is non-empty but contains no [InheritScope]
/// entry, and a class whose interface list is non-empty but contains nothing
/// perspective-shaped.
/// </summary>
/// <remarks>
/// One of this round's targets in this file is NOT covered here, because it matches the
/// "Roslyn-contract guard" shape already established as unreachable in earlier rounds:
/// <c>_analyzeType</c>'s <c>context.Symbol is not INamedTypeSymbol symbol =&gt; return</c>
/// (InheritScopeAnalyzer.cs:64). <c>Initialize</c> registers this callback with
/// <c>context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType)</c> — Roslyn guarantees
/// that a symbol action registered for <c>SymbolKind.NamedType</c> is always invoked with an
/// <see cref="INamedTypeSymbol"/> as <c>context.Symbol</c>. The pattern-match guard can never
/// actually fail; no source was found (or could be constructed) that reaches this branch.
/// </remarks>
/// <tests>Whizbang.Generators.Tests/InheritScopeAnalyzerTests.cs</tests>
[Category("Analyzers")]
public class InheritScopeAnalyzerCoverageTests {
  /// <summary>
  /// Scope inheritance is only ever meaningful for classes — [InheritScope]'s AttributeUsage
  /// restricts it to Class targets, and every perspective participant is a class. If the
  /// TypeKind guard (InheritScopeAnalyzer.cs:66-68) regressed and let non-class named types
  /// reach the attribute scan, a future relaxation of that AttributeUsage (or an unrelated
  /// attribute sharing the simple name) could start producing WHIZ400/WHIZ401 noise for a
  /// symbol kind that can never actually own scope data.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task InterfaceNamedType_NotAnalyzedAsClassAsync() {
    const string source = """
        namespace TestApp;

        public interface IWorkItemMarker {
          void Process();
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    var ours = diagnostics.Where(d => d.Id is "WHIZ400" or "WHIZ401").ToArray();
    await Assert.That(ours.Length).IsEqualTo(0)
      .Because("an interface can never carry [InheritScope] (AttributeUsage restricts it to classes), so the TypeKind guard must reject it before any attribute scan runs");
  }

  /// <summary>
  /// A class can carry attributes unrelated to scope inheritance without those attributes ever
  /// being mistaken for [InheritScope]. If the attribute-name scan (InheritScopeAnalyzer.cs:73-79)
  /// stopped short instead of running to the end of a non-matching attribute list, a class whose
  /// [InheritScope] was declared anywhere but first would silently escape both WHIZ400 and
  /// WHIZ401 — the false negative this analyzer exists to prevent, letting a misconfigured
  /// perspective read across a scope boundary without ever being flagged at build time.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AttributePresentButNotInheritScope_NoDiagnosticsAsync() {
    const string source = """
        using System;

        namespace TestApp;

        [Obsolete("superseded")]
        public class LegacyPlainClass {
          public int X { get; set; }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    var ours = diagnostics.Where(d => d.Id is "WHIZ400" or "WHIZ401").ToArray();
    await Assert.That(ours.Length).IsEqualTo(0)
      .Because("[Obsolete] is not [InheritScope]; the attribute scan must run to completion without a match and leave the class unanalyzed");
  }

  /// <summary>
  /// Scope inheritance decides which tenant or principal a nested read runs under — a class that
  /// implements some unrelated interface must still be flagged as non-perspective when none of
  /// its interfaces is IPerspectiveFor&lt;...&gt;. If the interface scan
  /// (InheritScopeAnalyzer.cs:107-111) stopped at the first non-matching interface instead of
  /// walking the full list, a perspective class declared with an incidental interface (logging,
  /// disposal, etc.) would wrongly escape WHIZ400, hiding a misapplied [InheritScope] that a
  /// developer could otherwise mistake for working scope inheritance.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NonPerspectiveInterfacePresent_StillReportsAuth010Async() {
    const string source = """
        using System;
        using Whizbang.Core.Lenses;

        namespace TestApp;

        [InheritScope(OnCreate = ScopeFields.Tenant | ScopeFields.User)]
        public class DisposablePerspectiveCandidate : IDisposable {
          public void Dispose() { }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ400").ToArray();
    await Assert.That(found.Length).IsEqualTo(1)
      .Because("IDisposable is a real, non-empty interface list that still contains nothing IPerspectiveFor-shaped, so the scan must walk past it and still report WHIZ400");
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("DisposablePerspectiveCandidate");
  }
}
