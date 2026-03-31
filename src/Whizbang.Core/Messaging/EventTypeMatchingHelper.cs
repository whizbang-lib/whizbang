using System;
using System.Collections.Generic;
using System.Linq;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Helper for determining if a message type name matches a known event type.
/// Handles assembly-qualified names with and without version information.
/// </summary>
public static class EventTypeMatchingHelper {
  /// <summary>
  /// Normalizes an assembly-qualified type name by removing version, culture, and public key token information.
  /// Handles both simple types and nested generic types (e.g., MessageEnvelope`1[[PayloadType, Assembly]]).
  /// This ensures consistent type name matching across different contexts (e.g., event matching, routing, serialization).
  /// Note: Nested type names retain the CLR format with + separator (e.g., "Outer+Nested").
  /// </summary>
  /// <param name="assemblyQualifiedTypeName">The assembly-qualified type name to normalize</param>
  /// <returns>Normalized type name with version information stripped</returns>
  /// <example>
  /// Simple type:
  /// Input:  "MyApp.Events.ProductCreatedEvent, MyApp.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
  /// Output: "MyApp.Events.ProductCreatedEvent, MyApp.Contracts"
  ///
  /// Generic type:
  /// Input:  "MessageEnvelope`1[[MyApp.ProductCreatedEvent, MyApp, Version=1.0.0.0, Culture=neutral]], Whizbang.Core, Version=1.0.0.0"
  /// Output: "MessageEnvelope`1[[MyApp.ProductCreatedEvent, MyApp]], Whizbang.Core"
  ///
  /// Nested type (CLR format with + preserved):
  /// Input:  "MyApp.AuthContracts+LoginCommand, MyApp, Version=1.0.0.0"
  /// Output: "MyApp.AuthContracts+LoginCommand, MyApp"
  /// </example>
  public static string NormalizeTypeName(string assemblyQualifiedTypeName) {
    if (string.IsNullOrEmpty(assemblyQualifiedTypeName)) {
      return assemblyQualifiedTypeName;
    }

    // Strip ", Version=..., Culture=..., PublicKeyToken=..." segments from assembly-qualified names.
    // Uses string scanning instead of regex to avoid backtracking timeouts on long/nested generic types.
    var result = new System.Text.StringBuilder(assemblyQualifiedTypeName.Length);
    var span = assemblyQualifiedTypeName.AsSpan();
    var i = 0;

    while (i < span.Length) {
      // Check for ", Version=" pattern (with optional whitespace after comma)
      if (i + 2 < span.Length && span[i] == ',' && _isVersionStart(span, i)) {
        // Skip this segment: advance past ", Version=value" and any following ", Culture=value", ", PublicKeyToken=value"
        i = _skipAssemblyMetadata(span, i);
      } else {
        result.Append(span[i]);
        i++;
      }
    }

    return result.ToString();
  }

  private static bool _isVersionStart(ReadOnlySpan<char> span, int commaIndex) {
    var j = commaIndex + 1;
    // Skip whitespace after comma
    while (j < span.Length && span[j] == ' ') { j++; }
    // Check for "Version="
    return j + 8 <= span.Length && span.Slice(j, 8).SequenceEqual("Version=".AsSpan());
  }

  private static int _skipAssemblyMetadata(ReadOnlySpan<char> span, int start) {
    // Skip ", Version=value" then optionally ", Culture=value" and ", PublicKeyToken=value"
    var i = start;
    // Skip up to 3 metadata segments (Version, Culture, PublicKeyToken)
    for (var seg = 0; seg < 3 && i < span.Length; seg++) {
      if (span[i] != ',') { break; }
      var j = i + 1;
      while (j < span.Length && span[j] == ' ') { j++; }
      // Check if this is a known metadata key
      if (_startsWithMetadataKey(span, j)) {
        // Skip to end of value (next comma, ']', or end)
        i = j;
        while (i < span.Length && span[i] != ',' && span[i] != ']') { i++; }
      } else {
        break;
      }
    }
    return i;
  }

  private static bool _startsWithMetadataKey(ReadOnlySpan<char> span, int pos) {
    return _startsWith(span, pos, "Version=") ||
           _startsWith(span, pos, "Culture=") ||
           _startsWith(span, pos, "PublicKeyToken=");
  }

  private static bool _startsWith(ReadOnlySpan<char> span, int pos, string prefix) {
    return pos + prefix.Length <= span.Length &&
           span.Slice(pos, prefix.Length).SequenceEqual(prefix.AsSpan());
  }

  /// <summary>
  /// Determines if the given message type name matches any of the provided event types.
  /// Supports assembly-qualified names with and without version information.
  /// Uses NormalizeTypeName for consistent type name matching.
  /// </summary>
  /// <param name="messageTypeName">The message type name to check (may be assembly-qualified)</param>
  /// <param name="eventTypes">Collection of known event types</param>
  /// <returns>True if the message type is an event, false otherwise</returns>
  public static bool IsEventType(string messageTypeName, IEnumerable<Type> eventTypes) {
    if (string.IsNullOrEmpty(messageTypeName)) {
      return false;
    }

    // Normalize message type name to "FullName, AssemblyName" format (strip version info)
    var normalizedMessageType = NormalizeTypeName(messageTypeName);

    // Check if any event type matches
    return eventTypes.Any(et => {
      var etNormalized = et.FullName + ", " + et.Assembly.GetName().Name;

      // Three matching strategies (ordered by specificity):
      // 1. Exact match on full assembly-qualified name (includes version)
      // 2. Match on FullName + AssemblyName (no version)
      // 3. Match normalized forms (strips version from both sides)
      return et.AssemblyQualifiedName == messageTypeName ||
             et.FullName + ", " + et.Assembly.GetName().Name == messageTypeName ||
             etNormalized == normalizedMessageType;
    });
  }
}
