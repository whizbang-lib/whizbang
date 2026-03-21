using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Handle for advancing a tracked event through lifecycle stages.
/// Encapsulates receptor invocation, tag processing, and ImmediateAsync chaining.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <see cref="AdvanceToAsync"/> invokes receptors and processes tags at the target stage,
/// then fires ImmediateAsync hooks. For Inline stages, all hooks are awaited before returning.
/// For Async stages, hooks are fire-and-forget with captured scope/context.
/// </para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator#tracking</docs>
public interface ILifecycleTracking {
  /// <summary>
  /// Gets the event ID being tracked.
  /// </summary>
  Guid EventId { get; }

  /// <summary>
  /// Gets the current lifecycle stage this tracking instance has reached.
  /// </summary>
  LifecycleStage CurrentStage { get; }

  /// <summary>
  /// Gets whether lifecycle processing has completed for this event.
  /// </summary>
  bool IsComplete { get; }

  /// <summary>
  /// Advances to the next stage. Invokes receptors and processes tags.
  /// For Async stages: receptors fire but don't block the calling worker.
  /// For Inline stages: awaits all receptors before returning.
  /// ImmediateAsync fires automatically after each stage.
  /// </summary>
  /// <param name="stage">The stage to advance to.</param>
  /// <param name="scopedProvider">Scoped service provider for receptor resolution.</param>
  /// <param name="ct">Cancellation token.</param>
  ValueTask AdvanceToAsync(
    LifecycleStage stage,
    IServiceProvider scopedProvider,
    CancellationToken ct);

  /// <summary>
  /// Advances to the next stage for multiple events (batch operation).
  /// Used by PerspectiveWorker for PrePerspective/PostPerspective stages.
  /// </summary>
  /// <param name="trackings">The tracking instances to advance.</param>
  /// <param name="stage">The stage to advance to.</param>
  /// <param name="scopedProvider">Scoped service provider for receptor resolution.</param>
  /// <param name="ct">Cancellation token.</param>
  static ValueTask AdvanceBatchAsync(
    IEnumerable<ILifecycleTracking> trackings,
    LifecycleStage stage,
    IServiceProvider scopedProvider,
    CancellationToken ct) {
    return _advanceBatchCoreAsync(trackings, stage, scopedProvider, ct);
  }

  /// <summary>
  /// Default implementation for batch advancement.
  /// </summary>
  private static async ValueTask _advanceBatchCoreAsync(
    IEnumerable<ILifecycleTracking> trackings,
    LifecycleStage stage,
    IServiceProvider scopedProvider,
    CancellationToken ct) {
    foreach (var tracking in trackings) {
      await tracking.AdvanceToAsync(stage, scopedProvider, ct).ConfigureAwait(false);
    }
  }
}
