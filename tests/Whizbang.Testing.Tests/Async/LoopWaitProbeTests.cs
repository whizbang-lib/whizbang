// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Whizbang.Testing.Async;

namespace Whizbang.Testing.Tests.Async;

/// <summary>
/// The loop-wait verification tools: a channel reader and a clock that count what is still pending, a budget that fails
/// a loop leaving waits behind, and a collector check for what no seam can count.
/// </summary>
/// <remarks>
/// Every count is driven, never timed: delays are an hour long and end by a cancel the test makes, and each kept or
/// released case has its opposite under the same helpers as its control.
/// </remarks>
/// <code-under-test>src/Whizbang.Testing/Async/CountingChannelReader.cs</code-under-test>
/// <code-under-test>src/Whizbang.Testing/Async/CountingTimeProvider.cs</code-under-test>
/// <code-under-test>src/Whizbang.Testing/Async/LoopWaitBudget.cs</code-under-test>
/// <code-under-test>src/Whizbang.Testing/Async/ReleaseCheck.cs</code-under-test>
[NotInParallel("LoopWaitProbeTests")]
public class LoopWaitProbeTests {
  private static readonly TimeSpan _hour = TimeSpan.FromHours(1);

  // ============== CountingChannelReader ==============

  [Test]
  public async Task CountingReader_AnAbandonedWait_IsOutstandingUntilItsTokenIsCanceledAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var reader = new CountingChannelReader<int>(channel.Reader);
    using var stopping = new CancellationTokenSource();

    var first = _abandon(reader, stopping.Token);
    var second = _abandon(reader, stopping.Token);
    List<Task<bool>> abandoned = [first, second];
    var beforeCancel = reader.OutstandingWaits;
    await stopping.CancelAsync();

    await Assert.That(beforeCancel).IsEqualTo(2);
    await Assert.That(reader.OutstandingWaits).IsEqualTo(0);
    await Assert.That(reader.WaitCalls).IsEqualTo(2);
    await Assert.That(abandoned.All(w => w.IsCanceled)).IsTrue();
  }

  [Test]
  public async Task CountingReader_AWaitAnItemCompletes_IsNotOutstandingAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var reader = new CountingChannelReader<int>(channel.Reader);

    var wait = reader.WaitToReadAsync(CancellationToken.None);
    channel.Writer.TryWrite(7);

    await Assert.That(await wait).IsTrue();
    await Assert.That(reader.OutstandingWaits).IsEqualTo(0);
  }

  [Test]
  public async Task CountingReader_ReadsPeeksAndCounts_ThroughTheInnerReaderAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var reader = new CountingChannelReader<int>(channel.Reader);
    channel.Writer.TryWrite(1);
    channel.Writer.TryWrite(2);

    await Assert.That(reader.CanCount).IsTrue();
    await Assert.That(reader.Count).IsEqualTo(2);
    await Assert.That(reader.CanPeek).IsTrue();
    await Assert.That(reader.TryPeek(out var peeked)).IsTrue();
    await Assert.That(peeked).IsEqualTo(1);
    await Assert.That(reader.TryRead(out var read)).IsTrue();
    await Assert.That(read).IsEqualTo(1);
    await Assert.That(await reader.ReadAsync()).IsEqualTo(2);
    channel.Writer.Complete();
    await reader.Completion;
    await Assert.That(reader.Completion.IsCompleted).IsTrue();
  }

  [Test]
  public async Task CountingReader_OnWait_RunsAfterEachWaitWithItsNumberAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var numbers = new List<int>();
    CountingChannelReader<int>? reader = null;
    reader = new CountingChannelReader<int>(channel.Reader, n => numbers.Add((n * 10) + reader!.WaitCalls));
    using var stopping = new CancellationTokenSource();

    var first = _abandon(reader, stopping.Token);
    var second = _abandon(reader, stopping.Token);
    List<Task<bool>> abandoned = [first, second];
    await stopping.CancelAsync();

    await Assert.That(abandoned.Count).IsEqualTo(2);
    await Assert.That(numbers).IsEquivalentTo([11, 22])
      .Because("each wait is recorded before the callback runs, numbered from one");
  }

  [Test]
  public async Task CountingReader_WithoutAnInnerReader_ThrowsAsync() {
    await Assert.That(() => new CountingChannelReader<int>(null!)).Throws<ArgumentNullException>();
  }

  // ============== CountingTimeProvider ==============

  [Test]
  public async Task CountingClock_ADelay_IsLiveUntilItIsCanceledAsync() {
    var clock = new CountingTimeProvider(TimeProvider.System);
    using var cts = new CancellationTokenSource();

    var delay = Task.Delay(_hour, clock, cts.Token);
    var whileWaiting = clock.LiveTimers;
    await cts.CancelAsync();

    await Assert.That(async () => await delay).Throws<OperationCanceledException>();
    await Assert.That(whileWaiting).IsEqualTo(1);
    await Assert.That(clock.LiveTimers).IsEqualTo(0);
    await Assert.That(clock.CreatedTimers).IsEqualTo(1);
  }

  [Test]
  public async Task CountingClock_ADelayLeftToRun_StaysLiveAsync() {
    var clock = new CountingTimeProvider(TimeProvider.System);
    using var cts = new CancellationTokenSource();

    var delay = Task.Delay(_hour, clock, cts.Token);
    cts.Dispose();

    await Assert.That(clock.LiveTimers).IsEqualTo(1)
      .Because("disposing the token's source does not cancel the delay, which holds its timer until it fires");
    GC.KeepAlive(delay);
  }

  [Test]
  public async Task CountingClock_Created_CompletesOnceThatManyTimersExistAsync() {
    var clock = new CountingTimeProvider(TimeProvider.System);
    var one = clock.Created(1);
    var three = clock.Created(3);

    await using var first = clock.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    var oneAfterFirst = one.IsCompleted;
    var threeAfterFirst = three.IsCompleted;
    await using var second = clock.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    await using var third = clock.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    await three;

    await Assert.That(oneAfterFirst).IsTrue();
    await Assert.That(threeAfterFirst).IsFalse();
    await Assert.That(clock.Created(2).IsCompleted).IsTrue()
      .Because("a count already reached completes at once");
  }

  [Test]
  public async Task CountingClock_ATimer_ChangesAndDisposesThroughTheInnerTimer_CountedOnceAsync() {
    var clock = new CountingTimeProvider(TimeProvider.System);
    var timer = clock.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    await Assert.That(timer.Change(_hour, Timeout.InfiniteTimeSpan)).IsTrue();
    timer.Dispose();
    await timer.DisposeAsync();

    await Assert.That(clock.LiveTimers).IsEqualTo(0)
      .Because("a timer disposed twice is released once");
  }

  [Test]
  public async Task CountingClock_KeepsTheInnerClocksTimeAsync() {
    var inner = new FixedClock();
    var clock = new CountingTimeProvider(inner);

    await Assert.That(clock.Inner).IsSameReferenceAs(inner);
    await Assert.That(clock.GetUtcNow()).IsEqualTo(FixedClock.Now);
    await Assert.That(clock.GetTimestamp()).IsEqualTo(42L);
    await Assert.That(clock.TimestampFrequency).IsEqualTo(1000L);
    await Assert.That(clock.LocalTimeZone).IsEqualTo(TimeZoneInfo.Utc);
  }

  [Test]
  public async Task CountingClock_WithoutAnInnerClock_ThrowsAsync() {
    await Assert.That(() => new CountingTimeProvider(null!)).Throws<ArgumentNullException>();
  }

  // ============== LoopWaitBudget ==============

  [Test]
  public async Task Budget_ALoopThatReusesItsWait_StaysWithinItAndReportsTheHighestCountAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var reader = new CountingChannelReader<int>(channel.Reader);
    using var stopping = new CancellationTokenSource();
    Task<bool>? pending = null;

    var highest = await new LoopWaitBudget()
      .AtMost("channel waits", () => reader.OutstandingWaits, 1)
      .RunAsync(10, _ => {
        if (pending is not { IsCompleted: false }) {
          pending = reader.WaitToReadAsync(stopping.Token).AsTask();
        }
        return Task.CompletedTask;
      });
    await stopping.CancelAsync();

    await Assert.That(highest["channel waits"]).IsEqualTo(1)
      .Because("the counter saw the one wait the loop holds");
  }

  [Test]
  public async Task Budget_ALoopThatAsksForAFreshWaitEachPass_FailsOnTheFirstPassOverItAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var reader = new CountingChannelReader<int>(channel.Reader);
    using var stopping = new CancellationTokenSource();

    List<Task<bool>> abandoned = [];
    var budget = new LoopWaitBudget().AtMost("channel waits", () => reader.OutstandingWaits, 1);
    var failure = await Assert.That(async () => await budget.RunAsync(10, _ => {
      abandoned.Add(_abandon(reader, stopping.Token));
      return Task.CompletedTask;
    })).Throws<LoopWaitBudgetExceededException>();
    await stopping.CancelAsync();

    await Assert.That(failure!.Counter).IsEqualTo("channel waits");
    await Assert.That(failure.Pass).IsEqualTo(2);
    await Assert.That(failure.Count).IsEqualTo(2);
    await Assert.That(failure.AllowedAtMost).IsEqualTo(1);
    await Assert.That(failure.Message).Contains("after pass 2");
  }

  [Test]
  public async Task Budget_RejectsWhatCannotBeABudgetAsync() {
    var budget = new LoopWaitBudget();

    await Assert.That(() => budget.AtMost(null!, () => 0, 1)).Throws<ArgumentNullException>();
    await Assert.That(() => budget.AtMost("x", null!, 1)).Throws<ArgumentNullException>();
    await Assert.That(() => budget.AtMost("x", () => 0, -1)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(async () => await budget.RunAsync(0, _ => Task.CompletedTask)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(async () => await budget.RunAsync(1, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task BudgetExceeded_HasTheStandardConstructorsAsync() {
    var inner = new InvalidOperationException("inner");

    await Assert.That(new LoopWaitBudgetExceededException().Counter).IsNull();
    await Assert.That(new LoopWaitBudgetExceededException("m").Message).IsEqualTo("m");
    await Assert.That(new LoopWaitBudgetExceededException("m", inner).InnerException).IsSameReferenceAs(inner);
  }

  // ============== ReleaseCheck ==============

  [Test]
  public async Task ReleaseCheck_AWaitWhoseTokenIsCanceled_IsReleasedAsync() {
    var channel = _syncChannel();
    using var stopping = new CancellationTokenSource();

    var released = ReleaseCheck.IsReleased(() => {
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
      var wait = channel.Reader.WaitToReadAsync(linked.Token).AsTask();
      linked.Cancel();
      return wait;
    });

    await Assert.That(released).IsTrue();
    GC.KeepAlive(channel);
  }

  [Test]
  public async Task ReleaseCheck_AnAbandonedWait_IsKeptByTheChannelAsync() {
    var channel = _syncChannel();
    using var stopping = new CancellationTokenSource();

    var released = ReleaseCheck.IsReleased(() => channel.Reader.WaitToReadAsync(stopping.Token).AsTask());

    await Assert.That(released).IsFalse()
      .Because("the positive control: the same helpers and collection, and only the channel's hold differs");
    GC.KeepAlive(channel);
  }

  [Test]
  public async Task ReleaseCheck_AnAbandonedRegistrationOnALongLivedToken_IsKeptAsync() {
    using var stopping = new CancellationTokenSource();

    var released = ReleaseCheck.IsReleased(_registerAndAbandon(dispose: false, stopping.Token));
    var releasedOnceDisposed = ReleaseCheck.IsReleased(_registerAndAbandon(dispose: true, stopping.Token));

    await Assert.That(released).IsFalse()
      .Because("a registration not disposed keeps its state alive on the token for the token's life");
    await Assert.That(releasedOnceDisposed).IsTrue();
  }

  [Test]
  public async Task ReleaseCheck_WithoutAFactory_ThrowsAsync() {
    await Assert.That(() => ReleaseCheck.IsReleased(null!)).Throws<ArgumentNullException>();
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static Func<object> _registerAndAbandon(bool dispose, CancellationToken token) => () => {
    var state = new object();
    var registration = token.Register(static s => GC.KeepAlive(s), state);
    if (dispose) {
      registration.Dispose();
    }
    return state;
  };

  /// <summary>Asks for a wait and keeps only its task: the wait the caller abandons.</summary>
  private static Task<bool> _abandon(CountingChannelReader<int> reader, CancellationToken token) =>
    reader.WaitToReadAsync(token).AsTask();

  /// <summary>An unbounded channel that finishes a canceled wait inline, so its release is complete when the cancel returns.</summary>
  private static Channel<int> _syncChannel() =>
    Channel.CreateUnbounded<int>(new UnboundedChannelOptions { AllowSynchronousContinuations = true });

  private sealed class FixedClock : TimeProvider {
    public static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override long GetTimestamp() => 42;
    public override long TimestampFrequency => 1000;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
  }
}
