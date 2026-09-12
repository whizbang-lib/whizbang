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
/// Reports an index declared on a JSON-only field that cannot be built or cannot be reached.
/// </summary>
/// <remarks>
/// <para>
/// Two separate things have to hold for a declared index to be worth creating. The value's cast out
/// of the document has to be immutable, or PostgreSQL refuses to build the index at all (WHIZ303).
/// And the document has to be mapped property by property, or no query against it ever compiles to
/// the extraction the index was built over, so the index is maintained forever and scanned by
/// nothing (WHIZ304). The second is the quieter failure and the more expensive one.
/// </para>
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
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/JsonIndexStorageAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JsonIndexDeclarationAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Perspectives";
  private const string JSON_INDEXED = "Whizbang.Core.Perspectives.JsonIndexedAttribute";
  private const string INDEX_ALL_FIELDS = "Whizbang.Core.Perspectives.IndexAllFieldsAttribute";

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

  /// <summary>
  /// WHIZ304: Warning - a model declares an index no query against it can reach.
  /// </summary>
  public static readonly DiagnosticDescriptor DeclaredIndexCannotBeReached = new(
      id: "WHIZ304",
      title: "Declared index cannot be reached for this model's storage",
      messageFormat: "'{0}' declares an index over its JSON, but the model holds a polymorphic member "
          + "({1}), so its document is stored as one serialized value rather than as mapped properties. "
          + "A filter on a field inside it never compiles to the extraction the index is built over, so "
          + "the index would be maintained on every write and scanned by nothing. Promote the fields you "
          + "filter on with [PhysicalField(Indexed = true)] to get real indexed columns, or remove the "
          + "declaration.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "A model holding an abstract member, or one marked for polymorphic serialization, cannot be "
          + "mapped property by property: that mapping reconstructs the declared type and loses the derived one it "
          + "was given. Such a model is stored as a single serialized value, and nothing inside that value is a "
          + "mapped property, so an index over an extraction from it is unreachable. Reported rather than skipped "
          + "because silence is the expensive outcome here: the index costs a write every time and returns nothing, "
          + "while the author believes the field is indexed and every query on it reads the whole table. A "
          + "promoted column is a real column and stays reachable whatever the rest of the document does."
  );

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [DeclaredIndexCannotBeBuilt, DeclaredIndexCannotBeReached];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    if (context is null) {
      return;
    }

    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();

    context.RegisterSyntaxNodeAction(_analyzeProperty, SyntaxKind.PropertyDeclaration);
    context.RegisterSyntaxNodeAction(
        _analyzeModel, SyntaxKind.ClassDeclaration, SyntaxKind.RecordDeclaration);
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

  /// <summary>
  /// Reports a model that asks for an index its storage puts out of reach.
  /// </summary>
  /// <remarks>
  /// Reported once on the model rather than once per field, because the cause is how the whole
  /// document is stored and not anything about a particular field. Naming the member that forces
  /// the storage is what makes it actionable: it is the thing the author would have to change, and
  /// it is rarely the field they decorated.
  /// </remarks>
  private static void _analyzeModel(SyntaxNodeAnalysisContext context) {
    var declaration = (TypeDeclarationSyntax)context.Node;

    if (context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken)
        is not INamedTypeSymbol model) {
      return;
    }

    if (!_declaresAnyIndex(model)) {
      return;
    }

    // Storing a hierarchy opaquely is a modeling decision, not a defect. Only a declared index makes
    // it a claim that cannot be met, so the question is asked in this order.
    var forcingMember = _polymorphicMember(model);
    if (forcingMember is null) {
      return;
    }

    context.ReportDiagnostic(Diagnostic.Create(
        DeclaredIndexCannotBeReached,
        declaration.Identifier.GetLocation(),
        model.Name,
        forcingMember));
  }

  /// <summary>Whether the model asks for any index over its JSON, per field or wholesale.</summary>
  private static bool _declaresAnyIndex(INamedTypeSymbol model) =>
    model.GetAttributes().Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, INDEX_ALL_FIELDS))
    || model.GetMembers().OfType<IPropertySymbol>().Any(p =>
        p.GetAttributes().Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, JSON_INDEXED)));

  /// <summary>
  /// The member that forces the model into opaque storage, described for the message.
  /// </summary>
  /// <remarks>
  /// Only the model's own properties are named. Polymorphism can also arrive from further down the
  /// graph, and following it to the exact member would make the message long and the walk expensive;
  /// the model name plus the reason is enough to act on, and the common case is a member declared
  /// right here.
  /// </remarks>
  private static string? _polymorphicMember(INamedTypeSymbol model) {
    if (!PolymorphicModelDiscovery.IsPolymorphic(model)) {
      return null;
    }

    var named = model.GetMembers().OfType<IPropertySymbol>()
        .Where(PolymorphicModelDiscovery.IsAnalyzableProperty)
        .FirstOrDefault(p => p.Type is INamedTypeSymbol t && PolymorphicModelDiscovery.IsPolymorphicType(t));

    return named is null
      ? "a polymorphic member in its graph"
      : $"{named.Name} is {TypeNameUtilities.Display(named.Type)}";
  }
}
