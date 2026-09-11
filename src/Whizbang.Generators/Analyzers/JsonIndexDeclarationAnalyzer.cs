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
/// Reports an index declared on a JSON-only field whose extraction cannot carry one.
/// </summary>
/// <remarks>
/// <para>
/// An index over a value held in a document is built from a cast out of that document, and an index
/// expression has to be immutable so its keys cannot go stale. The cast is immutable for text, the
/// integer family, numerics, booleans and identifiers, and stable for a timestamp or a date, which
/// PostgreSQL refuses to index at all.
/// </para>
/// <para>
/// Both ways of handling that silently are bad. Emitting the index anyway fails the schema pass at
/// startup with a PostgreSQL error that names no property, leaving the reader to work out which field
/// caused it. Skipping it quietly leaves a developer believing a field is indexed while every query
/// on it reads the whole table, which is the more expensive mistake because nothing ever surfaces it.
/// </para>
/// <para>
/// A blanket declaration on the model is deliberately not reported. It is not a claim about any one
/// field, so naming each field it cannot cover would be noise; those are skipped quietly and by
/// design.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/JsonIndexDeclarationAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JsonIndexDeclarationAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Perspectives";
  private const string JSON_INDEXED = "Whizbang.Core.Perspectives.JsonIndexedAttribute";

  /// <summary>
  /// WHIZ303: Warning - a field declares an index its stored form cannot carry.
  /// </summary>
  public static readonly DiagnosticDescriptor DeclaredIndexCannotBeBuilt = new(
      id: "WHIZ303",
      title: "Declared index cannot be built for this field's type",
      messageFormat: "'{0}' declares [JsonIndexed], but a {1} held in the model's JSON cannot carry an index: "
          + "the cast out of the document is not immutable, so PostgreSQL will not index it. "
          + "Promote it with [PhysicalField(Indexed = true)] to get a real indexed column, or remove the "
          + "declaration to leave the field unindexed.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "An index over a value stored in a JSON document is built from a cast out of that document, and an "
          + "index expression must be immutable so its keys cannot go stale. That holds for text, the integer family, "
          + "numerics, booleans and identifiers, but not for a timestamp or a date. Reported rather than skipped "
          + "because a declaration on a specific field is a claim about that field: silence would leave the author "
          + "believing it is indexed while every query on it scans. A blanket [IndexAllFields] is not reported, since "
          + "it claims nothing about any one field."
  );

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [DeclaredIndexCannotBeBuilt];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    if (context is null) {
      return;
    }

    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();

    context.RegisterSyntaxNodeAction(_analyzeProperty, SyntaxKind.PropertyDeclaration);
  }

  private static void _analyzeProperty(SyntaxNodeAnalysisContext context) {
    var declaration = (PropertyDeclarationSyntax)context.Node;
    if (declaration.AttributeLists.Count == 0) {
      return;
    }

    if (context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken)
        is not IPropertySymbol property) {
      return;
    }

    var declared = property.GetAttributes()
        .FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, JSON_INDEXED));
    if (declared is null) {
      return;
    }

    // The one question that matters, answered by the same code the generator uses to decide what to
    // emit. Asking it twice in two places is how the two would come to disagree.
    if (JsonIndexDiscovery.CastFor(property.Type) is not null) {
      return;
    }

    context.ReportDiagnostic(Diagnostic.Create(
        DeclaredIndexCannotBeBuilt,
        declaration.Identifier.GetLocation(),
        property.Name,
        TypeNameUtilities.Display(property.Type)));
  }
}
