// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// Every live pause-state subscription receives every change made after it subscribed (#1847).
/// </summary>
/// <remarks>
/// <para>
/// The subscriptions used to share one channel reader, so they competed for changes: each change
/// reached whichever subscription read it first and no other. Each test here holds several
/// subscriptions on one clock and asserts what each of them saw, in order.
/// </para>
/// <para>
/// The sampler timer never fires (a one-day interval). Every change is made by the test: a sample
/// a second after the last one with no CPU consumed reads as paused, one 100ms later (under the
/// 200ms floor for freeze detection) reads as running. Every wait is on a signal raised by a
/// handler, never on elapsed time.
/// </para>
/// </remarks>
/// <docs>extending/features/debugger-aware-clock</docs>
[Category("Core")]
[Category("Diagnostics")]
public class DebuggerAwareClockPauseFanOutTests {

  // Several components watch the same clock: a lease extender, a sync awaiter, a test harness.
  // Each of them has to see every change. A subscriber that misses "paused" keeps counting down a
  // timeout during a breakpoint; one that misses "running" waits forever for a resume it never hears.
  [Test]
  [Timeout(30000)]
  public async Task OnPauseStateChanged_SeveralSubscribers_EachReceivesEveryChangeInOrderAsync(
      CancellationToken ct) {
    using var driver = new PauseDriver();
    var subscribers = new[] { new Recorder(4), new Recorder(4), new Recorder(4) };
    var subscriptions = subscribers.Select(s => driver.Clock.OnPauseStateChanged(s.Record)).ToList();

    driver.Change(paused: true);
    driver.Change(paused: false);
    driver.Change(paused: true);
    driver.Change(paused: false);
    foreach (var subscriber in subscribers) {
      await subscriber.AllReceived.WaitAsync(ct);
    }

    foreach (var subscriber in subscribers) {
      await Assert.That(subscriber.Sequence).IsEqualTo("True,False,True,False")
        .Because("every live subscription must receive every change, in the order it happened, "
          + "rather than compete with the others for it");
    }
    subscriptions.ForEach(s => s.Dispose());
  }

  // A subscription taken after some changes starts with the next one. Replaying earlier changes
  // would tell a late subscriber the clock paused when it has long since resumed.
  [Test]
  [Timeout(30000)]
  public async Task OnPauseStateChanged_SubscribedLater_ReceivesOnlyTheChangesAfterItAsync(
      CancellationToken ct) {
    using var driver = new PauseDriver();
    var early = new Recorder(3);
    var late = new Recorder(1);
    using var earlySubscription = driver.Clock.OnPauseStateChanged(early.Record);

    driver.Change(paused: true);
    driver.Change(paused: false);
    using var lateSubscription = driver.Clock.OnPauseStateChanged(late.Record);
    driver.Change(paused: true);
    await early.AllReceived.WaitAsync(ct);
    await late.AllReceived.WaitAsync(ct);

    await Assert.That(early.Sequence).IsEqualTo("True,False,True");
    await Assert.That(late.Sequence).IsEqualTo("True")
      .Because("a subscription receives the changes made after it subscribed and none before");
  }

  // Releasing one subscription must not take changes away from the others, and the released one
  // must hear nothing more, while the clock keeps changing state.
  [Test]
  [Timeout(30000)]
  public async Task OnPauseStateChanged_OneReleased_ItReceivesNothingMoreAndTheOthersReceiveEverythingAsync(
      CancellationToken ct) {
    using var driver = new PauseDriver();
    var released = new Recorder(1);
    var first = new Recorder(4);
    var second = new Recorder(4);
    var releasedSubscription = driver.Clock.OnPauseStateChanged(released.Record);
    using var firstSubscription = driver.Clock.OnPauseStateChanged(first.Record);
    using var secondSubscription = driver.Clock.OnPauseStateChanged(second.Record);

    // Control: the released subscription was live and received the change before its release.
    driver.Change(paused: true);
    await released.AllReceived.WaitAsync(ct);
    releasedSubscription.Dispose();
    driver.Change(paused: false);
    driver.Change(paused: true);
    driver.Change(paused: false);
    await first.AllReceived.WaitAsync(ct);
    await second.AllReceived.WaitAsync(ct);

    await Assert.That(released.Sequence).IsEqualTo("True")
      .Because("once Dispose returns the handler is not called again");
    await Assert.That(first.Sequence).IsEqualTo("True,False,True,False");
    await Assert.That(second.Sequence).IsEqualTo("True,False,True,False")
      .Because("releasing one subscription must leave every other one receiving every change");
  }

  // A handler that throws is a bug in the subscriber, not in the clock. It must not cost the other
  // subscribers a change, it must not end its own subscription (the next change still reaches
  // it), and the failure must be logged with the exception so the broken handler can be found.
  [Test]
  [Timeout(30000)]
  public async Task OnPauseStateChanged_AHandlerThrows_OthersStillReceiveEveryChangeAndTheFailureIsLoggedAsync(
      CancellationToken ct) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging(b => b.AddFakeLogging());
    services.AddWhizbang();
    var wall = DateTimeOffset.UnixEpoch;
    services.Configure<DebuggerAwareClockOptions>(o => {
      o.Mode = DebuggerDetectionMode.CpuTimeSampling;
      o.SamplingInterval = TimeSpan.FromDays(1);
      o.CpuTimeSource = () => TimeSpan.Zero;
      o.WallClockSource = () => wall;
    });
    await using var provider = services.BuildServiceProvider();
    var clock = (DebuggerAwareClock)provider.GetRequiredService<IDebuggerAwareClock>();
    void change(bool paused) {
      wall += paused ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(100);
      clock.SampleCpuTime(null);
    }

    var thrower = new Recorder(2);
    var boom = new InvalidOperationException("subscriber failed");
    using var throwingSubscription = clock.OnPauseStateChanged(paused => {
      thrower.Record(paused);
      throw boom;
    });
    var healthy = new Recorder(2);
    using var healthySubscription = clock.OnPauseStateChanged(healthy.Record);

    change(paused: true);
    change(paused: false);
    await thrower.AllReceived.WaitAsync(ct);
    await healthy.AllReceived.WaitAsync(ct);
    var failures = await _waitForFailureLogsAsync(provider.GetFakeLogCollector(), 2, ct);

    await Assert.That(healthy.Sequence).IsEqualTo("True,False")
      .Because("a throwing subscriber must not take a change away from any other subscriber");
    await Assert.That(thrower.Sequence).IsEqualTo("True,False")
      .Because("a handler that threw stays subscribed: the next change still reaches it");
    await Assert.That(failures.Count).IsEqualTo(2);
    await Assert.That(failures[0].Exception).IsSameReferenceAs(boom)
      .Because("the log has to carry the handler's own exception so the broken subscriber can be found");
    await Assert.That(failures[0].Level).IsEqualTo(LogLevel.Error);
    await Assert.That(failures[0].Message).Contains("paused: True");
    await Assert.That(failures[1].Message).Contains("paused: False");
  }

  // A handler that blocks (a debugger session, a slow consumer, a lock it waits on) holds up only
  // itself. The other subscribers keep receiving every change while it is stuck, and when it
  // resumes it receives what it missed, in order, up to its buffer of the ten most recent changes:
  // a subscriber that falls further behind loses the oldest, so the last change it hears is
  // always the clock's current state.
  [Test]
  [Timeout(30000)]
  public async Task OnPauseStateChanged_AHandlerBlocks_OthersKeepReceivingAndItCatchesUpOnTheLatestAsync(
      CancellationToken ct) {
    using var driver = new PauseDriver();
    using var unblock = new SemaphoreSlim(0, 1);
    var blockedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    // The first change is in flight, the second is dropped as the oldest when the twelfth
    // arrives, and the ten from the third to the twelfth are buffered: eleven calls in all.
    var blocked = new Recorder(11);
    using var blockedSubscription = driver.Clock.OnPauseStateChanged(paused => {
      blocked.Record(paused);
      if (blocked.Count == 1) {
        blockedEntered.TrySetResult();
        unblock.Wait(ct);
      }
    });
    var live = new Recorder(12);
    using var liveSubscription = driver.Clock.OnPauseStateChanged(live.Record);

    // The live subscriber is let drain between batches of at most ten, so its own buffer never
    // overflows and every change it receives is one the blocked subscriber was offered too.
    driver.Change(paused: true);
    await blockedEntered.Task.WaitAsync(ct);
    await live.Reached(1).WaitAsync(ct);
    for (var i = 1; i < 11; i++) {
      driver.Change(paused: i % 2 == 0);
    }
    await live.Reached(11).WaitAsync(ct);
    driver.Change(paused: false);
    await live.AllReceived.WaitAsync(ct);
    var blockedCallsWhileLiveFinished = blocked.Count;
    unblock.Release();
    await blocked.AllReceived.WaitAsync(ct);

    await Assert.That(live.Sequence)
      .IsEqualTo("True,False,True,False,True,False,True,False,True,False,True,False")
      .Because("a blocked subscriber must not hold back any other subscriber");
    await Assert.That(blockedCallsWhileLiveFinished).IsEqualTo(1)
      .Because("the blocked handler was still inside its first call when the others had received all twelve");
    await Assert.That(blocked.Sequence)
      .IsEqualTo("True,True,False,True,False,True,False,True,False,True,False")
      .Because("a subscriber that fell more than ten changes behind keeps the newest ten, in order, "
        + "and drops the oldest; its last change is the clock's current state");
  }

  private static async Task<IReadOnlyList<FakeLogRecord>> _waitForFailureLogsAsync(
      FakeLogCollector collector, int expected, CancellationToken ct) {
    // The handler's exception is logged by its read loop after the handler returns, so the last
    // record can land just after the recorder's signal. The collector's own stream is the signal
    // waited on here, not elapsed time.
    var failures = new List<FakeLogRecord>();
    await foreach (var record in collector.GetLogsAsync(ct)) {
      if (record.Category == typeof(DebuggerAwareClock).FullName) {
        failures.Add(record);
        if (failures.Count == expected) {
          break;
        }
      }
    }
    return failures;
  }

  /// <summary>A clock whose pause state changes only when the test says so.</summary>
  private sealed class PauseDriver : IDisposable {
    private DateTimeOffset _wall = DateTimeOffset.UnixEpoch;

    public PauseDriver() {
      Clock = new DebuggerAwareClock(new DebuggerAwareClockOptions {
        Mode = DebuggerDetectionMode.CpuTimeSampling,
        SamplingInterval = TimeSpan.FromDays(1),
        CpuTimeSource = () => TimeSpan.Zero,
        WallClockSource = () => _wall
      });
    }

    public DebuggerAwareClock Clock { get; }

    public void Change(bool paused) {
      _wall += paused ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(100);
      Clock.SampleCpuTime(null);
    }

    public void Dispose() => Clock.Dispose();
  }

  /// <summary>Records the states one handler received and signals when it has seen enough.</summary>
  private sealed class Recorder(int expected) {
    private readonly List<bool> _received = [];
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

    public Task AllReceived => Reached(expected);

    public int Count {
      get {
        lock (_received) {
          return _received.Count;
        }
      }
    }

    public string Sequence {
      get {
        lock (_received) {
          return string.Join(",", _received);
        }
      }
    }

    /// <summary>Completes once this handler has received <paramref name="count"/> states.</summary>
    public Task Reached(int count) {
      lock (_received) {
        if (_received.Count >= count) {
          return Task.CompletedTask;
        }
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add((count, reached));
        return reached.Task;
      }
    }

    public void Record(bool paused) {
      lock (_received) {
        _received.Add(paused);
        foreach (var waiter in _waiters.Where(w => w.Count == _received.Count)) {
          waiter.Reached.TrySetResult();
        }
      }
    }
  }
}
