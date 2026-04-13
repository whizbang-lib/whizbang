using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for PerspectiveMetrics instrument creation and recording.
/// </summary>
/// <docs>observability/metrics</docs>
[Category("Core")]
[Category("Observability")]
public class PerspectiveMetricsTests {
  [Test]
  public async Task PerspectiveMetrics_MeterName_IsWhizbangPerspectivesAsync() {
    string meterName = PerspectiveMetrics.METER_NAME;
    await Assert.That(meterName).IsEqualTo("Whizbang.Perspectives");
  }

  [Test]
  public async Task PerspectiveMetrics_Constructor_CreatesAllInstrumentsAsync() {
    var metrics = new PerspectiveMetrics(new WhizbangMetrics());

    await Assert.That(metrics.BatchDuration).IsNotNull();
    await Assert.That(metrics.ClaimDuration).IsNotNull();
    await Assert.That(metrics.EventLoadDuration).IsNotNull();
    await Assert.That(metrics.RunnerDuration).IsNotNull();
    await Assert.That(metrics.CheckpointDuration).IsNotNull();
    await Assert.That(metrics.EventsProcessed).IsNotNull();
    await Assert.That(metrics.BatchesProcessed).IsNotNull();
    await Assert.That(metrics.StreamsUpdated).IsNotNull();
    await Assert.That(metrics.Errors).IsNotNull();
    await Assert.That(metrics.EmptyBatches).IsNotNull();
    await Assert.That(metrics.BatchWorkItems).IsNotNull();
    await Assert.That(metrics.BatchEventCount).IsNotNull();
    await Assert.That(metrics.BatchStreamGroups).IsNotNull();
    await Assert.That(metrics.Rewinds).IsNotNull();
    await Assert.That(metrics.RewindDuration).IsNotNull();
    await Assert.That(metrics.RewindEventsReplayed).IsNotNull();
    await Assert.That(metrics.RewindEventsBehind).IsNotNull();
  }

  [Test]
  public async Task PerspectiveMetrics_BatchDuration_RecordedPerBatchAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.BatchDuration.Record(250.0);

    var measurements = helper.GetByName("whizbang.perspective.batch.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(250.0);
  }

  [Test]
  public async Task PerspectiveMetrics_BatchDuration_IncludesPerspectiveNameTagAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.BatchDuration.Record(100.0, new KeyValuePair<string, object?>("perspective_name", "OrderSummary"));

    var measurements = helper.GetByName("whizbang.perspective.batch.duration");
    await Assert.That(measurements[0].Tags["perspective_name"]).IsEqualTo("OrderSummary");
  }

  [Test]
  public async Task PerspectiveMetrics_ClaimDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.ClaimDuration.Record(15.0);

    var measurements = helper.GetByName("whizbang.perspective.claim.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveMetrics_EventLoadDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.EventLoadDuration.Record(80.0);

    var measurements = helper.GetByName("whizbang.perspective.event_load.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveMetrics_RunnerDuration_RecordedPerStreamAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.RunnerDuration.Record(30.0);

    var measurements = helper.GetByName("whizbang.perspective.runner.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveMetrics_RunnerDuration_IncludesStreamIdTagAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    var streamId = Guid.CreateVersion7().ToString();
    metrics.RunnerDuration.Record(30.0, new KeyValuePair<string, object?>("stream_id", streamId));

    var measurements = helper.GetByName("whizbang.perspective.runner.duration");
    await Assert.That(measurements[0].Tags["stream_id"]).IsEqualTo(streamId);
  }

  [Test]
  public async Task PerspectiveMetrics_CheckpointDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.CheckpointDuration.Record(5.0);

    var measurements = helper.GetByName("whizbang.perspective.checkpoint.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveMetrics_EventsProcessed_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.EventsProcessed.Add(10);

    var measurements = helper.GetByName("whizbang.perspective.events_processed");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(10);
  }

  [Test]
  public async Task PerspectiveMetrics_BatchesProcessed_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.BatchesProcessed.Add(1);

    var measurements = helper.GetByName("whizbang.perspective.batches_processed");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveMetrics_StreamsUpdated_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.StreamsUpdated.Add(3);

    var measurements = helper.GetByName("whizbang.perspective.streams_updated");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveMetrics_Errors_IncludesErrorTypeTagAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.Errors.Add(1, new KeyValuePair<string, object?>("error_type", "ConcurrencyException"));

    var measurements = helper.GetByName("whizbang.perspective.errors");
    await Assert.That(measurements[0].Tags["error_type"]).IsEqualTo("ConcurrencyException");
  }

  [Test]
  public async Task PerspectiveMetrics_EmptyBatches_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.EmptyBatches.Add(1);

    var measurements = helper.GetByName("whizbang.perspective.empty_batches");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveMetrics_BatchWorkItems_RecordsCorrectCountAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.BatchWorkItems.Record(8);

    var measurements = helper.GetByName("whizbang.perspective.batch.work_items");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(8);
  }

  [Test]
  public async Task PerspectiveMetrics_BatchEventCount_RecordsCorrectCountAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.BatchEventCount.Record(42);

    var measurements = helper.GetByName("whizbang.perspective.batch.event_count");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(42);
  }

  [Test]
  public async Task PerspectiveMetrics_BatchStreamGroups_RecordsCorrectCountAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new PerspectiveMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.BatchStreamGroups.Record(5);

    var measurements = helper.GetByName("whizbang.perspective.batch.stream_groups");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(5);
  }
}
