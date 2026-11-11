// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2273 (owner decision 2026-11-11): a repaired event runs a receptor only when the receptor opts in, the event's origin
/// time is inside the handled lookback, and the handled ledger proves it was never handled here. Otherwise it is stored
/// and projected state only. The lookback in effect is clamped to the ledger's coverage, so a lookback grown past what
/// the ledger holds cannot make an incomplete window look complete. Every test fixes "now", and every event id is a
/// time-ordered id minted at a fixed instant, so the decisions never depend on the wall clock.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RepairReceptorGate.cs</code-under-test>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
public class RepairReceptorGateTests {
  private static readonly DateTimeOffset _now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _week = TimeSpan.FromDays(7);
  private static readonly DateTimeOffset _coveredLongAgo = _now - TimeSpan.FromDays(400);

  // An id whose origin time is `ago` before now: the time a version-7 id carries is the event's origin time.
  private static Guid _eventFrom(TimeSpan ago) => Guid.CreateVersion7(_now - ago);

  private static InboxMessage _child(Guid id, bool stateOnly = false) => new() {
    MessageId = id,
    HandlerName = "OrderPlacedHandler",
    Envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(id),
      Payload = JsonElement.Parse("{}"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      StateOnly = stateOnly,
    },
    EnvelopeType = "MessageEnvelope<OrderPlaced>",
    MessageType = "Contracts.OrderPlaced, Contracts",
  };

  private sealed class Ledger(DateTimeOffset? coversFrom, params Guid[] handled) {
    public List<IReadOnlyList<Guid>> Asked { get; } = [];
    public bool Unreadable { get; init; }

    public Task<HandledLedgerAnswer?> LookupAsync(IReadOnlyList<Guid> ids, CancellationToken ct) {
      ct.ThrowIfCancellationRequested();
      Asked.Add(ids);
      return Task.FromResult<HandledLedgerAnswer?>(Unreadable ? null : new HandledLedgerAnswer(coversFrom, handled.ToHashSet()));
    }
  }

  private static bool _repaired(InboxMessage child) => child.Envelope.Repaired;
  private static bool _stateOnly(InboxMessage child) => child.Envelope.StateOnly;

  [Test]
  public async Task ARepairedEventOlderThanTheLookback_NeverRunsReceptors_AndTheLedgerIsNotAskedAsync() {
    var old = _child(_eventFrom(TimeSpan.FromDays(30)));
    var ledger = new Ledger(_coveredLongAgo);

    var tally = await RepairReceptorGate.ApplyAsync([old], bundleStateOnly: false, _week, _now, ledger.LookupAsync);

    await Assert.That(_stateOnly(old)).IsTrue();
    await Assert.That(_repaired(old)).IsFalse();
    await Assert.That(tally[RepairReceptorReason.OutsideLookback]).IsEqualTo(1);
    await Assert.That(ledger.Asked).IsEmpty()
      .Because("nothing older than the lookback can be proven never handled, so the ledger has nothing to answer");
  }

  [Test]
  public async Task ARepairedEventInsideTheLookback_ThatTheLedgerShowsHandled_NeverRunsReceptorsAsync() {
    var id = _eventFrom(TimeSpan.FromDays(2));
    var handled = _child(id);

    var tally = await RepairReceptorGate.ApplyAsync([handled], false, _week, _now, new Ledger(_coveredLongAgo, id).LookupAsync);

    await Assert.That(_stateOnly(handled)).IsTrue();
    await Assert.That(_repaired(handled)).IsFalse();
    await Assert.That(tally[RepairReceptorReason.AlreadyHandled]).IsEqualTo(1);
  }

  [Test]
  public async Task ARepairedEventInsideTheLookback_AbsentFromTheLedger_RunsItsOptedInReceptorsAsync() {
    var neverHandled = _child(_eventFrom(TimeSpan.FromDays(2)));

    var tally = await RepairReceptorGate.ApplyAsync([neverHandled], false, _week, _now, new Ledger(_coveredLongAgo).LookupAsync);

    await Assert.That(_repaired(neverHandled)).IsTrue()
      .Because("the ledger covers the event's whole window and does not hold it: positive evidence it was never handled");
    await Assert.That(_stateOnly(neverHandled)).IsFalse();
    await Assert.That(tally[RepairReceptorReason.NeverHandled]).IsEqualTo(1);
  }

  /// <summary>
  /// The clamp: the setting was raised to 30 days, but the ledger has only covered the last 3 (it was created then, or
  /// kept only 3 days before the change). An event 10 days old is inside the setting and absent from the ledger, and
  /// still must not run receptors: its window is not covered, so its absence proves nothing.
  /// </summary>
  [Test]
  public async Task ALookbackGrownBeyondTheLedgersCoverage_IsClampedToIt_SoAnIncompleteWindowNeverLooksCompleteAsync() {
    var coversFrom = _now - TimeSpan.FromDays(3);
    var beforeCoverage = _child(_eventFrom(TimeSpan.FromDays(10)));
    var insideCoverage = _child(_eventFrom(TimeSpan.FromDays(1)));

    var tally = await RepairReceptorGate.ApplyAsync(
      [beforeCoverage, insideCoverage], false, TimeSpan.FromDays(30), _now, new Ledger(coversFrom).LookupAsync);

    await Assert.That(_stateOnly(beforeCoverage)).IsTrue();
    await Assert.That(_repaired(beforeCoverage)).IsFalse();
    await Assert.That(tally[RepairReceptorReason.BeforeLedgerCoverage]).IsEqualTo(1);
    await Assert.That(_repaired(insideCoverage)).IsTrue()
      .Because("inside the coverage the clamp changes nothing");
    await Assert.That(tally[RepairReceptorReason.NeverHandled]).IsEqualTo(1);
  }

  [Test]
  public async Task TheLedger_IsAskedOncePerBundle_OnlyForTheEventsTheLookbackAdmitsAsync() {
    var recentA = _eventFrom(TimeSpan.FromHours(1));
    var recentB = _eventFrom(TimeSpan.FromDays(6));
    var ledger = new Ledger(_coveredLongAgo);

    await RepairReceptorGate.ApplyAsync(
      [_child(recentA), _child(_eventFrom(TimeSpan.FromDays(8))), _child(recentB)], false, _week, _now, ledger.LookupAsync);

    await Assert.That(ledger.Asked).Count().IsEqualTo(1);
    await Assert.That(ledger.Asked[0]).IsEquivalentTo([recentA, recentB]);
  }

  [Test]
  public async Task AnEventWhoseIdCarriesNoTime_IsStateOnlyAsync() {
    var untimed = _child(TrackedGuid.NewRandom().Value);
    var ledger = new Ledger(_coveredLongAgo);

    var tally = await RepairReceptorGate.ApplyAsync([untimed], false, _week, _now, ledger.LookupAsync);

    await Assert.That(_stateOnly(untimed)).IsTrue()
      .Because("a random id has no origin time, so the event's age, and with it the ledger's say, is unknown");
    await Assert.That(tally[RepairReceptorReason.NoOriginTime]).IsEqualTo(1);
    await Assert.That(ledger.Asked).IsEmpty();
  }

  [Test]
  public async Task AZeroLookback_TurnsReceptorsOnRepairOff_WithoutAskingTheLedgerAsync() {
    var recent = _child(_eventFrom(TimeSpan.FromMinutes(5)));
    var ledger = new Ledger(_coveredLongAgo);

    var tally = await RepairReceptorGate.ApplyAsync([recent], false, TimeSpan.Zero, _now, ledger.LookupAsync);

    await Assert.That(_stateOnly(recent)).IsTrue();
    await Assert.That(tally[RepairReceptorReason.NoLookback]).IsEqualTo(1);
    await Assert.That(ledger.Asked).IsEmpty();
  }

  [Test]
  public async Task AStateOnlyBundle_StaysStateOnly_WithoutAskingTheLedgerAsync() {
    var backfilled = _child(_eventFrom(TimeSpan.FromMinutes(5)), stateOnly: true);
    var ledger = new Ledger(_coveredLongAgo);

    var tally = await RepairReceptorGate.ApplyAsync([backfilled], bundleStateOnly: true, _week, _now, ledger.LookupAsync);

    await Assert.That(_stateOnly(backfilled)).IsTrue();
    await Assert.That(_repaired(backfilled)).IsFalse();
    await Assert.That(tally[RepairReceptorReason.RequestedStateOnly]).IsEqualTo(1);
    await Assert.That(ledger.Asked).IsEmpty();
  }

  [Test]
  public async Task AStoreWithNoLedger_ProvesNothing_SoEveryEventIsStateOnlyAsync() {
    var recent = _child(_eventFrom(TimeSpan.FromMinutes(5)));

    var tally = await RepairReceptorGate.ApplyAsync([recent], false, _week, _now, new Ledger(coversFrom: null).LookupAsync);

    await Assert.That(_stateOnly(recent)).IsTrue();
    await Assert.That(tally[RepairReceptorReason.LedgerUnavailable]).IsEqualTo(1);
  }

  [Test]
  public async Task ALedgerThatCannotBeRead_ProvesNothing_SoEveryEventIsStateOnlyAsync() {
    var recent = _child(_eventFrom(TimeSpan.FromMinutes(5)));

    var tally = await RepairReceptorGate.ApplyAsync([recent], false, _week, _now, new Ledger(_coveredLongAgo) { Unreadable = true }.LookupAsync);

    await Assert.That(_stateOnly(recent)).IsTrue();
    await Assert.That(_repaired(recent)).IsFalse();
    await Assert.That(tally[RepairReceptorReason.LedgerUnavailable]).IsEqualTo(1);
  }

  [Test]
  public async Task ABundleWithNoChildren_DecidesNothingAsync() {
    var ledger = new Ledger(_coveredLongAgo);

    var tally = await RepairReceptorGate.ApplyAsync([], false, _week, _now, ledger.LookupAsync);

    await Assert.That(tally.Values.Sum()).IsEqualTo(0);
    await Assert.That(ledger.Asked).IsEmpty();
  }

  [Test]
  public async Task TheGate_RefusesNullArgumentsAsync() {
    await Assert.That(async () => await RepairReceptorGate.ApplyAsync(null!, false, _week, _now, new Ledger(null).LookupAsync))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await RepairReceptorGate.ApplyAsync([], false, _week, _now, null!))
      .Throws<ArgumentNullException>();
  }
}
