using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Test helpers for lifecycle tracking that drain detached tasks before returning.
/// Ensures fire-and-forget detached stages complete before assertions run.
/// </summary>
internal static class LifecycleTrackingTestExtensions {
  /// <summary>
  /// Advances to the given stage and drains any in-flight detached tasks.
  /// Use in tests instead of <see cref="ILifecycleTracking.AdvanceToAsync"/>
  /// to ensure detached receptors complete before assertions.
  /// </summary>
  public static async ValueTask AdvanceToAndDrainAsync(
      this ILifecycleTracking tracking,
      LifecycleStage stage,
      IServiceProvider scopedProvider,
      CancellationToken ct) {
    await tracking.AdvanceToAsync(stage, scopedProvider, ct);
    await tracking.DrainDetachedAsync();
  }
}
