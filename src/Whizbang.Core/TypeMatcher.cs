using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Whizbang.Core;

/// <summary>
/// Provides methods for matching type name strings with fuzzy matching capabilities.
/// </summary>
/// <remarks>
/// This class supports flexible type name matching based on MatchStrictness flags,
/// allowing you to ignore specific components (assembly, namespace, version, case)
/// when comparing type names. It also supports regex pattern matching.
/// </remarks>
public static class TypeMatcher {
  /// <summary>
  /// Determines whether two type name strings match according to the specified strictness flags.
  /// </summary>
  /// <param name="typeString1">The first type name string to compare.</param>
  /// <param name="typeString2">The second type name string to compare.</param>
  /// <param name="strictness">Flags controlling which components to ignore during matching.</param>
  /// <returns>True if the type names match according to the strictness rules; otherwise, false.</returns>
  /// <remarks>
  /// <para>
  /// Flags are applied in sequence:
  /// 1. IgnoreVersion - Strips version/culture/token information
  /// 2. IgnoreAssembly - Removes assembly name
  /// 3. IgnoreNamespace - Extracts simple type name only
  /// 4. IgnoreCase - Performs case-insensitive comparison
  /// </para>
  /// <para>
  /// Examples:
  /// - MatchStrictness.Exact: Strings must match exactly
  /// - MatchStrictness.IgnoreCase: Case-insensitive match
  /// - MatchStrictness.SimpleName: Match only simple type name
  /// - MatchStrictness.SimpleNameCaseInsensitive: Simple name, case-insensitive
  /// </para>
  /// </remarks>
  public static bool Matches(string typeString1, string typeString2, MatchStrictness strictness) {
    // Handle null/empty cases
    if (string.IsNullOrEmpty(typeString1) && string.IsNullOrEmpty(typeString2)) {
      return true;
    }
    if (string.IsNullOrEmpty(typeString1) || string.IsNullOrEmpty(typeString2)) {
      return false;
    }

    // Apply transformations in sequence based on flags

    // 1. Apply IgnoreVersion flag: strip version/culture/token from both strings
    if (strictness.HasFlag(MatchStrictness.IgnoreVersion)) {
      typeString1 = _stripVersionInfo(typeString1);
      typeString2 = _stripVersionInfo(typeString2);
    }

    // 2. Apply IgnoreAssembly flag: remove assembly part from both strings
    if (strictness.HasFlag(MatchStrictness.IgnoreAssembly)) {
      typeString1 = _stripAssembly(typeString1);
      typeString2 = _stripAssembly(typeString2);
    }

    // 3. Apply IgnoreNamespace flag: keep only simple type name
    if (strictness.HasFlag(MatchStrictness.IgnoreNamespace)) {
      typeString1 = _getSimpleName(typeString1);
      typeString2 = _getSimpleName(typeString2);
    }

    // 4. Apply IgnoreCase flag: compare case-insensitively
    var comparison = strictness.HasFlag(MatchStrictness.IgnoreCase)
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    return string.Equals(typeString1, typeString2, comparison);
  }

  /// <summary>
  /// Determines whether a type name string matches the specified regex pattern.
  /// </summary>
  /// <param name="typeString">The type name string to test.</param>
  /// <param name="pattern">The regex pattern to match against.</param>
  /// <returns>True if the type name matches the pattern; otherwise, false.</returns>
  /// <remarks>
  /// This method is useful for wildcard matching or complex type name filtering.
  /// Example patterns:
  /// - ".*Product.*" - Matches any type containing "Product"
  /// - ".*Event$" - Matches types ending with "Event"
  /// - "^ECommerce\..*" - Matches types starting with "ECommerce."
  /// </remarks>
  public static bool Matches(string typeString, Regex pattern) {
    ArgumentNullException.ThrowIfNull(pattern);

    if (string.IsNullOrEmpty(typeString)) {
      return false;
    }

    return pattern.IsMatch(typeString);
  }

  /// <summary>
  /// Strips version, culture, and public key token information from a type name string.
  /// </summary>
  /// <param name="typeString">The type name string (may include assembly and version info).</param>
  /// <returns>The type name with version info removed.</returns>
  /// <remarks>
  /// Converts: "Type, Assembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
  /// To: "Type, Assembly"
  /// </remarks>
  private static string _stripVersionInfo(string typeString) {
    if (string.IsNullOrEmpty(typeString)) {
      return typeString;
    }

    // Split by comma and take parts before version info
    var parts = typeString.Split(',').Select(p => p.Trim()).ToArray();

    if (parts.Length <= 2) {
      // No version info present (just "Type" or "Type, Assembly")
      return typeString;
    }

    // Return just type name and assembly name (first two parts)
    return $"{parts[0]}, {parts[1]}";
  }

  /// <summary>
  /// Strips the assembly name from a type name string, leaving only the type name.
  /// </summary>
  /// <param name="typeString">The type name string (may include assembly).</param>
  /// <returns>The type name without assembly information.</returns>
  /// <remarks>
  /// Converts: "Namespace.Type, Assembly"
  /// To: "Namespace.Type"
  /// </remarks>
  private static string _stripAssembly(string typeString) {
    if (string.IsNullOrEmpty(typeString)) {
      return typeString;
    }

    // Split by comma and take only the first part (type name with namespace)
    var parts = typeString.Split(',');
    return parts[0].Trim();
  }

  /// <summary>
  /// Extracts the simple type name from a fully qualified type name string.
  /// </summary>
  /// <param name="typeString">The type name string (may include namespace and assembly).</param>
  /// <returns>Just the simple type name without namespace or assembly.</returns>
  /// <remarks>
  /// Converts: "Namespace.SubNamespace.Type, Assembly"
  /// To: "Type"
  /// </remarks>
  private static string _getSimpleName(string typeString) {
    if (string.IsNullOrEmpty(typeString)) {
      return typeString;
    }

    // First strip assembly if present
    typeString = _stripAssembly(typeString);

    // Then extract just the type name (last part after last dot)
    var lastDotIndex = typeString.LastIndexOf('.');
    if (lastDotIndex >= 0 && lastDotIndex < typeString.Length - 1) {
      return typeString[(lastDotIndex + 1)..];
    }

    // No dot found, return as-is (already a simple name)
    return typeString;
  }
}
