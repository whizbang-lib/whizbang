using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The predecessor link on a keyed collective, at the receiver (#1003). A receiver applies a key's collectives in the
/// order it committed them, which is the order they arrived; the link restores the order they were sent in. A
/// collective whose predecessor this receiver handles but has not seen waits for it, for a bounded time, and the two
/// apply in order when it arrives.
/// </summary>
public partial class PerspectiveWorkerCollectiveSinkTests {
  private static readonly string _flipType = TypeNameFormatter.Format(typeof(FlipCollectiveEvent));

  private static MessageEnvelope<IEvent> _flip(Guid eventId, string chosen, Guid? predecessorId = null, string? predecessorType = null) =>
    _envelope(eventId, new FlipCollectiveEvent {
      Scope = new TenantCollectiveScope("t-1"),
      Chosen = chosen,
      PredecessorId = predecessorId,
      PredecessorType = predecessorType,
    });

  /// <summary>
  /// Both halves of a swap arrived, the second first: the receiver committed them backward. The link puts the
  /// predecessor first.
  /// </summary>
  [Test]
  public async Task CollectiveSink_Predecessor_LaterInTheRun_AppliesFirstAsync() {
    var streamId = TrackedGuid.New().Value;
    var second = _sinkWork(streamId);
    var first = _sinkWork(streamId);
    var firstId = Guid.CreateVersion7();
    var secondId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 2);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [second, first],
      eventStore: new EventStore { Deserialized = [_flip(secondId, "b", firstId, _flipType), _flip(firstId, "a")] },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(second.WorkId, secondId, CommitSequence: 1),
      new CollectiveSinkQueueEntry(first.WorkId, firstId, CommitSequence: 2),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.AllDispatched.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.EventWorkIdsCaptured(first.WorkId, second.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied).IsEquivalentTo(["a", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("The collective sent second applies second, though it was committed first here.");
    await Assert.That(dispatcher.State).IsEqualTo("b");
  }

  /// <summary>
  /// A collective applies at once when it waits for nothing this receiver will ever see: a predecessor of a type it
  /// does not handle, a link with no type, or a predecessor already in its store and applied.
  /// </summary>
  /// <param name="link">What the collective's link names.</param>
  [Test]
  [Arguments("unhandled-type")]
  [Arguments("no-type")]
  [Arguments("already-applied")]
  public async Task CollectiveSink_Predecessor_NothingToWaitFor_AppliesAtOnceAsync(string link) {
    var streamId = TrackedGuid.New().Value;
    var work = _sinkWork(streamId);
    var eventId = Guid.CreateVersion7();
    var predecessorId = Guid.CreateVersion7();
    var predecessorType = link switch {
      "unhandled-type" => "Some.Other.Collective, Other",
      "no-type" => null,
      _ => _flipType,
    };
    var dispatcher = new FlipDispatcher(expected: 1);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [work],
      eventStore: new EventStore { Deserialized = [_flip(eventId, "b", predecessorId, predecessorType)] },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);
    coordinator.SinkQueue = [new CollectiveSinkQueueEntry(work.WorkId, eventId, CommitSequence: 1)];
    if (link != "already-applied") {
      coordinator.MissingEventIds.Add(predecessorId);
    }

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.AllDispatched.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.EventWorkIdsCaptured(work.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied).IsEquivalentTo(["b"]);
  }

  /// <summary>With the wait turned off, a collective applies at once even though its predecessor has not arrived.</summary>
  [Test]
  public async Task CollectiveSink_Predecessor_WaitDisabled_AppliesAtOnceAsync() {
    var streamId = TrackedGuid.New().Value;
    var work = _sinkWork(streamId);
    var eventId = Guid.CreateVersion7();
    var predecessorId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 1);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [work],
      eventStore: new EventStore { Deserialized = [_flip(eventId, "b", predecessorId, _flipType)] },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher,
      predecessorWaitSeconds: 0);
    coordinator.SinkQueue = [new CollectiveSinkQueueEntry(work.WorkId, eventId, CommitSequence: 1)];
    coordinator.MissingEventIds.Add(predecessorId);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.AllDispatched.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied).IsEquivalentTo(["b"]);
  }

  /// <summary>
  /// A collective whose predecessor has not arrived waits, and the collective ahead of it in the run is complete; when
  /// the predecessor arrives the two apply in the order they were sent.
  /// </summary>
  [Test]
  public async Task CollectiveSink_Predecessor_NotYetArrived_WaitsThenAppliesBothInOrderAsync() {
    var clock = new HoldClock();
    var streamId = TrackedGuid.New().Value;
    var unlinked = _sinkWork(streamId);
    var waiting = _sinkWork(streamId);
    var predecessor = _sinkWork(streamId);
    var unlinkedId = Guid.CreateVersion7();
    var waitingId = Guid.CreateVersion7();
    var predecessorId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 3);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [unlinked, waiting],
      eventStore: new EventStore {
        Deserialized = [_flip(unlinkedId, "x"), _flip(waitingId, "b", predecessorId, _flipType), _flip(predecessorId, "a")],
      },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher,
      timeProvider: clock);
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(unlinked.WorkId, unlinkedId, CommitSequence: 1),
      new CollectiveSinkQueueEntry(waiting.WorkId, waitingId, CommitSequence: 2),
    ];
    coordinator.MissingEventIds.Add(predecessorId);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await clock.HoldArmed(1).WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.EventWorkIdsCaptured(unlinked.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    List<string> appliedWhileWaiting;
    lock (dispatcher.Applied) {
      appliedWhileWaiting = [.. dispatcher.Applied];
    }
    var completedWhileWaiting = harness.CompletionCapture.EventWorkIds.ToList();
    await Assert.That(worker.PendingCollectiveHoldWakes).IsEqualTo(1);

    // The predecessor arrives: committed after the collective waiting for it, and offered on its own.
    lock (coordinator.MissingEventIds) {
      coordinator.MissingEventIds.Remove(predecessorId);
    }
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(waiting.WorkId, waitingId, CommitSequence: 2),
      new CollectiveSinkQueueEntry(predecessor.WorkId, predecessorId, CommitSequence: 3),
    ];
    coordinator.OfferWork([predecessor]);
    await dispatcher.AllDispatched.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.EventWorkIdsCaptured(waiting.WorkId, predecessor.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    var wakesAfterBothApplied = worker.PendingCollectiveHoldWakes;
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* teardown */ }

    await Assert.That(appliedWhileWaiting).IsEquivalentTo(["x"])
      .Because("The collective ahead of the waiting one has nothing to wait for and is complete.");
    await Assert.That(completedWhileWaiting).DoesNotContain(waiting.WorkId);
    await Assert.That(dispatcher.Applied).IsEquivalentTo(["x", "a", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("The predecessor applies first, though it was committed after the collective waiting for it.");
    await Assert.That(wakesAfterBothApplied).IsEqualTo(0)
      .Because("Once nothing on the stream waits, the wake armed for the hold is disposed.");
  }

  /// <summary>
  /// A collective whose predecessor is queued on the stream but not in this run waits for the run that holds it,
  /// rather than applying ahead of it.
  /// </summary>
  [Test]
  public async Task CollectiveSink_Predecessor_QueuedButNotInThisRun_WaitsAsync() {
    var clock = new HoldClock();
    var streamId = TrackedGuid.New().Value;
    var waiting = _sinkWork(streamId);
    var waitingId = Guid.CreateVersion7();
    var predecessorId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 1);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [waiting],
      eventStore: new EventStore { Deserialized = [_flip(waitingId, "b", predecessorId, _flipType)] },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher,
      timeProvider: clock);
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(waiting.WorkId, waitingId, CommitSequence: 1),
      new CollectiveSinkQueueEntry(Guid.CreateVersion7(), predecessorId, CommitSequence: 2),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await clock.HoldArmed(1).WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* teardown */ }

    await Assert.That(dispatcher.Applied).IsEmpty();
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(waiting.WorkId);
    await Assert.That(worker.PendingCollectiveHoldWakes).IsEqualTo(0)
      .Because("Stopping the worker disposes its pending wakes.");
  }

  /// <summary>
  /// A predecessor that never arrives holds a collective only for the configured wait: the wake re-offers it, and it
  /// applies with a warning and a count. A second run inside the wait keeps waiting and moves the wake, not the
  /// wait's start.
  /// </summary>
  /// <param name="withMetrics">Whether the host registers the composite meters.</param>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task CollectiveSink_Predecessor_NeverArrives_AppliesWhenTheWaitRunsOutAsync(bool withMetrics) {
    var clock = new HoldClock();
    var streamId = TrackedGuid.New().Value;
    var waiting = _sinkWork(streamId);
    var waitingId = Guid.CreateVersion7();
    var predecessorId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 1);
    using var factory = new Whizbang.Core.Tests.Observability.TestMeterFactory();
    var metrics = withMetrics ? new CompositeMetrics(new WhizbangMetrics(factory)) : null;

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [waiting],
      eventStore: new EventStore { Deserialized = [_flip(waitingId, "b", predecessorId, _flipType)] },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher,
      compositeMetrics: metrics,
      timeProvider: clock,
      predecessorWaitSeconds: 30);
    coordinator.SinkQueue = [new CollectiveSinkQueueEntry(waiting.WorkId, waitingId, CommitSequence: 1)];
    coordinator.MissingEventIds.Add(predecessorId);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await clock.HoldArmed(1).WaitAsync(TimeSpan.FromSeconds(10));
    clock.Advance(TimeSpan.FromSeconds(10));
    coordinator.OfferWork([waiting]);
    await clock.HoldArmed(2).WaitAsync(TimeSpan.FromSeconds(10));
    var secondWait = clock.ArmedFor[1];
    clock.Advance(TimeSpan.FromSeconds(20));
    await dispatcher.AllDispatched.WaitAsync(TimeSpan.FromSeconds(10));
    await harness.CompletionCapture.EventWorkIdsCaptured(waiting.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(secondWait).IsEqualTo(TimeSpan.FromSeconds(20))
      .Because("The wait is counted from when the collective first started waiting.");
    await Assert.That(dispatcher.Applied).IsEquivalentTo(["b"]);
    if (withMetrics) {
      var meter = factory.CreatedMeters.Single(m => m.Name == CompositeMetrics.METER_NAME);
      await Assert.That(Whizbang.Core.Tests.Observability.ProbeMeterReader.ReadTotal(meter, "whizbang.collectives.predecessor_timed_out")).IsEqualTo(1);
    }
  }

  /// <summary>
  /// A fake clock that reports each time a collective's predecessor wake is armed, and for how long: the deterministic
  /// signal that a sink run has decided to wait.
  /// </summary>
  private sealed class HoldClock : FakeTimeProvider {
    private readonly Lock _lock = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];

    public HoldClock() : base(DateTimeOffset.UtcNow) {
    }

    /// <summary>The wait of every wake armed so far, in order.</summary>
    public List<TimeSpan> ArmedFor { get; } = [];

    /// <summary>Completes once <paramref name="count"/> wakes have been armed.</summary>
    public Task HoldArmed(int count) {
      lock (_lock) {
        if (ArmedFor.Count >= count) {
          return Task.CompletedTask;
        }
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add((count, signal));
        return signal.Task;
      }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
      dueTime == Timeout.InfiniteTimeSpan && period == Timeout.InfiniteTimeSpan
        ? new ArmingTimer(base.CreateTimer(callback, state, dueTime, period), this)
        : base.CreateTimer(callback, state, dueTime, period);

    private void _armed(TimeSpan dueTime) {
      lock (_lock) {
        ArmedFor.Add(dueTime);
        foreach (var (_, signal) in _waiters.Where(w => ArmedFor.Count >= w.Count).ToList()) {
          signal.TrySetResult();
        }
      }
    }

    private sealed class ArmingTimer(ITimer inner, HoldClock clock) : ITimer {
      public bool Change(TimeSpan dueTime, TimeSpan period) {
        var changed = inner.Change(dueTime, period);
        if (dueTime != Timeout.InfiniteTimeSpan) {
          clock._armed(dueTime);
        }
        return changed;
      }

      public void Dispose() => inner.Dispose();
      public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
  }
}
