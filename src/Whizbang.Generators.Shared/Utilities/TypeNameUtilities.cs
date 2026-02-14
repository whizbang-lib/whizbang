using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:GetSimpleName_INamedTypeSymbol_TopLevelClass_ReturnsNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:GetSimpleName_INamedTypeSymbol_NestedClass_ReturnsParentDotNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:GetSimpleName_String_FullyQualified_ReturnsSimpleNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:GetSimpleName_String_ArrayType_HandlesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:GetSimpleName_String_TupleType_HandlesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:GetSimpleName_String_NestedTuple_HandlesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:GetDbSetPropertyName_TopLevel_ReturnsNameWithSAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:GetDbSetPropertyName_Nested_ReturnsParentModelsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:GetTableBaseName_TopLevel_ReturnsNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:GetTableBaseName_Nested_ReturnsConcatenatedNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:FormatTypeNameForRuntime_ReturnsTypeCommaAssemblyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:SplitTupleParts_SimpleTuple_SplitsCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:SplitTupleParts_NestedParentheses_PreservesNestedAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:SplitTupleParts_Empty_ReturnsEmptyArrayAsync</tests>
/// Utilities for extracting and formatting type names from Roslyn symbols.
/// Consolidated from multiple generators for consistency and testability.
/// </summary>
public static class TypeNameUtilities {
  /// <summary>
  /// Gets a simple name for a type, including containing type for nested classes.
  /// For nested "Parent.Nested", returns "Parent.Nested".
  /// For top-level "Order", returns "Order".
  /// </summary>
  /// <remarks>Consolidated from PerspectiveDiscoveryGenerator, PerspectiveRunnerGenerator, PerspectiveRunnerRegistryGenerator.</remarks>
  public static string GetSimpleName(INamedTypeSymbol typeSymbol) {
    if (typeSymbol.ContainingType != null) {
      // Nested type - include containing type name
      return $"{typeSymbol.ContainingType.Name}.{typeSymbol.Name}";
    }
    // Top-level type - just the simple name
    return typeSymbol.Name;
  }

  /// <summary>
  /// Gets simple name from fully qualified string. Handles tuples, arrays, and nested types.
  /// E.g., "global::MyApp.Commands.CreateOrder" -> "CreateOrder"
  /// E.g., "(global::A.B, global::C.D)" -> "(B, D)"
  /// E.g., "global::MyApp.Events.NotificationEvent[]" -> "NotificationEvent[]"
  /// </summary>
  /// <remarks>Consolidated from ReceptorDiscoveryGenerator (most complete version).</remarks>
  public static string GetSimpleName(string fullyQualifiedName) {
    // Handle tuples: (Type1, Type2, ...)
    if (fullyQualifiedName.StartsWith("(", StringComparison.Ordinal) && fullyQualifiedName.EndsWith(")", StringComparison.Ordinal)) {
      var inner = fullyQualifiedName[1..^1];
      var parts = SplitTupleParts(inner);
      var simplifiedParts = new string[parts.Length];
      for (int i = 0; i < parts.Length; i++) {
        simplifiedParts[i] = GetSimpleName(parts[i].Trim());
      }
      return "(" + string.Join(", ", simplifiedParts) + ")";
    }

    // Handle arrays: Type[]
    if (fullyQualifiedName.EndsWith("[]", StringComparison.Ordinal)) {
      var baseType = fullyQualifiedName[..^2];
      return GetSimpleName(baseType) + "[]";
    }

    // Handle simple types
    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
  }

  /// <summary>
  /// Gets a name suitable for DbSet property naming.
  /// For nested "Parent.Model", returns "ParentModels".
  /// For top-level "Order", returns "Orders".
  /// </summary>
  /// <remarks>New utility for EFCoreServiceRegistrationGenerator to fix nested class DbSet naming.</remarks>
  public static string GetDbSetPropertyName(ITypeSymbol typeSymbol) {
    if (typeSymbol.ContainingType != null) {
      // Nested class: use containing type name + "Models"
      return typeSymbol.ContainingType.Name + "Models";
    }
    // Top-level class: use type name + "s" (simple pluralization)
    return typeSymbol.Name + "s";
  }

  /// <summary>
  /// Gets a name suitable for table name generation (input to snake_case conversion).
  /// For nested "Parent.Model", returns "ParentModel".
  /// For top-level "Order", returns "Order".
  /// </summary>
  /// <remarks>New utility for EFCoreServiceRegistrationGenerator to fix nested class table naming.</remarks>
  public static string GetTableBaseName(ITypeSymbol typeSymbol) {
    if (typeSymbol.ContainingType != null) {
      // Nested class: concatenate containing type name + nested type name
      return typeSymbol.ContainingType.Name + typeSymbol.Name;
    }
    // Top-level class: just the type name
    return typeSymbol.Name;
  }

  /// <summary>
  /// Formats a type name for runtime use with assembly qualification.
  /// Returns format: "TypeName, AssemblyName"
  /// E.g., "ECommerce.Contracts.ProductCreatedEvent, ECommerce.Contracts"
  /// </summary>
  /// <remarks>Moved from PerspectiveDiscoveryGenerator.</remarks>
  public static string FormatTypeNameForRuntime(ITypeSymbol typeSymbol) {
    if (typeSymbol == null) {
      throw new ArgumentNullException(nameof(typeSymbol));
    }

    // Get fully qualified type name WITHOUT global:: prefix
    var typeName = typeSymbol.ToDisplayString(new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
    ));

    // Get assembly name (simple name only, no version/culture/publicKeyToken)
    // For array types, get assembly from the element type (array types don't have ContainingAssembly)
    var assemblyName = typeSymbol is IArrayTypeSymbol arrayType
        ? arrayType.ElementType.ContainingAssembly.Name
        : typeSymbol.ContainingAssembly.Name;

    // Format: "TypeName, AssemblyName"
    return $"{typeName}, {assemblyName}";
  }

  /// <summary>
  /// Splits tuple parts respecting nested tuples and parentheses.
  /// E.g., "A, B, (C, D)" -> ["A", "B", "(C, D)"]
  /// </summary>
  /// <remarks>Moved from ReceptorDiscoveryGenerator.</remarks>
  public static string[] SplitTupleParts(string tupleContent) {
    var parts = new List<string>();
    var currentPart = new StringBuilder();
    var depth = 0;

    foreach (var ch in tupleContent) {
      if (ch == ',' && depth == 0) {
        parts.Add(currentPart.ToString());
        currentPart.Clear();
      } else {
        if (ch == '(') {
          depth++;
        } else if (ch == ')') {
          depth--;
        }

        currentPart.Append(ch);
      }
    }

    if (currentPart.Length > 0) {
      parts.Add(currentPart.ToString());
    }

    return [.. parts];
  }
}
