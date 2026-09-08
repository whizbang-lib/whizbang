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
  public PassiveCounter<int> ActiveTrackedEvents { get; }

  /// <summary>Events awaiting perspective WhenAll completion.</summary>
  public PassiveCounter<int> PendingPerspectiveStates { get; }

  /// <summary>Events awaiting segment WhenAll completion.</summary>
  public PassiveCounter<int> PendingWhenAllStates { get; }

  // Completion counters

  /// <summary>Individual perspective complete signals received.</summary>
  public PassiveCounter<long> PerspectiveCompletionsSignaled { get; }

  /// <summary>Events where all perspectives finished.</summary>
  public PassiveCounter<long> AllPerspectivesCompleted { get; }

  /// <summary>Events with no perspective expectations (key mismatch detector).</summary>
  public PassiveCounter<long> ExpectationsNotRegistered { get; }

  // Stage firing counters

  /// <summary>PostAllPerspectives stage executions.</summary>
  public PassiveCounter<long> PostAllPerspectivesFired { get; }

  /// <summary>PostLifecycle stage executions.</summary>
  public PassiveCounter<long> PostLifecycleFired { get; }

  /// <summary>Stage transitions (tag: stage).</summary>
  public PassiveCounter<long> StageTransitions { get; }

  // Cleanup

  /// <summary>Stale tracking entries cleaned by inactivity threshold.</summary>
  public PassiveCounter<long> StaleTrackingCleaned { get; }

  /// <summary>PostLifecycle stage errors that were isolated (per-event error isolation).</summary>
  public PassiveCounter<long> PostLifecycleErrors { get; }

  /// <summary>Stale entries preserved because perspectives were partially complete.</summary>
  public PassiveCounter<long> StaleTrackingPreservedPartialPerspectives { get; }

  /// <summary>Initializes a new instance of the <see cref="LifecycleCoordinatorMetrics"/> class.</summary>
  /// <param name="whizbangMetrics">The shared metrics factory providing the meter.</param>
  public LifecycleCoordinatorMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    ActiveTrackedEvents = meter.CreatePassiveUpDownCounter<int>(
      "whizbang.lifecycle_coordinator.active_tracked_events",
      description: "Events currently in lifecycle tracking");
    PendingPerspectiveStates = meter.CreatePassiveUpDownCounter<int>(
      "whizbang.lifecycle_coordinator.pending_perspective_states",
      description: "Events awaiting perspective WhenAll completion");
    PendingWhenAllStates = meter.CreatePassiveUpDownCounter<int>(
      "whizbang.lifecycle_coordinator.pending_when_all_states",
      description: "Events awaiting segment WhenAll completion");

    PerspectiveCompletionsSignaled = meter.CreatePassiveCounter<long>(
      "whizbang.lifecycle_coordinator.perspective_completions_signaled",
      description: "Individual perspective complete signals received");
    AllPerspectivesCompleted = meter.CreatePassiveCounter<long>(
      "whizbang.lifecycle_coordinator.all_perspectives_completed",
      description: "Events where all perspectives finished");
    ExpectationsNotRegistered = meter.CreatePassiveCounter<long>(
      "whizbang.lifecycle_coordinator.expectations_not_registered",
      description: "Events with no perspective expectations (key mismatch detector)");

    PostAllPerspectivesFired = meter.CreatePassiveCounter<long>(
      "whizbang.lifecycle_coordinator.post_all_perspectives_fired",
      description: "PostAllPerspectives stage executions");
    PostLifecycleFired = meter.CreatePassiveCounter<long>(
      "whizbang.lifecycle_coordinator.post_lifecycle_fired",
      description: "PostLifecycle stage executions");
    StageTransitions = meter.CreatePassiveCounter<long>(
      "whizbang.lifecycle_coordinator.stage_transitions",
      description: "Stage transitions (tag: stage)");

    StaleTrackingCleaned = meter.CreatePassiveCounter<long>(
      "whizbang.lifecycle_coordinator.stale_tracking_cleaned",
      description: "Stale tracking entries cleaned by inactivity threshold");

    PostLifecycleErrors = meter.CreatePassiveCounter<long>(
      "whizbang.lifecycle_coordinator.post_lifecycle_errors",
      description: "PostLifecycle stage errors that were isolated (per-event error isolation)");

    StaleTrackingPreservedPartialPerspectives = meter.CreatePassiveCounter<long>(
      "whizbang.lifecycle_coordinator.stale_tracking_preserved_partial_perspectives",
      description: "Stale entries preserved because perspectives were partially complete");
  }
}
