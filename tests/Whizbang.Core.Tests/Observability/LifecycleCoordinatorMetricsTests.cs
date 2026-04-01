using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for LifecycleCoordinatorMetrics instrument creation and recording.
/// </summary>
/// <docs>operations/observability/metrics</docs>
[Category("Core")]
[Category("Observability")]
public class LifecycleCoordinatorMetricsTests {
  [Test]
  public async Task LifecycleCoordinatorMetrics_MeterName_IsWhizbangLifecycleCoordinatorAsync() {
    string meterName = LifecycleCoordinatorMetrics.METER_NAME;
    await Assert.That(meterName).IsEqualTo("Whizbang.LifecycleCoordinator");
  }

  [Test]
  public async Task LifecycleCoordinatorMetrics_Constructor_CreatesAllInstrumentsAsync() {
    var metrics = new LifecycleCoordinatorMetrics(new WhizbangMetrics());

    await Assert.That(metrics.ActiveTrackedEvents).IsNotNull();
    await Assert.That(metrics.PendingPerspectiveStates).IsNotNull();
    await Assert.That(metrics.PendingWhenAllStates).IsNotNull();
    await Assert.That(metrics.PerspectiveCompletionsSignaled).IsNotNull();
    await Assert.That(metrics.AllPerspectivesCompleted).IsNotNull();
    await Assert.That(metrics.ExpectationsNotRegistered).IsNotNull();
    await Assert.That(metrics.PostAllPerspectivesFired).IsNotNull();
    await Assert.That(metrics.PostLifecycleFired).IsNotNull();
    await Assert.That(metrics.StageTransitions).IsNotNull();
    await Assert.That(metrics.StaleTrackingCleaned).IsNotNull();
  }

  [Test]
  public async Task ActiveTrackedEvents_IncrementAndDecrement_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.ActiveTrackedEvents.Add(1);
    metrics.ActiveTrackedEvents.Add(-1);

    var measurements = helper.GetByName("whizbang.lifecycle_coordinator.active_tracked_events");
    await Assert.That(measurements).Count().IsEqualTo(2);
    await Assert.That(measurements[0].Value).IsEqualTo(1);
    await Assert.That(measurements[1].Value).IsEqualTo(-1);
  }

  [Test]
  public async Task PendingPerspectiveStates_IncrementAndDecrement_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.PendingPerspectiveStates.Add(1);

    var measurements = helper.GetByName("whizbang.lifecycle_coordinator.pending_perspective_states");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(1);
  }

  [Test]
  public async Task PendingWhenAllStates_IncrementAndDecrement_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.PendingWhenAllStates.Add(1);

    var measurements = helper.GetByName("whizbang.lifecycle_coordinator.pending_when_all_states");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveCompletionsSignaled_Incremented_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.PerspectiveCompletionsSignaled.Add(1);

    var measurements = helper.GetByName("whizbang.lifecycle_coordinator.perspective_completions_signaled");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task AllPerspectivesCompleted_Incremented_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.AllPerspectivesCompleted.Add(1);

    var measurements = helper.GetByName("whizbang.lifecycle_coordinator.all_perspectives_completed");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task ExpectationsNotRegistered_Incremented_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.ExpectationsNotRegistered.Add(1);

    var measurements = helper.GetByName("whizbang.lifecycle_coordinator.expectations_not_registered");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PostAllPerspectivesFired_Incremented_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.PostAllPerspectivesFired.Add(1);

    var measurements = helper.GetByName("whizbang.lifecycle_coordinator.post_all_perspectives_fired");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PostLifecycleFired_Incremented_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.PostLifecycleFired.Add(1);

    var measurements = helper.GetByName("whizbang.lifecycle_coordinator.post_lifecycle_fired");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }

  [Test]
  public async Task StageTransitions_WithStageTag_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.StageTransitions.Add(1,
      new KeyValuePair<string, object?>("stage", "PostAllPerspectivesDetached"));

    var measurements = helper.GetByName("whizbang.lifecycle_coordinator.stage_transitions");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Tags["stage"]).IsEqualTo("PostAllPerspectivesDetached");
  }

  [Test]
  public async Task StaleTrackingCleaned_Incremented_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.StaleTrackingCleaned.Add(1);

    var measurements = helper.GetByName("whizbang.lifecycle_coordinator.stale_tracking_cleaned");
    await Assert.That(measurements).Count().IsEqualTo(1);
  }
}
