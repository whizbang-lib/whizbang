using System;
using System.Text;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:ToSnakeCase_PascalCase_ReturnsSnakeCaseAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:ToSnakeCase_EmptyString_ReturnsEmptyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:ToSnakeCase_SingleWord_ReturnsLowercaseAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:Pluralize_WithoutS_AddsSAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:Pluralize_WithS_ReturnsSameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:StripCommonSuffixes_Model_StripsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:StripCommonSuffixes_ReadModel_StripsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:StripCommonSuffixes_Dto_StripsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:ToDefaultRouteName_ReturnsApiPrefixedRouteAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/NamingConventionUtilitiesTests.cs:ToDefaultQueryName_ReturnsCamelCasePluralAsync</tests>
/// Utilities for converting between naming conventions.
/// Consolidated from multiple generators for consistency and testability.
/// </summary>
public static class NamingConventionUtilities {
  /// <summary>
  /// Converts PascalCase to snake_case.
  /// E.g., "OrderItem" -> "order_item"
  /// </summary>
  /// <remarks>Consolidated from EFCorePerspectiveConfigurationGenerator.</remarks>
  public static string ToSnakeCase(string input) {
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

  /// <summary>
  /// Simple pluralization: adds "s" if the name doesn't already end with "s".
  /// E.g., "Order" -> "Orders", "Address" -> "Addresss" (naive - use with caution)
  /// </summary>
  /// <remarks>Extracted from inline code in RestLensEndpointGenerator and GraphQLLensTypeGenerator.</remarks>
  public static string Pluralize(string name) {
    if (string.IsNullOrEmpty(name)) {
      return name;
    }
    return name.EndsWith("s", StringComparison.Ordinal) ? name : name + "s";
  }

  /// <summary>
  /// Strips common model suffixes: "ReadModel", "Model", "Dto".
  /// E.g., "OrderReadModel" -> "Order", "ProductDto" -> "Product"
  /// </summary>
  /// <remarks>Extracted from RestLensEndpointGenerator and GraphQLLensTypeGenerator.</remarks>
  public static string StripCommonSuffixes(string name) {
    if (string.IsNullOrEmpty(name)) {
      return name;
    }

    if (name.EndsWith("ReadModel", StringComparison.Ordinal)) {
      return name.Substring(0, name.Length - 9);
    }
    if (name.EndsWith("Model", StringComparison.Ordinal)) {
      return name.Substring(0, name.Length - 5);
    }
    if (name.EndsWith("Dto", StringComparison.Ordinal)) {
      return name.Substring(0, name.Length - 3);
    }

    return name;
  }

  /// <summary>
  /// Generates a default REST route name from a model type name.
  /// E.g., "OrderReadModel" -> "/api/orders"
  /// </summary>
  /// <remarks>Consolidated from RestLensEndpointGenerator.</remarks>
  public static string ToDefaultRouteName(string modelTypeName) {
    var name = StripCommonSuffixes(modelTypeName);
    var pluralized = Pluralize(name);
    var lowercased = char.ToLowerInvariant(pluralized[0]) + pluralized.Substring(1);
    return "/api/" + lowercased;
  }

  /// <summary>
  /// Generates a default GraphQL query name from a model type name.
  /// E.g., "OrderReadModel" -> "orders"
  /// </summary>
  /// <remarks>Consolidated from GraphQLLensTypeGenerator.</remarks>
  public static string ToDefaultQueryName(string modelTypeName) {
    var name = StripCommonSuffixes(modelTypeName);
    var pluralized = Pluralize(name);
    return char.ToLowerInvariant(pluralized[0]) + pluralized.Substring(1);
  }
}
