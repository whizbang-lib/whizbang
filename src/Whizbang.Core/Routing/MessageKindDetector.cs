using System.Reflection;

namespace Whizbang.Core.Routing;

/// <summary>
/// Detects the MessageKind for a type using priority-based detection.
/// </summary>
/// <remarks>
/// Detection priority:
/// 1. [MessageKind] attribute (explicit override)
/// 2. Interface implementation (ICommand, IEvent, IQuery)
/// 3. Namespace convention (Commands, Events, Queries in namespace)
/// 4. Type name suffix (Command, Event, Query, Created, Updated, Deleted)
/// </remarks>
/// <docs>core-concepts/routing#message-kind</docs>
public static class MessageKindDetector {
  // Event suffixes that indicate an event type
  private static readonly string[] _eventSuffixes = [
    "Event",
    "Created",
    "Updated",
    "Deleted"
  ];

  /// <summary>
  /// Detects the MessageKind for a type.
  /// </summary>
  /// <param name="type">The type to classify.</param>
  /// <returns>The detected MessageKind, or Unknown if cannot be determined.</returns>
  /// <exception cref="ArgumentNullException">Thrown when type is null.</exception>
  public static MessageKind Detect(Type type) {
    ArgumentNullException.ThrowIfNull(type);

    // Priority 1: Check for [MessageKind] attribute
    var attributeResult = _detectFromAttribute(type);
    if (attributeResult != MessageKind.Unknown) {
      return attributeResult;
    }

    // Priority 2: Check for interface implementation
    var interfaceResult = _detectFromInterface(type);
    if (interfaceResult != MessageKind.Unknown) {
      return interfaceResult;
    }

    // Priority 3: Check namespace convention
    var namespaceResult = _detectFromNamespace(type);
    if (namespaceResult != MessageKind.Unknown) {
      return namespaceResult;
    }

    // Priority 4: Check type name suffix
    return _detectFromTypeName(type);
  }

  /// <summary>
  /// Detects MessageKind from [MessageKind] attribute.
  /// </summary>
  private static MessageKind _detectFromAttribute(Type type) {
    var attribute = type.GetCustomAttribute<MessageKindAttribute>();
    return attribute?.Kind ?? MessageKind.Unknown;
  }

  /// <summary>
  /// Detects MessageKind from interface implementation.
  /// </summary>
  private static MessageKind _detectFromInterface(Type type) {
    // Check in order of specificity
    if (typeof(ICommand).IsAssignableFrom(type)) {
      return MessageKind.Command;
    }

    if (typeof(IEvent).IsAssignableFrom(type)) {
      return MessageKind.Event;
    }

    if (typeof(IQuery).IsAssignableFrom(type)) {
      return MessageKind.Query;
    }

    return MessageKind.Unknown;
  }

  /// <summary>
  /// Detects MessageKind from namespace convention.
  /// </summary>
  private static MessageKind _detectFromNamespace(Type type) {
    var ns = type.Namespace;
    if (string.IsNullOrEmpty(ns)) {
      return MessageKind.Unknown;
    }

    // Split namespace and check for conventional segments
    var segments = ns.Split('.');

    // Check if any segment matches our conventions (case-insensitive)
    foreach (var segment in segments) {
      if (string.Equals(segment, "Commands", StringComparison.OrdinalIgnoreCase)) {
        return MessageKind.Command;
      }

      if (string.Equals(segment, "Events", StringComparison.OrdinalIgnoreCase)) {
        return MessageKind.Event;
      }

      if (string.Equals(segment, "Queries", StringComparison.OrdinalIgnoreCase)) {
        return MessageKind.Query;
      }
    }

    return MessageKind.Unknown;
  }

  /// <summary>
  /// Detects MessageKind from type name suffix.
  /// </summary>
  private static MessageKind _detectFromTypeName(Type type) {
    var name = type.Name;

    // Check Command suffix first (most specific)
    if (name.EndsWith("Command", StringComparison.Ordinal)) {
      return MessageKind.Command;
    }

    // Check Query suffix
    if (name.EndsWith("Query", StringComparison.Ordinal)) {
      return MessageKind.Query;
    }

    // Check Event suffixes
    foreach (var suffix in _eventSuffixes) {
      if (name.EndsWith(suffix, StringComparison.Ordinal)) {
        return MessageKind.Event;
      }
    }

    return MessageKind.Unknown;
  }
}
