using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer that detects array properties in perspective models.
/// Arrays cause EF Core change tracking failures because IList.Add() doesn't work on fixed-size arrays.
/// Recommends using List&lt;T&gt; instead.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PerspectiveModelArrayAnalyzer : DiagnosticAnalyzer {
  // Diagnostic IDs: WHIZ200-299 reserved for model validation
  private const string CATEGORY = "Whizbang.ModelValidation";

  /// <summary>
  /// WHIZ200: Warning - Perspective model uses array property which causes EF Core tracking failures.
  /// </summary>
  public static readonly DiagnosticDescriptor ArrayPropertyInPerspectiveModel = new(
      id: "WHIZ200",
      title: "Perspective model should use List<T> instead of array",
      messageFormat: "Property '{0}' in perspective model '{1}' uses array type '{2}'. Use 'List<{3}>' instead to avoid EF Core change tracking errors.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Arrays in perspective models cause 'Collection was of a fixed size' errors during EF Core change tracking. " +
                   "EF Core's snapshot mechanism uses IList.Add() which fails on arrays. Use List<T> for collection properties."
  );

  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      ImmutableArray.Create(ArrayPropertyInPerspectiveModel);

  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();

    // Register for class declarations to find perspective models
    context.RegisterSyntaxNodeAction(_analyzeClass, SyntaxKind.ClassDeclaration);
    context.RegisterSyntaxNodeAction(_analyzeRecord, SyntaxKind.RecordDeclaration);
  }

  private static void _analyzeClass(SyntaxNodeAnalysisContext context) {
    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    _analyzeTypeDeclaration(context, classDeclaration);
  }

  private static void _analyzeRecord(SyntaxNodeAnalysisContext context) {
    var recordDeclaration = (RecordDeclarationSyntax)context.Node;
    _analyzeTypeDeclaration(context, recordDeclaration);
  }

  private static void _analyzeTypeDeclaration(SyntaxNodeAnalysisContext context, TypeDeclarationSyntax typeDeclaration) {
    var typeSymbol = context.SemanticModel.GetDeclaredSymbol(typeDeclaration);
    if (typeSymbol is null) {
      return;
    }

    // Check if this type is used as a perspective model
    if (!_isPerspectiveModel(typeSymbol)) {
      return;
    }

    // Check all properties for arrays
    foreach (var member in typeSymbol.GetMembers()) {
      if (member is IPropertySymbol propertySymbol) {
        _checkPropertyForArray(context, propertySymbol, typeSymbol);
      }
    }
  }

  private static bool _isPerspectiveModel(INamedTypeSymbol typeSymbol) {
    // Check if the type has [Perspective] attribute
    foreach (var attribute in typeSymbol.GetAttributes()) {
      var attributeName = attribute.AttributeClass?.ToDisplayString() ?? "";
      if (attributeName == "Whizbang.Core.Perspectives.PerspectiveAttribute" ||
          attributeName.EndsWith(".PerspectiveAttribute", System.StringComparison.Ordinal)) {
        return true;
      }
    }

    // Check if type name ends with "Model" (common convention for perspective models)
    // This is a heuristic - the main check is the attribute
    if (typeSymbol.Name.EndsWith("Model", System.StringComparison.Ordinal)) {
      // Check if it's referenced by any IPerspectiveFor<TModel, ...> in the compilation
      // For performance, we use the naming convention heuristic here
      // The attribute check above is the definitive one
      return _isReferencedAsPerspectiveModel(typeSymbol);
    }

    return false;
  }

  private static bool _isReferencedAsPerspectiveModel(INamedTypeSymbol typeSymbol) {
    // Check if any type in the compilation implements IPerspectiveFor<ThisType, ...>
    // This is a simplified check - we look for the [Perspective] attribute on the model
    // or if there's a [StreamId] property (indicating it's a stream model)

    foreach (var member in typeSymbol.GetMembers()) {
      if (member is IPropertySymbol property) {
        foreach (var attribute in property.GetAttributes()) {
          var attributeName = attribute.AttributeClass?.ToDisplayString() ?? "";
          if (attributeName == "Whizbang.Core.Perspectives.StreamIdAttribute" ||
              attributeName.EndsWith(".StreamIdAttribute", System.StringComparison.Ordinal)) {
            return true;
          }
        }
      }
    }

    return false;
  }

  private static bool _hasVectorFieldAttribute(IPropertySymbol propertySymbol) {
    foreach (var attribute in propertySymbol.GetAttributes()) {
      var attributeName = attribute.AttributeClass?.ToDisplayString() ?? "";
      if (attributeName == "Whizbang.Core.Lenses.VectorFieldAttribute" ||
          attributeName.EndsWith(".VectorFieldAttribute", System.StringComparison.Ordinal)) {
        return true;
      }
    }
    return false;
  }

  private static void _checkPropertyForArray(
      SyntaxNodeAnalysisContext context,
      IPropertySymbol propertySymbol,
      INamedTypeSymbol containingType) {

    // Skip properties with [VectorField] attribute - these are intentionally float[]
    // for vector embeddings and are handled specially by the source generator
    if (_hasVectorFieldAttribute(propertySymbol)) {
      return;
    }

    var propertyType = propertySymbol.Type;

    // Check if property type is an array
    if (propertyType is IArrayTypeSymbol arrayType) {
      // Get the element type for the suggestion
      var elementType = arrayType.ElementType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

      // Find the property declaration syntax for accurate location
      var location = propertySymbol.Locations.FirstOrDefault() ?? Location.None;

      var diagnostic = Diagnostic.Create(
          ArrayPropertyInPerspectiveModel,
          location,
          propertySymbol.Name,
          containingType.Name,
          propertyType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
          elementType
      );
      context.ReportDiagnostic(diagnostic);
    }
  }
}
