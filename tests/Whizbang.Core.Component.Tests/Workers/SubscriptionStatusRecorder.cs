// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Records every <see cref="TransportConsumerWorker.SubscriptionStatusChanged"/> a worker raises and lets a test
/// wait on the exact status transition it is about to assert, instead of on something that merely correlates
/// with it (a subscribe call, a hooked disconnect handler). Attach it before <c>StartAsync</c> so no change is
/// missed.
/// </summary>
/// <remarks>
/// Each record also captures the state as the handler saw it at the moment the change was raised, which is how
/// the tests prove the worker raises the change only after the fields that accompany it are recorded.
/// </remarks>
internal sealed class SubscriptionStatusRecorder : IDisposable {
  private readonly TransportConsumerWorker _worker;
  private readonly Lock _gate = new();
  private readonly List<Observed> _changes = [];
  private readonly List<(Func<IReadOnlyList<Observed>, bool> IsSatisfied, TaskCompletionSource Signal)> _waiters = [];

  public SubscriptionStatusRecorder(TransportConsumerWorker worker) {
    _worker = worker;
    _worker.SubscriptionStatusChanged += _onChanged;
  }

  /// <summary>One raised change, plus the state's fields as they were when it was raised.</summary>
  internal sealed record Observed(
    object? Sender,
    TransportDestination Destination,
    SubscriptionStatus PreviousStatus,
    SubscriptionStatus Status,
    SubscriptionStatus StateStatusAtRaise,
    int AttemptCountAtRaise,
    Exception? LastErrorAtRaise,
    bool HadSubscriptionAtRaise);

  /// <summary>A snapshot of every change raised so far, in the order they were raised.</summary>
  public IReadOnlyList<Observed> Changes {
    get {
      lock (_gate) {
        return [.. _changes];
      }
    }
  }

  /// <summary>Completes once the recorded changes satisfy <paramref name="isSatisfied"/>.</summary>
  public Task WhenAsync(Func<IReadOnlyList<Observed>, bool> isSatisfied) {
    lock (_gate) {
      if (isSatisfied(_changes)) {
        return Task.CompletedTask;
      }
      var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _waiters.Add((isSatisfied, signal));
      return signal.Task;
    }
  }

  /// <summary>Completes once <paramref name="destinations"/> distinct destinations have changed to <paramref name="status"/>.</summary>
  public Task WhenDestinationsReachAsync(SubscriptionStatus status, int destinations) =>
    WhenAsync(changes => changes.Where(c => c.Status == status).Select(c => c.Destination).Distinct().Count() >= destinations);

  /// <summary>Completes once the recorded changes contain <paramref name="count"/> changes to <paramref name="status"/>.</summary>
  public Task WhenChangedToAsync(SubscriptionStatus status, int count = 1) =>
    WhenAsync(changes => changes.Count(c => c.Status == status) >= count);

  public void Dispose() => _worker.SubscriptionStatusChanged -= _onChanged;

  private void _onChanged(object? sender, SubscriptionStatusChangedEventArgs e) {
    var observed = new Observed(
      sender,
      e.Destination,
      e.PreviousStatus,
      e.Status,
      e.State.Status,
      e.State.AttemptCount,
      e.State.LastError,
      e.State.Subscription is not null);

    List<TaskCompletionSource> ready = [];
    lock (_gate) {
      _changes.Add(observed);
      for (var i = _waiters.Count - 1; i >= 0; i--) {
        if (_waiters[i].IsSatisfied(_changes)) {
          ready.Add(_waiters[i].Signal);
          _waiters.RemoveAt(i);
        }
      }
    }
    foreach (var signal in ready) {
      signal.TrySetResult();
    }
  }
}
