using System.Reflection;

namespace Whizbang.Core.Routing;

/// <summary>
/// Detects the MessageKind for a type using priority-based detection.
/// </summary>
/// <remarks>
/// Detection priority:
/// 1. [MessageKind] attribute (explicit override)
/// 2. Framework system namespace (Whizbang.Core.Commands.System → System) — outranks
///    interface detection because framework system commands implement ICommand yet are
///    broadcast/run-control traffic, not domain commands
/// 3. Interface implementation (ICommand, IEvent, IQuery)
/// 4. Namespace convention (Commands, Events, Queries in namespace)
/// 5. Type name suffix (Command, Event, Query, Created, Updated, Deleted)
/// NOTE: the detector is deliberately OFF the routing hot path — dispatcher call sites
/// pass literal kinds; this classifier is metadata/tooling only.
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#message-kind</docs>
public static class MessageKindDetector {
  /// <summary>
  /// The framework system namespace subtree whose types classify as
  /// <see cref="MessageKind.System"/> (run-control, killswitches, durable system commands).
  /// Consumer namespaces that merely end in ".Commands.System" do NOT match — only the
  /// framework-owned subtree does.
  /// </summary>
  private const string FRAMEWORK_SYSTEM_NAMESPACE = "Whizbang.Core.Commands.System";

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

    // Priority 2: Framework system namespace — must precede interface detection because
    // framework system commands implement ICommand yet are System (broadcast) traffic.
    if (_isFrameworkSystemNamespace(type)) {
      return MessageKind.System;
    }

    // Priority 3: Check for interface implementation
    var interfaceResult = _detectFromInterface(type);
    if (interfaceResult != MessageKind.Unknown) {
      return interfaceResult;
    }

    // Priority 4: Check namespace convention
    var namespaceResult = _detectFromNamespace(type);
    if (namespaceResult != MessageKind.Unknown) {
      return namespaceResult;
    }

    // Priority 5: Check type name suffix
    return _detectFromTypeName(type);
  }

  /// <summary>
  /// True when the type lives in the framework system namespace subtree
  /// (<c>Whizbang.Core.Commands.System</c> or a sub-namespace of it).
  /// </summary>
  private static bool _isFrameworkSystemNamespace(Type type) {
    var ns = type.Namespace;
    if (string.IsNullOrEmpty(ns)) {
      return false;
    }

    return ns.Equals(FRAMEWORK_SYSTEM_NAMESPACE, StringComparison.Ordinal)
        || ns.StartsWith(FRAMEWORK_SYSTEM_NAMESPACE + ".", StringComparison.Ordinal);
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
    return _eventSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal))
      ? MessageKind.Event
      : MessageKind.Unknown;
  }
}
