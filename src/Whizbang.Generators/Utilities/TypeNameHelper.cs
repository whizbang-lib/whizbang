using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Utilities;

/// <summary>
/// Centralized type name formatting for consistent cross-service routing.
/// ALL generators must use these methods for type name operations.
/// </summary>
/// <remarks>
/// This ensures all generators use the same format (FullyQualifiedFormat with global:: prefix)
/// for type name comparisons, preventing mismatches between discovery and runtime routing.
/// </remarks>
internal static class TypeNameHelper {
  /// <summary>
  /// Standard format for all type names - always fully qualified with global::.
  /// </summary>
  private static readonly SymbolDisplayFormat _fullyQualifiedFormat =
    SymbolDisplayFormat.FullyQualifiedFormat;

  /// <summary>
  /// Gets a fully qualified type name for routing and comparison.
  /// Always returns "global::Namespace.TypeName" format.
  /// </summary>
  /// <param name="symbol">The type symbol to format.</param>
  /// <returns>Fully qualified type name with global:: prefix.</returns>
  public static string GetFullyQualifiedName(ITypeSymbol symbol) {
    return symbol.ToDisplayString(_fullyQualifiedFormat);
  }

  /// <summary>
  /// Gets a fully qualified type name for an interface's original definition.
  /// Used for generic interface matching like IPerspectiveFor&lt;T&gt;, IReceptor&lt;T&gt;.
  /// </summary>
  /// <param name="symbol">The named type symbol to format.</param>
  /// <returns>Fully qualified original definition name with global:: prefix.</returns>
  public static string GetOriginalDefinitionName(INamedTypeSymbol symbol) {
    return symbol.OriginalDefinition.ToDisplayString(_fullyQualifiedFormat);
  }

  /// <summary>
  /// Checks if a type implements an interface by fully qualified name.
  /// </summary>
  /// <param name="type">The type to check.</param>
  /// <param name="interfaceFullName">The fully qualified interface name (must include global:: prefix).</param>
  /// <returns>True if the type implements the interface.</returns>
  public static bool ImplementsInterface(ITypeSymbol type, string interfaceFullName) {
    return type.AllInterfaces.Any(i =>
      GetFullyQualifiedName(i) == interfaceFullName);
  }

  /// <summary>
  /// Checks if a type's original definition matches a generic interface pattern.
  /// Used for generic interfaces like IReceptor&lt;TMessage, TResponse&gt;.
  /// </summary>
  /// <param name="type">The type to check.</param>
  /// <param name="genericInterfacePattern">The pattern to match (e.g., "global::Whizbang.Core.IReceptor&lt;").</param>
  /// <returns>True if any interface matches the pattern.</returns>
  public static bool ImplementsGenericInterface(ITypeSymbol type, string genericInterfacePattern) {
    return type.AllInterfaces.Any(i =>
      GetOriginalDefinitionName(i).StartsWith(genericInterfacePattern, StringComparison.Ordinal));
  }

  /// <summary>
  /// Gets all interfaces implemented by a type, formatted with fully qualified names.
  /// </summary>
  /// <param name="type">The type to inspect.</param>
  /// <returns>Array of fully qualified interface names.</returns>
  public static string[] GetImplementedInterfaces(ITypeSymbol type) {
    return type.AllInterfaces
      .Select(GetFullyQualifiedName)
      .ToArray();
  }

  /// <summary>
  /// Finds an interface by fully qualified name from a type's implemented interfaces.
  /// </summary>
  /// <param name="type">The type to search.</param>
  /// <param name="interfaceFullName">The fully qualified interface name.</param>
  /// <returns>The matching interface, or null if not found.</returns>
  public static INamedTypeSymbol? FindInterface(ITypeSymbol type, string interfaceFullName) {
    return type.AllInterfaces.FirstOrDefault(i =>
      GetFullyQualifiedName(i) == interfaceFullName);
  }

  /// <summary>
  /// Finds an interface by original definition pattern from a type's implemented interfaces.
  /// Used for finding generic interfaces like IPerspectiveFor&lt;T, U&gt;.
  /// </summary>
  /// <param name="type">The type to search.</param>
  /// <param name="originalDefinitionName">The fully qualified original definition name.</param>
  /// <returns>The matching interface, or null if not found.</returns>
  public static INamedTypeSymbol? FindInterfaceByOriginalDefinition(
      ITypeSymbol type,
      string originalDefinitionName) {
    return type.AllInterfaces.FirstOrDefault(i =>
      GetOriginalDefinitionName(i) == originalDefinitionName);
  }
}
