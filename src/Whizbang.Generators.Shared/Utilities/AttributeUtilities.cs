// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Utilities for extracting values from Roslyn AttributeData.
/// Supports both named arguments ([Attr(Tag = "value")]) and constructor arguments ([Attr("value")]).
/// Consolidated from multiple generators for consistency and testability.
/// </summary>
/// <remarks>
/// All methods are AOT-compatible (no reflection). Uses Roslyn's AttributeData APIs only.
/// Named arguments are checked first and take precedence over constructor arguments.
/// Constructor parameter names are matched case-insensitively to property names.
/// </remarks>
/// <docs>extending/source-generators/attribute-utilities</docs>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/AttributeUtilitiesTests.cs</tests>
public static class AttributeUtilities {
  /// <summary>
  /// Gets a string property value from an attribute.
  /// Checks named arguments first, then constructor arguments.
  /// </summary>
  /// <param name="attribute">The attribute data to extract from.</param>
  /// <param name="propertyName">The property name to look for (case-insensitive for constructor args).</param>
  /// <returns>The string value, or null if not found.</returns>
  public static string? GetStringValue(AttributeData attribute, string propertyName) {
    // 1. Check named arguments first (takes precedence)
    var namedArg = attribute.NamedArguments
        .FirstOrDefault(a => a.Key == propertyName);

    if (namedArg.Key is not null && namedArg.Value.Value is string value) {
      return value;
    }

    // 2. Check constructor arguments
    if (attribute.AttributeConstructor is not null) {
      var constructorParams = attribute.AttributeConstructor.Parameters;
      for (var i = 0; i < constructorParams.Length && i < attribute.ConstructorArguments.Length; i++) {
        var param = constructorParams[i];
        // Case-insensitive match: constructor param "tag" matches property "Tag"
        if (string.Equals(param.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
            attribute.ConstructorArguments[i].Value is string ctorValue) {
          return ctorValue;
        }
      }
    }

    return null;
  }

  /// <summary>
  /// Gets a boolean property value from an attribute.
  /// Checks named arguments first, then constructor arguments.
  /// </summary>
  /// <param name="attribute">The attribute data to extract from.</param>
  /// <param name="propertyName">The property name to look for (case-insensitive for constructor args).</param>
  /// <param name="defaultValue">Value to return if property is not found.</param>
  /// <returns>The boolean value, or defaultValue if not found.</returns>
  public static bool GetBoolValue(AttributeData attribute, string propertyName, bool defaultValue) {
    // 1. Check named arguments first (takes precedence)
    var namedArg = attribute.NamedArguments
        .FirstOrDefault(a => a.Key == propertyName);

    if (namedArg.Key is not null && namedArg.Value.Value is bool value) {
      return value;
    }

    // 2. Check constructor arguments
    if (attribute.AttributeConstructor is not null) {
      var constructorParams = attribute.AttributeConstructor.Parameters;
      for (var i = 0; i < constructorParams.Length && i < attribute.ConstructorArguments.Length; i++) {
        var param = constructorParams[i];
        if (string.Equals(param.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
            attribute.ConstructorArguments[i].Value is bool ctorValue) {
          return ctorValue;
        }
      }
    }

    return defaultValue;
  }

  /// <summary>
  /// Gets an integer property value from an attribute.
  /// Checks named arguments first, then constructor arguments.
  /// </summary>
  /// <param name="attribute">The attribute data to extract from.</param>
  /// <param name="propertyName">The property name to look for (case-insensitive for constructor args).</param>
  /// <param name="defaultValue">Value to return if property is not found.</param>
  /// <returns>The integer value, or defaultValue if not found.</returns>
  public static int GetIntValue(AttributeData attribute, string propertyName, int defaultValue) {
    // 1. Check named arguments first (takes precedence)
    var namedArg = attribute.NamedArguments
        .FirstOrDefault(a => a.Key == propertyName);

    if (namedArg.Key is not null && namedArg.Value.Value is int value) {
      return value;
    }

    // 2. Check constructor arguments
    if (attribute.AttributeConstructor is not null) {
      var constructorParams = attribute.AttributeConstructor.Parameters;
      for (var i = 0; i < constructorParams.Length && i < attribute.ConstructorArguments.Length; i++) {
        var param = constructorParams[i];
        if (string.Equals(param.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
            attribute.ConstructorArguments[i].Value is int ctorValue) {
          return ctorValue;
        }
      }
    }

    return defaultValue;
  }

  /// <summary>
  /// Gets a string array property value from an attribute.
  /// Checks named arguments first, then constructor arguments.
  /// </summary>
  /// <param name="attribute">The attribute data to extract from.</param>
  /// <param name="propertyName">The property name to look for (case-insensitive for constructor args).</param>
  /// <returns>The string array, or null if not found.</returns>
  public static string[]? GetStringArrayValue(AttributeData attribute, string propertyName) {
    // 1. Check named arguments first (takes precedence)
    var namedArg = attribute.NamedArguments
        .FirstOrDefault(a => a.Key == propertyName);

    if (namedArg.Key is not null && namedArg.Value.Kind == TypedConstantKind.Array) {
      return [.. namedArg.Value.Values
          .Select(v => v.Value?.ToString() ?? "")
          .Where(s => !string.IsNullOrEmpty(s))];
    }

    // 2. Check constructor arguments
    if (attribute.AttributeConstructor is not null) {
      var constructorParams = attribute.AttributeConstructor.Parameters;
      for (var i = 0; i < constructorParams.Length && i < attribute.ConstructorArguments.Length; i++) {
        var param = constructorParams[i];
        if (string.Equals(param.Name, propertyName, StringComparison.OrdinalIgnoreCase)) {
          var arg = attribute.ConstructorArguments[i];
          if (arg.Kind == TypedConstantKind.Array) {
            return [.. arg.Values
                .Select(v => v.Value?.ToString() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))];
          }
        }
      }
    }

    return null;
  }

  /// <summary>
  /// The enumeration member an attribute's first constructor argument names, such as
  /// <c>LifecycleStage.PostInboxInline</c> in <c>[FireAt(LifecycleStage.PostInboxInline)]</c>.
  /// </summary>
  /// <param name="attribute">The attribute data to read.</param>
  /// <returns>
  /// The member, or null when the attribute has no constructor argument, its first argument is not an
  /// enumeration value, or the value is one the enumeration does not name (a cast of an undefined number).
  /// </returns>
  /// <remarks>
  /// The argument is read as the compiler bound it: an enumeration-typed argument carries its enumeration type.
  /// Asking the attribute class for its first declared constructor instead finds the wrong parameter as soon
  /// as the class declares a second constructor ahead of the enumeration one.
  /// </remarks>
  public static IFieldSymbol? FirstArgumentEnumMember(AttributeData attribute) {
    if (attribute.ConstructorArguments.Length == 0
        || attribute.ConstructorArguments[0].Kind != TypedConstantKind.Enum) {
      return null;
    }

    // An enumeration-kind constant always carries its enumeration type; only a null literal has none.
    var argument = attribute.ConstructorArguments[0];
    return argument.Type!.GetMembers().OfType<IFieldSymbol>()
        .FirstOrDefault(member => Equals(member.ConstantValue, argument.Value));
  }
}
