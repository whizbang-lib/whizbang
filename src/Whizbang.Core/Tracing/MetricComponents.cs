namespace Whizbang.Core.Tracing;

/// <summary>
/// Flags enum specifying which components should emit metrics.
/// </summary>
/// <remarks>
/// <para>
/// MetricComponents provides granular control over which parts of the system
/// generate metrics. This allows teams to focus metrics collection on specific
/// areas of interest while minimizing overhead.
/// </para>
/// <para>
/// Configuration can specify multiple components via comma-delimited strings:
/// <c>"Handlers, EventStore, Errors"</c>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Configuration via appsettings.json
/// {
///   "Whizbang": {
///     "Metrics": {
///       "Enabled": true,
///       "Components": ["Handlers", "EventStore", "Errors"]
///     }
///   }
/// }
///
/// // Programmatic configuration
/// services.AddWhizbang(options => {
///   options.Metrics.Enabled = true;
///   options.Metrics.Components = MetricComponents.Handlers
///                              | MetricComponents.EventStore
///                              | MetricComponents.Errors;
/// });
///
/// // Checking component flags
/// if (options.Components.HasFlag(MetricComponents.Handlers)) {
///   // Emit handler metrics
/// }
/// </code>
/// </example>
/// <docs>metrics/components</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/MetricComponentsTests.cs</tests>
[Flags]
public enum MetricComponents {
  /// <summary>
  /// No component metrics enabled.
  /// </summary>
  None = 0,

  /// <summary>
  /// Handler invocations, durations, and active counts.
  /// Metrics: invocations, successes, failures, early_returns, duration, active.
  /// </summary>
  Handlers = 1 << 0,

  /// <summary>
  /// Dispatch operations and receptor discovery.
  /// Metrics: dispatch.total, dispatch.duration, receptor.discovered.
  /// </summary>
  Dispatcher = 1 << 1,

  /// <summary>
  /// Message dispatching and receiving.
  /// Metrics: messages.dispatched, messages.received, messages.processing_time.
  /// </summary>
  Messages = 1 << 2,

  /// <summary>
  /// Event storage and publishing.
  /// Metrics: events.stored, events.published.
  /// </summary>
  Events = 1 << 3,

  /// <summary>
  /// Outbox write and delivery operations.
  /// Metrics: outbox.writes, outbox.pending, outbox.batch_size, outbox.delivery_latency.
  /// </summary>
  Outbox = 1 << 4,

  /// <summary>
  /// Inbox message consumption.
  /// Metrics: inbox.received, inbox.pending, inbox.batch_size, inbox.processing_time.
  /// </summary>
  Inbox = 1 << 5,

  /// <summary>
  /// Event store read and write operations.
  /// Metrics: eventstore.appends, eventstore.reads, eventstore.read_latency, eventstore.write_latency.
  /// </summary>
  EventStore = 1 << 6,

  /// <summary>
  /// Lifecycle stage transitions.
  /// Metrics: lifecycle.invocations, lifecycle.duration, lifecycle.skipped.
  /// </summary>
  Lifecycle = 1 << 7,

  /// <summary>
  /// Perspective updates, queries, and lag.
  /// Metrics: perspective.updates, perspective.duration, perspective.lag, perspective.errors.
  /// </summary>
  Perspectives = 1 << 8,

  /// <summary>
  /// Tag hook processing.
  /// Metrics: tags.processed, tags.duration, tags.errors.
  /// </summary>
  Tags = 1 << 9,

  /// <summary>
  /// Security context propagation.
  /// Metrics: security.context_propagations, security.missing_context.
  /// </summary>
  Security = 1 << 10,

  /// <summary>
  /// Background worker operations.
  /// Metrics: worker.iterations, worker.idle_time, worker.active, worker.errors.
  /// </summary>
  Workers = 1 << 11,

  /// <summary>
  /// Error and exception tracking.
  /// Metrics: errors.total, errors.unhandled.
  /// </summary>
  Errors = 1 << 12,

  /// <summary>
  /// Resilience policy metrics (circuit breakers, retries).
  /// Metrics: policy.circuit_breaks, policy.retries.
  /// </summary>
  Policies = 1 << 13,

  /// <summary>
  /// All components enabled.
  /// Use for development with full visibility.
  /// </summary>
  All = Handlers | Dispatcher | Messages | Events | Outbox | Inbox |
        EventStore | Lifecycle | Perspectives | Tags | Security |
        Workers | Errors | Policies
}
