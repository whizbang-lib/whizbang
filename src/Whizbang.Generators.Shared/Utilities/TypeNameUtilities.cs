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
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:FormatTypeNameForRuntime_NestedType_UsesPlusNotDotAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:FormatTypeNameForRuntime_DeeplyNestedType_UsesPlusForAllLevelsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:BuildClrTypeName_TopLevelClass_ReturnsNamespaceAndNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:BuildClrTypeName_NestedClass_UsesPlusSeparatorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:BuildClrTypeName_GlobalNamespace_ReturnsTypeNameOnlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:SplitTupleParts_SimpleTuple_SplitsCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:SplitTupleParts_NestedParentheses_PreservesNestedAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/TypeNameUtilitiesTests.cs:SplitTupleParts_Empty_ReturnsEmptyArrayAsync</tests>
/// Utilities for extracting and formatting type names from Roslyn symbols.
/// Consolidated from multiple generators for consistency and testability.
/// </summary>
#pragma warning disable RS0030 // the one place the raw Roslyn display APIs (ToDisplayString, ToDisplayParts, MetadataName) are allowed (issue #698)
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
  /// For nested "Parent.ParentModel" (nested starts with containing), returns "Parent" to avoid duplication.
  /// For top-level "Order", returns "Order".
  /// </summary>
  /// <remarks>
  /// Handles the common pattern where nested perspective models have names that start with
  /// the containing class name (e.g., ActiveAccount.ActiveAccount or ActiveAccount.ActiveAccountModel).
  /// In these cases, using just the containing name avoids redundant table names like
  /// "wh_per_active_account_active_account".
  /// </remarks>
  public static string GetTableBaseName(ITypeSymbol typeSymbol) {
    if (typeSymbol.ContainingType != null) {
      var containingName = typeSymbol.ContainingType.Name;
      var nestedName = typeSymbol.Name;

      // If nested starts with containing, use just containing to avoid duplication
      // e.g., ActiveAccount.ActiveAccount -> ActiveAccount
      // e.g., ActiveAccount.ActiveAccountModel -> ActiveAccount
      if (nestedName.StartsWith(containingName, StringComparison.Ordinal)) {
        return containingName;
      }

      // Different names: concatenate containing + nested
      return containingName + nestedName;
    }
    // Top-level class: just the type name
    return typeSymbol.Name;
  }

  /// <summary>
  /// Formats a type name for runtime/CLR use with assembly qualification.
  /// Returns format: "TypeName, AssemblyName"
  /// Uses CLR format where nested types are separated by '+' (not '.').
  /// E.g., "ECommerce.Contracts.ProductCreatedEvent, ECommerce.Contracts" (top-level)
  /// E.g., "Namespace.OuterClass+NestedEvent, Assembly" (nested type)
  /// </summary>
  /// <remarks>
  /// IMPORTANT: Uses '+' for nested types to match Type.FullName format.
  /// This is critical for database lookups where event types are stored in CLR format.
  /// </remarks>
  public static string FormatTypeNameForRuntime(ITypeSymbol typeSymbol) {
    if (typeSymbol == null) {
      throw new ArgumentNullException(nameof(typeSymbol));
    }

    // Build the CLR-format type name with '+' for nested types
    var typeName = BuildClrTypeName(typeSymbol);

    // Get assembly name (simple name only, no version/culture/publicKeyToken). Array types have
    // no ContainingAssembly, and a jagged array's element is itself an array (issue #706), so
    // unwrap every level: the innermost element's assembly is the array's assembly.
    var elementType = typeSymbol;
    while (elementType is IArrayTypeSymbol arrayType) {
      elementType = arrayType.ElementType;
    }
    var assemblyName = elementType.ContainingAssembly.Name;

    // Format: "TypeName, AssemblyName"
    return $"{typeName}, {assemblyName}";
  }

  /// <summary>
  /// Builds the CLR-format type name with '+' for nested types.
  /// Namespaces use '.' separator, nested types use '+' separator.
  /// E.g., "Namespace.OuterClass+NestedClass" for nested types.
  /// </summary>
  public static string BuildClrTypeName(ITypeSymbol typeSymbol) {
    // Handle named types (classes, structs, etc.)
    if (typeSymbol is INamedTypeSymbol namedType) {
      // Build the type hierarchy using '+' for nested types
      var typeChain = new List<string>();
      INamedTypeSymbol? current = namedType;

      while (current != null) {
        // Get simple name with generic arity if applicable
        var name = current.Name;
        if (current.TypeArguments.Length > 0) {
          name += "`" + current.TypeArguments.Length;
        }
        typeChain.Insert(0, name);
        current = current.ContainingType;
      }

      // Join nested types with '+'
      var typesPart = string.Join("+", typeChain);

      // Get namespace
      var ns = namedType.ContainingNamespace?.ToDisplayString();
      if (!string.IsNullOrEmpty(ns) && ns != "<global namespace>") {
        return $"{ns}.{typesPart}";
      }

      return typesPart;
    }

    // Fallback for other type symbols (arrays, etc.)
    return typeSymbol.ToDisplayString(new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
    ));
  }

  // ---------------------------------------------------------------------------------------------
  // The named rendering forms (issue #698). Every type-name string a generator writes is a key on
  // the other side (clr_type_name, event_type, registry JSON, generated source), and there is
  // exactly one correct rendering per form. The raw Roslyn display APIs (ToDisplayString,
  // ToDisplayParts, MetadataName) are banned outside this file so a local rendering cannot drift
  // from the runtime's TypeNameFormatter again.
  // ---------------------------------------------------------------------------------------------
  private static readonly SymbolDisplayFormat _fullyQualifiedWithNullability =
    SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
      SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

  /// <summary>
  /// The fully qualified C# form for generated source: <c>global::Ns.Outer.Inner&lt;T&gt;</c>.
  /// This is code, not a key: nested types use <c>.</c> and generics carry their arguments.
  /// </summary>
  public static string FullyQualified(ISymbol symbol) {
    if (symbol == null) {
      throw new ArgumentNullException(nameof(symbol));
    }
    return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
  }

  /// <summary>The fully qualified C# form with nullable reference annotations (<c>string?</c>).</summary>
  public static string FullyQualifiedWithNullability(ITypeSymbol symbol) {
    if (symbol == null) {
      throw new ArgumentNullException(nameof(symbol));
    }
    return symbol.ToDisplayString(_fullyQualifiedWithNullability);
  }

  /// <summary>The minimally qualified C# form (<c>Inner&lt;T&gt;</c>), for generated source with usings in scope.</summary>
  public static string MinimallyQualified(ISymbol symbol) {
    if (symbol == null) {
      throw new ArgumentNullException(nameof(symbol));
    }
    return symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
  }

  /// <summary>
  /// The display form (<c>Ns.Outer.Inner</c>): diagnostics, messages, and comparisons against a
  /// known display name (see <see cref="IsNamed"/>). Never a key: nested types render with
  /// <c>.</c> here and with <c>+</c> in the CLR form (<see cref="BuildClrTypeName"/>).
  /// </summary>
  public static string Display(ISymbol symbol) {
    if (symbol == null) {
      throw new ArgumentNullException(nameof(symbol));
    }
    return symbol.ToDisplayString();
  }

  /// <summary>Whether the symbol's display form equals a known display name, ordinal.</summary>
  public static bool IsNamed(ISymbol? symbol, string displayName) =>
    symbol != null && string.Equals(symbol.ToDisplayString(), displayName, StringComparison.Ordinal);

  /// <summary>
  /// The namespace as it appears in source, or an empty string for the global namespace (Roslyn
  /// renders that one as the literal <c>&lt;global namespace&gt;</c>, which is never a namespace).
  /// </summary>
  public static string NamespaceName(INamespaceSymbol? ns) =>
    ns == null || ns.IsGlobalNamespace ? "" : ns.ToDisplayString();

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
#pragma warning restore RS0030
