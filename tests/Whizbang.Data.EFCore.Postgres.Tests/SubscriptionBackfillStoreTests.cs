// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2092: the durable progress of a paced backfill. One row per (owner, type), queued idempotently; a window requested
/// awaits its answer; the answer moves the position to where the window stopped and ends the backfill when nothing more
/// remains; a late or duplicate answer changes nothing; and a backfill can be ended as unanswered.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SubscriptionBackfillSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/334_PacedSubscriptionBackfill.sql</code-under-test>
[Category("Integration")]
[NotInParallel("SubscriptionBackfillStore")]
[Category("Shard3")]
public class SubscriptionBackfillStoreTests : EFCoreTestBase {
  private static readonly DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
  private static readonly Guid _orders = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
  private const string PLACED = "App.Contracts.Orders.Placed, App.Contracts";
  private const string SHIPPED = "App.Contracts.Orders.Shipped, App.Contracts";

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task Enqueue_QueuesOnePerOwnerAndType_AndQueuingAgainKeepsTheRowAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    await coordinator.EnqueueSubscriptionBackfillsAsync([
      new SubscriptionBackfillSeed(_orders, "orders-svc", PLACED),
      new SubscriptionBackfillSeed(Guid.Empty, string.Empty, SHIPPED),
    ]);
    var first = await coordinator.GetSubscriptionBackfillsAsync();
    await coordinator.EnqueueSubscriptionBackfillsAsync([new SubscriptionBackfillSeed(_orders, "orders-svc", PLACED)]);
    await coordinator.EnqueueSubscriptionBackfillsAsync([]);
    var again = await coordinator.GetSubscriptionBackfillsAsync();

    await Assert.That(first).Count().IsEqualTo(2);
    var placed = first.Single(c => c.EventType == PLACED);
    await Assert.That(placed.OwnerServiceId).IsEqualTo(_orders);
    await Assert.That(placed.OwnerServiceName).IsEqualTo("orders-svc");
    await Assert.That(placed.CursorCommitSequence).IsNull();
    await Assert.That(placed.Window).IsEqualTo(0L);
    await Assert.That(placed.AnswerDueBy).IsNull();
    await Assert.That(placed.Outcome).IsEqualTo(SubscriptionBackfillOutcome.Running);
    await Assert.That(first.Single(c => c.EventType == SHIPPED).OwnerServiceId).IsEqualTo(Guid.Empty);
    await Assert.That(again.Select(c => c.BackfillId)).IsEquivalentTo(first.Select(c => c.BackfillId))
      .Because("a pair already queued keeps its row, its id and its progress (multi-instance safe)");
  }

  [Test]
  public async Task MarkRequested_AwaitsTheAnswer_AndASecondRequestOfTheSameWindowCountsAnAttemptAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var id = await _queueAsync(coordinator);

    await coordinator.MarkSubscriptionBackfillWindowRequestedAsync(id, 1, _now, _now.AddMinutes(180));
    var once = (await coordinator.GetSubscriptionBackfillsAsync()).Single();
    await coordinator.MarkSubscriptionBackfillWindowRequestedAsync(id, 1, _now.AddHours(3), _now.AddHours(6));
    var twice = (await coordinator.GetSubscriptionBackfillsAsync()).Single();

    await Assert.That(once.Window).IsEqualTo(1L);
    await Assert.That(once.AnswerDueBy).IsEqualTo(_now.AddMinutes(180));
    await Assert.That(once.WindowAttempts).IsEqualTo(1);
    await Assert.That(once.WindowsRequested).IsEqualTo(1L);
    await Assert.That(once.LastRequestedAt).IsEqualTo(_now);
    await Assert.That(twice.WindowAttempts).IsEqualTo(2).Because("the same window asked again is one more attempt");
    await Assert.That(twice.WindowsRequested).IsEqualTo(2L);
    await Assert.That(twice.AnswerDueBy).IsEqualTo(_now.AddHours(6));
  }

  [Test]
  public async Task RecordAnswer_MovesThePositionToWhereTheWindowStopped_AndTheNextWindowStartsItsAttemptsAtOneAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var id = await _queueAsync(coordinator);
    await coordinator.MarkSubscriptionBackfillWindowRequestedAsync(id, 1, _now, _now.AddMinutes(180));

    var applied = await coordinator.RecordSubscriptionBackfillAnswerAsync(new BackfillWindowAnswer {
      BackfillId = id,
      Window = 1,
      EventCount = 1_000,
      FirstCommitSequence = 12,
      StoppedAtCommitSequence = 4_000,
      HasMore = true,
      HeadCommitSequence = 90_000,
    }, _now.AddMinutes(2));
    await coordinator.MarkSubscriptionBackfillWindowRequestedAsync(id, 2, _now.AddMinutes(3), _now.AddMinutes(183));
    var next = (await coordinator.GetSubscriptionBackfillsAsync()).Single();

    await Assert.That(applied).IsNotNull();
    await Assert.That(applied!.CursorCommitSequence).IsEqualTo(4_000L);
    await Assert.That(applied.EventsReceived).IsEqualTo(1_000L);
    await Assert.That(applied.FirstCommitSequence).IsEqualTo(12L);
    await Assert.That(applied.HeadCommitSequence).IsEqualTo(90_000L);
    await Assert.That(applied.AnswerDueBy).IsNull().Because("the answer landed, so the next window may be requested");
    await Assert.That(applied.CompletedAt).IsNull();
    await Assert.That(applied.Outcome).IsEqualTo(SubscriptionBackfillOutcome.Running);
    await Assert.That(next.WindowAttempts).IsEqualTo(1).Because("a new window starts its attempts at one");
  }

  [Test]
  public async Task RecordAnswer_WithNothingMore_CompletesTheBackfill_AndKeepsTheFirstSequenceAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var id = await _queueAsync(coordinator);
    await coordinator.MarkSubscriptionBackfillWindowRequestedAsync(id, 1, _now, _now.AddMinutes(180));
    await coordinator.RecordSubscriptionBackfillAnswerAsync(new BackfillWindowAnswer {
      BackfillId = id,
      Window = 1,
      EventCount = 10,
      FirstCommitSequence = 5,
      StoppedAtCommitSequence = 50,
      HasMore = true,
      HeadCommitSequence = 60,
    }, _now);
    await coordinator.MarkSubscriptionBackfillWindowRequestedAsync(id, 2, _now, _now.AddMinutes(180));

    var done = await coordinator.RecordSubscriptionBackfillAnswerAsync(new BackfillWindowAnswer {
      BackfillId = id,
      Window = 2,
      EventCount = 0,
      HasMore = false,
    }, _now.AddMinutes(1));

    await Assert.That(done!.CompletedAt).IsEqualTo(_now.AddMinutes(1));
    await Assert.That(done.Outcome).IsEqualTo(SubscriptionBackfillOutcome.Complete);
    await Assert.That(done.CursorCommitSequence).IsEqualTo(50L).Because("a window that selected nothing does not move the position");
    await Assert.That(done.FirstCommitSequence).IsEqualTo(5L);
    await Assert.That(done.HeadCommitSequence).IsEqualTo(60L).Because("an answer that knows no head keeps the last one");
    await Assert.That(done.EventsReceived).IsEqualTo(10L);
  }

  [Test]
  public async Task RecordAnswer_LateDuplicateOrForAnotherWindow_ChangesNothingAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var id = await _queueAsync(coordinator);
    var answer = new BackfillWindowAnswer { BackfillId = id, Window = 1, EventCount = 3, StoppedAtCommitSequence = 30, HasMore = true };

    var beforeAnyRequest = await coordinator.RecordSubscriptionBackfillAnswerAsync(answer, _now);
    await coordinator.MarkSubscriptionBackfillWindowRequestedAsync(id, 1, _now, _now.AddMinutes(180));
    var wrongWindow = await coordinator.RecordSubscriptionBackfillAnswerAsync(answer with { Window = 7 }, _now);
    var applied = await coordinator.RecordSubscriptionBackfillAnswerAsync(answer, _now);
    var duplicate = await coordinator.RecordSubscriptionBackfillAnswerAsync(answer, _now);
    var unknown = await coordinator.RecordSubscriptionBackfillAnswerAsync(answer with { BackfillId = TrackedGuid.New().Value }, _now);
    var row = (await coordinator.GetSubscriptionBackfillsAsync()).Single();

    await Assert.That(beforeAnyRequest).IsNull();
    await Assert.That(wrongWindow).IsNull();
    await Assert.That(applied).IsNotNull();
    await Assert.That(duplicate).IsNull().Because("a second copy of the answer would count its events twice");
    await Assert.That(unknown).IsNull();
    await Assert.That(row.EventsReceived).IsEqualTo(3L);
  }

  [Test]
  public async Task End_EndsARunningBackfill_AndAnAnswerAfterwardsChangesNothingAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var id = await _queueAsync(coordinator);
    await coordinator.MarkSubscriptionBackfillWindowRequestedAsync(id, 1, _now, _now.AddMinutes(180));

    await coordinator.EndSubscriptionBackfillAsync(id, SubscriptionBackfillOutcome.Unanswered, _now.AddHours(9));
    var late = await coordinator.RecordSubscriptionBackfillAnswerAsync(
      new BackfillWindowAnswer { BackfillId = id, Window = 1, EventCount = 3, StoppedAtCommitSequence = 30 }, _now.AddHours(10));
    await coordinator.EndSubscriptionBackfillAsync(id, SubscriptionBackfillOutcome.Complete, _now.AddHours(11));
    var row = (await coordinator.GetSubscriptionBackfillsAsync()).Single();

    await Assert.That(late).IsNull();
    await Assert.That(row.Outcome).IsEqualTo(SubscriptionBackfillOutcome.Unanswered).Because("an ended backfill stays as it ended");
    await Assert.That(row.CompletedAt).IsEqualTo(_now.AddHours(9));
    await Assert.That(row.AnswerDueBy).IsNull();
  }

  private static async Task<Guid> _queueAsync(IWorkCoordinator coordinator) {
    await coordinator.EnqueueSubscriptionBackfillsAsync([new SubscriptionBackfillSeed(_orders, "orders-svc", PLACED)]);
    return (await coordinator.GetSubscriptionBackfillsAsync()).Single().BackfillId;
  }
}
