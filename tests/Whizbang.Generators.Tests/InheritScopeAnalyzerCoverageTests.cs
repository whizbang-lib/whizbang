// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  /// attribute sharing the simple name) could start producing WHIZ170/WHIZ171 noise for a
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

    var ours = diagnostics.Where(d => d.Id is "WHIZ170" or "WHIZ171").ToArray();
    await Assert.That(ours.Length).IsEqualTo(0)
      .Because("an interface can never carry [InheritScope] (AttributeUsage restricts it to classes), so the TypeKind guard must reject it before any attribute scan runs");
  }

  /// <summary>
  /// A class can carry attributes unrelated to scope inheritance without those attributes ever
  /// being mistaken for [InheritScope]. If the attribute-name scan (InheritScopeAnalyzer.cs:73-79)
  /// stopped short instead of running to the end of a non-matching attribute list, a class whose
  /// [InheritScope] was declared anywhere but first would silently escape both WHIZ170 and
  /// WHIZ171 — the false negative this analyzer exists to prevent, letting a misconfigured
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

    var ours = diagnostics.Where(d => d.Id is "WHIZ170" or "WHIZ171").ToArray();
    await Assert.That(ours.Length).IsEqualTo(0)
      .Because("[Obsolete] is not [InheritScope]; the attribute scan must run to completion without a match and leave the class unanalyzed");
  }

  /// <summary>
  /// A perspective class may list an unrelated interface before its perspective interface. If the
  /// interface scan stopped at the first non-matching interface instead of walking the full list,
  /// [InheritScope] on such a perspective class would escape WHIZ170, and the developer would believe
  /// an attribute the runner never reads was in effect.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task PerspectiveClassWithAnIncidentalInterfaceFirst_StillReportsWhiz170Async() {
    const string source = """
        using System;
        using Whizbang.Core.Lenses;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class FooModel { public System.Guid Id { get; set; } }
        public class FooEvent { public System.Guid StreamId { get; set; } }

        [InheritScope(OnCreate = ScopeFields.Tenant | ScopeFields.User)]
        public class DisposableProjection : IDisposable, IPerspectiveFor<FooModel, FooEvent> {
          public void Dispose() { }
          public FooModel Apply(FooModel current, FooEvent e) => new();
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ170").ToArray();
    await Assert.That(found.Length).IsEqualTo(1)
      .Because("IDisposable comes first, so the scan must walk past it to the perspective interface");
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("DisposableProjection");
  }

  /// <summary>
  /// A class that implements only unrelated interfaces is not a perspective, and may be the model of
  /// a perspective in another assembly, so the attribute on it is not reported.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ClassWithOnlyUnrelatedInterfaces_IsNotReportedAsync() {
    const string source = """
        using System;
        using Whizbang.Core.Lenses;

        namespace TestApp;

        [InheritScope(OnCreate = ScopeFields.Tenant | ScopeFields.User)]
        public class DisposableModel : IDisposable {
          public void Dispose() { }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ170")).IsEqualTo(0);
  }
}
