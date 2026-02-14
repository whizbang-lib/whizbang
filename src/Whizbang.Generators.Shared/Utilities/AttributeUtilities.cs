using System.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/AttributeUtilitiesTests.cs:GetStringValue_ExistingProperty_ReturnsValueAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/AttributeUtilitiesTests.cs:GetStringValue_MissingProperty_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/AttributeUtilitiesTests.cs:GetBoolValue_ExistingProperty_ReturnsValueAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/AttributeUtilitiesTests.cs:GetBoolValue_MissingProperty_ReturnsDefaultAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/AttributeUtilitiesTests.cs:GetIntValue_ExistingProperty_ReturnsValueAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/AttributeUtilitiesTests.cs:GetIntValue_MissingProperty_ReturnsDefaultAsync</tests>
/// Utilities for extracting values from Roslyn AttributeData.
/// Consolidated from multiple generators for consistency and testability.
/// </summary>
public static class AttributeUtilities {
  /// <summary>
  /// Gets a string property value from an attribute's named arguments.
  /// Returns null if the property is not found or is not a string.
  /// </summary>
  /// <remarks>Consolidated from RestLensEndpointGenerator and GraphQLLensTypeGenerator.</remarks>
  public static string? GetStringValue(AttributeData attribute, string propertyName) {
    var namedArg = attribute.NamedArguments
        .FirstOrDefault(a => a.Key == propertyName);

    if (namedArg.Key is null || namedArg.Value.Value is not string value) {
      return null;
    }

    return value;
  }

  /// <summary>
  /// Gets a boolean property value from an attribute's named arguments.
  /// Returns the default value if the property is not found or is not a boolean.
  /// </summary>
  /// <remarks>Consolidated from RestLensEndpointGenerator and GraphQLLensTypeGenerator.</remarks>
  public static bool GetBoolValue(AttributeData attribute, string propertyName, bool defaultValue) {
    var namedArg = attribute.NamedArguments
        .FirstOrDefault(a => a.Key == propertyName);

    if (namedArg.Key is null || namedArg.Value.Value is not bool value) {
      return defaultValue;
    }

    return value;
  }

  /// <summary>
  /// Gets an integer property value from an attribute's named arguments.
  /// Returns the default value if the property is not found or is not an integer.
  /// </summary>
  /// <remarks>Consolidated from RestLensEndpointGenerator and GraphQLLensTypeGenerator.</remarks>
  public static int GetIntValue(AttributeData attribute, string propertyName, int defaultValue) {
    var namedArg = attribute.NamedArguments
        .FirstOrDefault(a => a.Key == propertyName);

    if (namedArg.Key is null || namedArg.Value.Value is not int value) {
      return defaultValue;
    }

    return value;
  }
}
