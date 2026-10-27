// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Keeps track of the background work the host must wait for while it stops, before it disposes the service provider that
/// work creates its scopes from: the lifecycle receptor invocations the framework starts in the background (the
/// <c>*Detached</c> stages and the tag handlers), and the producers that start more of it on a timer.
/// </summary>
/// <remarks>
/// <para>
/// A detached stage is fire-and-forget for the code that starts it, but not for the host: one still running when the
/// container was disposed failed with <see cref="System.ObjectDisposedException"/> and logged an error for every message
/// it had not reached yet. The container registers one instance (<c>TryAddWhizbangDefaults</c>), and the host's stop-time
/// flush first stops the registered producers and then drains it (#1721).
/// </para>
/// <para>
/// A component that owns its own stop (a hosted worker, a batcher) can also hold a private instance, track its own
/// background work in it, and drain it from that stop.
/// </para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-stages</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Messaging/DetachedLifecycleWorkTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchStrategyShutdownFlushTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Shutdown/DetachedStagesShutdownTests.cs</tests>
public sealed class DetachedLifecycleWork {
  private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
  private readonly ConcurrentDictionary<object, Func<CancellationToken, Task>> _producers = new();

  /// <summary>
  /// Raised when a drain starts waiting for invocations still running, with how many there are. A stopping host that
  /// raises it is holding off disposing the service provider until they finish.
  /// </summary>
  /// <docs>fundamentals/lifecycle/lifecycle-stages</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Messaging/DetachedLifecycleWorkTests.cs</tests>
  public event Action<int>? Waiting;

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
  /// Registers a producer of background work, such as a work coordinator strategy that flushes on a timer, to be stopped
  /// by <see cref="StopProducersAsync"/> before the drain. Stopping it must end what it starts on its own and flush what
  /// it holds while the service provider is still alive.
  /// </summary>
  /// <param name="stopAsync">Stops the producer and flushes what it holds.</param>
  /// <returns>Removes the registration; a producer disposed on its own unregisters so it is not stopped again.</returns>
  /// <docs>fundamentals/lifecycle/lifecycle-stages</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Messaging/DetachedLifecycleWorkTests.cs</tests>
  public IDisposable RegisterProducer(Func<CancellationToken, Task> stopAsync) {
    ArgumentNullException.ThrowIfNull(stopAsync);
    var registration = new Registration(this);
    _producers[registration] = stopAsync;
    return registration;
  }

  /// <summary>
  /// Stops every registered producer, one after the other. Each producer is stopped even when an earlier one fails; the
  /// failures are raised together once all have been stopped.
  /// </summary>
  /// <param name="cancellationToken">The host's stop deadline, handed to each producer.</param>
  /// <exception cref="AggregateException">One or more producers failed to stop.</exception>
  /// <docs>fundamentals/lifecycle/lifecycle-stages</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Messaging/DetachedLifecycleWorkTests.cs</tests>
  public async Task StopProducersAsync(CancellationToken cancellationToken) {
    List<Exception>? failures = null;
    foreach (var stopAsync in _producers.Values) {
      try {
        await stopAsync(cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        (failures ??= []).Add(ex);
      }
    }
    if (failures is not null) {
      throw new AggregateException("One or more producers of background work failed to stop.", failures);
    }
  }

  /// <summary>
  /// Completes once every tracked invocation has finished, including any started while it waited. An invocation's own
  /// failure or cancellation is its own to report, so neither is raised here.
  /// </summary>
  /// <param name="cancellationToken">Stops the wait; the invocations themselves keep running.</param>
  /// <exception cref="System.OperationCanceledException">The wait was canceled before every invocation finished.</exception>
  public async Task DrainAsync(CancellationToken cancellationToken) {
    var announced = false;
    while (!_inFlight.IsEmpty) {
      var pending = _inFlight.Keys;
      if (!announced) {
        announced = true;
        Waiting?.Invoke(pending.Count);
      }
      await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      cancellationToken.ThrowIfCancellationRequested();
      foreach (var finished in pending) {
        _inFlight.TryRemove(finished, out _);
      }
    }
  }

  private sealed class Registration(DetachedLifecycleWork owner) : IDisposable {
    public void Dispose() => owner._producers.TryRemove(this, out _);
  }
}
