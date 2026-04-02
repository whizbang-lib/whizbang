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
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.LifecycleCoordinator";
#pragma warning restore CA1707

  // Active tracking gauges (up/down counters for current state)

  /// <summary>Events currently in lifecycle tracking.</summary>
  public UpDownCounter<int> ActiveTrackedEvents { get; }

  /// <summary>Events awaiting perspective WhenAll completion.</summary>
  public UpDownCounter<int> PendingPerspectiveStates { get; }

  /// <summary>Events awaiting segment WhenAll completion.</summary>
  public UpDownCounter<int> PendingWhenAllStates { get; }

  // Completion counters

  /// <summary>Individual perspective complete signals received.</summary>
  public Counter<long> PerspectiveCompletionsSignaled { get; }

  /// <summary>Events where all perspectives finished.</summary>
  public Counter<long> AllPerspectivesCompleted { get; }

  /// <summary>Events with no perspective expectations (key mismatch detector).</summary>
  public Counter<long> ExpectationsNotRegistered { get; }

  // Stage firing counters

  /// <summary>PostAllPerspectives stage executions.</summary>
  public Counter<long> PostAllPerspectivesFired { get; }

  /// <summary>PostLifecycle stage executions.</summary>
  public Counter<long> PostLifecycleFired { get; }

  /// <summary>Stage transitions (tag: stage).</summary>
  public Counter<long> StageTransitions { get; }

  // Cleanup

  /// <summary>Stale tracking entries cleaned by inactivity threshold.</summary>
  public Counter<long> StaleTrackingCleaned { get; }

  /// <summary>Stale entries preserved because perspectives were partially complete.</summary>
  public Counter<long> StaleTrackingPreservedPartialPerspectives { get; }

  /// <summary>Initializes a new instance of the <see cref="LifecycleCoordinatorMetrics"/> class.</summary>
  /// <param name="whizbangMetrics">The shared metrics factory providing the meter.</param>
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

    StaleTrackingPreservedPartialPerspectives = meter.CreateCounter<long>(
      "whizbang.lifecycle_coordinator.stale_tracking_preserved_partial_perspectives",
      description: "Stale entries preserved because perspectives were partially complete");
  }
}
