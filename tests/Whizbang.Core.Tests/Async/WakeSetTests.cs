// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Async;
using Whizbang.Testing.Async;

namespace Whizbang.Core.Tests.Async;

/// <summary>
/// Locks the contract that makes <see cref="WakeSet"/> the one safe way for a loop to wait on several wake sources:
/// it says which source fired, keeps one pending wait per source across passes, runs one timer it stops when another
/// source wins, releases every wait it made when disposed, and parks an idle pass without allocating.
/// </summary>
/// <remarks>
/// Every wait is driven, never timed: the clock is a fake the test moves, or a manual clock whose timer the test fires,
/// and what is still pending is counted through the shipped tools (<see cref="CountingChannelReader{T}"/>,
/// <see cref="CountingTimeProvider"/>, <see cref="LoopWaitBudget"/>, <see cref="ReleaseCheck"/>).
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Async/WakeSet.cs</code-under-test>
[Category("Unit")]
public class WakeSetTests {
  private static readonly TimeSpan _poll = TimeSpan.FromSeconds(1);

  // ============== Which source fired ==============

  [Test]
  public async Task WaitAsync_AnItemOnOneChannel_WakesWithThatChannelOnlyAsync() {
    var work = Channel.CreateUnbounded<int>();
    var drain = Channel.CreateUnbounded<int>();
    using var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    var workSource = set.Add(work.Reader);
    var drainSource = set.Add(drain.Reader);

    var waiting = set.WaitAsync(_poll);
    var parked = !waiting.IsCompleted;
    work.Writer.TryWrite(1);
    var wake = await waiting;

    await Assert.That(parked).IsTrue();
    await Assert.That(wake.Reason).IsEqualTo(WakeReason.Source);
    await Assert.That(wake.Woke(workSource)).IsTrue();
    await Assert.That(wake.Woke(drainSource)).IsFalse();
    await Assert.That(wake.IsTimeout).IsFalse();
    await Assert.That(wake.IsStopped).IsFalse();
  }

  [Test]
  public async Task WaitAsync_SourcesAlreadyReady_ReturnsAtOnceWithEveryReadySourceAsync() {
    var work = Channel.CreateUnbounded<int>();
    var drain = Channel.CreateUnbounded<int>();
    using var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    var workSource = set.Add(work.Reader);
    var drainSource = set.Add(drain.Reader);
    work.Writer.TryWrite(1);
    drain.Writer.TryWrite(2);

    var waiting = set.WaitAsync(_poll);

    await Assert.That(waiting.IsCompleted).IsTrue();
    var wake = await waiting;
    await Assert.That(wake.Woke(workSource) && wake.Woke(drainSource)).IsTrue();
  }

  [Test]
  public async Task WaitAsync_AnItemLeftUnread_WakesTheNextPassTooAsync() {
    var work = Channel.CreateUnbounded<int>();
    using var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    var source = set.Add(work.Reader);
    work.Writer.TryWrite(1);

    var first = await set.WaitAsync(_poll);
    var second = await set.WaitAsync(_poll);

    await Assert.That(first.Woke(source) && second.Woke(source)).IsTrue()
      .Because("a source is a level, as with Task.WhenAny: an item nobody read is still there");
  }

  [Test]
  public async Task WaitAsync_NothingFires_EndsOnTheTimeoutOfTheInjectedClockAsync() {
    var clock = new FakeTimeProvider();
    using var set = new WakeSet(clock, CancellationToken.None);
    set.Add(Channel.CreateUnbounded<int>().Reader);

    var waiting = set.WaitAsync(_poll);
    clock.Advance(_poll - TimeSpan.FromTicks(1));
    var early = waiting.IsCompleted;
    clock.Advance(TimeSpan.FromTicks(1));
    var wake = await waiting;

    await Assert.That(early).IsFalse();
    await Assert.That(wake.IsTimeout).IsTrue();
    await Assert.That(wake.Ready).IsEqualTo(0UL);
  }

  [Test]
  public async Task WaitAsync_AZeroTimeout_ReturnsATimeoutWithoutParkingAsync() {
    using var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    set.Add(Channel.CreateUnbounded<int>().Reader);

    var waiting = set.WaitAsync(TimeSpan.Zero);

    await Assert.That(waiting.IsCompleted).IsTrue();
    await Assert.That((await waiting).IsTimeout).IsTrue();
  }

  [Test]
  public async Task WaitAsync_AnInfiniteTimeout_WaitsForASourceAsync() {
    var clock = new FakeTimeProvider();
    var work = Channel.CreateUnbounded<int>();
    using var set = new WakeSet(clock, CancellationToken.None);
    var source = set.Add(work.Reader);

    var waiting = set.WaitAsync(Timeout.InfiniteTimeSpan);
    clock.Advance(TimeSpan.FromDays(1));
    var afterADay = waiting.IsCompleted;
    work.Writer.TryWrite(1);

    await Assert.That(afterADay).IsFalse();
    await Assert.That((await waiting).Woke(source)).IsTrue();
  }

  [Test]
  public async Task WaitAsync_AChannelItsWriterCompleted_ReportsItCompletedAsync() {
    var work = Channel.CreateUnbounded<int>();
    using var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    var source = set.Add(work.Reader);

    var waiting = set.WaitAsync(_poll);
    var before = source.IsChannelCompleted;
    work.Writer.Complete();
    var wake = await waiting;

    await Assert.That(before).IsFalse();
    await Assert.That(wake.Woke(source)).IsTrue();
    await Assert.That(source.IsChannelCompleted).IsTrue();
  }

  // ============== Nothing left behind ==============

  [Test]
  public async Task IdlePasses_EndedByTheTimeout_KeepOneWaitOnTheChannelAndOneTimerAsync() {
    var fake = new FakeTimeProvider();
    var clock = new CountingTimeProvider(fake);
    var reader = new CountingChannelReader<int>(Channel.CreateUnbounded<int>().Reader);
    using var set = new WakeSet(clock, CancellationToken.None);
    set.Add(reader);

    var highest = await new LoopWaitBudget()
      .AtMost("channel waits", () => reader.OutstandingWaits, 1)
      .AtMost("timers", () => clock.LiveTimers, 1)
      .RunAsync(50, async _ => {
        var waiting = set.WaitAsync(_poll);
        fake.Advance(_poll);
        await waiting;
      });

    await Assert.That(highest["channel waits"]).IsEqualTo(1)
      .Because("the counter saw the one wait the set carries from pass to pass");
    await Assert.That(reader.WaitCalls).IsEqualTo(1)
      .Because("a wait nothing completed is kept, never asked for again");
    await Assert.That(clock.CreatedTimers).IsEqualTo(1)
      .Because("every pass re-arms the set's one timer; none makes a delay of its own");
  }

  [Test]
  public async Task Passes_WonByAChannel_StopTheTimerSoNoStaleTimeoutEndsALaterPassAsync() {
    var clock = new FakeTimeProvider();
    var work = Channel.CreateUnbounded<int>();
    using var set = new WakeSet(clock, CancellationToken.None);
    var source = set.Add(work.Reader);

    var first = set.WaitAsync(_poll);
    work.Writer.TryWrite(1);
    await Assert.That((await first).Woke(source)).IsTrue();
    work.Reader.TryRead(out _);

    var second = set.WaitAsync(Timeout.InfiniteTimeSpan);
    clock.Advance(_poll * 2);

    await Assert.That(second.IsCompleted).IsFalse()
      .Because("the first pass's timeout was stopped when the channel won, so it cannot end the second pass");
    work.Writer.TryWrite(2);
    await Assert.That((await second).Woke(source)).IsTrue();
  }

  [Test]
  public async Task Dispose_ReleasesEveryWaitTheSetMadeAsync() {
    var reader = new CountingChannelReader<int>(Channel.CreateUnbounded<int>().Reader);
    var signal = new WakeSignal();
    var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    set.Add(reader);
    set.Add(signal);

    var waiting = set.WaitAsync(_poll);
    var heldWhileWaiting = (reader.OutstandingWaits, signal.PendingWaiters);
    set.Dispose();
    var wake = await waiting;

    await Assert.That(heldWhileWaiting).IsEqualTo((1, 1));
    await Assert.That(wake.IsStopped).IsTrue()
      .Because("disposing the set wakes its pending wait as stopped");
    await Assert.That(reader.OutstandingWaits).IsEqualTo(0)
      .Because("the channel wait ran on the set's own token, which disposing cancels");
    await Assert.That(signal.PendingWaiters).IsEqualTo(0);
  }

  [Test]
  public async Task Dispose_ReleasesTheSetFromAGateThatNeverOpensAsync() {
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var releasedOnceDisposed = ReleaseCheck.IsReleased(() => _parkOnGate(gate.Task, dispose: true));
    var releasedWithoutDispose = ReleaseCheck.IsReleased(() => _parkOnGate(gate.Task, dispose: false));

    await Assert.That(releasedOnceDisposed).IsTrue()
      .Because("disposing cancels the set's wait on the gate, which takes it off the gate's continuations");
    await Assert.That(releasedWithoutDispose).IsFalse()
      .Because("the positive control: a set left parked is kept by the gate it waits on");
    GC.KeepAlive(gate);
  }

  // ============== Wake signals and gates ==============

  [Test]
  public async Task ASignal_WakesTheSet_AndIsConsumedOnEveryWakeAsync() {
    var clock = new FakeTimeProvider();
    var signal = new WakeSignal();
    using var set = new WakeSet(clock, CancellationToken.None);
    var source = set.Add(signal);

    var waiting = set.WaitAsync(_poll);
    signal.Set();
    var bySignal = await waiting;
    var raisedAfterSignalWake = signal.HasPendingSignal;

    signal.Set();
    var alreadyRaised = set.WaitAsync(_poll);
    var immediate = alreadyRaised.IsCompleted;
    await alreadyRaised;

    await Assert.That(bySignal.Woke(source)).IsTrue();
    await Assert.That(raisedAfterSignalWake).IsFalse()
      .Because("the set lowers the signal before the loop looks for work");
    await Assert.That(immediate).IsTrue()
      .Because("a signal raised between passes wakes the next pass at once");
  }

  [Test]
  public async Task ASignal_IsConsumedOnATimeoutToo_AndKeepsOneWaiterAcrossPassesAsync() {
    var clock = new FakeTimeProvider();
    var signal = new WakeSignal();
    using var set = new WakeSet(clock, CancellationToken.None);
    set.Add(signal);

    var highest = await new LoopWaitBudget()
      .AtMost("signal waiters", () => signal.PendingWaiters, 1)
      .RunAsync(20, async _ => {
        var waiting = set.WaitAsync(_poll);
        clock.Advance(_poll);
        await waiting;
      });

    await Assert.That(highest["signal waiters"]).IsEqualTo(1);
  }

  [Test]
  public async Task AGate_WakesTheSetOnceOpen_AndOnEveryPassAfterAsync() {
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    var source = set.Add(gate.Task);

    var waiting = set.WaitAsync(_poll);
    gate.SetResult();
    var opened = await waiting;
    var after = await set.WaitAsync(_poll);

    await Assert.That(opened.Woke(source) && after.Woke(source)).IsTrue();
  }

  [Test]
  public async Task AGateAlreadyOpen_IsReadyOnTheFirstPassAsync() {
    using var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    var source = set.Add(Task.CompletedTask);

    var wake = await set.WaitAsync(_poll);

    await Assert.That(wake.Woke(source)).IsTrue();
  }

  // ============== Stopping ==============

  [Test]
  public async Task TheStoppingToken_WakesAPendingWaitAsStopped_AndEveryWaitAfterAsync() {
    var signal = new WakeSignal();
    using var stopping = new CancellationTokenSource();
    using var set = new WakeSet(new FakeTimeProvider(), stopping.Token);
    set.Add(Channel.CreateUnbounded<int>().Reader);
    set.Add(signal);
    signal.Set();
    await set.WaitAsync(_poll);

    var waiting = set.WaitAsync(_poll);
    await stopping.CancelAsync();
    var stopped = await waiting;
    signal.Set();
    var after = await set.WaitAsync(_poll);

    await Assert.That(stopped.IsStopped).IsTrue();
    await Assert.That(after.IsStopped).IsTrue();
    await Assert.That(signal.HasPendingSignal).IsTrue()
      .Because("a stop is not a wake the loop acts on, so the signal is left raised");
  }

  [Test]
  public async Task AStoppingTokenAlreadyCanceled_StopsTheFirstWaitAsync() {
    using var set = new WakeSet(new FakeTimeProvider(), new CancellationToken(canceled: true));
    set.Add(Channel.CreateUnbounded<int>().Reader);

    var wake = await set.WaitAsync(_poll);

    await Assert.That(wake.IsStopped).IsTrue();
  }

  // ============== The timer ==============

  [Test]
  public async Task ATimerFire_EndsTheParkedPassAsATimeout_AndArmsAndStopsTheOneTimerAsync() {
    var clock = new ManualClock();
    var work = Channel.CreateUnbounded<int>();
    using var set = new WakeSet(clock, CancellationToken.None);
    var source = set.Add(work.Reader);

    var timedOut = set.WaitAsync(_poll);
    var armedFor = clock.Timer!.Due;
    clock.Timer.Fire();
    var byTimer = await timedOut;
    var wonByChannel = set.WaitAsync(_poll);
    work.Writer.TryWrite(1);
    var byChannel = await wonByChannel;

    await Assert.That(armedFor).IsEqualTo(_poll);
    await Assert.That(byTimer.IsTimeout).IsTrue();
    await Assert.That(byChannel.Woke(source)).IsTrue();
    await Assert.That(clock.Timer.Due).IsEqualTo(Timeout.InfiniteTimeSpan)
      .Because("the timer is stopped once the channel wins, so the pass leaves no timeout running");
  }

  [Test]
  public async Task ATimerFireWithNoPassParked_OrAnInfinitePass_DoesNothingAsync() {
    var clock = new ManualClock();
    var work = Channel.CreateUnbounded<int>();
    using var set = new WakeSet(clock, CancellationToken.None);
    var source = set.Add(work.Reader);

    clock.Timer!.Fire();
    var waiting = set.WaitAsync(Timeout.InfiniteTimeSpan);
    clock.Timer.Fire();
    var afterFire = waiting.IsCompleted;
    work.Writer.TryWrite(1);

    await Assert.That(afterFire).IsFalse();
    await Assert.That((await waiting).Woke(source)).IsTrue();
  }

  [Test]
  public async Task AnIdlePark_AllocatesNothingAsync() {
    var clock = new ManualClock();
    using var set = new WakeSet(clock, CancellationToken.None);
    set.Add(Channel.CreateUnbounded<int>().Reader);
    set.Add(new WakeSignal());
    for (var warm = 0; warm < 3; warm++) {
      var w = set.WaitAsync(_poll);
      clock.Timer!.Fire();
      await w;
    }

    var before = GC.GetAllocatedBytesForCurrentThread();
    var waiting = set.WaitAsync(_poll);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    clock.Timer!.Fire();
    await waiting;

    await Assert.That(allocated).IsEqualTo(0L)
      .Because("an idle pass reuses the pending waits, the timer and the awaitable");
  }

  // ============== Misuse ==============

  [Test]
  public async Task Misuse_IsRefusedAsync() {
    var clock = new FakeTimeProvider();
    var set = new WakeSet(clock, CancellationToken.None);
    set.Add(Channel.CreateUnbounded<int>().Reader);

    await Assert.That(() => new WakeSet(null!, CancellationToken.None)).Throws<ArgumentNullException>();
    await Assert.That(() => set.Add((ChannelReader<int>)null!)).Throws<ArgumentNullException>();
    await Assert.That(() => set.Add((WakeSignal)null!)).Throws<ArgumentNullException>();
    await Assert.That(() => set.Add((Task)null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await set.WaitAsync(TimeSpan.FromSeconds(-2))).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => default(Wake).Woke(null!)).Throws<ArgumentNullException>();

    var pending = set.WaitAsync(_poll);
    await Assert.That(async () => await set.WaitAsync(_poll)).Throws<InvalidOperationException>();
    await Assert.That(() => set.Add(new WakeSignal())).Throws<InvalidOperationException>();
    clock.Advance(_poll);
    await pending;

    set.Dispose();
    set.Dispose();
    await Assert.That(async () => await set.WaitAsync(_poll)).Throws<ObjectDisposedException>();
    await Assert.That(() => set.Add(new WakeSignal())).Throws<ObjectDisposedException>();
  }

  [Test]
  public async Task MoreThanSixtyFourSources_AreRefusedAsync() {
    using var set = new WakeSet(new FakeTimeProvider(), CancellationToken.None);
    for (var i = 0; i < 64; i++) {
      set.Add(new WakeSignal());
    }

    await Assert.That(() => set.Add(new WakeSignal())).Throws<InvalidOperationException>();
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static WakeSet _parkOnGate(Task gate, bool dispose) {
    var set = new WakeSet(TimeProvider.System, CancellationToken.None);
    set.Add(gate);
    // A zero timeout arms every source (here: the set's wait on the gate) and returns without parking, so nothing but
    // the gate can be what keeps the set; a parked wait's own completion would finish on the thread pool.
    if (!set.WaitAsync(TimeSpan.Zero).AsTask().IsCompletedSuccessfully) {
      throw new InvalidOperationException("a zero timeout returns at once");
    }
    if (dispose) {
      set.Dispose();
    }
    return set;
  }

  /// <summary>A clock whose one timer the test fires by hand, without moving time.</summary>
  private sealed class ManualClock : TimeProvider {
    public ManualTimer? Timer { get; private set; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
      Timer = new ManualTimer(callback, state, dueTime);
  }

  private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer {
    public TimeSpan Due { get; private set; } = due;

    public void Fire() => callback(state);

    public bool Change(TimeSpan dueTime, TimeSpan period) {
      Due = dueTime;
      return true;
    }

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}
