using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for LifecycleMetrics instrument creation and recording.
/// </summary>
/// <docs>observability/metrics</docs>
[Category("Core")]
[Category("Observability")]
public class LifecycleMetricsTests {
  [Test]
  public async Task LifecycleMetrics_MeterName_IsWhizbangLifecycleAsync() {
    string meterName = LifecycleMetrics.METER_NAME;
    await Assert.That(meterName).IsEqualTo("Whizbang.Lifecycle");
  }

  [Test]
  public async Task LifecycleMetrics_Constructor_CreatesAllInstrumentsAsync() {
    var metrics = new LifecycleMetrics(new WhizbangMetrics());

    await Assert.That(metrics.StageDuration).IsNotNull();
    await Assert.That(metrics.ReceptorDuration).IsNotNull();
    await Assert.That(metrics.StageInvocations).IsNotNull();
    await Assert.That(metrics.ReceptorInvocations).IsNotNull();
    await Assert.That(metrics.ReceptorErrors).IsNotNull();
    await Assert.That(metrics.TagHookDuration).IsNotNull();
    await Assert.That(metrics.TagProcessingDuration).IsNotNull();
    await Assert.That(metrics.TagHookInvocations).IsNotNull();
    await Assert.That(metrics.TagHookErrors).IsNotNull();
  }

  [Test]
  public async Task LifecycleMetrics_StageDuration_RecordedPerStageAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.StageDuration.Record(10.0);

    var measurements = helper.GetByName("whizbang.lifecycle.stage.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(10.0);
  }

  [Test]
  public async Task LifecycleMetrics_StageDuration_IncludesStageAndMessageTypeTagsAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.StageDuration.Record(10.0,
      new KeyValuePair<string, object?>("stage", "PreDistributeAsync"),
      new KeyValuePair<string, object?>("message_type", "CreateOrder"));

    var measurements = helper.GetByName("whizbang.lifecycle.stage.duration");
    await Assert.That(measurements[0].Tags["stage"]).IsEqualTo("PreDistributeAsync");
    await Assert.That(measurements[0].Tags["message_type"]).IsEqualTo("CreateOrder");
  }

  [Test]
  public async Task LifecycleMetrics_ReceptorDuration_RecordedPerReceptorAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.ReceptorDuration.Record(3.5);

    var measurements = helper.GetByName("whizbang.lifecycle.receptor.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task LifecycleMetrics_ReceptorDuration_IncludesReceptorIndexTagAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.ReceptorDuration.Record(3.5, new KeyValuePair<string, object?>("receptor_index", "2"));

    var measurements = helper.GetByName("whizbang.lifecycle.receptor.duration");
    await Assert.That(measurements[0].Tags["receptor_index"]).IsEqualTo("2");
  }

  [Test]
  public async Task LifecycleMetrics_StageInvocations_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.StageInvocations.Add(1, new KeyValuePair<string, object?>("stage", "PostDistributeInline"));

    var measurements = helper.GetByName("whizbang.lifecycle.stage.invocations");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Tags["stage"]).IsEqualTo("PostDistributeInline");
  }

  [Test]
  public async Task LifecycleMetrics_ReceptorInvocations_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.ReceptorInvocations.Add(1);

    var measurements = helper.GetByName("whizbang.lifecycle.receptor.invocations");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task LifecycleMetrics_ReceptorErrors_IncludesErrorTypeTagAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.ReceptorErrors.Add(1, new KeyValuePair<string, object?>("error_type", "NullReferenceException"));

    var measurements = helper.GetByName("whizbang.lifecycle.receptor.errors");
    await Assert.That(measurements[0].Tags["error_type"]).IsEqualTo("NullReferenceException");
  }

  [Test]
  public async Task LifecycleMetrics_TagHookDuration_RecordedPerHookAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.TagHookDuration.Record(2.0);

    var measurements = helper.GetByName("whizbang.lifecycle.tag_hook.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task LifecycleMetrics_TagHookDuration_IncludesHookTypeAndStageTagsAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.TagHookDuration.Record(2.0,
      new KeyValuePair<string, object?>("hook_type", "AuditTag"),
      new KeyValuePair<string, object?>("fire_at_stage", "PreOutboxAsync"));

    var measurements = helper.GetByName("whizbang.lifecycle.tag_hook.duration");
    await Assert.That(measurements[0].Tags["hook_type"]).IsEqualTo("AuditTag");
    await Assert.That(measurements[0].Tags["fire_at_stage"]).IsEqualTo("PreOutboxAsync");
  }

  [Test]
  public async Task LifecycleMetrics_TagProcessingDuration_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.TagProcessingDuration.Record(15.0);

    var measurements = helper.GetByName("whizbang.lifecycle.tag_processing.duration");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task LifecycleMetrics_TagHookInvocations_IncrementedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.TagHookInvocations.Add(1);

    var measurements = helper.GetByName("whizbang.lifecycle.tag_hook.invocations");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task LifecycleMetrics_TagHookErrors_IncludesErrorTypeTagAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.TagHookErrors.Add(1, new KeyValuePair<string, object?>("error_type", "TimeoutException"));

    var measurements = helper.GetByName("whizbang.lifecycle.tag_hook.errors");
    await Assert.That(measurements[0].Tags["error_type"]).IsEqualTo("TimeoutException");
  }

  [Test]
  public async Task LifecycleMetrics_AllStages_CanBeRecordedAsync() {
    // Verify all 20 lifecycle stages can be recorded as tag values
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    var stages = new[] {
      "LocalImmediateAsync", "LocalImmediateInline",
      "PreDistributeAsync", "PreDistributeInline",
      "DistributeAsync",
      "PostDistributeAsync", "PostDistributeInline",
      "PreOutboxAsync", "PreOutboxInline",
      "PostOutboxAsync", "PostOutboxInline",
      "PreInboxAsync", "PreInboxInline",
      "PostInboxAsync", "PostInboxInline",
      "PrePerspectiveAsync", "PrePerspectiveInline",
      "PostPerspectiveAsync", "PostPerspectiveInline",
      "AfterReceptorCompletion"
    };

    foreach (var stage in stages) {
      metrics.StageInvocations.Add(1, new KeyValuePair<string, object?>("stage", stage));
    }

    var measurements = helper.GetByName("whizbang.lifecycle.stage.invocations");
    await Assert.That(measurements).Count().IsEqualTo(20)
      .Because("all 20 lifecycle stages should be recordable");
  }
}
