// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators.Suppressors;

/// <summary>
/// Suppresses unused-parameter findings (IDE0060, CA1801, Roslynator RCS1163, Sonar S1172) on a parameter the
/// framework requires on a method it binds by convention rather than by interface (#1378). Today that is the
/// event parameter of a <c>[CollectiveApplyFor]</c> handler: the generator learns the event type from it, so a
/// handler that never reads the event still has to declare it, and an analyzer cannot know that because no
/// interface says so. Nothing else is suppressed: the optional <c>ICollectiveQuery</c> parameter is dropped
/// rather than suppressed, and a method the generator does not bind keeps every finding.
/// </summary>
/// <remarks>
/// A suppressor runs inside the consumer's build, so the suppression holds in the IDE, in <c>dotnet build</c>,
/// and for analyzers that run in the build. A suppressed finding is reported as suppressed, not hidden, so it
/// remains auditable.
/// </remarks>
/// <docs>operations/diagnostics/convention-bound-parameters</docs>
/// <tests>tests/Whizbang.Generators.Tests/Suppressors/ConventionBoundParameterSuppressorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Component.Tests/ConventionBoundParameterGuardTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConventionBoundParameterSuppressor : DiagnosticSuppressor {
  private const string JUSTIFICATION =
    "The framework binds a [CollectiveApplyFor] handler by its shape and learns the event type from this parameter, so the parameter is required even when the handler does not read it.";

  private static readonly ImmutableArray<SuppressionDescriptor> _suppressions = [
    new("WHIZS001", "IDE0060", JUSTIFICATION),
    new("WHIZS002", "CA1801", JUSTIFICATION),
    new("WHIZS003", "RCS1163", JUSTIFICATION),
    new("WHIZS004", "S1172", JUSTIFICATION),
  ];

  /// <inheritdoc />
  public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => _suppressions;

  /// <inheritdoc />
  public override void ReportSuppressions(SuppressionAnalysisContext context) {
    foreach (var diagnostic in context.ReportedDiagnostics) {
      if (_isFrameworkRequiredParameter(diagnostic, context)) {
        var descriptor = _suppressions.First(s => s.SuppressedDiagnosticId == diagnostic.Id);
        context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
      }
    }
  }

  private static bool _isFrameworkRequiredParameter(Diagnostic diagnostic, SuppressionAnalysisContext context) {
    var tree = diagnostic.Location.SourceTree;
    if (tree is null
        || tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan) is not ParameterSyntax parameter) {
      return false;
    }
    var model = context.GetSemanticModel(tree);
    return model.GetDeclaredSymbol(parameter, context.CancellationToken) is IParameterSymbol { Ordinal: 0 } symbol
      && symbol.ContainingSymbol is IMethodSymbol method
      && CollectiveApplyShape.BoundModel(method) is not null;
  }
}
