using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for rewind-specific instruments in <see cref="PerspectiveMetrics"/>.
/// </summary>
/// <tests>src/Whizbang.Core/Observability/PerspectiveMetrics.cs</tests>
[Category("Core")]
[Category("Observability")]
public class PerspectiveRewindMetricsTests {

  [Test]
  public async Task PerspectiveMetrics_RewindInstruments_CreatedAsync() {
    var metrics = new PerspectiveMetrics(new WhizbangMetrics());

    await Assert.That(metrics.Rewinds).IsNotNull();
    await Assert.That(metrics.RewindDuration).IsNotNull();
    await Assert.That(metrics.RewindEventsReplayed).IsNotNull();
    await Assert.That(metrics.RewindEventsBehind).IsNotNull();
  }

  [Test]
  public async Task RewindCounter_RecordsWithTagsAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.Rewinds.Add(1,
      new KeyValuePair<string, object?>("perspective_name", "OrderPerspective"),
      new KeyValuePair<string, object?>("has_snapshot", true));

    var measurements = helper.GetByName("whizbang.perspective.rewinds");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(1);
  }

  [Test]
  public async Task RewindDuration_RecordsHistogramAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.RewindDuration.Record(150.5,
      new KeyValuePair<string, object?>("perspective_name", "OrderPerspective"));

    var measurements = helper.GetByName("whizbang.perspective.rewind.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(150.5);
  }

  [Test]
  public async Task RewindEventsReplayed_RecordsHistogramAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.RewindEventsReplayed.Record(42,
      new KeyValuePair<string, object?>("perspective_name", "OrderPerspective"));

    var measurements = helper.GetByName("whizbang.perspective.rewind.events_replayed");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(42);
  }

  [Test]
  public async Task RewindEventsBehind_RecordsHistogramAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.RewindEventsBehind.Record(7,
      new KeyValuePair<string, object?>("perspective_name", "OrderPerspective"));

    var measurements = helper.GetByName("whizbang.perspective.rewind.events_behind");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(7);
  }
}
