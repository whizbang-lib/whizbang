// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Issue #1788: the inbox rows this process holds, queued, dispatching or waiting for their commit. A held row keeps
/// its lease (renewed up to a cap, counted per state), is dispatched once however many copies reach the channel, and
/// stops being held the moment its life here ends, so the next claim can offer it again.
/// </summary>
[Category("Messaging")]
public sealed class InboxChannelWriterHeldRowsTests {
  private static readonly TimeSpan _renewAfter = TimeSpan.FromSeconds(150);

  private static (FakeTimeProvider Clock, InboxChannelWriter Writer) _writer() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    return (clock, new InboxChannelWriter(clock));
  }

  [Test]
  public async Task TheFirstCopyTakesTheDispatchTicket_ADuplicateCopyDoesNotAsync() {
    var (_, writer) = _writer();
    var work = _work();
    await writer.WriteAsync(work);
    await writer.WriteAsync(work);

    await Assert.That(writer.TryBeginDispatch(work.MessageId)).IsTrue();
    await Assert.That(writer.TryBeginDispatch(work.MessageId)).IsFalse()
      .Because("a row is dispatched once in a process however many copies were queued");
  }

  [Test]
  public async Task ARowNotHeld_HasNoTicketAsync() {
    var (_, writer) = _writer();
    await Assert.That(writer.TryBeginDispatch((Guid)TrackedGuid.New())).IsFalse()
      .Because("a copy of a row this process released must not dispatch on a lease it no longer has");
  }

  [Test]
  public async Task EndDispatch_ReleasesTheRow_UnlessItsCommitIsPendingAsync() {
    var (_, writer) = _writer();
    var plain = _work();
    var committing = _work();
    await writer.WriteAsync(plain);
    await writer.WriteAsync(committing);
    _ = writer.TryBeginDispatch(plain.MessageId);
    _ = writer.TryBeginDispatch(committing.MessageId);

    writer.MarkCommitPending(committing.MessageId);
    writer.EndDispatch(plain.MessageId);
    writer.EndDispatch(committing.MessageId);

    await Assert.That(writer.IsInFlight(plain.MessageId)).IsFalse()
      .Because("a row whose dispatch ended without a pending commit is no longer held, so the next claim can offer it");
    await Assert.That(writer.IsInFlight(committing.MessageId)).IsTrue()
      .Because("until the commit lands the row is still unprocessed in the store, and re-queuing it would dispatch it twice");
    writer.RemoveInFlight(committing.MessageId);
    await Assert.That(writer.IsInFlight(committing.MessageId)).IsFalse();
  }

  [Test]
  public async Task MarkCommitPendingAndEndDispatch_OnARowNotHeld_DoNothingAsync() {
    var (_, writer) = _writer();
    var id = (Guid)TrackedGuid.New();
    writer.MarkCommitPending(id);
    writer.EndDispatch(id);
    await Assert.That(writer.IsInFlight(id)).IsFalse();
  }

  [Test]
  public async Task Sweep_RenewsARowOnceItsLeaseIsHalfSpent_ThenNotAgainUntilTheNextHalfAsync() {
    var (clock, writer) = _writer();
    var work = _work();
    await writer.WriteAsync(work);

    await Assert.That(writer.SweepLeases(_renewAfter, maxRenewals: 6).Renew).IsEmpty();
    clock.Advance(_renewAfter);
    await Assert.That(writer.SweepLeases(_renewAfter, maxRenewals: 6).Renew).IsEquivalentTo([work.MessageId]);
    await Assert.That(writer.SweepLeases(_renewAfter, maxRenewals: 6).Renew).IsEmpty()
      .Because("a lease just renewed is not due again until another half lease has passed");
    clock.Advance(_renewAfter);
    await Assert.That(writer.SweepLeases(_renewAfter, maxRenewals: 6).Renew).IsEquivalentTo([work.MessageId]);
  }

  [Test]
  public async Task Sweep_PastTheCap_ReleasesTheRowAndReportsItsStateAndTypeAsync() {
    var (clock, writer) = _writer();
    var work = _work();
    await writer.WriteAsync(work);
    for (var i = 0; i < 2; i++) {
      clock.Advance(_renewAfter);
      _ = writer.SweepLeases(_renewAfter, maxRenewals: 2);
    }

    clock.Advance(_renewAfter);
    var sweep = writer.SweepLeases(_renewAfter, maxRenewals: 2);

    await Assert.That(sweep.Renew).IsEmpty();
    await Assert.That(sweep.Lapsed).IsEquivalentTo([new LapsedInboxLease(work.MessageId, work.MessageType, InboxHoldState.Queued, 2)]);
    await Assert.That(writer.IsInFlight(work.MessageId)).IsFalse();
    await Assert.That(writer.TryBeginDispatch(work.MessageId)).IsFalse()
      .Because("the queued copy of a released row must not dispatch: another instance may hold it now");
  }

  [Test]
  public async Task Sweep_StartingDispatch_GivesTheRowAFreshSetOfRenewalsAsync() {
    var (clock, writer) = _writer();
    var work = _work();
    await writer.WriteAsync(work);
    for (var i = 0; i < 2; i++) {
      clock.Advance(_renewAfter);
      _ = writer.SweepLeases(_renewAfter, maxRenewals: 2);
    }

    _ = writer.TryBeginDispatch(work.MessageId);
    clock.Advance(_renewAfter);
    var sweep = writer.SweepLeases(_renewAfter, maxRenewals: 2);

    await Assert.That(sweep.Renew).IsEquivalentTo([work.MessageId])
      .Because("time spent queued must not use up the renewals a running handler is allowed");
    await Assert.That(sweep.Lapsed).IsEmpty();
  }

  [Test]
  public async Task Sweep_ReportsADispatchingRowThatRanOutAsDispatchingAsync() {
    var (clock, writer) = _writer();
    var work = _work();
    await writer.WriteAsync(work);
    _ = writer.TryBeginDispatch(work.MessageId);

    clock.Advance(_renewAfter);
    var sweep = writer.SweepLeases(_renewAfter, maxRenewals: 0);

    await Assert.That(sweep.Lapsed.Single().State).IsEqualTo(InboxHoldState.Dispatching);
  }

  [Test]
  public async Task ARenewedRow_IsNotAgedOutOfTheHeldSetAsync() {
    var (clock, writer) = _writer();
    var held = _work();
    await writer.WriteAsync(held);
    for (var i = 0; i < 5; i++) {
      clock.Advance(_renewAfter);
      _ = writer.SweepLeases(_renewAfter, maxRenewals: 6);
    }

    // 750 s after it arrived, past the 600 s age-out, but renewed 150 s ago.
    await writer.WriteAsync(_work());

    await Assert.That(writer.IsInFlight(held.MessageId)).IsTrue()
      .Because("the age-out bounds rows nobody renews; a row whose lease is being kept is still held");
  }

  private static InboxWork _work() {
    var id = (Guid)TrackedGuid.New();
    return new InboxWork {
      MessageId = id,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(id),
        Payload = JsonElement.Parse("{}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      },
      MessageType = "Contracts.Orders.OrderPlaced, Contracts",
      StreamId = (Guid)TrackedGuid.New(),
    };
  }
}
