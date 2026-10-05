// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Roslyn analyzer that reports a collective predicate filtering a perspective field that has no
/// index of its own, which makes every collective apply read every row of the table.
/// </summary>
/// <remarks>
/// <para>
/// A <c>[CollectiveApplyFor]</c> handler's <c>Where</c> becomes the <c>WHERE</c> of one
/// <c>UPDATE</c>, and the collective compiler turns each field it names into an extraction from the
/// stored document, <c>data -&gt;&gt; 'OverlayId' = @p</c>, or into the promoted column. Only an
/// index over that extraction or column answers it. The whole-document index answers containment,
/// not extraction, so <c>[PerspectiveQueries(MatchOnAnyField = true)]</c> is no answer here. An
/// earlier release created such indexes during the apply itself; nothing does any more, so a field
/// without <c>[Indexed]</c> is scanned on every new database.
/// </para>
/// <para>
/// Every reference to a field of a perspective row inside a collective handler is a predicate: the
/// setters work on the model, never on the row, so the row only appears in the cohort, including a
/// sibling cohort reached through <c>ICollectiveQuery.Of&lt;TOther&gt;()</c>.
/// </para>
/// <para>
/// Two ways out, as the decision record for this diagnostic asked for. The build property
/// <c>WhizbangCollectiveIndexWarning=false</c> turns it off for a whole compilation. A reasoned
/// <c>[SuppressIndexAdvisory("reason")]</c> on the perspective class turns it off for that
/// perspective's predicates, and on the field, the model or the assembly it records the same decision
/// it records for lens filters.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz309</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/CollectivePredicateIndexAnalyzerTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/CodeFixes/IndexedCodeFixProviderTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class CollectivePredicateIndexAnalyzer : DiagnosticAnalyzer {
  // Diagnostic IDs: WHIZ300-399 reserved for perspective validation
  private const string CATEGORY = "Whizbang.PerspectiveValidation";

  /// <summary>The build property that turns the warning off for a whole compilation when <c>false</c>.</summary>
  internal const string SWITCH_PROPERTY = "build_property.WhizbangCollectiveIndexWarning";

  private const string COLLECTIVE_APPLY_FOR_ATTRIBUTE = "Whizbang.Core.Perspectives.CollectiveApplyForAttribute";
  private const string PERSPECTIVE_ROW_PREFIX = "Whizbang.Core.Lenses.PerspectiveRow<";
  private const string DATA_PROPERTY = "Data";

  /// <summary>
  /// WHIZ309: Warning - a collective predicate filters a perspective field that has no index of its own.
  /// </summary>
  public static readonly DiagnosticDescriptor CollectivePredicateFieldHasNoIndex = new(
      id: "WHIZ309",
      title: "Collective predicate filters an unindexed field",
      messageFormat: "This collective predicate filters '{0}.{1}', which has no index of its own, so every apply reads every row of the perspective. Mark it [Indexed], or record the decision with [SuppressIndexAdvisory(\"reason\")] on the field, the model or the perspective.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "A collective apply is one UPDATE whose WHERE is the handler's predicate, and each field the " +
                   "predicate names is read as an extraction from the stored document (or as its promoted column). Only an " +
                   "index over that extraction answers it: [Indexed] on the field builds one, and [PhysicalField] plus " +
                   "[Indexed] indexes the column. The whole-document index does not, so declaring " +
                   "[PerspectiveQueries(MatchOnAnyField = true)] is no answer. Nothing creates an index at apply time, so " +
                   "without one every apply scans the table. Set the MSBuild property WhizbangCollectiveIndexWarning to " +
                   "false to turn the warning off for a project, or record a deliberate scan with " +
                   "[SuppressIndexAdvisory(\"reason\")] on the perspective, the model, the field or the assembly."
  );

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CollectivePredicateFieldHasNoIndex];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();

    context.RegisterCompilationStartAction(static start => {
      // Read once per compilation rather than per node: the switch is a project setting.
      if (!_isTurnedOff(start.Options.AnalyzerConfigOptionsProvider.GlobalOptions)) {
        start.RegisterSyntaxNodeAction(_analyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
      }
    });
  }

  private static bool _isTurnedOff(AnalyzerConfigOptions options) =>
    options.TryGetValue(SWITCH_PROPERTY, out var value)
    && string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase);

  private static void _analyzeMemberAccess(SyntaxNodeAnalysisContext context) {
    var node = (MemberAccessExpressionSyntax)context.Node;

    // Syntax first, which settles almost every member access in a compilation without a semantic
    // question: the shape is `<row>.Data.<Field>`, inside a method.
    if (node.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: DATA_PROPERTY } document
        || node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not { } method) {
      return;
    }

    var handler = context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken)!;
    if (!handler.GetAttributes().Any(static a => TypeNameUtilities.IsNamed(a.AttributeClass, COLLECTIVE_APPLY_FOR_ATTRIBUTE))
        || _modelBehind(context, document) is not { } model
        || context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol is not IPropertySymbol field
        || _isIndexed(field, model)
        || PerspectiveFilterIndexAnalyzer.IsSuppressed(field, model, context.Compilation.Assembly)
        || PerspectiveFilterIndexAnalyzer.HasReasonedSuppression(handler.ContainingType.GetAttributes())) {
      return;
    }

    // The field's declaration rides along as an additional location, which is where the code fix
    // writes [Indexed]. A field from a referenced assembly has none, and gets no fix.
    var declarations = field.DeclaringSyntaxReferences.Select(static r => Location.Create(r.SyntaxTree, r.Span));

    context.ReportDiagnostic(Diagnostic.Create(
      CollectivePredicateFieldHasNoIndex,
      node.Name.GetLocation(),
      declarations,
      TypeNameUtilities.MinimallyQualified(model),
      field.Name));
  }

  /// <summary>
  /// The model of the perspective row that <paramref name="document"/>'s <c>Data</c> belongs to, or
  /// null when it is a <c>Data</c> member of anything else.
  /// </summary>
  private static INamedTypeSymbol? _modelBehind(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax document) =>
    context.SemanticModel.GetSymbolInfo(document, context.CancellationToken).Symbol is IPropertySymbol {
      ContainingType: { TypeArguments.Length: 1 } row,
    } && TypeNameUtilities.Display(row).StartsWith(PERSPECTIVE_ROW_PREFIX, StringComparison.Ordinal)
      ? row.TypeArguments[0] as INamedTypeSymbol
      : null;

  /// <summary>
  /// Whether the field has an index the collective compiler's comparison can use: a row key, an
  /// indexed column, or an ordered index over the plain extraction. A folded or pattern-matching
  /// index is over a different expression, so it does not count.
  /// </summary>
  private static bool _isIndexed(IPropertySymbol field, INamedTypeSymbol model) =>
    PerspectiveFilterIndexAnalyzer.IsIndexBacked(field)
    || JsonIndexDiscovery.From(model).Any(i =>
      string.Equals(i.PropertyName, field.Name, StringComparison.Ordinal) && i.Ordered && !i.CaseInsensitive);
}
