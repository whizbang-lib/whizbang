using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage for the three metric-instrument properties on <see cref="PinnedPoolMetrics"/>. Nothing
/// elsewhere reads <c>BorrowDuration</c>, <c>BorrowTimeouts</c>, or <c>ConnectionRecycles</c>
/// directly, so the property getters — and the instruments they expose — have never been
/// exercised. Each test gets its own Meter via <see cref="TestMeterFactory"/> and
/// filters the listener by that exact Meter instance (not by name), the same isolation pattern
/// <c>TableStatisticsMetricsCoverageTests</c> uses, so a sibling test's instruments can't leak into
/// this one's readings. If the pinned-pool borrow histogram or timeout/recycle counters silently
/// stopped reporting, an operator watching pool saturation would see nothing where a growing worker
/// backlog is actually starving on connections.
/// </summary>
public class PinnedPoolMetricsCoverageTests {

  [Test]
  public async Task BorrowDuration_RecordsAgainstItsOwnMeterAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new PinnedPoolMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.BorrowDuration.Record(12.5, new KeyValuePair<string, object?>("worker", "claim"));

    var readings = helper.GetByName("whizbang.workers.pinned_pool.borrow.duration");
    await Assert.That(readings.Count).IsEqualTo(1)
      .Because("the borrow-duration histogram must actually record what it's told, not just exist on the class");
    await Assert.That(readings[0].Value).IsEqualTo(12.5);
  }

  [Test]
  public async Task BorrowTimeouts_CountsAgainstItsOwnMeterAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new PinnedPoolMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.BorrowTimeouts.Add(1, new KeyValuePair<string, object?>("worker", "claim"));

    var readings = helper.GetByName("whizbang.workers.pinned_pool.borrow.timeouts");
    await Assert.That(readings.Count).IsEqualTo(1)
      .Because("a borrow timeout that never increments this counter would leave operators unable to see workers starving on the pinned pool");
    await Assert.That(readings[0].Value).IsEqualTo(1d);
  }

  [Test]
  public async Task ConnectionRecycles_CountsAgainstItsOwnMeterAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new PinnedPoolMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.ConnectionRecycles.Add(1);

    var readings = helper.GetByName("whizbang.workers.pinned_pool.connection_recycles");
    await Assert.That(readings.Count).IsEqualTo(1)
      .Because("recycle counts are the only signal that ConnectionLifetimeSeconds is actually rotating pinned connections");
    await Assert.That(readings[0].Value).IsEqualTo(1d);
  }
}
