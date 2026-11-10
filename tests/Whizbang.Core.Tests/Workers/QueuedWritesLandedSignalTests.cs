// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// One signal for "every queued work-table write has landed" (#1899): every flush worker idle at the same moment. Each
/// worker's own signal is not enough, because a flush can queue a write to another flusher (a handler commit that
/// fails queues a failure): reading the flushers one after another can see each idle at a different moment and never
/// all at once. The signal reads every flusher twice and settles only when no flusher was offered an item between the
/// two readings and each was idle at its first; then all of them were idle together.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/QueuedWritesLandedSignal.cs</code-under-test>
/// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
[Category("Workers")]
public sealed class QueuedWritesLandedSignalTests {
  /// <summary>A flusher whose readings and idle waits the test scripts.</summary>
  private sealed class ScriptedFlusher : IQueuedWriteFlusher {
    private readonly Queue<QueuedWriteActivity> _readings = new();
    private QueuedWriteActivity _last;
    private readonly Queue<Task> _waits = new();

    public ScriptedFlusher(params QueuedWriteActivity[] readings) {
      foreach (var reading in readings) {
        _readings.Enqueue(reading);
      }
      _last = readings.Length > 0 ? readings[^1] : default;
    }

    public int WaitCalls { get; private set; }

    public CancellationToken LastWaitToken { get; private set; }

    /// <summary>The idle wait the next call returns; a call with none scripted is already idle.</summary>
    public void NextWait(Task wait) => _waits.Enqueue(wait);

    public QueuedWriteActivity Activity => _readings.Count > 0 ? _readings.Dequeue() : _last;

    public bool IsIdle => _last.Pending == 0;

    public event WorkProcessingIdleHandler? OnWorkProcessingIdle { add { } remove { } }

    public Task WaitForIdleAsync(CancellationToken cancellationToken = default) {
      WaitCalls++;
      LastWaitToken = cancellationToken;
      return _waits.Count > 0 ? _waits.Dequeue() : Task.CompletedTask;
    }
  }

  private static QueuedWriteActivity _at(long version, long pending = 0) => new(version, pending);

  [Test]
  public async Task WithNoFlushers_EverythingHasLandedAsync() {
    var signal = new QueuedWritesLandedSignal([]);

    await Assert.That(signal.AllLanded).IsTrue();
    await Assert.That(signal.PendingWrites).IsEqualTo(0);
    await Assert.That(signal.Flushers).IsEmpty();
    await Assert.That(signal.WaitForAllLandedAsync(CancellationToken.None).IsCompleted).IsTrue();
  }

  [Test]
  public async Task AFlusherWithAWritePending_HasNotLandedAsync() {
    var signal = new QueuedWritesLandedSignal([new ScriptedFlusher(_at(3)), new ScriptedFlusher(_at(5, pending: 2))]);

    await Assert.That(signal.AllLanded).IsFalse();
  }

  [Test]
  public async Task AFlusherOfferedAnItemBetweenTheTwoReadings_HasNotLandedAsync() {
    // Idle at both readings, but offered an item and flushed it between them: another flusher read idle before that
    // flush may have been handed a write by it, so the readings do not show a moment when all were idle.
    var signal = new QueuedWritesLandedSignal([new ScriptedFlusher(_at(1), _at(2))]);

    await Assert.That(signal.AllLanded).IsFalse();
  }

  [Test]
  public async Task EveryFlusherIdleAndUnchangedAcrossBothReadings_HasLandedAsync() {
    var signal = new QueuedWritesLandedSignal([new ScriptedFlusher(_at(4), _at(4)), new ScriptedFlusher(_at(9), _at(9))]);

    await Assert.That(signal.AllLanded).IsTrue();
  }

  [Test]
  public async Task PendingWrites_SumsEveryFlusher_CountingAFlusherListedTwiceOnceAsync() {
    var shared = new ScriptedFlusher(_at(7, pending: 2));
    var signal = new QueuedWritesLandedSignal([shared, new ScriptedFlusher(_at(1, pending: 3)), shared]);

    await Assert.That(signal.PendingWrites).IsEqualTo(5);
    await Assert.That(signal.Flushers.Count).IsEqualTo(2);
  }

  [Test]
  public async Task TheFlushersAreReadOnFirstUse_NotWhenTheSignalIsBuilt_AndOnlyOnceAsync() {
    // The container builds the signal from the hosted services; reading them while the signal is constructed would
    // resolve every hosted service from inside a resolution, so the list is taken on first use.
    var enumerations = 0;
    IEnumerable<IQueuedWriteFlusher> flushers() {
      enumerations++;
      yield return new ScriptedFlusher(_at(1));
    }

    var signal = new QueuedWritesLandedSignal(flushers());
    var beforeUse = enumerations;
    _ = signal.AllLanded;
    _ = signal.PendingWrites;

    await Assert.That(beforeUse).IsEqualTo(0);
    await Assert.That(enumerations).IsEqualTo(1);
  }

  [Test]
  public async Task TheWait_WaitsForEveryFlusher_ThenReadsThemAllAtOnceAsync() {
    var outbox = new ScriptedFlusher(_at(2), _at(2));
    var failures = new ScriptedFlusher(_at(6), _at(6));
    var outboxIdle = new TaskCompletionSource();
    var failuresIdle = new TaskCompletionSource();
    outbox.NextWait(outboxIdle.Task);
    failures.NextWait(failuresIdle.Task);
    var signal = new QueuedWritesLandedSignal([outbox, failures]);
    using var cts = new CancellationTokenSource();

    var wait = signal.WaitForAllLandedAsync(cts.Token);
    var afterFirst = wait.IsCompleted;
    outboxIdle.SetResult();
    var afterOutbox = wait.IsCompleted;
    failuresIdle.SetResult();
    await wait;

    await Assert.That(afterFirst).IsFalse();
    await Assert.That(afterOutbox).IsFalse()
      .Because("the failure flusher still has a write in progress");
    await Assert.That(outbox.LastWaitToken).IsEqualTo(cts.Token);
    await Assert.That(failures.LastWaitToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task TheWait_WhenAFlusherWasOfferedMoreWhileItWaited_WaitsAgainAsync() {
    // The commit flusher went idle, but by the time the readings were taken it had been offered another item: the
    // wait goes round again instead of reporting a moment that never happened.
    var commits = new ScriptedFlusher(_at(1), _at(2), _at(2), _at(2));
    var secondIdle = new TaskCompletionSource();
    commits.NextWait(Task.CompletedTask);
    commits.NextWait(secondIdle.Task);
    var signal = new QueuedWritesLandedSignal([commits]);

    var wait = signal.WaitForAllLandedAsync(CancellationToken.None);
    var beforeSecondIdle = wait.IsCompleted;
    var waitsBeforeSecondIdle = commits.WaitCalls;
    secondIdle.SetResult();
    await wait;

    await Assert.That(beforeSecondIdle).IsFalse();
    await Assert.That(waitsBeforeSecondIdle).IsEqualTo(2);
    await Assert.That(commits.WaitCalls).IsEqualTo(2);
  }

  [Test]
  public async Task TheWait_IsEndedByItsTokenAsync() {
    var flusher = new ScriptedFlusher(_at(1, pending: 1));
    using var cts = new CancellationTokenSource();
    flusher.NextWait(Task.Delay(Timeout.Infinite, cts.Token));
    var signal = new QueuedWritesLandedSignal([flusher]);

    var wait = signal.WaitForAllLandedAsync(cts.Token);
    await cts.CancelAsync();

    await Assert.That(async () => await wait).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task NullFlushers_AreRefusedAsync() {
    await Assert.That(() => new QueuedWritesLandedSignal(null!)).Throws<ArgumentNullException>();
  }
}
