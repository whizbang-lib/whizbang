using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for lifecycle coordinator state tracking: active events, perspective WhenAll,
/// stage transitions, and stale tracking cleanup.
/// Meter name: Whizbang.LifecycleCoordinator
/// </summary>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/LifecycleCoordinatorMetricsTests.cs</tests>
public sealed class LifecycleCoordinatorMetrics {
#pragma warning disable CA1707
  public const string METER_NAME = "Whizbang.LifecycleCoordinator";
#pragma warning restore CA1707

  // Active tracking gauges (up/down counters for current state)
  public UpDownCounter<int> ActiveTrackedEvents { get; }
  public UpDownCounter<int> PendingPerspectiveStates { get; }
  public UpDownCounter<int> PendingWhenAllStates { get; }

  // Completion counters
  public Counter<long> PerspectiveCompletionsSignaled { get; }
  public Counter<long> AllPerspectivesCompleted { get; }
  public Counter<long> ExpectationsNotRegistered { get; }

  // Stage firing counters
  public Counter<long> PostAllPerspectivesFired { get; }
  public Counter<long> PostLifecycleFired { get; }
  public Counter<long> StageTransitions { get; }

  // Cleanup
  public Counter<long> StaleTrackingCleaned { get; }

  public LifecycleCoordinatorMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    ActiveTrackedEvents = meter.CreateUpDownCounter<int>(
      "whizbang.lifecycle_coordinator.active_tracked_events",
      description: "Events currently in lifecycle tracking");
    PendingPerspectiveStates = meter.CreateUpDownCounter<int>(
      "whizbang.lifecycle_coordinator.pending_perspective_states",
      description: "Events awaiting perspective WhenAll completion");
    PendingWhenAllStates = meter.CreateUpDownCounter<int>(
      "whizbang.lifecycle_coordinator.pending_when_all_states",
      description: "Events awaiting segment WhenAll completion");

    PerspectiveCompletionsSignaled = meter.CreateCounter<long>(
      "whizbang.lifecycle_coordinator.perspective_completions_signaled",
      description: "Individual perspective complete signals received");
    AllPerspectivesCompleted = meter.CreateCounter<long>(
      "whizbang.lifecycle_coordinator.all_perspectives_completed",
      description: "Events where all perspectives finished");
    ExpectationsNotRegistered = meter.CreateCounter<long>(
      "whizbang.lifecycle_coordinator.expectations_not_registered",
      description: "Events with no perspective expectations (key mismatch detector)");

    PostAllPerspectivesFired = meter.CreateCounter<long>(
      "whizbang.lifecycle_coordinator.post_all_perspectives_fired",
      description: "PostAllPerspectives stage executions");
    PostLifecycleFired = meter.CreateCounter<long>(
      "whizbang.lifecycle_coordinator.post_lifecycle_fired",
      description: "PostLifecycle stage executions");
    StageTransitions = meter.CreateCounter<long>(
      "whizbang.lifecycle_coordinator.stage_transitions",
      description: "Stage transitions (tag: stage)");

    StaleTrackingCleaned = meter.CreateCounter<long>(
      "whizbang.lifecycle_coordinator.stale_tracking_cleaned",
      description: "Stale tracking entries cleaned by inactivity threshold");
  }
}
