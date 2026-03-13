using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for TransportMetrics instrument creation and recording.
/// </summary>
/// <docs>observability/metrics</docs>
[Category("Core")]
[Category("Observability")]
public class TransportMetricsTests {
  [Test]
  public async Task TransportMetrics_MeterName_IsWhizbangTransportAsync() {
    string meterName = TransportMetrics.METER_NAME;
    await Assert.That(meterName).IsEqualTo("Whizbang.Transport");
  }

  [Test]
  public async Task TransportMetrics_Constructor_CreatesAllInstrumentsAsync() {
    var metrics = new TransportMetrics(new WhizbangMetrics());

    await Assert.That(metrics.InboxReceiveDuration).IsNotNull();
    await Assert.That(metrics.InboxDedupDuration).IsNotNull();
    await Assert.That(metrics.InboxProcessingDuration).IsNotNull();
    await Assert.That(metrics.InboxCompletionDuration).IsNotNull();
    await Assert.That(metrics.InboxSecurityContextDuration).IsNotNull();
    await Assert.That(metrics.InboxMessagesReceived).IsNotNull();
    await Assert.That(metrics.InboxMessagesProcessed).IsNotNull();
    await Assert.That(metrics.InboxMessagesDeduplicated).IsNotNull();
    await Assert.That(metrics.InboxMessagesFailed).IsNotNull();
    await Assert.That(metrics.InboxSubscriptionRetries).IsNotNull();
    await Assert.That(metrics.OutboxPublishDuration).IsNotNull();
    await Assert.That(metrics.OutboxReadinessWaitDuration).IsNotNull();
    await Assert.That(metrics.OutboxMessagesPublished).IsNotNull();
    await Assert.That(metrics.OutboxMessagesFailed).IsNotNull();
    await Assert.That(metrics.OutboxPublishRetries).IsNotNull();
    await Assert.That(metrics.ActiveSubscriptions).IsNotNull();
    await Assert.That(metrics.EventStoreAppendDuration).IsNotNull();
    await Assert.That(metrics.EventStoreQueryDuration).IsNotNull();
    await Assert.That(metrics.EventsStored).IsNotNull();
    await Assert.That(metrics.EventsQueried).IsNotNull();
  }

  [Test]
  public async Task TransportMetrics_InboxReceiveDuration_RecordedPerMessageAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxReceiveDuration.Record(45.0);

    var measurements = helper.GetByName("whizbang.transport.inbox.receive.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(45.0);
  }

  [Test]
  public async Task TransportMetrics_InboxDedupDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxDedupDuration.Record(5.0);

    var measurements = helper.GetByName("whizbang.transport.inbox.dedup.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_InboxProcessingDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxProcessingDuration.Record(100.0);

    var measurements = helper.GetByName("whizbang.transport.inbox.processing.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_InboxCompletionDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxCompletionDuration.Record(3.0);

    var measurements = helper.GetByName("whizbang.transport.inbox.completion.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_InboxSecurityContextDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxSecurityContextDuration.Record(20.0);

    var measurements = helper.GetByName("whizbang.transport.inbox.security_context.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_InboxMessagesReceived_IncrementedWithTopicTagAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxMessagesReceived.Add(1, new KeyValuePair<string, object?>("topic", "orders"));

    var measurements = helper.GetByName("whizbang.transport.inbox.messages_received");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Tags["topic"]).IsEqualTo("orders");
  }

  [Test]
  public async Task TransportMetrics_InboxMessagesProcessed_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxMessagesProcessed.Add(1);

    var measurements = helper.GetByName("whizbang.transport.inbox.messages_processed");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_InboxMessagesDeduplicated_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxMessagesDeduplicated.Add(1);

    var measurements = helper.GetByName("whizbang.transport.inbox.messages_deduplicated");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_InboxMessagesFailed_IncludesFailureReasonTagAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxMessagesFailed.Add(1, new KeyValuePair<string, object?>("failure_reason", "timeout"));

    var measurements = helper.GetByName("whizbang.transport.inbox.messages_failed");
    await Assert.That(measurements[0].Tags["failure_reason"]).IsEqualTo("timeout");
  }

  [Test]
  public async Task TransportMetrics_OutboxPublishDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.OutboxPublishDuration.Record(30.0);

    var measurements = helper.GetByName("whizbang.transport.outbox.publish.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_OutboxPublishDuration_IncludesDestinationTagAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.OutboxPublishDuration.Record(30.0, new KeyValuePair<string, object?>("destination", "orders-topic"));

    var measurements = helper.GetByName("whizbang.transport.outbox.publish.duration");
    await Assert.That(measurements[0].Tags["destination"]).IsEqualTo("orders-topic");
  }

  [Test]
  public async Task TransportMetrics_OutboxReadinessWaitDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.OutboxReadinessWaitDuration.Record(500.0);

    var measurements = helper.GetByName("whizbang.transport.outbox.readiness_wait.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_OutboxMessagesPublished_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.OutboxMessagesPublished.Add(1);

    var measurements = helper.GetByName("whizbang.transport.outbox.messages_published");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_OutboxMessagesFailed_IncludesFailureReasonTagAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.OutboxMessagesFailed.Add(1, new KeyValuePair<string, object?>("failure_reason", "TransportNotReady"));

    var measurements = helper.GetByName("whizbang.transport.outbox.messages_failed");
    await Assert.That(measurements[0].Tags["failure_reason"]).IsEqualTo("TransportNotReady");
  }

  [Test]
  public async Task TransportMetrics_ActiveSubscriptions_IncrementedOnSubscribeAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.ActiveSubscriptions.Add(1);

    var measurements = helper.GetByName("whizbang.transport.active_subscriptions");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_ActiveSubscriptions_DecrementedOnUnsubscribeAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.ActiveSubscriptions.Add(1);
    metrics.ActiveSubscriptions.Add(-1);

    var measurements = helper.GetByName("whizbang.transport.active_subscriptions");
    await Assert.That(measurements).Count().IsEqualTo(2);
    await Assert.That(measurements[1].Value).IsEqualTo(-1);
  }

  [Test]
  public async Task TransportMetrics_EventStoreAppendDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.EventStoreAppendDuration.Record(10.0);

    var measurements = helper.GetByName("whizbang.event_store.append.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_EventStoreQueryDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.EventStoreQueryDuration.Record(25.0);

    var measurements = helper.GetByName("whizbang.event_store.query.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_EventsStored_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.EventsStored.Add(1);

    var measurements = helper.GetByName("whizbang.event_store.events_stored");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task TransportMetrics_EventsQueried_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.EventsQueried.Add(1);

    var measurements = helper.GetByName("whizbang.event_store.events_queried");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }
}
