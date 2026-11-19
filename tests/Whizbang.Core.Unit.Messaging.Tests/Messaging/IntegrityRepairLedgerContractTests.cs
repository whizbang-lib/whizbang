// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The repair ladder every <see cref="IIntegrityRepairLedger"/> keeps, written once and run against each ledger so the
/// in-memory fallback and the durable ledger cannot drift (#2029, #2030, #2034). The in-memory ledger runs it in
/// <c>InMemoryIntegrityRepairLedgerContractTests</c>; the durable one, against a real database, in
/// <c>DurableIntegrityRepairLedgerContractTests</c> (this file is linked into that project).
/// </summary>
/// <remarks>
/// The drain's claim and the commit-time bundle answer are not part of the ledger interface (they belong to the durable
/// store alone) and are covered by <c>IntegrityRepairAnswerSqlTests</c>. A window-bounded type-lane mark needs the
/// windows the durable store stamps on per-stream rows, so only the unbounded form is in the contract.
/// </remarks>
/// <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
public abstract class IntegrityRepairLedgerContractTests {
  private const int MAX_ATTEMPTS = 8;

  // Fixed instants, not the host clock: every operation takes the caller's clock.
  private static readonly DateTimeOffset _t0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _backoff = TimeSpan.FromSeconds(300);
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);
  private static readonly TimeSpan _answerTimeout = TimeSpan.FromMinutes(180);

  /// <summary>A fresh, empty ledger for one test.</summary>
  protected abstract Task<IIntegrityRepairLedger> CreateLedgerAsync();

  [Test]
  public async Task Repair_PastTheAttemptBudget_IsRefusedForGood_WithNoTerminalCadenceAsync() {
    var ledger = await CreateLedgerAsync();
    var key = _key(TrackedGuid.New().Value, TrackedGuid.New().Value);
    await _reportAsync(ledger, key, deficit: 2, at: _t0);

    var first = await ledger.TryBeginRepairAsync(key, _t0, _backoff, maxAttempts: 2);
    var second = await ledger.TryBeginRepairAsync(key, _t0.AddHours(1), _backoff, maxAttempts: 2);
    var third = await ledger.TryBeginRepairAsync(key, _t0.AddHours(2), _backoff, maxAttempts: 2);
    var muchLater = await ledger.TryBeginRepairAsync(key, _t0.AddDays(30), _backoff, maxAttempts: 2);

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsTrue();
    await Assert.That(third).IsFalse().Because("two attempts are the budget");
    await Assert.That(muchLater).IsFalse().Because("the budget ends the asking; it is not a slower cadence");
  }

  [Test]
  public async Task Repair_WhileTheAnswerIsPending_IsRefused_UntilTheAnswerTimeoutAsync() {
    var ledger = await CreateLedgerAsync();
    var key = _key(TrackedGuid.New().Value, TrackedGuid.New().Value);
    await _reportAsync(ledger, key, deficit: 1, at: _t0);
    _ = await ledger.TryBeginRepairAsync(key, _t0, _backoff, MAX_ATTEMPTS);

    var marked = await ledger.MarkAnswerPendingAsync([key], null, null, _t0, _answerTimeout);
    var inFlight = await ledger.TryBeginRepairAsync(key, _t0.AddHours(1), _backoff, MAX_ATTEMPTS);
    var afterTimeout = await ledger.TryBeginRepairAsync(key, _t0 + _answerTimeout, _backoff, MAX_ATTEMPTS);

    await Assert.That(marked).IsEqualTo(1);
    await Assert.That(inFlight).IsFalse().Because("the backoff has passed, but the answer to the last request is on its way");
    await Assert.That(afterTimeout).IsTrue().Because("an answer that never came is asked for again once the timeout passes");
  }

  [Test]
  public async Task MarkAnswerPending_ABucketTheLedgerDoesNotTrack_MarksNothingAsync() {
    var ledger = await CreateLedgerAsync();

    var marked = await ledger.MarkAnswerPendingAsync(
      [_key(TrackedGuid.New().Value, TrackedGuid.New().Value)], null, null, _t0, _answerTimeout);

    await Assert.That(marked).IsEqualTo(0);
  }

  [Test]
  public async Task Report_TheLocalDigestMoved_ResetsTheLadder_LiftsTheMarks_AndClearsThePendingAnswerAsync() {
    var ledger = await CreateLedgerAsync();
    var origin = TrackedGuid.New().Value;
    var exhausted = _key(origin, TrackedGuid.New().Value);
    var unhealable = _key(origin, TrackedGuid.New().Value);
    var pending = _key(origin, TrackedGuid.New().Value);
    foreach (var key in new[] { exhausted, unhealable, pending }) {
      await _reportAsync(ledger, key, deficit: 3, at: _t0);
      _ = await ledger.TryBeginRepairAsync(key, _t0, _backoff, maxAttempts: 1);
    }
    _ = await ledger.MarkUnhealableBatchAsync([unhealable], _t0);
    _ = await ledger.MarkAnswerPendingAsync([pending], null, null, _t0, _answerTimeout);

    foreach (var key in new[] { exhausted, unhealable, pending }) {
      await _reportAsync(ledger, key, deficit: 2, at: _t0.AddMinutes(1), localLo: 5, localHi: 6);
    }

    await Assert.That(await ledger.TryBeginRepairAsync(exhausted, _t0.AddMinutes(2), _backoff, maxAttempts: 1)).IsTrue()
      .Because("the receiver's own events changed, which is what repair acts on: a fresh budget");
    await Assert.That(await ledger.TryBeginRepairAsync(unhealable, _t0.AddMinutes(2), _backoff, maxAttempts: 1)).IsTrue()
      .Because("whatever a redelivery showed about the old events no longer holds");
    await Assert.That(await ledger.TryBeginRepairAsync(pending, _t0.AddMinutes(2), _backoff, maxAttempts: 1)).IsTrue()
      .Because("a local change clears the pending answer too");
  }

  [Test]
  public async Task Report_OnlyTheOriginsDigestMoved_KeepsTheBudget_AndTheMarkAsync() {
    var ledger = await CreateLedgerAsync();
    var origin = TrackedGuid.New().Value;
    var exhausted = _key(origin, TrackedGuid.New().Value);
    var unhealable = _key(origin, TrackedGuid.New().Value);
    await _reportAsync(ledger, exhausted, deficit: 3, at: _t0);
    await _reportAsync(ledger, unhealable, deficit: 3, at: _t0);
    _ = await ledger.TryBeginRepairAsync(exhausted, _t0, _backoff, maxAttempts: 1);
    _ = await ledger.MarkUnhealableBatchAsync([unhealable], _t0);

    await _reportAsync(ledger, exhausted, deficit: 3, at: _t0.AddMinutes(1), originLo: 99, originHi: 98);
    await _reportAsync(ledger, unhealable, deficit: 3, at: _t0.AddMinutes(1), originLo: 99, originHi: 98);

    await Assert.That(await ledger.TryBeginRepairAsync(exhausted, _t0.AddDays(1), _backoff, maxAttempts: 1)).IsFalse()
      .Because("new events on the origin's side do not change what a redelivery can do here");
    await Assert.That(await ledger.TryBeginRepairAsync(unhealable, _t0.AddDays(1), _backoff, MAX_ATTEMPTS)).IsFalse();
  }

  [Test]
  public async Task Report_TheDeficitGrew_ResetsTheLadder_AndLiftsTheMarkAsync() {
    var ledger = await CreateLedgerAsync();
    var origin = TrackedGuid.New().Value;
    var exhausted = _key(origin, TrackedGuid.New().Value);
    var unhealable = _key(origin, TrackedGuid.New().Value);
    await _reportAsync(ledger, exhausted, deficit: 3, at: _t0);
    await _reportAsync(ledger, unhealable, deficit: 3, at: _t0);
    _ = await ledger.TryBeginRepairAsync(exhausted, _t0, _backoff, maxAttempts: 1);
    _ = await ledger.MarkUnhealableBatchAsync([unhealable], _t0);

    await _reportAsync(ledger, exhausted, deficit: 5, at: _t0.AddMinutes(1), originLo: 99, originHi: 98);
    await _reportAsync(ledger, unhealable, deficit: 5, at: _t0.AddMinutes(1), originLo: 99, originHi: 98);

    await Assert.That(await ledger.TryBeginRepairAsync(exhausted, _t0.AddMinutes(2), _backoff, maxAttempts: 1)).IsTrue()
      .Because("more events are missing than before: a new incident");
    await Assert.That(await ledger.TryBeginRepairAsync(unhealable, _t0.AddMinutes(2), _backoff, MAX_ATTEMPTS)).IsTrue();
  }

  [Test]
  public async Task Repair_OnTheTypeLane_StandsDown_WhilePerStreamRequestsOfTheTypeAwaitTheirAnswersAsync() {
    var ledger = await CreateLedgerAsync();
    var origin = TrackedGuid.New().Value;
    var perStream = _key(origin, TrackedGuid.New().Value);
    var otherType = new IntegrityRepairLedger.DivergenceKey(origin, null, "Contracts.OtherProbe", TrackedGuid.New().Value);
    // Same type, never requested: nothing is on the way for it, so it alone would not stand the lane down.
    var notRequested = _key(origin, TrackedGuid.New().Value);
    await _reportAsync(ledger, notRequested, deficit: 1, at: _t0);
    await _reportAsync(ledger, perStream, deficit: 1, at: _t0);
    await _reportAsync(ledger, otherType, deficit: 1, at: _t0);
    _ = await ledger.MarkAnswerPendingAsync([perStream, otherType], null, null, _t0, _answerTimeout);

    var whilePending = await ledger.TryBeginRepairAsync(_key(origin, Guid.Empty), _t0.AddMinutes(1), _backoff, MAX_ATTEMPTS);
    var anotherTypesLane = await ledger.TryBeginRepairAsync(
      new IntegrityRepairLedger.DivergenceKey(origin, null, "Contracts.ThirdProbe", Guid.Empty), _t0.AddMinutes(1), _backoff, MAX_ATTEMPTS);
    var afterTimeout = await ledger.TryBeginRepairAsync(_key(origin, Guid.Empty), _t0 + _answerTimeout, _backoff, MAX_ATTEMPTS);

    await Assert.That(whilePending).IsFalse().Because("the per-stream requests already re-ship the type's events");
    await Assert.That(anotherTypesLane).IsTrue().Because("only the type's own per-stream requests stand it down");
    await Assert.That(afterTimeout).IsTrue();
  }

  [Test]
  public async Task MarkAnswerPending_OnTheTypeLane_WithoutAWindow_CoversEveryPerStreamRowOfTheTypeAsync() {
    var ledger = await CreateLedgerAsync();
    var origin = TrackedGuid.New().Value;
    var perStream = _key(origin, TrackedGuid.New().Value);
    var otherType = new IntegrityRepairLedger.DivergenceKey(origin, null, "Contracts.OtherProbe", TrackedGuid.New().Value);
    await _reportAsync(ledger, perStream, deficit: 1, at: _t0);
    await _reportAsync(ledger, otherType, deficit: 1, at: _t0);

    var marked = await ledger.MarkAnswerPendingAsync([_key(origin, Guid.Empty)], null, null, _t0, _answerTimeout);

    await Assert.That(marked).IsEqualTo(1).Because("the type lane itself is not tracked; its one per-stream row is");
    await Assert.That(await ledger.TryBeginRepairAsync(perStream, _t0.AddMinutes(1), _backoff, MAX_ATTEMPTS)).IsFalse()
      .Because("the whole-type request answers this stream too");
    await Assert.That(await ledger.TryBeginRepairAsync(otherType, _t0.AddMinutes(1), _backoff, MAX_ATTEMPTS)).IsTrue();
  }

  [Test]
  public async Task MarkHealed_ForgetsTheBucket_SoALaterDivergenceStartsAFreshLadderAsync() {
    var ledger = await CreateLedgerAsync();
    var key = _key(TrackedGuid.New().Value, TrackedGuid.New().Value);
    await _reportAsync(ledger, key, deficit: 1, at: _t0);
    _ = await ledger.TryBeginRepairAsync(key, _t0, _backoff, maxAttempts: 1);

    await ledger.MarkHealedAsync(key);

    await Assert.That(await ledger.TryBeginRepairAsync(key, _t0.AddMinutes(1), _backoff, maxAttempts: 1)).IsTrue();
  }

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, Guid stream) =>
    new(origin, null, "Contracts.ContractProbe", stream);

  private static async Task _reportAsync(
      IIntegrityRepairLedger ledger, IntegrityRepairLedger.DivergenceKey key, long deficit, DateTimeOffset at,
      long originLo = 11, long originHi = 21, long localLo = 0, long localHi = 0) =>
    _ = await ledger.TryBeginReportBatchAsync(
      [new IntegrityReportObservation(key, originLo, originHi, localLo, localHi, IsDeficit: true, DeficitEvents: deficit)],
      at, _cooldown);
}
