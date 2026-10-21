// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Suppressors;

namespace Whizbang.Generators.Tests.Suppressors;

/// <summary>
/// The framework binds a <c>[CollectiveApplyFor]</c> handler by its shape, not by an interface, so a consumer's
/// unused-parameter rules see a parameter nothing reads and flag it (#1378). The event parameter is the one the
/// framework genuinely requires: it is how the generator learns the event type. These tests run stand-ins for
/// the four rules (each reports every parameter of every method) and check the suppressor hides exactly that
/// parameter on exactly a handler the generator binds, and nothing else.
/// </summary>
[Category("Analyzers")]
public class ConventionBoundParameterSuppressorTests {
  private static readonly string[] _suppressedRules = ["CA1801", "IDE0060", "RCS1163", "S1172"];

  private const string HANDLERS = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Messaging;
    using Whizbang.Core.Perspectives;

    namespace Consumer;

    public record ArchiveJobsCollectiveEvent : CollectiveEventBase;
    public sealed class JobModel { public Guid Id { get; set; } }
    public sealed class PlainEvent { }

    public sealed class Handlers {
      [CollectiveApplyFor]
      public ICollectiveSpec<JobModel> Bound(ArchiveJobsCollectiveEvent e, ICollectiveQuery query) => null!;

      public ICollectiveSpec<JobModel> NotAttributed(ArchiveJobsCollectiveEvent e) => null!;

      [CollectiveApplyFor]
      public object WrongReturn(ArchiveJobsCollectiveEvent e) => null!;

      [CollectiveApplyFor]
      public ICollectiveSpec<JobModel> NotACollectiveEvent(PlainEvent e) => null!;
    }
    """;

  [Test]
  [Arguments("IDE0060")]
  [Arguments("CA1801")]
  [Arguments("RCS1163")]
  [Arguments("S1172")]
  [RequiresAssemblyFiles()]
  public async Task TheEventParameterOfABoundHandler_IsSuppressedAsync(string ruleId) {
    var reported = await _analyzeAsync(ruleId);

    var eventParameter = reported.Single(d => _at(d, "Bound", "e"));
    await Assert.That(eventParameter.IsSuppressed).IsTrue()
      .Because("the framework requires the event parameter: it is how the generator learns the event type");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheQueryParameter_IsNotSuppressedAsync() {
    var reported = await _analyzeAsync("RCS1163");

    var query = reported.Single(d => _at(d, "Bound", "query"));
    await Assert.That(query.IsSuppressed).IsFalse()
      .Because("the query parameter is optional: a handler that does not read it drops it");
  }

  [Test]
  [Arguments("NotAttributed")]
  [Arguments("WrongReturn")]
  [Arguments("NotACollectiveEvent")]
  [RequiresAssemblyFiles()]
  public async Task AMethodTheGeneratorDoesNotBind_IsNotSuppressedAsync(string method) {
    var reported = await _analyzeAsync("S1172");

    var parameter = reported.Single(d => _at(d, method, "e"));
    await Assert.That(parameter.IsSuppressed).IsFalse()
      .Because("only a handler the generator binds has a parameter the framework requires");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ARuleReportedOnTheMethodRatherThanAParameter_IsNotSuppressedAsync() {
    var reported = await _analyzeAsync("IDE0060", onMethods: true);

    var onMethod = reported.Single(d => _methodNamed(d) == "Bound");
    await Assert.That(onMethod.IsSuppressed).IsFalse();
  }

  [Test]
  public async Task EverySuppression_NamesTheRuleItSuppressesAndWhyAsync() {
    var suppressions = new ConventionBoundParameterSuppressor().SupportedSuppressions;

    await Assert.That(suppressions.Select(s => s.SuppressedDiagnosticId).Order())
      .IsEquivalentTo(_suppressedRules);
    await Assert.That(suppressions.Select(s => s.Id).Distinct().Count()).IsEqualTo(4);
    await Assert.That(suppressions.All(s => s.Id.StartsWith("WHIZS", StringComparison.Ordinal))).IsTrue();
    await Assert.That(suppressions.All(s => s.Justification.ToString(CultureInfo.InvariantCulture).Contains("[CollectiveApplyFor]", StringComparison.Ordinal))).IsTrue();
  }

  [RequiresAssemblyFiles()]
  private static async Task<ImmutableArray<Diagnostic>> _analyzeAsync(string ruleId, bool onMethods = false) {
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(HANDLERS);
    var options = new CompilationWithAnalyzersOptions(
      new AnalyzerOptions([]), onAnalyzerException: null, concurrentAnalysis: false,
      logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true);
    var withAnalyzers = compilation.WithAnalyzers(
      [new UnusedParameterStandIn(ruleId, onMethods), new ConventionBoundParameterSuppressor()], options);
    return (await withAnalyzers.GetAnalyzerDiagnosticsAsync()).Where(d => d.Id == ruleId).ToImmutableArray();
  }

  private static bool _at(Diagnostic diagnostic, string method, string parameter) {
    var node = diagnostic.Location.SourceTree!.GetRoot().FindNode(diagnostic.Location.SourceSpan);
    return node is ParameterSyntax p
      && p.Identifier.Text == parameter
      && p.FirstAncestorOrSelf<MethodDeclarationSyntax>()!.Identifier.Text == method;
  }

  private static string? _methodNamed(Diagnostic diagnostic) =>
    (diagnostic.Location.SourceTree!.GetRoot().FindNode(diagnostic.Location.SourceSpan) as MethodDeclarationSyntax)?.Identifier.Text;

  /// <summary>Reports <paramref name="id"/> on every parameter (or every method) it sees.</summary>
  /// <remarks>
  /// A test stand-in run in-process by <c>CompilationWithAnalyzers</c>, never loaded by a compiler, so the
  /// rules for shipping an analyzer (netstandard target, no Workspaces reference, constant ids) do not apply.
  /// </remarks>
#pragma warning disable RS1036, RS1038, RS1041
  [DiagnosticAnalyzer(LanguageNames.CSharp)]
  private sealed class UnusedParameterStandIn(string id, bool onMethods) : DiagnosticAnalyzer {
#pragma warning disable RS1017
    private readonly DiagnosticDescriptor _rule = new(id, "unused parameter", "unused parameter", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);
#pragma warning restore RS1017

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

    public override void Initialize(AnalysisContext context) {
      context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
      context.EnableConcurrentExecution();
      if (onMethods) {
        context.RegisterSyntaxNodeAction(
          c => c.ReportDiagnostic(Diagnostic.Create(_rule, c.Node.GetLocation())), SyntaxKind.MethodDeclaration);
      } else {
        context.RegisterSyntaxNodeAction(
          c => c.ReportDiagnostic(Diagnostic.Create(_rule, c.Node.GetLocation())), SyntaxKind.Parameter);
      }
    }
  }
#pragma warning restore RS1036, RS1038, RS1041
}
