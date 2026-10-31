// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports a method meant to be a collective handler that <see cref="CollectiveApplyShape"/> does not bind (#1936). A
/// method carrying <c>[CollectiveApplyFor]</c> with another shape, or an implementation of
/// <c>ICollectiveApplyFor&lt;TModel, TEvent&gt;.Apply</c> without the attribute, used to be left out of the collective
/// registry without a word, so its collective event never reached the model.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Generators.Tests/CollectiveApplyShapeAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class CollectiveApplyShapeAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Perspectives";
  private const string COLLECTIVE_APPLY_FOR = "Whizbang.Core.Perspectives.ICollectiveApplyFor<TModel, TEvent>";

  /// <summary>
  /// WHIZ116: Error - a method meant to be a collective Apply does not match the shape the framework binds, so it never
  /// runs.
  /// </summary>
  /// <docs>operations/diagnostics/whiz116</docs>
  /// <tests>tests/Whizbang.Generators.Tests/CollectiveApplyShapeAnalyzerTests.cs:AnAttributedMethodThatDoesNotMatchTheShape_IsAnErrorNamingWhatIsWrongAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/CollectiveApplyShapeAnalyzerTests.cs:AnICollectiveApplyForImplementationWithoutTheAttribute_IsAnErrorAsync</tests>
  public static readonly DiagnosticDescriptor UnboundCollectiveApply = new(
      id: "WHIZ116",
      title: "Collective Apply does not match the shape the framework binds",
      messageFormat: "Method '{0}' on '{1}' is meant to be a collective Apply but is never bound: {2}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "A collective handler is found by its shape: a method carrying [CollectiveApplyFor] that returns ICollectiveSpec<TModel> and takes the collective event, optionally followed by the ICollectiveQuery context. A method meant to be one that differs in any part is left out of the collective registry, so its collective event never changes the model."
  );

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [UnboundCollectiveApply];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeMethod, SymbolKind.Method);
  }

  private static void _analyzeMethod(SymbolAnalysisContext context) {
    var method = (IMethodSymbol)context.Symbol;
    var reason = CollectiveApplyShape.Attribute(method) is not null
      ? CollectiveApplyShape.Mismatch(method)
      : _missingAttribute(method);
    if (reason is not null) {
      context.ReportDiagnostic(Diagnostic.Create(
          UnboundCollectiveApply, method.Locations[0], method.Name, method.ContainingType.Name, reason));
    }
  }

  /// <summary>
  /// The reason an implementation of <c>ICollectiveApplyFor.Apply</c> without the attribute is never bound, or null when
  /// <paramref name="method"/> implements no such member.
  /// </summary>
  private static string? _missingAttribute(IMethodSymbol method) {
    var type = method.ContainingType;
    var implemented = type.AllInterfaces
      .Where(static i => TypeNameUtilities.IsNamed(i.OriginalDefinition, COLLECTIVE_APPLY_FOR))
      .FirstOrDefault(i => i.GetMembers(method.Name)
        .Any(member => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method)));
    return implemented is null
      ? null
      : $"it implements {TypeNameUtilities.MinimallyQualified(implemented)}.Apply but has no [CollectiveApplyFor], and the attribute is how a collective handler is found.";
  }
}
