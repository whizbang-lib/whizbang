// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// One repair build at a time (stream-integrity R1b): the gate every <see cref="RedeliveryRequestReceptor"/> build passes.
/// Redelivery requests arrive in bursts (per-bucket auto-repair and subscription-expansion broadcasts land together), and
/// each build holds a page of event bodies plus its serialized composite; unbounded concurrency multiplies that footprint
/// and has run origins out of memory under a first full audit. Requests queue here; inbox redelivery semantics make
/// waiting safe.
/// </summary>
/// <remarks>
/// <para>
/// The Postgres driver registers one per service container, so a service's builds share it. A receptor built where the
/// container holds none uses <see cref="Process"/>, the gate every such receptor in the process shares.
/// </para>
/// <para>
/// <see cref="Building"/>, <see cref="Queued"/> and <see cref="WhenQueuedAsync"/> say what the gate is doing: a request
/// counts as queued only while it waits for a build already in progress, never while it passes a free gate.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#one-repair-build-at-a-time</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBuildGateTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryRequestReceptorTests.cs:Receptor_ConcurrentRequests_BuildOneAtATimeAsync</tests>
public sealed class RedeliveryBuildGate {
  private readonly Lock _lock = new();
  private readonly LinkedList<TaskCompletionSource> _waiting = new();
  private readonly List<(int Count, TaskCompletionSource Signal)> _watchers = [];
  private bool _held;

  /// <summary>The gate shared by every receptor whose service container registers none.</summary>
  public static RedeliveryBuildGate Process { get; } = new();

  /// <summary>Whether a build holds the gate.</summary>
  public bool Building {
    get {
      lock (_lock) {
        return _held;
      }
    }
  }

  /// <summary>How many requests are waiting for the build in progress.</summary>
  public int Queued {
    get {
      lock (_lock) {
        return _waiting.Count;
      }
    }
  }

  /// <summary>
  /// Completes once at least <paramref name="count"/> requests are waiting for the build in progress: at once when that
  /// many already are.
  /// </summary>
  /// <param name="count">How many waiting requests to wait for; 1 or more.</param>
  /// <returns>A task that completes when that many requests are queued.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1.</exception>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryBuildGateTests.cs</tests>
  public Task WhenQueuedAsync(int count) {
    ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
    lock (_lock) {
      if (_waiting.Count >= count) {
        return Task.CompletedTask;
      }
      var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _watchers.Add((count, signal));
      return signal.Task;
    }
  }

  /// <summary>Takes the gate, or waits in line while a build holds it; a canceled wait leaves the line.</summary>
  internal async Task EnterAsync(CancellationToken cancellationToken) {
    TaskCompletionSource turn;
    lock (_lock) {
      if (!_held) {
        _held = true;
        return;
      }
      turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _waiting.AddLast(turn);
      foreach (var watcher in _watchers.Where(w => w.Count <= _waiting.Count).ToList()) {
        _watchers.Remove(watcher);
        watcher.Signal.TrySetResult();
      }
    }
    await using (cancellationToken.Register(() => {
      lock (_lock) {
        // Still in line: it leaves it. Already handed the gate: nothing is in line to remove, the turn keeps its result,
        // and the build goes on.
        _ = _waiting.Remove(turn);
        _ = turn.TrySetCanceled(cancellationToken);
      }
    }).ConfigureAwait(false)) {
      await turn.Task.ConfigureAwait(false);
    }
  }

  /// <summary>Ends the build in progress, handing the gate to the next request in line, or freeing it.</summary>
  internal void Exit() {
    lock (_lock) {
      if (_waiting.First is { } next) {
        _waiting.RemoveFirst();
        next.Value.TrySetResult();
        return;
      }
      _held = false;
    }
  }
}
