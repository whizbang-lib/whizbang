// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The keepalive that holds a duty's lease through one long piece of exclusive work (#2127): it verifies the grant at
/// half its renew interval for as long as it runs, never twice at once, and not at all once disposed.
/// </summary>
/// <remarks>
/// Driven by a fake clock and a grant that reports each verify on a channel, so every assertion waits on the verify it
/// expects rather than on time passing. A tick fires inside <see cref="FakeTimeProvider.Advance"/>, and each tick is
/// exactly one verify (run at once, or right after the one in flight), awaited through the channel.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Startup/DutyGrantKeepalive.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
[Category("Startup")]
public class DutyGrantKeepaliveTests {
  private static readonly TimeSpan _interval = TimeSpan.FromSeconds(10);
  private static readonly TimeSpan _tick = TimeSpan.FromSeconds(5);

  /// <summary>A grant with a lease, which reports each verify and answers as it is told.</summary>
  private sealed class LeasedGrant(TimeSpan? renewInterval) : IDutyGrant {
    private readonly Channel<int> _verifies = Channel.CreateUnbounded<int>();
    private int _count;

    public string Duty => StartupDuties.MIGRATOR;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public TimeSpan? RenewInterval => renewInterval;

    /// <summary>When set, each verify waits for it before answering.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, each verify throws it.</summary>
    public Exception? Throws { get; set; }

    public bool Answer { get; set; } = true;
    public int Count => Volatile.Read(ref _count);

    public async Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) {
      var n = Interlocked.Increment(ref _count);
      _verifies.Writer.TryWrite(n);
      if (Gate is { } gate) {
        await gate.Task;
      }
      if (Throws is { } ex) {
        throw ex;
      }
      return Answer;
    }

    public ValueTask<int> NextVerifyAsync(CancellationToken cancellationToken) => _verifies.Reader.ReadAsync(cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class GrantWithoutALease : IDutyGrant {
    public string Duty => StartupDuties.MAINTAINER;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class RecordingLogger : ILogger {
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (Entries) {
        Entries.Add((logLevel, formatter(state, exception), exception));
      }
    }
  }

  [Test]
  public async Task RenewInterval_OfAGrantWithoutALease_IsNullAsync() {
    IDutyGrant grant = new GrantWithoutALease();

    await Assert.That(grant.RenewInterval).IsNull();
  }

  [Test]
  [Timeout(30000)]
  public async Task Start_OverALeasedGrant_VerifiesItAtHalfItsRenewInterval_ForAsLongAsItRunsAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var grant = new LeasedGrant(_interval);
    await using var keepalive = DutyGrantKeepalive.Start(grant, time);

    await Assert.That(keepalive.IsRunning).IsTrue();
    await Assert.That(keepalive.TickInterval).IsEqualTo(_tick);

    time.Advance(_tick);
    await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(1);
    time.Advance(_tick);
    await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(2);
    time.Advance(_tick);
    await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(3)
      .Because("three ticks are three verifies: the work the grant is held for can run as long as it needs");
  }

  [Test]
  public async Task Start_BeforeTheFirstTick_VerifiesNothingAsync() {
    var time = new FakeTimeProvider();
    var grant = new LeasedGrant(_interval);
    await using var keepalive = DutyGrantKeepalive.Start(grant, time);

    time.Advance(_tick - TimeSpan.FromTicks(1));

    await Assert.That(grant.Count).IsEqualTo(0)
      .Because("the grant was renewed when it was won; the first renewal is owed a tick later");
  }

  [Test]
  public async Task Start_WithNoGrant_KeepsNothingAliveAsync() {
    await using var keepalive = DutyGrantKeepalive.Start(null, new FakeTimeProvider());

    await Assert.That(keepalive.IsRunning).IsFalse();
    await Assert.That(keepalive.TickInterval).IsNull();
  }

  [Test]
  public async Task Start_OverAGrantWithoutALease_KeepsNothingAliveAsync() {
    var time = new FakeTimeProvider();
    await using var keepalive = DutyGrantKeepalive.Start(new GrantWithoutALease(), time);

    time.Advance(TimeSpan.FromHours(1));

    await Assert.That(keepalive.IsRunning).IsFalse();
    await Assert.That(keepalive.TickInterval).IsNull();
  }

  [Test]
  public async Task Start_WithNoClock_IsRefusedAsync() {
    await Assert.That(() => DutyGrantKeepalive.Start(new LeasedGrant(_interval), null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  [Timeout(30000)]
  public async Task Dispose_StopsTheTicks_SoNothingRenewsTheGrantAfterItAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var grant = new LeasedGrant(_interval);
    var keepalive = DutyGrantKeepalive.Start(grant, time);
    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);

    await keepalive.DisposeAsync();
    time.Advance(TimeSpan.FromHours(1));

    await Assert.That(keepalive.IsRunning).IsFalse();
    await Assert.That(grant.Count).IsEqualTo(1)
      .Because("a grant released after the keepalive stopped must not be renewed by it");
  }

  [Test]
  public async Task ATickThatArrivesAfterTheStop_VerifiesNothingAsync() {
    var grant = new LeasedGrant(_interval);
    var keepalive = DutyGrantKeepalive.Start(grant, new FakeTimeProvider());
    await keepalive.DisposeAsync();

    keepalive.Tick();

    await Assert.That(grant.Count).IsEqualTo(0)
      .Because("a timer callback already on its way when the timer was disposed must not renew a released grant");
  }

  [Test]
  public async Task Dispose_Twice_IsHarmlessAsync() {
    var keepalive = DutyGrantKeepalive.Start(new LeasedGrant(_interval), new FakeTimeProvider());

    await keepalive.DisposeAsync();
    await keepalive.DisposeAsync();

    await Assert.That(keepalive.IsRunning).IsFalse();
  }

  [Test]
  [Timeout(30000)]
  public async Task Dispose_WaitsForTheVerifyInFlight_BeforeItReturnsAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var grant = new LeasedGrant(_interval) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
    var keepalive = DutyGrantKeepalive.Start(grant, time);
    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);
    time.Advance(_tick);

    var disposing = keepalive.DisposeAsync().AsTask();

    await Assert.That(disposing.IsCompleted).IsFalse()
      .Because("the owner releases the grant once this returns, so no renewal may still be on its way");
    grant.Gate.SetResult();
    await disposing;
    await Assert.That(keepalive.IsRunning).IsFalse();
    await Assert.That(grant.Count).IsEqualTo(1)
      .Because("the verify a tick was owed is not run once the keepalive is stopping");
  }

  [Test]
  [Timeout(30000)]
  public async Task TicksWhileTheLastVerifyStillRuns_AreCoalescedIntoOneVerifyAfterItAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var grant = new LeasedGrant(_interval) { Gate = gate };
    await using var keepalive = DutyGrantKeepalive.Start(grant, time);
    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);

    time.Advance(_tick);
    time.Advance(_tick);
    await Assert.That(grant.Count).IsEqualTo(1)
      .Because("a slow database is asked once at a time; the ticks that found it still answering wait for it");

    grant.Gate = null;
    gate.SetResult();
    await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(2)
      .Because("the ticks it missed are owed one verify, run as soon as the slow one answers, so no renewal is lost");

    time.Advance(_tick);
    await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(3)
      .Because("two missed ticks were one verify, not two: the renewals never queue up behind a slow database");
  }

  [Test]
  [Timeout(30000)]
  public async Task AVerifyThatAnswersFalse_DoesNotStopTheKeepaliveAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var grant = new LeasedGrant(_interval) { Answer = false };
    await using var keepalive = DutyGrantKeepalive.Start(grant, time);
    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);

    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);

    await Assert.That(keepalive.IsRunning).IsTrue()
      .Because("false may be a database the grant could not reach; the lease may still stand and the next tick asks again");
    await Assert.That(grant.Count).IsEqualTo(2);
  }

  [Test]
  [Timeout(30000)]
  public async Task AVerifyThatThrows_IsReported_AndTheNextTickAsksAgainAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var logger = new RecordingLogger();
    var failure = new InvalidOperationException("renewal statement failed");
    var grant = new LeasedGrant(_interval) { Throws = failure };
    await using var keepalive = DutyGrantKeepalive.Start(grant, time, logger);
    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);

    grant.Throws = null;
    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);

    (LogLevel Level, string Message, Exception? Exception)[] entries;
    lock (logger.Entries) {
      entries = [.. logger.Entries];
    }
    await Assert.That(entries).Count().IsEqualTo(1);
    await Assert.That(entries[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(entries[0].Exception).IsSameReferenceAs(failure);
    await Assert.That(entries[0].Message).Contains("migrator");
    await Assert.That(grant.Count).IsEqualTo(2);
  }

  [Test]
  [Timeout(30000)]
  public async Task AVerifyCanceledByAnythingButTheDispose_IsReportedAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var logger = new RecordingLogger();
    var grant = new LeasedGrant(_interval) { Throws = new OperationCanceledException("the database canceled the statement") };
    await using var keepalive = DutyGrantKeepalive.Start(grant, time, logger);

    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);

    await Assert.That(logger.Entries).Count().IsEqualTo(1)
      .Because("a statement canceled while the keepalive runs is a renewal that did not happen");
  }

  [Test]
  [Timeout(30000)]
  public async Task AVerifyCanceledByTheDispose_IsNotReportedAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var logger = new RecordingLogger();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var grant = new LeasedGrant(_interval) { Gate = gate, Throws = new OperationCanceledException() };
    var keepalive = DutyGrantKeepalive.Start(grant, time, logger);
    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);

    var disposing = keepalive.DisposeAsync().AsTask();
    gate.SetResult();
    await disposing;

    await Assert.That(logger.Entries).IsEmpty()
      .Because("a verify the dispose canceled is the keepalive stopping, not a failure");
  }
  // ── #2165: a stuck holder's role lapses ─────────────────────────────

  private static readonly TimeSpan _stallLimit = TimeSpan.FromSeconds(12);

  [Test]
  [Timeout(30000)]
  public async Task AHolderThatReportsNoProgress_StopsBeingRenewed_PastTheStallLimit_AndSaysSoOnceAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var logger = new RecordingLogger();
    var grant = new LeasedGrant(_interval);
    await using var keepalive = DutyGrantKeepalive.Start(grant, time, logger, _stallLimit);

    time.Advance(_tick);
    await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(1);
    time.Advance(_tick);
    await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(2);
    time.Advance(_tick);   // 15 seconds without progress: past the 12-second limit
    time.Advance(TimeSpan.FromHours(1));

    await Assert.That(grant.Count).IsEqualTo(2)
      .Because("a holder that is alive but stuck must let its lease lapse, so another instance takes the role over");
    await Assert.That(keepalive.IsRunning).IsFalse();
    await Assert.That(keepalive.IsStalled).IsTrue();
    await Assert.That(logger.Entries).Count().IsEqualTo(1).Because("said once, not once per tick");
    await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(logger.Entries[0].Message).Contains(StartupDuties.MIGRATOR);
    await Assert.That(logger.Entries[0].Message).Contains("00:00:12");
  }

  [Test]
  [Timeout(30000)]
  public async Task AHolderThatReportsProgress_IsRenewedPastTheStallLimit_ForAsLongAsItDoesAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var grant = new LeasedGrant(_interval);
    await using var keepalive = DutyGrantKeepalive.Start(grant, time, stallLimit: _stallLimit);

    for (var verify = 1; verify <= 8; verify++) {
      time.Advance(_tick);
      await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(verify);
      keepalive.NoteProgress();
    }

    await Assert.That(keepalive.IsStalled).IsFalse()
      .Because("40 seconds of work, each tick of it progress: a long pass that is working keeps its role");
  }

  [Test]
  [Timeout(30000)]
  public async Task ProgressAfterTheStall_DoesNotTakeTheRoleBackAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var grant = new LeasedGrant(_interval);
    await using var keepalive = DutyGrantKeepalive.Start(grant, time, stallLimit: _stallLimit);
    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);
    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);
    time.Advance(_tick);   // stalled at 15 seconds

    keepalive.NoteProgress();
    time.Advance(_tick * 4);

    await Assert.That(grant.Count).IsEqualTo(2)
      .Because("the lease may already have lapsed to another instance; renewing it now would be two migrators");
  }

  [Test]
  public async Task NoteProgress_OnAKeepaliveOfNothing_IsHarmlessAsync() {
    await using var keepalive = DutyGrantKeepalive.Start(null, new FakeTimeProvider(), stallLimit: _stallLimit);

    keepalive.NoteProgress();

    await Assert.That(keepalive.IsStalled).IsFalse();
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  public async Task AStallLimitThatIsNotPositive_IsRefusedAsync(int seconds) {
    await Assert.That(() => DutyGrantKeepalive.Start(new LeasedGrant(_interval), new FakeTimeProvider(), stallLimit: TimeSpan.FromSeconds(seconds)))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  [Timeout(30000)]
  public async Task AVerifyOwedWhenTheStallArrives_IsNotRunAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var grant = new LeasedGrant(_interval) { Gate = gate };
    var keepalive = DutyGrantKeepalive.Start(grant, time, stallLimit: _stallLimit);
    time.Advance(_tick);
    await grant.NextVerifyAsync(cancellationToken);   // in flight, held by the gate
    time.Advance(_tick);                               // owed
    time.Advance(_tick);                               // stalled at 15 seconds

    gate.SetResult();
    await keepalive.DisposeAsync();

    await Assert.That(grant.Count).IsEqualTo(1)
      .Because("a verify owed to a holder that has since stalled would renew a lease that must lapse");
  }
}
