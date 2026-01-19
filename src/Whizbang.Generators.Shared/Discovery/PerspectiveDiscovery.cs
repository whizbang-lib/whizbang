using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Shared.Discovery;

/// <summary>
/// Reusable perspective discovery logic for source generators.
/// Used by Core generator (for runtime dispatch) and database-specific generators
/// (for DbContext configuration). This shared implementation ensures consistent
/// discovery across all generators while avoiding code duplication.
/// </summary>
/// <tests>N/A - Utility class not currently used by generators</tests>
public static class PerspectiveDiscovery {

  private const string HANDLE_PERSPECTIVE_INTERFACE = "Whizbang.Core.IHandlePerspective<TEvent, TState>";
  private const string PERSPECTIVE_ROW_TYPE = "Whizbang.Core.Lenses.PerspectiveRow<TModel>";
  private const string DBSET_TYPE = "Microsoft.EntityFrameworkCore.DbSet<T>";

  /// <summary>
  /// Syntactic predicate for potential perspective handler classes.
  /// This is a FAST filter that runs before semantic analysis.
  /// </summary>
  /// <param name="node">Syntax node to check</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>True if node might be a perspective handler (requires semantic analysis)</returns>
  /// <tests>tests/Whizbang.Generators.Tests/Discovery/PerspectiveDiscoveryTests.cs:IsPotentialPerspectiveHandler_ClassWithBaseList_ReturnsTrueAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/Discovery/PerspectiveDiscoveryTests.cs:IsPotentialPerspectiveHandler_ClassWithoutBaseList_ReturnsFalseAsync</tests>
  public static bool IsPotentialPerspectiveHandler(SyntaxNode node, CancellationToken ct) {
    // Fast syntactic check: class with base list (implements interface)
    return node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 };
  }

  /// <summary>
  /// Extracts perspective information from a class implementing IHandlePerspective&lt;TEvent, TState&gt;.
  /// This performs semantic analysis to discover the event and state types.
  /// </summary>
  /// <param name="context">Generator syntax context with semantic model</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>
  /// PerspectiveInfo if the class implements IHandlePerspective, null otherwise.
  /// </returns>
  /// <tests>tests/Whizbang.Generators.Tests/Discovery/PerspectiveDiscoveryTests.cs:ExtractFromHandler_PerspectiveHandler_ReturnsPerspectiveInfoAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/Discovery/PerspectiveDiscoveryTests.cs:ExtractFromHandler_NonPerspectiveClass_ReturnsNullAsync</tests>
  public static PerspectiveInfo? ExtractFromHandler(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var classDecl = (ClassDeclarationSyntax)context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct);

    if (symbol is not INamedTypeSymbol namedSymbol) {
      return null;
    }

    // Look for IHandlePerspective<TEvent, TState> interface
    var perspectiveInterface = namedSymbol.AllInterfaces
        .FirstOrDefault(i =>
            i.IsGenericType &&
            i.ConstructedFrom.ToDisplayString() == HANDLE_PERSPECTIVE_INTERFACE);

    if (perspectiveInterface is null) {
      return null;
    }

    // Extract type arguments
    var eventType = perspectiveInterface.TypeArguments[0];
    var stateType = perspectiveInterface.TypeArguments[1];

    // Generate table name from state type (snake_case convention)
    var tableName = _toSnakeCase(stateType.Name);

    return new PerspectiveInfo(
        HandlerType: namedSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        EventType: eventType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        StateType: stateType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        TableName: tableName
    );
  }

  /// <summary>
  /// Syntactic predicate for potential DbSet properties.
  /// This is a FAST filter that runs before semantic analysis.
  /// </summary>
  /// <param name="node">Syntax node to check</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>True if node might be a DbSet property (requires semantic analysis)</returns>
  /// <tests>tests/Whizbang.Generators.Tests/Discovery/PerspectiveDiscoveryTests.cs:IsPotentialDbSetProperty_PropertyDeclaration_ReturnsTrueAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/Discovery/PerspectiveDiscoveryTests.cs:IsPotentialDbSetProperty_NonPropertyDeclaration_ReturnsFalseAsync</tests>
  public static bool IsPotentialDbSetProperty(SyntaxNode node, CancellationToken ct) {
    // Fast syntactic check: property declaration
    return node is PropertyDeclarationSyntax;
  }

  /// <summary>
  /// Extracts perspective information from a DbSet&lt;PerspectiveRow&lt;TModel&gt;&gt; property.
  /// This performs semantic analysis to discover the model type from the property signature.
  /// </summary>
  /// <param name="context">Generator syntax context with semantic model</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>
  /// PerspectiveInfo if the property is DbSet&lt;PerspectiveRow&lt;T&gt;&gt;, null otherwise.
  /// Note: HandlerType and EventType will be null (not discoverable from DbSet).
  /// </returns>
  /// <tests>tests/Whizbang.Generators.Tests/Discovery/PerspectiveDiscoveryTests.cs:ExtractFromDbSet_PerspectiveRowDbSet_ReturnsPerspectiveInfoAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/Discovery/PerspectiveDiscoveryTests.cs:ExtractFromDbSet_NonPerspectiveDbSet_ReturnsNullAsync</tests>
  public static PerspectiveInfo? ExtractFromDbSet(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var propertyDecl = (PropertyDeclarationSyntax)context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(propertyDecl, ct);

    if (symbol is not IPropertySymbol propertySymbol) {
      return null;
    }

    // Check if property type is DbSet<T>
    if (propertySymbol.Type is not INamedTypeSymbol propertyType) {
      return null;
    }

    if (propertyType.ConstructedFrom.ToDisplayString() != DBSET_TYPE) {
      return null;
    }

    // Check if T is PerspectiveRow<TModel>
    if (propertyType.TypeArguments[0] is not INamedTypeSymbol typeArg) {
      return null;
    }

    if (typeArg.ConstructedFrom.ToDisplayString() != PERSPECTIVE_ROW_TYPE) {
      return null;
    }

    // Extract TModel from PerspectiveRow<TModel>
    var modelType = typeArg.TypeArguments[0];

    return new PerspectiveInfo(
        HandlerType: null,  // Not discoverable from DbSet
        EventType: null,    // Not discoverable from DbSet
        StateType: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        TableName: propertySymbol.Name  // Use property name as table name
    );
  }

  /// <summary>
  /// Converts PascalCase to snake_case for database table names.
  /// Examples: ProductDto → product_dto, OrderReadModel → order_read_model
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/Discovery/PerspectiveDiscoveryTests.cs:ToSnakeCase_PascalCase_ReturnsSnakeCaseAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/Discovery/PerspectiveDiscoveryTests.cs:ToSnakeCase_EmptyString_ReturnsEmptyAsync</tests>
  private static string _toSnakeCase(string input) {
    if (string.IsNullOrEmpty(input)) {
      return input;
    }

    var sb = new StringBuilder();
    sb.Append(char.ToLowerInvariant(input[0]));

    for (int i = 1; i < input.Length; i++) {
      char c = input[i];
      if (char.IsUpper(c)) {
        sb.Append('_');
        sb.Append(char.ToLowerInvariant(c));
      } else {
        sb.Append(c);
      }
    }

    return sb.ToString();
  }
}
