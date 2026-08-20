namespace Whizbang.Core.Routing;

/// <summary>
/// Classifies a message for routing purposes.
/// Determined by priority: attribute, interface, namespace convention, type name suffix.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#message-kind</docs>
public enum MessageKind {
  /// <summary>Could not determine message kind.</summary>
  Unknown = 0,
  /// <summary>Intent to change state - routes to owner's inbox.</summary>
  Command,
  /// <summary>Notification of state change - routes to owner's outbox.</summary>
  Event,
  /// <summary>Request for data - routes to owner's inbox, expects response.</summary>
  Query,
  /// <summary>
  /// Framework system/broadcast traffic — run-control commands, killswitches, and durable
  /// system commands every service must receive (e.g. perspective rebuilds). Routed by the
  /// outbox strategies' system branch; currently lands on the same entities commands use
  /// (dormant capability — a later phase redirects it to the dedicated broadcast inbox).
  /// </summary>
  System
}
