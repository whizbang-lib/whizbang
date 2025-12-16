using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Defensive guard methods for Roslyn semantic model operations.
/// These methods throw exceptions for "should never happen" scenarios that indicate
/// Roslyn compiler bugs rather than application logic errors.
/// </summary>
/// <remarks>
/// <para><strong>Why these guards throw exceptions:</strong></para>
/// <list type="bullet">
/// <item>Roslyn semantic model operations should never return null for valid syntax nodes</item>
/// <item>Null returns indicate malformed compilation units or compiler bugs</item>
/// <item>Exceptions provide fail-fast behavior for debugging Roslyn issues</item>
/// <item>Centralizes defensive checks in one place</item>
/// </list>
/// <para><strong>Coverage exclusion rationale:</strong></para>
/// <para>
/// This entire class is excluded from code coverage because it contains defensive checks
/// for Roslyn compiler bugs. Testing these would require malformed compilation units or
/// mocking Roslyn internals. The guards exist for robustness but are not expected to
/// execute in practice.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Generators.Tests/RoslynGuardsTests.cs</tests>
[ExcludeFromCodeCoverage]
public static class RoslynGuards {

  /// <summary>
  /// Gets the declared symbol for a class declaration, or throws if null.
  /// </summary>
  /// <param name="classDeclaration">The class declaration syntax node</param>
  /// <param name="semanticModel">The semantic model</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The declared class symbol (never null)</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown if Roslyn returns null for a valid class declaration, indicating a compiler bug.
  /// </exception>
  /// <remarks>
  /// This should never throw in practice. If it does, it indicates:
  /// <list type="bullet">
  /// <item>A malformed semantic model (Roslyn compiler bug)</item>
  /// <item>An invalid cast after syntactic predicate filtering (generator bug)</item>
  /// <item>Roslyn returning null for valid syntax (Roslyn internal failure)</item>
  /// </list>
  /// </remarks>
  public static INamedTypeSymbol GetClassSymbolOrThrow(
      ClassDeclarationSyntax classDeclaration,
      SemanticModel semanticModel,
      System.Threading.CancellationToken cancellationToken) {

    var symbol = semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) as INamedTypeSymbol ?? throw new InvalidOperationException(
          $"Roslyn returned null symbol for class declaration '{classDeclaration.Identifier.Text}'. " +
          "This indicates a Roslyn compiler bug or malformed compilation unit.");
    return symbol;
  }

  /// <summary>
  /// Gets the declared symbol for a record declaration, or throws if null.
  /// </summary>
  /// <param name="recordDeclaration">The record declaration syntax node</param>
  /// <param name="semanticModel">The semantic model</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The declared record symbol (never null)</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown if Roslyn returns null for a valid record declaration, indicating a compiler bug.
  /// </exception>
  public static INamedTypeSymbol GetRecordSymbolOrThrow(
      RecordDeclarationSyntax recordDeclaration,
      SemanticModel semanticModel,
      System.Threading.CancellationToken cancellationToken) {

    var symbol = semanticModel.GetDeclaredSymbol(recordDeclaration, cancellationToken) as INamedTypeSymbol ?? throw new InvalidOperationException(
          $"Roslyn returned null symbol for record declaration '{recordDeclaration.Identifier.Text}'. " +
          "This indicates a Roslyn compiler bug or malformed compilation unit.");
    return symbol;
  }

  /// <summary>
  /// Gets the method symbol for an invocation expression, or throws if null.
  /// </summary>
  /// <param name="invocation">The invocation expression syntax node</param>
  /// <param name="semanticModel">The semantic model</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The method symbol (never null)</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown if Roslyn returns null or non-method symbol for an invocation expression.
  /// </exception>
  public static IMethodSymbol GetMethodSymbolOrThrow(
      InvocationExpressionSyntax invocation,
      SemanticModel semanticModel,
      System.Threading.CancellationToken cancellationToken) {

    var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
    var methodSymbol = symbolInfo.Symbol as IMethodSymbol ?? throw new InvalidOperationException(
          $"Roslyn returned null or non-method symbol for invocation expression at {invocation.GetLocation()}. " +
          "This indicates a Roslyn compiler bug or malformed compilation unit.");
    return methodSymbol;
  }

  /// <summary>
  /// Gets the type symbol for an expression, or throws if null.
  /// </summary>
  /// <param name="expression">The expression syntax node</param>
  /// <param name="semanticModel">The semantic model</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The type symbol (never null)</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown if Roslyn returns null type info for an expression.
  /// </exception>
  public static ITypeSymbol GetTypeSymbolOrThrow(
      SyntaxNode expression,
      SemanticModel semanticModel,
      System.Threading.CancellationToken cancellationToken) {

    var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
    var typeSymbol = typeInfo.Type ?? throw new InvalidOperationException(
          $"Roslyn returned null type info for expression at {expression.GetLocation()}. " +
          "This indicates a Roslyn compiler bug or malformed compilation unit.");
    return typeSymbol;
  }

  /// <summary>
  /// Gets the declared symbol for a type declaration (record or class), handling the switch expression.
  /// </summary>
  /// <param name="node">The syntax node (must be RecordDeclarationSyntax or ClassDeclarationSyntax)</param>
  /// <param name="semanticModel">The semantic model</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The declared type symbol (never null)</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown if node is unexpected type or Roslyn returns null.
  /// </exception>
  /// <remarks>
  /// This method encapsulates the switch expression pattern used by multiple generators.
  /// By placing the switch in this excluded class, we avoid coverage gaps from defensive branches.
  /// </remarks>
  public static INamedTypeSymbol GetTypeSymbolFromNode(
      SyntaxNode node,
      SemanticModel semanticModel,
      System.Threading.CancellationToken cancellationToken) {

    return node switch {
      RecordDeclarationSyntax record => GetRecordSymbolOrThrow(record, semanticModel, cancellationToken),
      ClassDeclarationSyntax @class => GetClassSymbolOrThrow(@class, semanticModel, cancellationToken),
      _ => throw new InvalidOperationException(
          $"Unexpected node type: {node.GetType().Name}. " +
          "Expected RecordDeclarationSyntax or ClassDeclarationSyntax.")
    };
  }

  /// <summary>
  /// Validates that a generic interface has the expected number of type arguments.
  /// </summary>
  /// <param name="interfaceSymbol">The interface symbol to validate</param>
  /// <param name="expectedCount">Expected number of type arguments</param>
  /// <param name="interfaceName">Name of the interface (for error message)</param>
  /// <exception cref="InvalidOperationException">
  /// Thrown if the interface doesn't have the expected number of type arguments.
  /// </exception>
  /// <remarks>
  /// This defensive check ensures Roslyn didn't return a malformed generic interface.
  /// For example, IReceptor&lt;TMessage, TResponse&gt; must have exactly 2 type arguments.
  /// </remarks>
  public static void ValidateTypeArgumentCount(
      INamedTypeSymbol interfaceSymbol,
      int expectedCount,
      string interfaceName) {

    if (interfaceSymbol.TypeArguments.Length != expectedCount) {
      throw new InvalidOperationException(
          $"Expected {interfaceName} to have {expectedCount} type argument(s), " +
          $"but Roslyn returned {interfaceSymbol.TypeArguments.Length}. " +
          "This indicates a Roslyn compiler bug or malformed compilation unit.");
    }
  }

  /// <summary>
  /// Gets the containing class for a syntax node, or throws if not found.
  /// </summary>
  /// <param name="node">The syntax node to find the containing class for</param>
  /// <returns>The containing class declaration (never null)</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown if the node is not contained within a class declaration.
  /// </exception>
  /// <remarks>
  /// Valid C# requires certain constructs (like method invocations) to be within a class.
  /// If Roslyn reports a node without a containing class, it indicates invalid syntax.
  /// </remarks>
  public static ClassDeclarationSyntax GetContainingClassOrThrow(SyntaxNode node) {
    var containingClass = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault() ?? throw new InvalidOperationException(
          $"Node at {node.GetLocation()} is not contained within a class declaration. " +
          "This indicates invalid C# syntax or a Roslyn parsing error.");
    return containingClass;
  }

  /// <summary>
  /// Gets the underlying type of a Nullable&lt;T&gt; type, or throws if the type is malformed.
  /// </summary>
  /// <param name="type">The type symbol to check (must be Nullable&lt;T&gt;)</param>
  /// <param name="expectedTypeName">Expected fully qualified name of the underlying type</param>
  /// <returns>True if this is Nullable&lt;expectedType&gt;, false if not nullable</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown if the type has SpecialType.System_Nullable_T but can't be cast to INamedTypeSymbol.
  /// </exception>
  /// <remarks>
  /// Nullable&lt;T&gt; should always be an INamedTypeSymbol in Roslyn's type system.
  /// If the cast fails, it indicates a Roslyn internal inconsistency.
  /// </remarks>
  public static bool IsNullableOfType(ITypeSymbol type, string expectedTypeName) {
    // Not a nullable type at all
    if (type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T) {
      return false;
    }

    // Should be INamedTypeSymbol with type arguments
    var namedType = type as INamedTypeSymbol ?? throw new InvalidOperationException(
          $"Type '{type}' has SpecialType.System_Nullable_T but is not INamedTypeSymbol. " +
          "This indicates a Roslyn type system inconsistency.");

    // Check if Nullable<expectedType>
    return namedType.TypeArguments.Length > 0 &&
           namedType.TypeArguments[0].ToDisplayString() == expectedTypeName;
  }
}
