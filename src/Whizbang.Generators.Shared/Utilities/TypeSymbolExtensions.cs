using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Extension methods for INamedTypeSymbol that handle inheritance hierarchies.
/// Provides consistent utilities for walking type hierarchies to extract properties and methods,
/// including those inherited from base classes.
/// </summary>
/// <remarks>
/// All methods are AOT-compatible (no reflection). Uses Roslyn's Symbol APIs only.
/// Properties and methods are deduplicated by name/signature, with derived class members taking precedence.
/// </remarks>
/// <docs>source-generators/type-symbol-extensions</docs>
/// <tests>Whizbang.Generators.Tests/Utilities/TypeSymbolExtensionsTests.cs</tests>
public static class TypeSymbolExtensions {
  /// <summary>
  /// Gets all properties from a type and its base types.
  /// Deduplicates by name (derived class properties take precedence).
  /// </summary>
  /// <param name="typeSymbol">The type to analyze</param>
  /// <param name="includeNonPublic">If true, includes non-public properties (default: false)</param>
  /// <param name="includeStatic">If true, includes static properties (default: false)</param>
  /// <param name="stopAtSystemObject">If true, stops before System.Object (default: true)</param>
  /// <returns>An enumerable of property symbols from the type hierarchy</returns>
  public static IEnumerable<IPropertySymbol> GetAllProperties(
      this INamedTypeSymbol typeSymbol,
      bool includeNonPublic = false,
      bool includeStatic = false,
      bool stopAtSystemObject = true) {
    var seenNames = new HashSet<string>();
    var current = typeSymbol;

    while (current is not null) {
      if (stopAtSystemObject && current.SpecialType == SpecialType.System_Object) {
        break;
      }

      foreach (var member in current.GetMembers().OfType<IPropertySymbol>()) {
        var isAccessible = includeNonPublic ||
            member.DeclaredAccessibility == Accessibility.Public;
        var isStaticMatch = includeStatic || !member.IsStatic;

        if (isAccessible && isStaticMatch && seenNames.Add(member.Name)) {
          yield return member;
        }
      }
      current = current.BaseType;
    }
  }

  /// <summary>
  /// Gets all public instance property names from a type and its base types.
  /// Convenience method that returns just the property names as a string array.
  /// </summary>
  /// <param name="typeSymbol">The type to analyze</param>
  /// <returns>Array of property names</returns>
  public static string[] GetAllPublicPropertyNames(this INamedTypeSymbol typeSymbol) {
    return typeSymbol.GetAllProperties().Select(p => p.Name).ToArray();
  }

  /// <summary>
  /// Finds the first property with a specific attribute in the inheritance chain.
  /// Searches from the most derived type up to base classes.
  /// </summary>
  /// <param name="typeSymbol">The type to analyze</param>
  /// <param name="attributeFullName">The fully qualified attribute name (e.g., "global::Whizbang.Core.StreamIdAttribute")</param>
  /// <param name="includeNonPublic">If true, includes non-public properties (default: true)</param>
  /// <returns>The first property with the attribute, or null if not found</returns>
  public static IPropertySymbol? FindPropertyWithAttribute(
      this INamedTypeSymbol typeSymbol,
      string attributeFullName,
      bool includeNonPublic = true) {
    var current = typeSymbol;

    while (current is not null && current.SpecialType != SpecialType.System_Object) {
      foreach (var member in current.GetMembers().OfType<IPropertySymbol>()) {
        if (!includeNonPublic && member.DeclaredAccessibility != Accessibility.Public) {
          continue;
        }

        if (member.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == attributeFullName)) {
          return member;
        }
      }
      current = current.BaseType;
    }
    return null;
  }

  /// <summary>
  /// Gets all methods from a type and its base types.
  /// Deduplicates by method signature (name + parameter types), with derived class methods taking precedence.
  /// </summary>
  /// <param name="typeSymbol">The type to analyze</param>
  /// <param name="includeNonPublic">If true, includes non-public methods (default: false)</param>
  /// <param name="includeStatic">If true, includes static methods (default: false)</param>
  /// <returns>An enumerable of method symbols from the type hierarchy</returns>
  public static IEnumerable<IMethodSymbol> GetAllMethods(
      this INamedTypeSymbol typeSymbol,
      bool includeNonPublic = false,
      bool includeStatic = false) {
    var seenSignatures = new HashSet<string>();
    var current = typeSymbol;

    while (current is not null && current.SpecialType != SpecialType.System_Object) {
      foreach (var member in current.GetMembers().OfType<IMethodSymbol>()) {
        // Skip special methods (constructors, property accessors, etc.)
        if (member.MethodKind != MethodKind.Ordinary) {
          continue;
        }

        var isAccessible = includeNonPublic ||
            member.DeclaredAccessibility == Accessibility.Public;
        var isStaticMatch = includeStatic || !member.IsStatic;

        // Use method signature for deduplication (name + parameter types)
        var signature = $"{member.Name}({string.Join(",", member.Parameters.Select(p => p.Type.ToDisplayString()))})";

        if (isAccessible && isStaticMatch && seenSignatures.Add(signature)) {
          yield return member;
        }
      }
      current = current.BaseType;
    }
  }

  /// <summary>
  /// Finds the first method with a specific attribute in the inheritance chain.
  /// Searches from the most derived type up to base classes.
  /// </summary>
  /// <param name="typeSymbol">The type to analyze</param>
  /// <param name="attributeFullName">The fully qualified attribute name</param>
  /// <param name="includeNonPublic">If true, includes non-public methods (default: true)</param>
  /// <returns>The first method with the attribute, or null if not found</returns>
  public static IMethodSymbol? FindMethodWithAttribute(
      this INamedTypeSymbol typeSymbol,
      string attributeFullName,
      bool includeNonPublic = true) {
    var current = typeSymbol;

    while (current is not null && current.SpecialType != SpecialType.System_Object) {
      foreach (var member in current.GetMembers().OfType<IMethodSymbol>()) {
        if (member.MethodKind != MethodKind.Ordinary) {
          continue;
        }

        if (!includeNonPublic && member.DeclaredAccessibility != Accessibility.Public) {
          continue;
        }

        if (member.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == attributeFullName)) {
          return member;
        }
      }
      current = current.BaseType;
    }
    return null;
  }

  /// <summary>
  /// Gets all methods with a specific name from a type and its base types.
  /// Useful for finding all overloads of a method (e.g., all "Apply" methods).
  /// </summary>
  /// <param name="typeSymbol">The type to analyze</param>
  /// <param name="methodName">The method name to search for</param>
  /// <param name="includeNonPublic">If true, includes non-public methods (default: false)</param>
  /// <returns>An enumerable of method symbols with the specified name</returns>
  public static IEnumerable<IMethodSymbol> GetAllMethodsByName(
      this INamedTypeSymbol typeSymbol,
      string methodName,
      bool includeNonPublic = false) {
    return typeSymbol.GetAllMethods(includeNonPublic)
        .Where(m => m.Name == methodName);
  }
}
