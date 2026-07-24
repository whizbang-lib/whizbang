using System.Diagnostics.Metrics;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for MetricAssertionHelper using isolated meter instances.
/// Each test uses a unique meter name to avoid cross-test interference.
/// </summary>
[Category("Core")]
[Category("Observability")]
public class MetricAssertionHelperTests {
  [Test]
  public async Task MetricHelper_CollectsHistogramValues_ByInstrumentNameAsync() {
    // Arrange - use unique meter name to avoid cross-test interference
    var meterName = $"Test.Meter.{Guid.CreateVersion7()}";
    using var meter = new Meter(meterName);
    using var helper = new MetricAssertionHelper(meterName);
    var histogram = meter.CreateHistogram<double>("test.histogram");

    // Act
    histogram.Record(42.5);
    histogram.Record(99.1);

    // Assert
    var measurements = helper.GetByName("test.histogram");
    await Assert.That(measurements).Count().IsEqualTo(2)
      .Because("two histogram values were recorded");
    await Assert.That(measurements[0].Value).IsEqualTo(42.5);
    await Assert.That(measurements[1].Value).IsEqualTo(99.1);
  }

  [Test]
  public async Task MetricHelper_CollectsCounterValues_ByInstrumentNameAsync() {
    // Arrange
    var meterName = $"Test.Meter.{Guid.CreateVersion7()}";
    using var meter = new Meter(meterName);
    using var helper = new MetricAssertionHelper(meterName);
    var counter = meter.CreateCounter<long>("test.counter");

    // Act
    counter.Add(1);
    counter.Add(1);
    counter.Add(1);

    // Assert
    var measurements = helper.GetByName("test.counter");
    await Assert.That(measurements).Count().IsEqualTo(3)
      .Because("counter was incremented three times");
  }

  [Test]
  public async Task MetricHelper_FiltersByTags_ReturnsMatchingOnlyAsync() {
    // Arrange
    var meterName = $"Test.Meter.{Guid.CreateVersion7()}";
    using var meter = new Meter(meterName);
    using var helper = new MetricAssertionHelper(meterName);
    var histogram = meter.CreateHistogram<double>("test.tagged");

    // Act
    histogram.Record(10.0, new KeyValuePair<string, object?>("strategy", "scoped"));
    histogram.Record(20.0, new KeyValuePair<string, object?>("strategy", "immediate"));

    // Assert
    var measurements = helper.GetByName("test.tagged");
    await Assert.That(measurements).Count().IsEqualTo(2);
    await Assert.That(measurements[0].Tags["strategy"]).IsEqualTo("scoped");
    await Assert.That(measurements[1].Tags["strategy"]).IsEqualTo("immediate");
  }

  [Test]
  public async Task MetricHelper_ReturnsEmpty_WhenNoMatchingInstrumentAsync() {
    // Arrange
    using var helper = new MetricAssertionHelper("NonExistent.Meter");

    // Act & Assert
    var measurements = helper.GetByName("whizbang.nonexistent.metric");
    await Assert.That(measurements).Count().IsEqualTo(0)
      .Because("no instruments match the meter name");
  }
}
