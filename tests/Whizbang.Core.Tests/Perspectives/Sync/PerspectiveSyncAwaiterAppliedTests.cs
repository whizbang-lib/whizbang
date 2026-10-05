// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Workers;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Waiting until a local perspective has applied an event, whoever published it (#959).
/// </summary>
/// <remarks>
/// Every step is driven, never timed: the fake ledger hands out one answer per read and signals each read,
/// and the awaiter's re-read backoff and timeout run on a <see cref="FakeTimeProvider"/> the test advances.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/Sync/PerspectiveSyncAwaiter.cs</code-under-test>
public class PerspectiveSyncAwaiterAppliedTests {
  private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
  private static string _name => TypeNameFormatter.GetPerspectiveName(typeof(OrderPerspective));

  private sealed class OrderPerspective;

  private static PerspectiveSyncAwaiter _awaiter(IWorkCoordinator coordinator, ISyncEventTracker tracker, TimeProvider time) =>
    new(coordinator,
      new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled }),
      NullLogger<PerspectiveSyncAwaiter>.Instance,
      tracker,
      new ScopedEventTracker(),
      new AsyncLocalLifecycleContextAccessor(),
      time);

  [Test]
  public async Task WaitForApplied_AnEventFromAnotherService_CompletesOnlyOnceTheLedgerRecordsItAsync() {
    var eventId = Guid.CreateVersion7();
    var ledger = new ScriptedLedger(
      new AppliedEventStatus(AppliedEventState.NotArrived, eventId),
      new AppliedEventStatus(AppliedEventState.Pending, eventId),
      new AppliedEventStatus(AppliedEventState.Applied, eventId));
    var time = new FakeTimeProvider();
    var awaiter = _awaiter(ledger, new SyncEventTracker(), time);

    var waiting = awaiter.WaitForAppliedAsync(typeof(OrderPerspective), eventId, _timeout);

    await ledger.WaitForReadAsync(1);
    await Assert.That(waiting.IsCompleted).IsFalse()
      .Because("the event has not reached this service yet, which must not read as synced");
    time.Advance(TimeSpan.FromMilliseconds(50));
    await ledger.WaitForReadAsync(2);
    await Assert.That(waiting.IsCompleted).IsFalse()
      .Because("the event is stored but this service's perspective has not applied it");
    time.Advance(TimeSpan.FromMilliseconds(100));

    var result = await waiting;
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
    await Assert.That(result.PerspectiveName).IsEqualTo(_name);
    await Assert.That(ledger.Inquiries[0]).IsEqualTo(new AppliedEventInquiry(_name, eventId));
  }

  [Test]
  public async Task WaitForApplied_ALocalApply_WakesTheWaiterWithoutTheLedgerAsync() {
    var eventId = Guid.CreateVersion7();
    var ledger = new ScriptedLedger(new AppliedEventStatus(AppliedEventState.Pending, eventId));
    var tracker = new SyncEventTracker();
    var awaiter = _awaiter(ledger, tracker, new FakeTimeProvider());

    var waiting = awaiter.WaitForAppliedAsync(typeof(OrderPerspective), eventId, _timeout);
    await ledger.WaitForReadAsync(1);
    tracker.MarkApplied([eventId], _name);

    var result = await waiting;
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("the worker in this process committed the apply; the ledger's flush can lag it");
  }

  [Test]
  public async Task WaitForApplied_ACollectiveApply_WakesAWaiterForAnyPerspectiveAsync() {
    var eventId = Guid.CreateVersion7();
    var ledger = new ScriptedLedger(new AppliedEventStatus(AppliedEventState.Pending, eventId));
    var tracker = new SyncEventTracker();
    var awaiter = _awaiter(ledger, tracker, new FakeTimeProvider());

    var waiting = awaiter.WaitForAppliedAsync(typeof(OrderPerspective), eventId, _timeout);
    await ledger.WaitForReadAsync(1);
    tracker.MarkApplied([eventId], CollectiveRouting.SINK_PERSPECTIVE_NAME);

    var result = await waiting;
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("a collective event is applied once, by the sink, to every model it targets");
  }

  [Test]
  public async Task WaitForApplied_NothingToApply_ReportsNoPendingEventsAsync() {
    var eventId = Guid.CreateVersion7();
    var ledger = new ScriptedLedger(new AppliedEventStatus(AppliedEventState.NotApplicable, eventId));
    var awaiter = _awaiter(ledger, new SyncEventTracker(), new FakeTimeProvider());

    var result = await awaiter.WaitForAppliedAsync(typeof(OrderPerspective), eventId, _timeout);

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  [Test]
  public async Task WaitForApplied_NeverApplied_TimesOutAtTheBoundAsync() {
    var eventId = Guid.CreateVersion7();
    var ledger = new ScriptedLedger(new AppliedEventStatus(AppliedEventState.Pending, eventId));
    var time = new FakeTimeProvider();
    var awaiter = _awaiter(ledger, new SyncEventTracker(), time);

    var waiting = awaiter.WaitForAppliedAsync(typeof(OrderPerspective), eventId, TimeSpan.FromSeconds(2));
    await ledger.WaitForReadAsync(1);
    time.Advance(TimeSpan.FromSeconds(2));

    var result = await waiting;
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }

  [Test]
  public async Task WaitForApplied_ReReadsOnAGrowingBackoff_CappedAtOneSecondAsync() {
    var eventId = Guid.CreateVersion7();
    var ledger = new ScriptedLedger(new AppliedEventStatus(AppliedEventState.Pending, eventId));
    var time = new FakeTimeProvider();
    var awaiter = _awaiter(ledger, new SyncEventTracker(), time);
    using var cts = new CancellationTokenSource();

    var waiting = awaiter.WaitForAppliedAsync(typeof(OrderPerspective), eventId, _timeout, cts.Token);
    await ledger.WaitForReadAsync(1);
    var reads = 1;
    foreach (var step in new[] { 50, 100, 200, 400, 800, 1000, 1000 }) {
      time.Advance(TimeSpan.FromMilliseconds(step - 1));
      await Assert.That(ledger.ReadCount).IsEqualTo(reads).Because($"the next read is due {step} ms after the last");
      time.Advance(TimeSpan.FromMilliseconds(1));
      await ledger.WaitForReadAsync(++reads);
    }

    await cts.CancelAsync();
    await Assert.That(async () => await waiting).Throws<OperationCanceledException>()
      .Because("the caller's own cancellation is not a timeout");
  }

  [Test]
  public async Task WaitForApplied_ByStreamAndPosition_ResolvesTheEventAndWakesOnItsLocalApplyAsync() {
    var streamId = Guid.CreateVersion7();
    var resolved = Guid.CreateVersion7();
    var ledger = new ScriptedLedger(
      new AppliedEventStatus(AppliedEventState.NotArrived, null),
      new AppliedEventStatus(AppliedEventState.Pending, resolved));
    var tracker = new SyncEventTracker();
    var time = new FakeTimeProvider();
    var awaiter = _awaiter(ledger, tracker, time);

    var waiting = awaiter.WaitForAppliedAsync(typeof(OrderPerspective), streamId, streamPosition: 3, _timeout);
    await ledger.WaitForReadAsync(1);
    time.Advance(TimeSpan.FromMilliseconds(50));
    await ledger.WaitForReadAsync(2);
    tracker.MarkApplied([resolved], _name);

    var result = await waiting;
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(ledger.Inquiries[0]).IsEqualTo(new AppliedEventInquiry(_name, null, streamId, 3));
  }

  [Test]
  public async Task WaitForApplied_WithACoordinatorThatCannotReadTheLedger_WaitsOnTheLocalApplyAsync() {
    var eventId = Guid.CreateVersion7();
    var tracker = new SyncEventTracker();
    var awaiter = _awaiter(new NoOpWorkCoordinator(), tracker, new FakeTimeProvider());

    var waiting = awaiter.WaitForAppliedAsync(typeof(OrderPerspective), eventId, _timeout);
    await Assert.That(waiting.IsCompleted).IsFalse();
    tracker.MarkApplied([eventId], _name);

    await Assert.That((await waiting).Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  [Test]
  public async Task WaitForApplied_ByPosition_WithACoordinatorThatCannotReadTheLedger_ThrowsAsync() {
    var awaiter = _awaiter(new NoOpWorkCoordinator(), new SyncEventTracker(), new FakeTimeProvider());

    await Assert.That(async () => await awaiter.WaitForAppliedAsync(typeof(OrderPerspective), Guid.CreateVersion7(), 1, _timeout))
      .Throws<NotSupportedException>()
      .Because("a position means nothing without the event store that assigned it");
  }

  [Test]
  public async Task WaitForApplied_WithInvalidArguments_ThrowsAsync() {
    var awaiter = _awaiter(new NoOpWorkCoordinator(), new SyncEventTracker(), new FakeTimeProvider());

    await Assert.That(async () => await awaiter.WaitForAppliedAsync(null!, Guid.CreateVersion7(), _timeout))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await awaiter.WaitForAppliedAsync(null!, Guid.CreateVersion7(), 1, _timeout))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await awaiter.WaitForAppliedAsync(typeof(OrderPerspective), Guid.CreateVersion7(), 0, _timeout))
      .Throws<ArgumentOutOfRangeException>()
      .Because("a stream's first event is at position 1");
  }

  // ── The existing explicit-id wait ([AwaitPerspectiveSync]) ─────────────────────────────────────────────

  [Test]
  public async Task WaitForStream_WithAnUntrackedEventId_WaitsForTheLedgerInsteadOfReportingSyncedAsync() {
    var eventId = Guid.CreateVersion7();
    var ledger = new ScriptedLedger(
      new AppliedEventStatus(AppliedEventState.Pending, eventId),
      new AppliedEventStatus(AppliedEventState.Applied, eventId));
    var time = new FakeTimeProvider();
    var awaiter = _awaiter(ledger, new SyncEventTracker(), time);

    var waiting = awaiter.WaitForStreamAsync(typeof(OrderPerspective), Guid.CreateVersion7(), null, _timeout, eventId);
    await ledger.WaitForReadAsync(1);
    await Assert.That(waiting.IsCompleted).IsFalse()
      .Because("an inbound event handled here was never tracked here; it used to read as synced at once");
    time.Advance(TimeSpan.FromMilliseconds(50));

    await Assert.That((await waiting).Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  [Test]
  public async Task WaitForStream_WithATrackedEventId_KeepsTheInProcessWaitAsync() {
    var eventId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var ledger = new ScriptedLedger(new AppliedEventStatus(AppliedEventState.Pending, eventId));
    var tracker = new SyncEventTracker();
    tracker.TrackEvent(typeof(object), eventId, streamId, _name);
    var awaiter = _awaiter(ledger, tracker, new FakeTimeProvider());

    var waiting = awaiter.WaitForStreamAsync(typeof(OrderPerspective), streamId, null, _timeout, eventId);
    tracker.MarkProcessedByPerspective([eventId], _name);

    await Assert.That((await waiting).Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(ledger.ReadCount).IsEqualTo(0)
      .Because("an event this process tracks is waited for exactly as before");
  }

  [Test]
  public async Task WaitForStream_WithAnUntrackedEventId_AndNoLedger_ReportsSyncedAsBeforeAsync() {
    var awaiter = _awaiter(new NoOpWorkCoordinator(), new SyncEventTracker(), new FakeTimeProvider());

    var result = await awaiter.WaitForStreamAsync(
      typeof(OrderPerspective), Guid.CreateVersion7(), null, _timeout, Guid.CreateVersion7());

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("a work coordinator that cannot read the ledger keeps the old behavior");
    await Assert.That(result.EventsTracked).IsEqualTo(1);
  }

  [Test]
  public async Task DefaultInterfaceMembers_ThrowNotSupportedAsync() {
    IPerspectiveSyncAwaiter awaiter = new MinimalAwaiter();

    await Assert.That(async () => await awaiter.WaitForAppliedAsync(typeof(OrderPerspective), Guid.CreateVersion7(), _timeout))
      .Throws<NotSupportedException>();
    await Assert.That(async () => await awaiter.WaitForAppliedAsync(typeof(OrderPerspective), Guid.CreateVersion7(), 1, _timeout))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task DefaultCoordinatorMember_CannotReadTheLedgerAsync() {
    var status = await ((IWorkCoordinator)new NoOpWorkCoordinator())
      .GetAppliedEventStatusAsync(new AppliedEventInquiry(_name, Guid.CreateVersion7()));

    await Assert.That(status).IsNull();
  }

  /// <summary>A ledger that answers each read with the next scripted status (the last repeats) and signals each read.</summary>
  private sealed class ScriptedLedger(params AppliedEventStatus[] answers) : NoOpWorkCoordinator, IWorkCoordinator {
    private readonly ConcurrentQueue<AppliedEventStatus> _answers = new(answers);
    private readonly Lock _gate = new();
    private TaskCompletionSource _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AppliedEventStatus _last = answers[^1];
    private int _readCount;

    public List<AppliedEventInquiry> Inquiries { get; } = [];

    public int ReadCount {
      get {
        lock (_gate) {
          return _readCount;
        }
      }
    }

    /// <summary>Completes once <paramref name="count"/> reads have happened: a signal per read, never a poll.</summary>
    public async Task WaitForReadAsync(int count) {
      while (true) {
        Task next;
        lock (_gate) {
          if (_readCount >= count) {
            return;
          }
          next = _read.Task;
        }
        await next.WaitAsync(TimeSpan.FromSeconds(10));
      }
    }

    ValueTask<AppliedEventStatus?> IWorkCoordinator.GetAppliedEventStatusAsync(
        AppliedEventInquiry inquiry, CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      lock (_gate) {
        Inquiries.Add(inquiry);
      }
      if (_answers.TryDequeue(out var next)) {
        _last = next;
      }
      TaskCompletionSource read;
      lock (_gate) {
        _readCount++;
        read = _read;
        _read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      }
      read.TrySetResult();
      return ValueTask.FromResult<AppliedEventStatus?>(_last);
    }
  }

  /// <summary>An <see cref="IPerspectiveSyncAwaiter"/> that implements only the members it must.</summary>
  private sealed class MinimalAwaiter : IPerspectiveSyncAwaiter {
    public Guid AwaiterId { get; } = Guid.CreateVersion7();
    public Task<SyncResult> WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      throw new InvalidOperationException();
    public Task<bool> IsCaughtUpAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      throw new InvalidOperationException();
    public Task<SyncResult> WaitForStreamAsync(Type perspectiveType, Guid streamId, Type[]? eventTypes, TimeSpan timeout,
        Guid? eventIdToAwait = null, CancellationToken ct = default) =>
      throw new InvalidOperationException();
  }
}
