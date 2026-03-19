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
  public const string METER_NAME = "Whizbang.Transport";
#pragma warning restore CA1707

  private readonly Meter _meter;

  // Inbox (receive side)
  public Histogram<double> InboxReceiveDuration { get; }
  public Histogram<double> InboxDedupDuration { get; }
  public Histogram<double> InboxProcessingDuration { get; }
  public Histogram<double> InboxCompletionDuration { get; }
  public Histogram<double> InboxSecurityContextDuration { get; }

  // Inbox counters
  public Counter<long> InboxMessagesReceived { get; }
  public Counter<long> InboxMessagesProcessed { get; }
  public Counter<long> InboxMessagesDeduplicated { get; }
  public Counter<long> InboxMessagesFailed { get; }
  public Counter<long> InboxSubscriptionRetries { get; }

  // Outbox (send side)
  public Histogram<double> OutboxPublishDuration { get; }
  public Histogram<double> OutboxReadinessWaitDuration { get; }

  // Outbox counters
  public Counter<long> OutboxMessagesPublished { get; }
  public Counter<long> OutboxMessagesFailed { get; }
  public Counter<long> OutboxPublishRetries { get; }

  // Gauges
  public UpDownCounter<int> ActiveSubscriptions { get; }

  // Event store
  public Histogram<double> EventStoreAppendDuration { get; }
  public Histogram<double> EventStoreQueryDuration { get; }
  public Counter<long> EventsStored { get; }
  public Counter<long> EventsQueried { get; }

  public TransportMetrics(WhizbangMetrics whizbangMetrics) {
    _meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    InboxReceiveDuration = _meter.CreateHistogram<double>("whizbang.transport.inbox.receive.duration", "ms", "Full _handleMessageAsync: receive → process → complete");
    InboxDedupDuration = _meter.CreateHistogram<double>("whizbang.transport.inbox.dedup.duration", "ms", "First FlushAsync (INSERT ... ON CONFLICT)");
    InboxProcessingDuration = _meter.CreateHistogram<double>("whizbang.transport.inbox.processing.duration", "ms", "OrderedStreamProcessor.ProcessInboxWorkAsync");
    InboxCompletionDuration = _meter.CreateHistogram<double>("whizbang.transport.inbox.completion.duration", "ms", "Second FlushAsync (report completions)");
    InboxSecurityContextDuration = _meter.CreateHistogram<double>("whizbang.transport.inbox.security_context.duration", "ms", "SecurityContextHelper.EstablishFullContextAsync");

    InboxMessagesReceived = _meter.CreateCounter<long>("whizbang.transport.inbox.messages_received", description: "Messages received from transport");
    InboxMessagesProcessed = _meter.CreateCounter<long>("whizbang.transport.inbox.messages_processed", description: "Successfully processed");
    InboxMessagesDeduplicated = _meter.CreateCounter<long>("whizbang.transport.inbox.messages_deduplicated", description: "Rejected as duplicates");
    InboxMessagesFailed = _meter.CreateCounter<long>("whizbang.transport.inbox.messages_failed", description: "Processing failures");
    InboxSubscriptionRetries = _meter.CreateCounter<long>("whizbang.transport.inbox.subscription_retries", description: "Transport subscription retry attempts");

    OutboxPublishDuration = _meter.CreateHistogram<double>("whizbang.transport.outbox.publish.duration", "ms", "_publishStrategy.PublishAsync to transport");
    OutboxReadinessWaitDuration = _meter.CreateHistogram<double>("whizbang.transport.outbox.readiness_wait.duration", "ms", "Time waiting for transport readiness");

    OutboxMessagesPublished = _meter.CreateCounter<long>("whizbang.transport.outbox.messages_published", description: "Messages published to transport");
    OutboxMessagesFailed = _meter.CreateCounter<long>("whizbang.transport.outbox.messages_failed", description: "Publish failures by reason");
    OutboxPublishRetries = _meter.CreateCounter<long>("whizbang.transport.outbox.publish_retries", description: "Retry attempts");

    ActiveSubscriptions = _meter.CreateUpDownCounter<int>("whizbang.transport.active_subscriptions", description: "Currently active transport subscriptions");

    EventStoreAppendDuration = _meter.CreateHistogram<double>("whizbang.event_store.append.duration", "ms", "AppendAsync latency");
    EventStoreQueryDuration = _meter.CreateHistogram<double>("whizbang.event_store.query.duration", "ms", "GetEventsBetweenPolymorphicAsync latency");
    EventsStored = _meter.CreateCounter<long>("whizbang.event_store.events_stored", description: "Events appended");
    EventsQueried = _meter.CreateCounter<long>("whizbang.event_store.events_queried", description: "Event queries executed");
  }
}
