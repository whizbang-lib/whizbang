using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Roslyn analyzer that ensures all perspective interfaces on a class use the same TModel type.
/// When a class implements multiple IPerspectiveFor and/or IPerspectiveWithActionsFor interfaces,
/// they must all share the same model type for consistency.
/// </summary>
/// <remarks>
/// <para>
/// This analyzer catches the common mistake of accidentally implementing perspective interfaces
/// with different model types on the same class, which would cause runtime errors.
/// </para>
/// <para>
/// <strong>Valid:</strong>
/// </para>
/// <code>
/// class OrderPerspective :
///     IPerspectiveFor&lt;OrderView, OrderCreated&gt;,
///     IPerspectiveWithActionsFor&lt;OrderView, OrderDeleted&gt;  // Same OrderView ✓
/// </code>
/// <para>
/// <strong>Invalid:</strong>
/// </para>
/// <code>
/// class BadPerspective :
///     IPerspectiveFor&lt;OrderView, OrderCreated&gt;,
///     IPerspectiveWithActionsFor&lt;ProductView, ProductDeleted&gt;  // Different model ✗
/// </code>
/// </remarks>
/// <docs>diagnostics/whiz300</docs>
/// <tests>Whizbang.Generators.Tests/Analyzers/PerspectiveModelConsistencyAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PerspectiveModelConsistencyAnalyzer : DiagnosticAnalyzer {
  // Diagnostic IDs: WHIZ300-399 reserved for perspective interface validation
  private const string CATEGORY = "Whizbang.PerspectiveValidation";

  /// <summary>
  /// WHIZ300: Error - Perspective class uses inconsistent model types across interfaces.
  /// </summary>
  public static readonly DiagnosticDescriptor InconsistentModelTypes = new(
      id: "WHIZ300",
      title: "Perspective interfaces must use the same model type",
      messageFormat: "Perspective '{0}' implements multiple perspective interfaces with different model types: {1}. All perspective interfaces on a class must use the same TModel type.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "When a class implements multiple IPerspectiveFor<TModel, TEvent> and/or IPerspectiveWithActionsFor<TModel, TEvent> interfaces, " +
                   "they must all use the same TModel type. Different model types would cause the perspective runner to fail."
  );

  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      ImmutableArray.Create(InconsistentModelTypes);

  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();

    // Register for class declarations to find perspectives
    context.RegisterSyntaxNodeAction(_analyzeClass, SyntaxKind.ClassDeclaration);
  }

  private static void _analyzeClass(SyntaxNodeAnalysisContext context) {
    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration);

    if (classSymbol is null) {
      return;
    }

    // Find all perspective interfaces (IPerspectiveFor and IPerspectiveWithActionsFor)
    // Use ToDisplayString() (not OriginalDefinition) to get the constructed type name
    // e.g., "Whizbang.Core.Perspectives.IPerspectiveFor<OrderView, OrderCreated>"
    var perspectiveInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var name = i.ToDisplayString();
          // Match both IPerspectiveFor<TModel, ...> and IPerspectiveWithActionsFor<TModel, ...>
          return name.StartsWith("Whizbang.Core.Perspectives.IPerspectiveFor<", StringComparison.Ordinal) ||
                 name.StartsWith("Whizbang.Core.Perspectives.IPerspectiveWithActionsFor<", StringComparison.Ordinal);
        })
        .ToList();

    // Need at least 2 interfaces to have a consistency issue
    if (perspectiveInterfaces.Count < 2) {
      return;
    }

    // Extract model types from all interfaces (first type argument is always TModel)
    var modelTypes = perspectiveInterfaces
        .Select(i => i.TypeArguments[0])
        .Distinct(SymbolEqualityComparer.Default)
        .ToList();

    // If all model types are the same, no issue
    if (modelTypes.Count == 1) {
      return;
    }

    // Report error - multiple different model types found
    var modelTypeNames = string.Join(", ", modelTypes
        .Where(t => t is not null)
        .Select(t => t!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));

    var diagnostic = Diagnostic.Create(
        InconsistentModelTypes,
        classDeclaration.Identifier.GetLocation(),
        classSymbol.Name,
        modelTypeNames
    );
    context.ReportDiagnostic(diagnostic);
  }
}
