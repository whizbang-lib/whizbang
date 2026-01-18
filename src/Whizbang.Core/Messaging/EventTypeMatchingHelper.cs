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
  /// </example>
  public static string NormalizeTypeName(string assemblyQualifiedTypeName) {
    if (string.IsNullOrEmpty(assemblyQualifiedTypeName)) {
      return assemblyQualifiedTypeName;
    }

    // For generic types like MessageEnvelope`1[[PayloadType, Assembly, Version=..., ...]], OuterAssembly, Version=..., ...
    // we need to strip version info from BOTH the inner type and the outer type.
    // Strategy: Use regex to replace ", Version=..., Culture=..., PublicKeyToken=..." patterns anywhere in the string.

    // Pattern matches: ", Version=X, Culture=Y, PublicKeyToken=Z" or any subset
    // This works for both simple types and nested generic types
    // Timeout added to prevent ReDoS attacks (S6444)
    var result = System.Text.RegularExpressions.Regex.Replace(
      assemblyQualifiedTypeName,
      @",\s*Version=[^,\]]+(?:,\s*Culture=[^,\]]+)?(?:,\s*PublicKeyToken=[^,\]]+)?",
      "",
      System.Text.RegularExpressions.RegexOptions.None,
      TimeSpan.FromSeconds(1)
    );

    return result;
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
