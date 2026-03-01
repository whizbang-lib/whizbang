namespace Whizbang.Core.Tracing;

/// <summary>
/// Flags enum defining which components should emit trace output.
/// Use bitwise OR to combine multiple components.
/// </summary>
/// <remarks>
/// <para>
/// Configure via <see cref="TracingOptions.Components"/> to control
/// which parts of Whizbang emit traces. When a component is not included,
/// its tracing code is effectively disabled.
/// </para>
/// <example>
/// <code>
/// // Trace only handlers and errors
/// options.Components = TraceComponents.Handlers | TraceComponents.Errors;
///
/// // Trace everything
/// options.Components = TraceComponents.All;
/// </code>
/// </example>
/// </remarks>
/// <docs>observability/tracing#components</docs>
[Flags]
public enum TraceComponents {
  /// <summary>No tracing enabled.</summary>
  None = 0,

  /// <summary>Handler invocations, completions, and failures.</summary>
  Handlers = 1 << 0,

  /// <summary>Lifecycle stage transitions (PreDistribute, PostDistribute, etc.).</summary>
  Lifecycle = 1 << 1,

  /// <summary>Dispatcher operations and receptor discovery.</summary>
  Dispatcher = 1 << 2,

  /// <summary>Message dispatch and routing.</summary>
  Messages = 1 << 3,

  /// <summary>Event creation and publishing.</summary>
  Events = 1 << 4,

  /// <summary>Outbox writes and delivery.</summary>
  Outbox = 1 << 5,

  /// <summary>Inbox reads and processing.</summary>
  Inbox = 1 << 6,

  /// <summary>Event store reads and writes.</summary>
  EventStore = 1 << 7,

  /// <summary>Perspective updates and queries.</summary>
  Perspectives = 1 << 8,

  /// <summary>Tag hook processing.</summary>
  Tags = 1 << 9,

  /// <summary>Security context propagation.</summary>
  Security = 1 << 10,

  /// <summary>Background worker operations.</summary>
  Workers = 1 << 11,

  /// <summary>Error and exception handling.</summary>
  Errors = 1 << 12,

  /// <summary>All components enabled.</summary>
  All = ~None
}
