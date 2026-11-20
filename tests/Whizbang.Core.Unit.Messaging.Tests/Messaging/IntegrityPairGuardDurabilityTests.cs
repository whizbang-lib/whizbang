// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Workers;
using Whizbang.Rules;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2377: a pair's halt survives a restart. The guard writes a halt, its alert and its resumption to the store as they
/// happen (<see cref="IWorkCoordinator.IntegrityRecordPairHaltAsync"/> and its siblings), and a new process loads the open
/// halts once before its first verdict, so the halted-at time, and with it the alert clock, carries on where it was.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityPairGuard.cs</code-under-test>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
[Category("Messaging")]
public class IntegrityPairGuardDurabilityTests {
  private const string PEER = "orders-svc";
  private const string SHARED = "Contracts.OrderPlaced";
  private static readonly DateTimeOffset _t0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

  private sealed class Rules(string hash) : IRuleFingerprintSource {
    public RuleSet RulesFor(IEnumerable<string>? eventTypes) =>
      new(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION,
        new Dictionary<string, string>(StringComparer.Ordinal) { [SHARED] = hash });
  }

  private sealed class CapturingLog : ILogger<IntegrityPairGuard> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, exception is null ? formatter(state, exception) : $"{formatter(state, exception)} {exception.Message}"));
  }

  /// <summary>A store that keeps halts as the database would: one open halt per pair, the earliest halted-at kept.</summary>
  private sealed class HaltStore : NoOpWorkCoordinator, IWorkCoordinator {
    public Dictionary<(string Peer, IntegrityPairSide Side), IntegrityPairHaltRecord> Open { get; } = [];
    public List<(string Peer, IntegrityPairSide Side, DateTimeOffset At)> Resumed { get; } = [];
    public int Loads { get; private set; }
    public int Records { get; private set; }
    public Exception? FailLoad { get; set; }
    public TaskCompletionSource? HoldLoad { get; set; }
    public Action? DuringRecord { get; set; }
    public Exception? FailWrites { get; set; }

    public Task<IReadOnlyList<IntegrityPairHaltRecord>?> IntegrityLoadPairHaltsAsync(CancellationToken cancellationToken = default) {
      Loads++;
      if (FailLoad is { } failure) {
        FailLoad = null;
        throw failure;
      }
      return HoldLoad is { } hold ? _afterAsync(hold.Task) : Task.FromResult<IReadOnlyList<IntegrityPairHaltRecord>?>([.. Open.Values]);
    }

    private async Task<IReadOnlyList<IntegrityPairHaltRecord>?> _afterAsync(Task hold) {
      await hold;
      return [.. Open.Values];
    }

    public Task<IntegrityPairHaltRecord?> IntegrityRecordPairHaltAsync(IntegrityPairHaltRecord halt, CancellationToken cancellationToken = default) {
      Records++;
      if (FailWrites is { } failure) {
        throw failure;
      }
      DuringRecord?.Invoke();
      var key = (halt.Peer, halt.Side);
      var kept = halt;
      if (Open.TryGetValue(key, out var open)) {
        var earliest = open.HaltedAt < halt.HaltedAt ? open.HaltedAt : halt.HaltedAt;
        kept = halt with { HaltedAt = earliest, AlertedAt = open.AlertedAt };
      }
      Open[key] = kept;
      return Task.FromResult<IntegrityPairHaltRecord?>(kept);
    }

    public Task IntegrityRecordPairHaltAlertAsync(string peer, IntegrityPairSide side, DateTimeOffset at, CancellationToken cancellationToken = default) {
      if (FailWrites is { } failure) {
        throw failure;
      }
      if (Open.TryGetValue((peer, side), out var open)) {
        Open[(peer, side)] = open with { AlertedAt = open.AlertedAt ?? at };
      }
      return Task.CompletedTask;
    }

    public Task IntegrityRecordPairResumedAsync(string peer, IntegrityPairSide side, DateTimeOffset at, CancellationToken cancellationToken = default) {
      if (FailWrites is { } failure) {
        throw failure;
      }
      Open.Remove((peer, side));
      Resumed.Add((peer, side, at));
      return Task.CompletedTask;
    }
  }

  private static (IntegrityPairGuard Guard, CapturingLog Log) _process(FakeTimeProvider time, TimeSpan alertAfter) {
    var options = new StreamIntegrityOptions();
    options.PairGuard.HaltedAlertAfter = alertAfter;
    var log = new CapturingLog();
    return (new IntegrityPairGuard(new Rules("a"), Options.Create(options), log, time), log);
  }

  private static RuleFingerprint _differing() => RuleFingerprint.From(new Rules("b").RulesFor(null));
  private static RuleFingerprint _matching() => RuleFingerprint.From(new Rules("a").RulesFor(null));

  [Test]
  public async Task ARestartDuringAHalt_KeepsTheHaltedAt_AndTheAlertFiresOnScheduleAsync() {
    var store = new HaltStore();
    var time = new FakeTimeProvider(_t0);
    var (before, _) = _process(time, TimeSpan.FromHours(2));
    _ = await before.AdmitAsync(store, PEER, IntegrityPairSide.Consumer, _differing());
    time.Advance(TimeSpan.FromMinutes(90));

    var (after, log) = _process(time, TimeSpan.FromHours(2));   // the service restarted
    _ = await after.AdmitAsync(store, PEER, IntegrityPairSide.Consumer, _differing());

    await Assert.That(after.Pairs.Single().HaltedSince).IsEqualTo(_t0)
      .Because("the halt began before the restart, so the clock that drives the alert keeps running from then");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Critical)).IsFalse().Because("90 minutes is under two hours");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Error)).IsFalse()
      .Because("the halt was already reported before the restart; the same halt is not news");

    time.Advance(TimeSpan.FromMinutes(30));
    _ = await after.AdmitAsync(store, PEER, IntegrityPairSide.Consumer, _differing());

    await Assert.That(log.Entries.Count(e => e.Level == LogLevel.Critical)).IsEqualTo(1)
      .Because("two hours after the halt began, not two hours after the restart");
    await Assert.That(store.Open[(PEER, IntegrityPairSide.Consumer)].AlertedAt).IsEqualTo(_t0.AddHours(2));
  }

  [Test]
  public async Task AnAlertedHalt_IsNotAlertedAgainAfterARestartAsync() {
    var store = new HaltStore();
    var time = new FakeTimeProvider(_t0);
    var (before, _) = _process(time, TimeSpan.FromMinutes(30));
    _ = await before.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _differing());
    time.Advance(TimeSpan.FromMinutes(31));
    _ = await before.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _differing());

    var (after, log) = _process(time, TimeSpan.FromMinutes(30));
    _ = await after.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _differing());

    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Critical)).IsFalse()
      .Because("the alert went out before the restart; a restart is not a reason to page again");
    await Assert.That(after.Pairs.Single().Alerted).IsTrue();
  }

  [Test]
  public async Task Resuming_RecordsTheResumedAt_AndClosesTheHaltAsync() {
    var store = new HaltStore();
    var time = new FakeTimeProvider(_t0);
    var (before, _) = _process(time, TimeSpan.FromHours(2));
    _ = await before.AdmitAsync(store, PEER, IntegrityPairSide.Consumer, _differing());
    time.Advance(TimeSpan.FromMinutes(10));

    var (after, log) = _process(time, TimeSpan.FromHours(2));
    var verdict = await after.AdmitAsync(store, PEER, IntegrityPairSide.Consumer, _matching());

    await Assert.That(verdict.IsAgreed).IsTrue();
    await Assert.That(store.Resumed).IsEquivalentTo([(PEER, IntegrityPairSide.Consumer, _t0.AddMinutes(10))])
      .Because("a halt that began before the restart still ends in the history");
    await Assert.That(store.Open).IsEmpty();
    await Assert.That(log.Entries.Single().Message).Contains("resumed").And.Contains("00:10:00");
  }

  [Test]
  public async Task TheHalt_IsWrittenWithBothSidesRulesAndTheDifferingTypesAsync() {
    var store = new HaltStore();
    var (guard, _) = _process(new FakeTimeProvider(_t0), TimeSpan.FromHours(2));
    var peer = _differing();

    _ = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Origin, peer);
    _ = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Origin, peer);

    var halt = store.Open[(PEER, IntegrityPairSide.Origin)];
    await Assert.That(store.Records).IsEqualTo(1).Because("written when the state changes, not on every message");
    await Assert.That(halt.Reason).IsEqualTo(RulePairHaltReason.TypeRules);
    await Assert.That(halt.DifferingTypes).IsEquivalentTo([SHARED]);
    await Assert.That(halt.LocalFingerprint).IsEqualTo(guard.Stamp([SHARED]).Fingerprint);
    await Assert.That(halt.LocalSemanticsVersion).IsEqualTo(RuleSemantics.VERSION);
    await Assert.That(halt.LocalEligibilitySqlVersion).IsEqualTo(RuleSemantics.ELIGIBILITY_SQL_VERSION);
    await Assert.That(halt.PeerFingerprint).IsEqualTo(peer.Fingerprint);
    await Assert.That(halt.PeerSemanticsVersion).IsEqualTo(RuleSemantics.VERSION);
    await Assert.That(halt.PeerEligibilitySqlVersion).IsEqualTo(RuleSemantics.ELIGIBILITY_SQL_VERSION);
    await Assert.That(halt.HaltedAt).IsEqualTo(_t0);
  }

  [Test]
  public async Task AHaltForAnotherReason_IsWrittenAgain_KeepingTheHaltedAtAsync() {
    var store = new HaltStore();
    var time = new FakeTimeProvider(_t0);
    var (guard, _) = _process(time, TimeSpan.FromHours(2));
    _ = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _differing());
    time.Advance(TimeSpan.FromMinutes(5));

    _ = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Origin, null);

    var halt = store.Open[(PEER, IntegrityPairSide.Origin)];
    await Assert.That(halt.Reason).IsEqualTo(RulePairHaltReason.NoFingerprint);
    await Assert.That(halt.PeerFingerprint).IsNull();
    await Assert.That(halt.PeerSemanticsVersion).IsNull();
    await Assert.That(halt.HaltedAt).IsEqualTo(_t0).Because("the pair has been halted since the first reason");
  }

  [Test]
  public async Task AnEarlierHaltAnotherInstanceKept_IsTheOneTheClockRunsFromAsync() {
    var store = new HaltStore();
    var time = new FakeTimeProvider(_t0);
    var (guard, _) = _process(time, TimeSpan.FromHours(2));
    _ = await guard.AdmitAsync(store, "billing-svc", IntegrityPairSide.Origin, _matching());   // hydrates: nothing open
    var (sibling, _) = _process(new FakeTimeProvider(_t0.AddMinutes(-40)), TimeSpan.FromHours(2));
    _ = await sibling.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _differing());

    _ = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _differing());

    await Assert.That(guard.Pairs.Single(p => p.Peer == PEER).HaltedSince).IsEqualTo(_t0.AddMinutes(-40))
      .Because("another instance of this service halted the pair first; one service has one halt");
  }

  [Test]
  public async Task TheOpenHalts_AreLoadedOnce_AndWhatThisProcessAlreadyKnowsWinsAsync() {
    var store = new HaltStore();
    var time = new FakeTimeProvider(_t0);
    var (other, _) = _process(time, TimeSpan.FromHours(2));
    _ = await other.AdmitAsync(store, PEER, IntegrityPairSide.Consumer, _differing());
    var (guard, _) = _process(time, TimeSpan.FromHours(2));
    _ = guard.Admit(PEER, IntegrityPairSide.Consumer, _matching());   // learned live, before the load
    var loadsBefore = store.Loads;   // the other process's own load

    _ = await guard.AdmitAsync(store, "billing-svc", IntegrityPairSide.Consumer, _matching());
    _ = await guard.AdmitAsync(store, "billing-svc", IntegrityPairSide.Consumer, _matching());

    await Assert.That(store.Loads - loadsBefore).IsEqualTo(1).Because("one read per process");
    await Assert.That(guard.Pairs.Single(p => p.Peer == PEER).HaltedSince).IsNull()
      .Because("a verdict this process reached itself is newer than the kept copy");
  }

  [Test]
  public async Task ALoadThatFails_IsLogged_TheVerdictStands_AndTheNextCallLoadsAgainAsync() {
    var store = new HaltStore { FailLoad = new InvalidOperationException("store down") };
    var time = new FakeTimeProvider(_t0);
    var (guard, log) = _process(time, TimeSpan.FromHours(2));

    var verdict = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Consumer, _matching());
    _ = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Consumer, _matching());

    await Assert.That(verdict.IsAgreed).IsTrue().Because("the store keeps history; it never decides a verdict");
    await Assert.That(log.Entries.Single(e => e.Level == LogLevel.Warning).Message).Contains("store down");
    await Assert.That(store.Loads).IsEqualTo(2).Because("a failed load is not a load");
  }

  [Test]
  public async Task AWriteThatFails_IsLogged_AndTheVerdictStandsAsync() {
    var store = new HaltStore();
    var time = new FakeTimeProvider(_t0);
    var (guard, log) = _process(time, TimeSpan.Zero);
    _ = await guard.AdmitAsync(store, "billing-svc", IntegrityPairSide.Origin, _matching());
    store.FailWrites = new InvalidOperationException("disk full");

    var halted = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _differing());
    var resumed = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _matching());

    await Assert.That(halted.IsAgreed).IsFalse();
    await Assert.That(resumed.IsAgreed).IsTrue();
    await Assert.That(log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("disk full"))).IsEqualTo(2)
      .Because("the halt with its alert, and the resumption, each say what was not kept");
  }

  [Test]
  public async Task ACanceledLoadOrWrite_PropagatesAsync() {
    var time = new FakeTimeProvider(_t0);
    var (loading, _) = _process(time, TimeSpan.FromHours(2));
    var (writing, _) = _process(time, TimeSpan.FromHours(2));
    var writes = new HaltStore();
    _ = await writing.AdmitAsync(writes, "billing-svc", IntegrityPairSide.Origin, _matching());
    writes.FailWrites = new OperationCanceledException();

    await Assert.That(async () => await loading.AdmitAsync(
        new HaltStore { FailLoad = new OperationCanceledException() }, PEER, IntegrityPairSide.Origin, _matching()))
      .Throws<OperationCanceledException>().Because("a stopping host stops");
    await Assert.That(async () => await writing.AdmitAsync(writes, PEER, IntegrityPairSide.Origin, _differing()))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task WithoutAStore_TheGuardKeepsItsStateInMemoryAsync() {
    var (guard, _) = _process(new FakeTimeProvider(_t0), TimeSpan.FromHours(2));

    var verdict = await guard.AdmitAsync(null, PEER, IntegrityPairSide.Origin, _differing());

    await Assert.That(verdict.IsAgreed).IsFalse();
    await Assert.That(guard.Pairs.Single().HaltedSince).IsEqualTo(_t0);
  }

  [Test]
  public async Task AStoreThatKeepsNothing_LeavesTheGuardAsItWasAsync() {
    var (guard, _) = _process(new FakeTimeProvider(_t0), TimeSpan.Zero);

    var halted = await guard.AdmitAsync(new NoOpWorkCoordinator(), PEER, IntegrityPairSide.Origin, _differing());
    var resumed = await guard.AdmitAsync(new NoOpWorkCoordinator(), PEER, IntegrityPairSide.Origin, _matching());

    await Assert.That(halted.IsAgreed).IsFalse().Because("an engine without the table still guards, in memory");
    await Assert.That(resumed.IsAgreed).IsTrue();
  }

  [Test]
  public async Task ConcurrentFirstVerdicts_ShareOneLoadAsync() {
    var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var store = new HaltStore { HoldLoad = hold };
    var (guard, _) = _process(new FakeTimeProvider(_t0), TimeSpan.FromHours(2));

    var first = guard.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _matching());
    var second = guard.AdmitAsync(store, "billing-svc", IntegrityPairSide.Origin, _matching());
    hold.SetResult();
    _ = await first;
    _ = await second;

    await Assert.That(store.Loads).IsEqualTo(1).Because("the second verdict waits on the load the first started");
  }

  [Test]
  public async Task AnAlertAnotherInstanceAlreadyRaised_IsTakenUp_AndNotRaisedAgainAsync() {
    var store = new HaltStore();
    var time = new FakeTimeProvider(_t0);
    var (guard, log) = _process(time, TimeSpan.FromHours(2));
    _ = await guard.AdmitAsync(store, "billing-svc", IntegrityPairSide.Origin, _matching());   // loaded before the halt
    store.Open[(PEER, IntegrityPairSide.Origin)] = new IntegrityPairHaltRecord(PEER, IntegrityPairSide.Origin,
      RulePairHaltReason.TypeRules, [SHARED], 1, 1, "fp", 1, 1, "fp", _t0.AddHours(-3), _t0.AddHours(-1));   // a sibling's

    _ = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _differing());
    time.Advance(TimeSpan.FromMinutes(1));
    _ = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _differing());

    await Assert.That(guard.Pairs.Single(p => p.Peer == PEER).Alerted).IsTrue();
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Critical)).IsFalse()
      .Because("one service has one halt, and its alert already went out");
  }

  [Test]
  public async Task AnAlertDueAtOnce_IsKeptWithTheHaltAsync() {
    var store = new HaltStore();
    var (guard, log) = _process(new FakeTimeProvider(_t0), TimeSpan.Zero);

    _ = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Consumer, _differing());

    await Assert.That(log.Entries.Count(e => e.Level == LogLevel.Critical)).IsEqualTo(1);
    await Assert.That(store.Open[(PEER, IntegrityPairSide.Consumer)].AlertedAt).IsEqualTo(_t0);
    await Assert.That(guard.Pairs.Single().Alerted).IsTrue();
  }

  [Test]
  public async Task APairThatResumedWhileItsHaltWasBeingWritten_StaysResumedAsync() {
    var store = new HaltStore();
    var (guard, _) = _process(new FakeTimeProvider(_t0), TimeSpan.FromHours(2));
    _ = await guard.AdmitAsync(store, "billing-svc", IntegrityPairSide.Origin, _matching());
    store.DuringRecord = () => guard.Admit(PEER, IntegrityPairSide.Origin, _matching());   // a concurrent message agrees

    _ = await guard.AdmitAsync(store, PEER, IntegrityPairSide.Origin, _differing());

    await Assert.That(guard.Pairs.Single(p => p.Peer == PEER).HaltedSince).IsNull()
      .Because("taking up the kept halt must not undo a resumption that happened meanwhile");
  }
}
