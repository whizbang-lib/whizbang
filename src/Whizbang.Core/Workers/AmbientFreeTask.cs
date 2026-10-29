// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// Starts a long-lived worker that belongs to the component starting it, not to the caller whose call happened to
/// start it (#1883). <see cref="Task.Run(Func{Task}, CancellationToken)"/> copies the caller's
/// <see cref="ExecutionContext"/> into the task, so every <see cref="AsyncLocal{T}"/> value the caller held (its pinned
/// connection in <see cref="PinnedConnectionContext"/>, its scope, tenant and activity) would stay visible to the
/// worker for its whole life, long after the caller released them and while the worker serves other callers. The
/// worker is started with the flow suppressed, so it begins with no ambient context at all.
/// </summary>
/// <docs>extending/internals/event-ordering-invariant</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatcherAmbientContextTests.cs</tests>
internal static class AmbientFreeTask {
  /// <summary>
  /// Runs <paramref name="work"/> on the thread pool without the caller's <see cref="ExecutionContext"/>. A caller
  /// that already suppressed the flow keeps it suppressed; any other caller's flow is restored before this returns.
  /// </summary>
  /// <param name="work">The worker to start.</param>
  /// <param name="cancellationToken">Passed to <see cref="Task.Run(Func{Task}, CancellationToken)"/>: canceled before the start, the work never runs.</param>
  /// <returns>The started worker.</returns>
  internal static Task Run(Func<Task> work, CancellationToken cancellationToken) {
    if (ExecutionContext.IsFlowSuppressed()) {
      return Task.Run(work, cancellationToken);
    }
    // Started inside the block and returned after it: the flow is restored before the caller's next await.
    Task started;
    using (ExecutionContext.SuppressFlow()) {
      started = Task.Run(work, cancellationToken);
    }
    return started;
  }
}
