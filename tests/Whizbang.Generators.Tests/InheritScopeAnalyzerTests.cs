// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="InheritScopeAnalyzer"/> diagnostics WHIZ170 and WHIZ171.
/// The perspective runner reads <c>[InheritScope]</c> from the perspective's model type, so the
/// analyzer reports the attribute where the runner ignores it (a perspective class over another
/// model) and never where it takes effect.
/// </summary>
/// <tests>Whizbang.Generators/InheritScopeAnalyzer.cs</tests>
[Category("Analyzers")]
public class InheritScopeAnalyzerTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeOnPerspectiveModel_IsNotReportedAsync() {
    const string source = """
        using Whizbang.Core.Lenses;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        [InheritScope(OnCreate = ScopeFields.Tenant | ScopeFields.User)]
        public class FooModel { public System.Guid Id { get; set; } }
        public class FooEvent { public System.Guid StreamId { get; set; } }

        public class FooProjection : IPerspectiveFor<FooModel, FooEvent> {
          public FooModel Apply(FooModel current, FooEvent e) => new();
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ170")).IsEqualTo(0)
      .Because("the model is where the runner reads [InheritScope], so the attribute takes effect there");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeOnAClassNoPerspectiveImplements_IsNotReportedAsync() {
    // The class may be the model of a perspective declared in another assembly, which this
    // compilation cannot see, so it is never reported.
    const string source = """
        using Whizbang.Core.Lenses;

        namespace TestApp;

        [InheritScope(OnCreate = ScopeFields.Tenant | ScopeFields.User)]
        public class SharedModel {
          public int X { get; set; }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ170")).IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeOnPerspectiveClass_ReportsWhiz170NamingTheModelAsync() {
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

    var found = diagnostics.Where(d => d.Id == "WHIZ170").ToArray();
    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    var message = found[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("FooProjection");
    await Assert.That(message).Contains("FooModel");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeOnPerspectiveWithActions_ReportsWhiz170Async() {
    const string source = """
        using Whizbang.Core.Lenses;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class FooModel { public System.Guid Id { get; set; } }
        public class FooEvent : Whizbang.Core.IEvent { public System.Guid StreamId { get; set; } }

        [InheritScope(OnCreate = ScopeFields.Tenant)]
        public class FooProjection : IPerspectiveWithActionsFor<FooModel, FooEvent> {
          public ApplyResult<FooModel> Apply(FooModel current, FooEvent e) => ApplyResult<FooModel>.None();
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ170")).IsEqualTo(1)
      .Because("a perspective with actions also has its model read for [InheritScope], not the perspective class");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeOnGlobalPerspective_ReportsWhiz170Async() {
    const string source = """
        using Whizbang.Core.Lenses;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class FooModel { public System.Guid Id { get; set; } }
        public class FooEvent : Whizbang.Core.IEvent { public System.Guid StreamId { get; set; } }

        [InheritScope(OnCreate = ScopeFields.Tenant)]
        public class FooProjection : IGlobalPerspectiveFor<FooModel, string, FooEvent> {
          public string GetPartitionKey(FooEvent @event) => "p";
          public FooModel Apply(FooModel current, FooEvent e) => new();
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ170")).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeOnAPerspectiveThatIsItsOwnModel_IsNotReportedAsync() {
    const string source = """
        using Whizbang.Core.Lenses;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class FooEvent { public System.Guid StreamId { get; set; } }

        [InheritScope(OnCreate = ScopeFields.Tenant)]
        public class SelfModel : IPerspectiveFor<SelfModel, FooEvent> {
          public System.Guid Id { get; set; }
          public SelfModel Apply(SelfModel current, FooEvent e) => current;
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ170")).IsEqualTo(0)
      .Because("the class is its own model, so the runner reads the attribute from it");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeAllNone_ReportsWhiz171SayingRowsGetNoScopeAsync() {
    const string source = """
        using Whizbang.Core.Lenses;

        namespace TestApp;

        [InheritScope(OnCreate = ScopeFields.None, Always = ScopeFields.None)]
        public class FooModel { public System.Guid Id { get; set; } }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ171").ToArray();
    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Info);
    var message = found[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("FooModel");
    await Assert.That(message).Contains("no scope");
    await Assert.That(message).DoesNotContain("omit the attribute")
      .Because("omitting the attribute copies every field, the opposite of all-None, so it is not the advice");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeWithDefaults_IsNotReportedAsWhiz171Async() {
    // [InheritScope] with no arguments copies every field at creation, so it is not all-None.
    const string source = """
        using Whizbang.Core.Lenses;

        namespace TestApp;

        [InheritScope]
        public class FooModel { public System.Guid Id { get; set; } }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ171")).IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_InheritScopeAlwaysOnly_IsNotReportedAsWhiz171Async() {
    // OnCreate defaults to every field, so setting only Always is not all-None.
    const string source = """
        using Whizbang.Core.Lenses;

        namespace TestApp;

        [InheritScope(Always = ScopeFields.User)]
        public class FooModel { public System.Guid Id { get; set; } }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<InheritScopeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ171")).IsEqualTo(0);
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

    await Assert.That(diagnostics.Count(d => d.Id is "WHIZ170" or "WHIZ171")).IsEqualTo(0);
  }
}
