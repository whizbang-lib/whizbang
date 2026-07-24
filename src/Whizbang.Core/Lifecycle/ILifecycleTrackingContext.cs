using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Extended lifecycle context available through the coordinator.
/// Provides timing, stage history, cancellation, and dynamic hook registration
/// beyond the base <see cref="ILifecycleContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Optionally injectable by receptors. Extends the base lifecycle context with
/// coordinator-specific capabilities like stage history and cancellation.
/// </para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator#context</docs>
public interface ILifecycleTrackingContext : ILifecycleContext {
  /// <summary>
  /// Gets elapsed time in the current stage. Debug-aware: pauses when debugger is attached.
  /// </summary>
  TimeSpan StageElapsed { get; }

  /// <summary>
  /// Gets total elapsed time since tracking began. Debug-aware.
  /// </summary>
  TimeSpan TotalElapsed { get; }

  /// <summary>
  /// Gets the service instance processing this event.
  /// </summary>
  ServiceInstanceInfo? ServiceInstance { get; }

  /// <summary>
  /// Gets the number of events in the current batch.
  /// Game loop workers: count of events. Independent mode: 1.
  /// </summary>
  int BatchSize { get; }

  /// <summary>
  /// Stages this tracking instance has passed through, with timing.
  /// Only tracks the current hydrated run, not across persistence boundaries.
  /// </summary>
  IReadOnlyList<StageRecord> StageHistory { get; }

  /// <summary>
  /// Cancels remaining stages in this lifecycle.
  /// </summary>
  /// <param name="reason">The reason for cancellation.</param>
  void Cancel(string reason);

  /// <summary>
  /// Gets whether this lifecycle has been cancelled.
  /// </summary>
  bool IsCancelled { get; }

  /// <summary>
  /// Gets the reason for cancellation, if cancelled.
  /// </summary>
  string? CancellationReason { get; }

  /// <summary>
  /// Registers a delegate hook to fire at a specific stage.
  /// </summary>
  /// <param name="stage">The lifecycle stage at which to fire.</param>
  /// <param name="hook">The hook delegate to invoke.</param>
  void OnStage(LifecycleStage stage, Func<ILifecycleTrackingContext, CancellationToken, ValueTask> hook);
}
