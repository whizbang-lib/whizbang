using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Shared.Discovery;

/// <summary>
/// Reusable DbContext discovery logic for database-specific generators.
/// Discovers DbContext classes and their existing perspective DbSet properties.
/// </summary>
public static class DbContextDiscovery {

  private const string DBCONTEXT_BASE_TYPE = "Microsoft.EntityFrameworkCore.DbContext";
  private const string PERSPECTIVE_ROW_TYPE = "Whizbang.Core.Lenses.PerspectiveRow<TModel>";

  /// <summary>
  /// Syntactic predicate for potential DbContext classes.
  /// This is a FAST filter that runs before semantic analysis.
  /// </summary>
  /// <param name="node">Syntax node to check</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>True if node might be a DbContext (requires semantic analysis)</returns>
  public static bool IsPotentialDbContext(SyntaxNode node, CancellationToken ct) {
    // Fast syntactic check: class with base list (inherits from something)
    return node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 };
  }

  /// <summary>
  /// Extracts DbContext information including existing perspective DbSet properties.
  /// This performs semantic analysis to find the base DbContext type and scan properties.
  /// </summary>
  /// <param name="context">Generator syntax context with semantic model</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>
  /// DbContextInfo if the class inherits from DbContext, null otherwise.
  /// </returns>
  public static DbContextInfo? Extract(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var classDecl = (ClassDeclarationSyntax)context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct);

    if (symbol is not INamedTypeSymbol namedSymbol) {
      return null;
    }

    // Walk up the inheritance chain to find DbContext
    var baseType = namedSymbol.BaseType;
    bool inheritsFromDbContext = false;

    while (baseType != null) {
      if (baseType.ToDisplayString() == DBCONTEXT_BASE_TYPE) {
        inheritsFromDbContext = true;
        break;
      }
      baseType = baseType.BaseType;
    }

    if (!inheritsFromDbContext) {
      return null;
    }

    // Extract existing perspective DbSet properties
    var existingPerspectives = ExtractExistingPerspectives(namedSymbol);

    // Get namespace
    var ns = namedSymbol.ContainingNamespace.IsGlobalNamespace
        ? ""
        : namedSymbol.ContainingNamespace.ToDisplayString();

    return new DbContextInfo(
        ClassName: namedSymbol.Name,
        FullyQualifiedName: namedSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        Namespace: ns,
        ExistingPerspectives: existingPerspectives,
        Location: classDecl.GetLocation()
    );
  }

  /// <summary>
  /// Extracts state types from existing DbSet&lt;PerspectiveRow&lt;TModel&gt;&gt; properties.
  /// Generator should not emit DbSet properties for these state types.
  /// </summary>
  private static ImmutableArray<string> ExtractExistingPerspectives(INamedTypeSymbol dbContextSymbol) {
    var builder = ImmutableArray.CreateBuilder<string>();

    foreach (var member in dbContextSymbol.GetMembers()) {
      if (member is not IPropertySymbol property) {
        continue;
      }

      // Check if property type is DbSet<PerspectiveRow<TModel>>
      if (property.Type is not INamedTypeSymbol propertyType) {
        continue;
      }

      if (!propertyType.IsGenericType) {
        continue;
      }

      // Check if it's DbSet<T>
      if (propertyType.ConstructedFrom.ToDisplayString() != "Microsoft.EntityFrameworkCore.DbSet<T>") {
        continue;
      }

      // Check if T is PerspectiveRow<TModel>
      if (propertyType.TypeArguments[0] is not INamedTypeSymbol typeArg) {
        continue;
      }

      if (!typeArg.IsGenericType) {
        continue;
      }

      if (typeArg.ConstructedFrom.ToDisplayString() != PERSPECTIVE_ROW_TYPE) {
        continue;
      }

      // Found a PerspectiveRow<TModel> - extract TModel
      var modelType = typeArg.TypeArguments[0];
      builder.Add(modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    return builder.ToImmutable();
  }
}
