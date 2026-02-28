using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Whizbang.Core.Tracing;

/// <summary>
/// Central metrics instrumentation for Whizbang using System.Diagnostics.Metrics.
/// </summary>
/// <remarks>
/// <para>
/// WhizbangMetrics provides static access to all metric instruments used by Whizbang.
/// Metrics are collected via OpenTelemetry or any compatible metrics backend.
/// </para>
/// <para>
/// <strong>Meter Name:</strong> <c>Whizbang</c> (configurable via <see cref="MetricsOptions.MeterName"/>).
/// </para>
/// <para>
/// <strong>Integration with OpenTelemetry:</strong>
/// <code>
/// services.AddOpenTelemetry()
///     .WithMetrics(builder => builder
///         .AddMeter("Whizbang"));
/// </code>
/// </para>
/// </remarks>
/// <docs>metrics/overview</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/WhizbangMetricsTests.cs</tests>
public static class WhizbangMetrics {
  /// <summary>
  /// The meter name used for all Whizbang metrics.
  /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores (editorconfig requires ALL_UPPER for constants)
  public const string METER_NAME = "Whizbang";
#pragma warning restore CA1707

  private static readonly Meter _meter = new(METER_NAME, typeof(WhizbangMetrics).Assembly.GetName().Version?.ToString() ?? "1.0.0");

  // ========================================
  // Handler Metrics
  // ========================================

  /// <summary>
  /// Counter for total handler invocations.
  /// Tags: handler, message_type, status
  /// </summary>
  public static readonly Counter<long> HandlerInvocations =
      _meter.CreateCounter<long>(
          "whizbang.handler.invocations",
          unit: "{invocation}",
          description: "Total number of handler invocations");

  /// <summary>
  /// Counter for successful handler completions.
  /// Tags: handler, message_type
  /// </summary>
  public static readonly Counter<long> HandlerSuccesses =
      _meter.CreateCounter<long>(
          "whizbang.handler.successes",
          unit: "{success}",
          description: "Number of successful handler completions");

  /// <summary>
  /// Counter for handler failures.
  /// Tags: handler, message_type, exception_type
  /// </summary>
  public static readonly Counter<long> HandlerFailures =
      _meter.CreateCounter<long>(
          "whizbang.handler.failures",
          unit: "{failure}",
          description: "Number of handler failures");

  /// <summary>
  /// Counter for handler early returns.
  /// Tags: handler, message_type
  /// </summary>
  public static readonly Counter<long> HandlerEarlyReturns =
      _meter.CreateCounter<long>(
          "whizbang.handler.early_returns",
          unit: "{early_return}",
          description: "Number of handler early returns");

  /// <summary>
  /// Histogram for handler execution duration.
  /// Tags: handler, message_type, status
  /// </summary>
  public static readonly Histogram<double> HandlerDuration =
      _meter.CreateHistogram<double>(
          "whizbang.handler.duration",
          unit: "ms",
          description: "Handler execution duration in milliseconds");

  /// <summary>
  /// Up/down counter for currently active handlers.
  /// Tags: handler
  /// </summary>
  public static readonly UpDownCounter<long> HandlerActive =
      _meter.CreateUpDownCounter<long>(
          "whizbang.handler.active",
          unit: "{handler}",
          description: "Number of currently executing handlers");

  // ========================================
  // Dispatcher Metrics
  // ========================================

  /// <summary>
  /// Counter for total dispatch operations.
  /// Tags: message_type, route
  /// </summary>
  public static readonly Counter<long> DispatchTotal =
      _meter.CreateCounter<long>(
          "whizbang.dispatch.total",
          unit: "{dispatch}",
          description: "Total number of dispatch operations");

  /// <summary>
  /// Histogram for dispatch duration.
  /// Tags: route
  /// </summary>
  public static readonly Histogram<double> DispatchDuration =
      _meter.CreateHistogram<double>(
          "whizbang.dispatch.duration",
          unit: "ms",
          description: "Dispatch operation duration in milliseconds");

  /// <summary>
  /// Counter for receptor discovery operations.
  /// Tags: message_type, receptor_count
  /// </summary>
  public static readonly Counter<long> ReceptorDiscovered =
      _meter.CreateCounter<long>(
          "whizbang.receptor.discovered",
          unit: "{discovery}",
          description: "Number of receptor discovery operations");

  // ========================================
  // Message Metrics
  // ========================================

  /// <summary>
  /// Counter for messages dispatched.
  /// Tags: message_type, route
  /// </summary>
  public static readonly Counter<long> MessagesDispatched =
      _meter.CreateCounter<long>(
          "whizbang.messages.dispatched",
          unit: "{message}",
          description: "Number of messages dispatched");

  /// <summary>
  /// Counter for messages received.
  /// Tags: message_type, transport
  /// </summary>
  public static readonly Counter<long> MessagesReceived =
      _meter.CreateCounter<long>(
          "whizbang.messages.received",
          unit: "{message}",
          description: "Number of messages received");

  /// <summary>
  /// Histogram for message processing time.
  /// Tags: message_type
  /// </summary>
  public static readonly Histogram<double> MessagesProcessingTime =
      _meter.CreateHistogram<double>(
          "whizbang.messages.processing_time",
          unit: "ms",
          description: "Message processing time in milliseconds");

  // ========================================
  // Event Metrics
  // ========================================

  /// <summary>
  /// Counter for events stored.
  /// Tags: event_type, stream_id
  /// </summary>
  public static readonly Counter<long> EventsStored =
      _meter.CreateCounter<long>(
          "whizbang.events.stored",
          unit: "{event}",
          description: "Number of events stored");

  /// <summary>
  /// Counter for events published.
  /// Tags: event_type
  /// </summary>
  public static readonly Counter<long> EventsPublished =
      _meter.CreateCounter<long>(
          "whizbang.events.published",
          unit: "{event}",
          description: "Number of events published");

  // ========================================
  // Outbox Metrics
  // ========================================

  /// <summary>
  /// Counter for outbox writes.
  /// Tags: transport
  /// </summary>
  public static readonly Counter<long> OutboxWrites =
      _meter.CreateCounter<long>(
          "whizbang.outbox.writes",
          unit: "{write}",
          description: "Number of outbox writes");

  /// <summary>
  /// Up/down counter for pending outbox messages.
  /// Tags: transport
  /// </summary>
  public static readonly UpDownCounter<long> OutboxPending =
      _meter.CreateUpDownCounter<long>(
          "whizbang.outbox.pending",
          unit: "{message}",
          description: "Number of pending outbox messages");

  /// <summary>
  /// Histogram for outbox batch sizes.
  /// Tags: transport
  /// </summary>
  public static readonly Histogram<long> OutboxBatchSize =
      _meter.CreateHistogram<long>(
          "whizbang.outbox.batch_size",
          unit: "{message}",
          description: "Outbox batch size");

  /// <summary>
  /// Histogram for outbox delivery latency.
  /// Tags: transport
  /// </summary>
  public static readonly Histogram<double> OutboxDeliveryLatency =
      _meter.CreateHistogram<double>(
          "whizbang.outbox.delivery_latency",
          unit: "ms",
          description: "Outbox delivery latency in milliseconds");

  // ========================================
  // Inbox Metrics
  // ========================================

  /// <summary>
  /// Counter for inbox messages received.
  /// Tags: transport, message_type
  /// </summary>
  public static readonly Counter<long> InboxReceived =
      _meter.CreateCounter<long>(
          "whizbang.inbox.received",
          unit: "{message}",
          description: "Number of inbox messages received");

  /// <summary>
  /// Up/down counter for pending inbox messages.
  /// Tags: transport
  /// </summary>
  public static readonly UpDownCounter<long> InboxPending =
      _meter.CreateUpDownCounter<long>(
          "whizbang.inbox.pending",
          unit: "{message}",
          description: "Number of pending inbox messages");

  /// <summary>
  /// Histogram for inbox batch sizes.
  /// Tags: transport
  /// </summary>
  public static readonly Histogram<long> InboxBatchSize =
      _meter.CreateHistogram<long>(
          "whizbang.inbox.batch_size",
          unit: "{message}",
          description: "Inbox batch size");

  /// <summary>
  /// Histogram for inbox processing time.
  /// Tags: transport
  /// </summary>
  public static readonly Histogram<double> InboxProcessingTime =
      _meter.CreateHistogram<double>(
          "whizbang.inbox.processing_time",
          unit: "ms",
          description: "Inbox processing time in milliseconds");

  /// <summary>
  /// Counter for duplicate inbox messages.
  /// Tags: transport
  /// </summary>
  public static readonly Counter<long> InboxDuplicates =
      _meter.CreateCounter<long>(
          "whizbang.inbox.duplicates",
          unit: "{message}",
          description: "Number of duplicate inbox messages");

  // ========================================
  // EventStore Metrics
  // ========================================

  /// <summary>
  /// Counter for event store appends.
  /// Tags: stream_type
  /// </summary>
  public static readonly Counter<long> EventStoreAppends =
      _meter.CreateCounter<long>(
          "whizbang.eventstore.appends",
          unit: "{append}",
          description: "Number of event store append operations");

  /// <summary>
  /// Counter for event store reads.
  /// Tags: stream_type
  /// </summary>
  public static readonly Counter<long> EventStoreReads =
      _meter.CreateCounter<long>(
          "whizbang.eventstore.reads",
          unit: "{read}",
          description: "Number of event store read operations");

  /// <summary>
  /// Histogram for events per append operation.
  /// </summary>
  public static readonly Histogram<long> EventStoreEventsPerAppend =
      _meter.CreateHistogram<long>(
          "whizbang.eventstore.events_per_append",
          unit: "{event}",
          description: "Number of events per append operation");

  /// <summary>
  /// Histogram for event store read latency.
  /// </summary>
  public static readonly Histogram<double> EventStoreReadLatency =
      _meter.CreateHistogram<double>(
          "whizbang.eventstore.read_latency",
          unit: "ms",
          description: "Event store read latency in milliseconds");

  /// <summary>
  /// Histogram for event store write latency.
  /// </summary>
  public static readonly Histogram<double> EventStoreWriteLatency =
      _meter.CreateHistogram<double>(
          "whizbang.eventstore.write_latency",
          unit: "ms",
          description: "Event store write latency in milliseconds");

  // ========================================
  // Lifecycle Metrics
  // ========================================

  /// <summary>
  /// Counter for lifecycle stage invocations.
  /// Tags: stage, message_type
  /// </summary>
  public static readonly Counter<long> LifecycleInvocations =
      _meter.CreateCounter<long>(
          "whizbang.lifecycle.invocations",
          unit: "{invocation}",
          description: "Number of lifecycle stage invocations");

  /// <summary>
  /// Histogram for lifecycle stage duration.
  /// Tags: stage
  /// </summary>
  public static readonly Histogram<double> LifecycleDuration =
      _meter.CreateHistogram<double>(
          "whizbang.lifecycle.duration",
          unit: "ms",
          description: "Lifecycle stage duration in milliseconds");

  /// <summary>
  /// Counter for skipped lifecycle stages.
  /// Tags: stage, reason
  /// </summary>
  public static readonly Counter<long> LifecycleSkipped =
      _meter.CreateCounter<long>(
          "whizbang.lifecycle.skipped",
          unit: "{skip}",
          description: "Number of skipped lifecycle stages");

  // ========================================
  // Perspective Metrics
  // ========================================

  /// <summary>
  /// Counter for perspective updates.
  /// Tags: perspective, event_type
  /// </summary>
  public static readonly Counter<long> PerspectiveUpdates =
      _meter.CreateCounter<long>(
          "whizbang.perspective.updates",
          unit: "{update}",
          description: "Number of perspective updates");

  /// <summary>
  /// Histogram for perspective update duration.
  /// Tags: perspective
  /// </summary>
  public static readonly Histogram<double> PerspectiveDuration =
      _meter.CreateHistogram<double>(
          "whizbang.perspective.duration",
          unit: "ms",
          description: "Perspective update duration in milliseconds");

  /// <summary>
  /// Histogram for perspective lag.
  /// Tags: perspective
  /// </summary>
  public static readonly Histogram<double> PerspectiveLag =
      _meter.CreateHistogram<double>(
          "whizbang.perspective.lag",
          unit: "ms",
          description: "Perspective lag in milliseconds");

  /// <summary>
  /// Counter for perspective errors.
  /// Tags: perspective, error_type
  /// </summary>
  public static readonly Counter<long> PerspectiveErrors =
      _meter.CreateCounter<long>(
          "whizbang.perspective.errors",
          unit: "{error}",
          description: "Number of perspective errors");

  // ========================================
  // Worker Metrics
  // ========================================

  /// <summary>
  /// Counter for worker iterations.
  /// Tags: worker_type
  /// </summary>
  public static readonly Counter<long> WorkerIterations =
      _meter.CreateCounter<long>(
          "whizbang.worker.iterations",
          unit: "{iteration}",
          description: "Number of worker iterations");

  /// <summary>
  /// Histogram for worker idle time.
  /// Tags: worker_type
  /// </summary>
  public static readonly Histogram<double> WorkerIdleTime =
      _meter.CreateHistogram<double>(
          "whizbang.worker.idle_time",
          unit: "ms",
          description: "Worker idle time in milliseconds");

  /// <summary>
  /// Up/down counter for active workers.
  /// Tags: worker_type
  /// </summary>
  public static readonly UpDownCounter<long> WorkerActive =
      _meter.CreateUpDownCounter<long>(
          "whizbang.worker.active",
          unit: "{worker}",
          description: "Number of active workers");

  /// <summary>
  /// Counter for worker errors.
  /// Tags: worker_type, error_type
  /// </summary>
  public static readonly Counter<long> WorkerErrors =
      _meter.CreateCounter<long>(
          "whizbang.worker.errors",
          unit: "{error}",
          description: "Number of worker errors");

  // ========================================
  // Error Metrics
  // ========================================

  /// <summary>
  /// Counter for total errors.
  /// Tags: component, exception_type
  /// </summary>
  public static readonly Counter<long> ErrorsTotal =
      _meter.CreateCounter<long>(
          "whizbang.errors.total",
          unit: "{error}",
          description: "Total number of errors");

  /// <summary>
  /// Counter for unhandled errors.
  /// Tags: component
  /// </summary>
  public static readonly Counter<long> ErrorsUnhandled =
      _meter.CreateCounter<long>(
          "whizbang.errors.unhandled",
          unit: "{error}",
          description: "Number of unhandled errors");

  // ========================================
  // Policy Metrics
  // ========================================

  /// <summary>
  /// Counter for circuit breaker activations.
  /// Tags: policy_name
  /// </summary>
  public static readonly Counter<long> PolicyCircuitBreaks =
      _meter.CreateCounter<long>(
          "whizbang.policy.circuit_breaks",
          unit: "{activation}",
          description: "Number of circuit breaker activations");

  /// <summary>
  /// Counter for retry attempts.
  /// Tags: policy_name, attempt
  /// </summary>
  public static readonly Counter<long> PolicyRetries =
      _meter.CreateCounter<long>(
          "whizbang.policy.retries",
          unit: "{retry}",
          description: "Number of retry attempts");

  // ========================================
  // Security Metrics
  // ========================================

  /// <summary>
  /// Counter for security context propagations.
  /// </summary>
  public static readonly Counter<long> SecurityContextPropagations =
      _meter.CreateCounter<long>(
          "whizbang.security.context_propagations",
          unit: "{propagation}",
          description: "Number of security context propagations");

  /// <summary>
  /// Counter for missing security context.
  /// Tags: handler
  /// </summary>
  public static readonly Counter<long> SecurityMissingContext =
      _meter.CreateCounter<long>(
          "whizbang.security.missing_context",
          unit: "{missing}",
          description: "Number of missing security context occurrences");

  // ========================================
  // Tags Metrics
  // ========================================

  /// <summary>
  /// Counter for tags processed.
  /// Tags: tag_type, hook
  /// </summary>
  public static readonly Counter<long> TagsProcessed =
      _meter.CreateCounter<long>(
          "whizbang.tags.processed",
          unit: "{tag}",
          description: "Number of tags processed");

  /// <summary>
  /// Histogram for tag processing duration.
  /// Tags: tag_type
  /// </summary>
  public static readonly Histogram<double> TagsDuration =
      _meter.CreateHistogram<double>(
          "whizbang.tags.duration",
          unit: "ms",
          description: "Tag processing duration in milliseconds");

  /// <summary>
  /// Counter for tag processing errors.
  /// Tags: tag_type, error_type
  /// </summary>
  public static readonly Counter<long> TagsErrors =
      _meter.CreateCounter<long>(
          "whizbang.tags.errors",
          unit: "{error}",
          description: "Number of tag processing errors");
}
