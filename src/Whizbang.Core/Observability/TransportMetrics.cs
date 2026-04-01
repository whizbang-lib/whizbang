using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for inbox receive, outbox publish, transport readiness, and event store.
/// Meter name: Whizbang.Transport
/// </summary>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TransportMetricsTests.cs</tests>
public sealed class TransportMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Transport";
#pragma warning restore CA1707


  // Inbox (receive side)

  /// <summary>Full receive duration: receive, process, and complete.</summary>
  public Histogram<double> InboxReceiveDuration { get; }

  /// <summary>Duration of first FlushAsync deduplication (INSERT ... ON CONFLICT).</summary>
  public Histogram<double> InboxDedupDuration { get; }

  /// <summary>Duration of OrderedStreamProcessor.ProcessInboxWorkAsync.</summary>
  public Histogram<double> InboxProcessingDuration { get; }

  /// <summary>Duration of second FlushAsync to report completions.</summary>
  public Histogram<double> InboxCompletionDuration { get; }

  /// <summary>Duration of SecurityContextHelper.EstablishFullContextAsync.</summary>
  public Histogram<double> InboxSecurityContextDuration { get; }

  // Inbox counters

  /// <summary>Messages received from transport.</summary>
  public Counter<long> InboxMessagesReceived { get; }

  /// <summary>Successfully processed messages.</summary>
  public Counter<long> InboxMessagesProcessed { get; }

  /// <summary>Messages rejected as duplicates.</summary>
  public Counter<long> InboxMessagesDeduplicated { get; }

  /// <summary>Processing failures.</summary>
  public Counter<long> InboxMessagesFailed { get; }

  /// <summary>Transport subscription retry attempts.</summary>
  public Counter<long> InboxSubscriptionRetries { get; }

  // Outbox (send side)

  /// <summary>Duration of _publishStrategy.PublishAsync to transport.</summary>
  public Histogram<double> OutboxPublishDuration { get; }

  /// <summary>Time waiting for transport readiness.</summary>
  public Histogram<double> OutboxReadinessWaitDuration { get; }

  // Outbox counters

  /// <summary>Messages published to transport.</summary>
  public Counter<long> OutboxMessagesPublished { get; }

  /// <summary>Publish failures by reason.</summary>
  public Counter<long> OutboxMessagesFailed { get; }

  /// <summary>Retry attempts.</summary>
  public Counter<long> OutboxPublishRetries { get; }

  // Concurrency and batching

  /// <summary>Time waiting for concurrency semaphore slot (ms).</summary>
  public Histogram<double> InboxConcurrencyWaitDuration { get; }

  /// <summary>Current number of messages being processed concurrently.</summary>
  public UpDownCounter<int> InboxConcurrentMessages { get; }

  /// <summary>Number of messages per inbox batch flush.</summary>
  public Histogram<double> InboxBatchSize { get; }

  /// <summary>Time first message in batch waited before flush (ms).</summary>
  public Histogram<double> InboxBatchWaitDuration { get; }

  /// <summary>Total inbox batch flushes.</summary>
  public Counter<long> InboxBatchFlushes { get; }

  // Gauges

  /// <summary>Currently active transport subscriptions.</summary>
  public UpDownCounter<int> ActiveSubscriptions { get; }

  // Event store

  /// <summary>AppendAsync latency.</summary>
  public Histogram<double> EventStoreAppendDuration { get; }

  /// <summary>GetEventsBetweenPolymorphicAsync latency.</summary>
  public Histogram<double> EventStoreQueryDuration { get; }

  /// <summary>Events appended.</summary>
  public Counter<long> EventsStored { get; }

  /// <summary>Event queries executed.</summary>
  public Counter<long> EventsQueried { get; }

  /// <summary>Initializes a new instance of the <see cref="TransportMetrics"/> class.</summary>
  /// <param name="whizbangMetrics">The shared metrics factory providing the meter.</param>
  public TransportMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    InboxReceiveDuration = meter.CreateHistogram<double>("whizbang.transport.inbox.receive.duration", "ms", "Full _handleMessageAsync: receive → process → complete");
    InboxDedupDuration = meter.CreateHistogram<double>("whizbang.transport.inbox.dedup.duration", "ms", "First FlushAsync (INSERT ... ON CONFLICT)");
    InboxProcessingDuration = meter.CreateHistogram<double>("whizbang.transport.inbox.processing.duration", "ms", "OrderedStreamProcessor.ProcessInboxWorkAsync");
    InboxCompletionDuration = meter.CreateHistogram<double>("whizbang.transport.inbox.completion.duration", "ms", "Second FlushAsync (report completions)");
    InboxSecurityContextDuration = meter.CreateHistogram<double>("whizbang.transport.inbox.security_context.duration", "ms", "SecurityContextHelper.EstablishFullContextAsync");

    InboxMessagesReceived = meter.CreateCounter<long>("whizbang.transport.inbox.messages_received", description: "Messages received from transport");
    InboxMessagesProcessed = meter.CreateCounter<long>("whizbang.transport.inbox.messages_processed", description: "Successfully processed");
    InboxMessagesDeduplicated = meter.CreateCounter<long>("whizbang.transport.inbox.messages_deduplicated", description: "Rejected as duplicates");
    InboxMessagesFailed = meter.CreateCounter<long>("whizbang.transport.inbox.messages_failed", description: "Processing failures");
    InboxSubscriptionRetries = meter.CreateCounter<long>("whizbang.transport.inbox.subscription_retries", description: "Transport subscription retry attempts");

    OutboxPublishDuration = meter.CreateHistogram<double>("whizbang.transport.outbox.publish.duration", "ms", "_publishStrategy.PublishAsync to transport");
    OutboxReadinessWaitDuration = meter.CreateHistogram<double>("whizbang.transport.outbox.readiness_wait.duration", "ms", "Time waiting for transport readiness");

    OutboxMessagesPublished = meter.CreateCounter<long>("whizbang.transport.outbox.messages_published", description: "Messages published to transport");
    OutboxMessagesFailed = meter.CreateCounter<long>("whizbang.transport.outbox.messages_failed", description: "Publish failures by reason");
    OutboxPublishRetries = meter.CreateCounter<long>("whizbang.transport.outbox.publish_retries", description: "Retry attempts");

    InboxConcurrencyWaitDuration = meter.CreateHistogram<double>("whizbang.transport.inbox.concurrency_wait.duration", "ms", "Time waiting for concurrency semaphore slot");
    InboxConcurrentMessages = meter.CreateUpDownCounter<int>("whizbang.transport.inbox.concurrent_messages", description: "Current concurrent message handlers");
    InboxBatchSize = meter.CreateHistogram<double>("whizbang.transport.inbox.batch.size", "{messages}", "Messages per inbox batch flush");
    InboxBatchWaitDuration = meter.CreateHistogram<double>("whizbang.transport.inbox.batch.wait.duration", "ms", "Time first message in batch waited before flush");
    InboxBatchFlushes = meter.CreateCounter<long>("whizbang.transport.inbox.batch.flushes", description: "Total inbox batch flushes");

    ActiveSubscriptions = meter.CreateUpDownCounter<int>("whizbang.transport.active_subscriptions", description: "Currently active transport subscriptions");

    EventStoreAppendDuration = meter.CreateHistogram<double>("whizbang.event_store.append.duration", "ms", "AppendAsync latency");
    EventStoreQueryDuration = meter.CreateHistogram<double>("whizbang.event_store.query.duration", "ms", "GetEventsBetweenPolymorphicAsync latency");
    EventsStored = meter.CreateCounter<long>("whizbang.event_store.events_stored", description: "Events appended");
    EventsQueried = meter.CreateCounter<long>("whizbang.event_store.events_queried", description: "Event queries executed");
  }
}
