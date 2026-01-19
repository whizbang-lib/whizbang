using System;
using System.Linq;

namespace Whizbang.Core;

/// <summary>
/// Utility for consistent type name formatting across event stores, work coordinators, and message associations.
/// Ensures all code paths use the same "TypeName, AssemblyName" format for type matching.
/// </summary>
/// <remarks>
/// Why this format?
/// - Matches wh_message_associations format used in perspective auto-checkpoint creation
/// - Compatible with fuzzy matching in migration 006 (handles Version/Culture/PublicKeyToken differences)
/// - Deterministic and consistent across .NET versions
/// - Works with Type.GetType() for deserialization
///
/// Format: "Namespace.TypeName, AssemblyName"
/// Example: "ECommerce.Contracts.Events.ProductCreatedEvent, ECommerce.Contracts"
///
/// NOT using:
/// - Type.Name (too short, can't deserialize)
/// - Type.FullName (no assembly info, can't deserialize across assemblies)
/// - Type.AssemblyQualifiedName (includes version info that changes across builds)
/// </remarks>
public static class TypeNameFormatter {
  /// <summary>
  /// Formats a type to "TypeName, AssemblyName" format for consistent storage and matching.
  /// This is the standard format used across Whizbang for type identification.
  /// </summary>
  /// <param name="type">The type to format</param>
  /// <returns>Type name in format "TypeName, AssemblyName"</returns>
  /// <exception cref="InvalidOperationException">If type doesn't have required metadata</exception>
  public static string Format(Type type) {
    ArgumentNullException.ThrowIfNull(type);

    var typeName = type.FullName
      ?? throw new InvalidOperationException($"Type {type.Name} does not have a FullName");

    var assemblyName = type.Assembly.GetName().Name
      ?? throw new InvalidOperationException($"Type {type.Name} does not have an Assembly.GetName().Name");

    return $"{typeName}, {assemblyName}";
  }

  /// <summary>
  /// Parses a type name string to extract "TypeName, AssemblyName" format, handling various input formats defensively.
  /// Supports:
  /// - Short form: "TypeName, AssemblyName" (returned as-is)
  /// - Long form: "TypeName, AssemblyName, Version=X, Culture=Y, PublicKeyToken=Z" (extracts first two parts)
  /// - FullName only: "TypeName" (throws - cannot determine assembly)
  /// </summary>
  /// <param name="typeNameString">The type name string in any supported format</param>
  /// <returns>Type name in format "TypeName, AssemblyName"</returns>
  /// <exception cref="InvalidOperationException">If type name doesn't contain assembly information</exception>
  public static string Parse(string typeNameString) {
    ArgumentException.ThrowIfNullOrWhiteSpace(typeNameString);

    // Split on commas and trim whitespace
    var parts = typeNameString.Split(',').Select(p => p.Trim()).ToArray();

    if (parts.Length < 2) {
      throw new InvalidOperationException(
        $"Type name '{typeNameString}' does not contain assembly information. " +
        "Expected format: 'TypeName, AssemblyName' or 'TypeName, AssemblyName, Version=...'");
    }

    // Extract first two parts (TypeName, AssemblyName)
    // Ignore Version=, Culture=, PublicKeyToken= if present
    return $"{parts[0]}, {parts[1]}";
  }

  /// <summary>
  /// Attempts to parse a type name string, returning null if parsing fails.
  /// Useful when handling potentially invalid type names from database or external sources.
  /// </summary>
  /// <param name="typeNameString">The type name string to parse</param>
  /// <param name="result">The parsed type name in "TypeName, AssemblyName" format, or null if parsing failed</param>
  /// <returns>True if parsing succeeded, false otherwise</returns>
  public static bool TryParse(string? typeNameString, out string? result) {
    result = null;

    if (string.IsNullOrWhiteSpace(typeNameString)) {
      return false;
    }

    try {
      result = Parse(typeNameString);
      return true;
    } catch {
      return false;
    }
  }
}
