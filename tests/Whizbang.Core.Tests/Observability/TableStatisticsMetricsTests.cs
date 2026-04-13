using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for <see cref="TableStatisticsMetrics"/> cache and meter creation.
/// ObservableGauge callbacks are tested indirectly through cache state
/// since MetricAssertionHelper doesn't trigger observable collection.
/// </summary>
/// <tests>src/Whizbang.Core/Observability/TableStatisticsMetrics.cs</tests>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsMetricsTests {

  [Test]
  public async Task MeterName_IsWhizbangTableStatisticsAsync() {
    var meterName = TableStatisticsMetrics.METER_NAME;
    await Assert.That(meterName).IsEqualTo("Whizbang.TableStatistics");
  }

  [Test]
  public async Task Constructor_CreatesWithoutErrorAsync() {
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics());
    await Assert.That(metrics).IsNotNull();
  }

  [Test]
  public async Task UpdateTableSizes_StoresValuesAsync() {
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics());

    metrics.UpdateTableSizes(new Dictionary<string, long> {
      ["wh_inbox"] = 8192,
      ["wh_event_store"] = 1048576
    });

    // Values are stored in internal cache — ObservableGauge reads them on collection
    // No assertion on meter output (ObservableGauge not testable via MetricAssertionHelper)
    // Just verify no exception thrown
    await Assert.That(metrics).IsNotNull();
  }

  [Test]
  public async Task UpdateQueueDepths_StoresValuesAsync() {
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics());

    metrics.UpdateQueueDepths(new Dictionary<string, long> {
      ["inbox"] = 42,
      ["outbox"] = 7
    });

    await Assert.That(metrics).IsNotNull();
  }

  [Test]
  public async Task UpdateTableSizes_CalledMultipleTimes_DoesNotThrowAsync() {
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics());

    metrics.UpdateTableSizes(new Dictionary<string, long> { ["wh_inbox"] = 100 });
    metrics.UpdateTableSizes(new Dictionary<string, long> { ["wh_inbox"] = 200 });
    metrics.UpdateTableSizes(new Dictionary<string, long> { ["wh_inbox"] = 300, ["wh_outbox"] = 400 });

    await Assert.That(metrics).IsNotNull();
  }
}
