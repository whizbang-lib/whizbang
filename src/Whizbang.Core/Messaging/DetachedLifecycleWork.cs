// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Keeps track of the lifecycle receptor invocations a flush starts in the background (the <c>*Detached</c> stages), so
/// the host can wait for them while it stops, before it disposes the service provider they create their scopes from.
/// </summary>
/// <remarks>
/// A detached stage is fire-and-forget for the flush that starts it, but not for the host: one still running when the
/// container was disposed failed with <see cref="System.ObjectDisposedException"/> and logged an error for every
/// message it had not reached yet. The worker pipeline registers one instance and drains it once the batchers have
/// flushed at shutdown.
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DetachedLifecycleWorkTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchStrategyShutdownFlushTests.cs</tests>
public sealed class DetachedLifecycleWork {
  private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

  /// <summary>The number of detached invocations started and not yet finished.</summary>
  public int InFlightCount => _inFlight.Count;

  /// <summary>Records a detached invocation until it finishes, however it finishes.</summary>
  /// <param name="invocation">The background invocation.</param>
  public void Track(Task invocation) {
    _inFlight.TryAdd(invocation, 0);
    // Registered after the add, so an invocation that already finished is removed at once rather than kept forever.
    _ = invocation.ContinueWith(
      static (finished, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(finished, out _),
      _inFlight,
      CancellationToken.None,
      TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);
  }

  /// <summary>
  /// Completes once every tracked invocation has finished, including any started while it waited. An invocation's own
  /// failure or cancellation is its own to report, so neither is raised here.
  /// </summary>
  /// <param name="cancellationToken">Stops the wait; the invocations themselves keep running.</param>
  /// <exception cref="System.OperationCanceledException">The wait was canceled before every invocation finished.</exception>
  public async Task DrainAsync(CancellationToken cancellationToken) {
    while (!_inFlight.IsEmpty) {
      var pending = _inFlight.Keys;
      await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      cancellationToken.ThrowIfCancellationRequested();
      foreach (var finished in pending) {
        _inFlight.TryRemove(finished, out _);
      }
    }
  }
}
