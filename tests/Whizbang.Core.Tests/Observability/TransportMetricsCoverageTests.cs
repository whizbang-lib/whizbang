using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage-round tests for five <see cref="TransportMetrics"/> instruments that
/// <see cref="TransportMetricsTests"/> never exercises: concurrency-wait, in-flight concurrency,
/// batch size, batch-wait, and batch-flush count. None of their get-only properties are ever read
/// there, so neither the accessor nor a real recorded value ever flows through them.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/TransportMetrics.cs</code-under-test>
public class TransportMetricsCoverageTests {

  // If this histogram stopped recording, a semaphore-saturated inbox -- messages queueing behind
  // the concurrency limit -- would look identical to a healthy one until throughput visibly
  // collapses, because wait time behind the slot is the only leading indicator of that saturation.
  [Test]
  public async Task TransportMetrics_InboxConcurrencyWaitDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxConcurrencyWaitDuration.Record(12.5);

    var measurements = helper.GetByName("whizbang.transport.inbox.concurrency_wait.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(12.5);
  }

  // If this gauge stopped tracking -- especially the decrement -- a permit leak (a handler that
  // throws before releasing its concurrency slot) would be invisible right up until the semaphore is
  // fully exhausted and every new message blocks, with no metric able to explain why.
  [Test]
  public async Task TransportMetrics_InboxConcurrentMessages_TracksUpAndDownAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxConcurrentMessages.Add(1);
    metrics.InboxConcurrentMessages.Add(-1);

    var measurements = helper.GetByName("whizbang.transport.inbox.concurrent_messages");
    await Assert.That(measurements).Count().IsEqualTo(2);
    await Assert.That(measurements[0].Value).IsEqualTo(1);
    await Assert.That(measurements[1].Value).IsEqualTo(-1);
  }

  // Losing this histogram hides whether inbox batches are flushing near their configured cap or
  // trickling single messages -- the first thing an operator checks when batching throughput drops,
  // and without it a tuning problem is indistinguishable from a traffic problem.
  [Test]
  public async Task TransportMetrics_InboxBatchSize_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxBatchSize.Record(25.0);

    var measurements = helper.GetByName("whizbang.transport.inbox.batch.size");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(25.0);
  }

  // Without this histogram, a batch flush that stopped firing on its wait-timeout path -- only ever
  // flushing once full -- would be invisible until latency complaints arrive, with no metric able to
  // say whether messages are waiting on size or on time.
  [Test]
  public async Task TransportMetrics_InboxBatchWaitDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxBatchWaitDuration.Record(80.0);

    var measurements = helper.GetByName("whizbang.transport.inbox.batch.wait.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(80.0);
  }

  // If this counter stopped incrementing, "few flushes because traffic is low" and "flushes
  // silently stopped happening" render as the identical flat line; only this count, read alongside
  // InboxBatchSize, tells the two apart.
  [Test]
  public async Task TransportMetrics_InboxBatchFlushes_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new TransportMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.InboxBatchFlushes.Add(1);

    var measurements = helper.GetByName("whizbang.transport.inbox.batch.flushes");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }
}
