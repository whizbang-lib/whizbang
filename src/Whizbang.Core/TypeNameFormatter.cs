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

  /// <summary>
  /// Extracts the full type name (namespace + type) without assembly qualifier or global:: prefix.
  /// Handles all common formats: assembly-qualified, global:: prefixed, simple names.
  /// </summary>
  /// <example>
  /// "MyApp.Events.OrderCreated, MyApp" → "MyApp.Events.OrderCreated"
  /// "global::MyApp.Events.OrderCreated" → "MyApp.Events.OrderCreated"
  /// "MyApp.Events.OrderCreated" → "MyApp.Events.OrderCreated"
  /// </example>
  public static string GetFullName(string typeName) {
    if (string.IsNullOrEmpty(typeName)) {
      return typeName;
    }

    var result = typeName;

    // Strip global:: prefix
    if (result.StartsWith("global::", StringComparison.Ordinal)) {
      result = result[8..];
    }

    // Strip assembly qualifier (everything after first comma)
    var commaIndex = result.IndexOf(',');
    if (commaIndex >= 0) {
      result = result[..commaIndex].Trim();
    }

    return result;
  }

  /// <summary>
  /// Extracts just the simple type name (no namespace, no assembly).
  /// Preserves nested type separators (+).
  /// </summary>
  /// <example>
  /// "MyApp.Events.OrderCreated, MyApp" → "OrderCreated"
  /// "global::MyApp.Events.OrderCreated" → "OrderCreated"
  /// "MyApp.Events.SessionContracts+EndedEvent" → "SessionContracts+EndedEvent"
  /// </example>
  public static string GetSimpleName(string typeName) {
    if (string.IsNullOrEmpty(typeName)) {
      return typeName;
    }

    var fullName = GetFullName(typeName);

    var lastDot = fullName.LastIndexOf('.');
    return lastDot >= 0 ? fullName[(lastDot + 1)..] : fullName;
  }

  /// <summary>
  /// Extracts the namespace from a type name string.
  /// </summary>
  /// <example>
  /// "MyApp.Events.OrderCreated, MyApp" → "MyApp.Events"
  /// "global::MyApp.Events.OrderCreated" → "MyApp.Events"
  /// "OrderCreated" → null
  /// </example>
  public static string? GetNamespace(string typeName) {
    if (string.IsNullOrEmpty(typeName)) {
      return null;
    }

    var fullName = GetFullName(typeName);

    var lastDot = fullName.LastIndexOf('.');
    return lastDot >= 0 ? fullName[..lastDot] : null;
  }

  /// <summary>
  /// Gets the CLR-format perspective name for a type.
  /// This produces the same format as TypeNameUtilities.BuildClrTypeName() in generators,
  /// ensuring consistency between compile-time registration and runtime lookup.
  /// Uses '+' for nested types and '.' for namespaces (standard CLR format).
  /// </summary>
  /// <example>
  /// typeof(MyApp.OrderPerspective) → "MyApp.OrderPerspective"
  /// typeof(MyApp.Container.Nested) → "MyApp.Container+Nested"
  /// </example>
  /// <param name="perspectiveType">The perspective type.</param>
  /// <returns>CLR-format type name suitable for database storage and sync tracking.</returns>
  public static string GetPerspectiveName(Type perspectiveType) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    return perspectiveType.FullName
        ?? throw new InvalidOperationException($"Type {perspectiveType.Name} does not have a FullName");
  }

  /// <summary>
  /// Extracts the payload namespace from a generic envelope type string.
  /// The transport delivers types like "MessageEnvelope`1[[Payload.Type, Assembly]], Whizbang.Core".
  /// This extracts the namespace of the inner payload type, not the envelope wrapper.
  /// Falls back to <see cref="GetNamespace"/> if no generic parameter is found.
  /// </summary>
  /// <example>
  /// "Whizbang...MessageEnvelope`1[[MyApp.Chat.MyEvent, MyApp]], Whizbang" → "MyApp.Chat"
  /// "MyApp.Chat.MyEvent, MyApp" → "MyApp.Chat" (no generic wrapper — falls back)
  /// "MyApp.Chat.Contracts+MyEvent, MyApp" → "MyApp.Chat" (nested type)
  /// </example>
  public static string? GetPayloadNamespace(string envelopeType) {
    if (string.IsNullOrEmpty(envelopeType)) {
      return null;
    }

    // Extract inner type from generic: "...`1[[Inner.Type, Assembly]]..."
    var openBracket = envelopeType.IndexOf("[[", StringComparison.Ordinal);
    if (openBracket >= 0) {
      var closeBracket = envelopeType.IndexOf("]]", openBracket, StringComparison.Ordinal);
      if (closeBracket > openBracket) {
        var innerType = envelopeType[(openBracket + 2)..closeBracket];
        // GetFullName strips assembly, then we strip nested type (+) to get outer class
        var fullName = GetFullName(innerType);
        var plusIdx = fullName.IndexOf('+');
        var outerType = plusIdx > 0 ? fullName[..plusIdx] : fullName;
        var lastDot = outerType.LastIndexOf('.');
        return lastDot > 0 ? outerType[..lastDot] : null;
      }
    }

    // No generic wrapper — fall back to regular namespace extraction
    return GetNamespace(envelopeType);
  }
}
