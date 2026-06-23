using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// REDs that lock the cross-pod stale-read regression race the production strand traces back to.
///
/// <para>The race: two perspective workers (different pods, or the same pod's parallel slice 17
/// consumer threads) each receive an event for the same stream. Both load the projection row at
/// approximately the same time and BOTH see "no row" or an earlier state. Each applies its own
/// event against its loaded model. Both then call <see cref="IDbUpsertStrategy.UpsertPerspectiveRowAsync"/>.
/// The atomic <c>INSERT … ON CONFLICT (id) DO UPDATE</c> collapses any insert race, but the data
/// in the UPDATE branch is the second writer's stale-read-derived in-memory model — which may
/// have been computed against an earlier state. The result is last-writer-wins on the row's
/// data, even when one writer was applying a forward transition (Pending → Completed) and the
/// other was applying a backward-only-from-Pending transition (Pending → Running). The
/// production strand was: pod B's Started write regressed a row pod A had already advanced to
/// Completed (saga 019ee73d / 019ef473).</para>
///
/// <para>These tests are RED today. They go GREEN when the storage layer learns to refuse a
/// write that would regress a row's advancement signal, OR when stream-pinning is enforced
/// so the second pod can't process the same stream concurrently. Either path closes the race
/// at the framework level; consumers shouldn't have to invent it per saga.</para>
/// </summary>
[Category("Integration")]
[Category("Regression")]
[Category("Storage")]
public class CrossPodStaleReadRegressionRaceTests : EFCoreTestBase {
  [After(Test)]
  public Task ClearPathOneProviderAsync() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
    return Task.CompletedTask;
  }

  private static void EnableAtomicPath() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>
      PerspectivePersistenceJsonContext.CreateOptions(
        MessageJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);
  }

  private static PerspectiveMetadata Meta(string eventType) =>
    new() {
      EventType = eventType,
      EventId = TrackedGuid.NewMedo().Value.ToString(),
      Timestamp = DateTime.UtcNow,
      CommitSequence = null  // Deliberately null — production saga_item writes ship NULL here today.
    };

  private static Order OrderWith(Guid id, string status, decimal amount) =>
    new() {
      OrderId = new TestOrderId(id),
      Amount = amount,
      Status = status
    };

  // ────────────────────────────────────────────────────────────────────
  //   RED-3a: stale-read order (Completed-first, Running-second-stale)
  // ────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Pod A loads the row (doesn't exist). Pod A applies its terminal event → in-memory
  /// "Completed/200". Pod A commits.
  ///
  /// <para>Pod B's load happened BEFORE A's commit, so B's in-memory model was computed
  /// against "no row" too. B applies an earlier-lifecycle event → in-memory "Running/50".
  /// B commits AFTER A. B's UPSERT hits ON CONFLICT (the row A inserted) → DO UPDATE →
  /// the row's data is overwritten with B's stale "Running/50". The production strand
  /// shape: per-item projection regresses from terminal back to Running.</para>
  ///
  /// <para>Current behavior: row ends at "Running". After the framework-level fix
  /// (optimistic-concurrency / stream-pinning / perspective-state-aware guard):
  /// row stays at "Completed".</para>
  /// </summary>
  [Test]
  public async Task StaleSecondWriter_RegressesTerminalRowToEarlierState_StoreFailsToProtectAsync() {
    EnableAtomicPath();
    var id = TrackedGuid.NewMedo().Value;
    var strategy = new PostgresUpsertStrategy();
    var scope = new PerspectiveScope();

    // ─ Pod A: load (no row), apply terminal event in-memory, write ─
    await using (var ctxA = CreateDbContext()) {
      // (Equivalent of a runner loading "no row" then Applying Completed)
      var modelA = OrderWith(id, "Completed", 200m);
      await strategy.UpsertPerspectiveRowAsync(ctxA, "wh_per_order", id, modelA, Meta("OrderCompleted"), scope);
    }

    // ─ Pod B: had loaded the same "no row" snapshot BEFORE A committed, applied an earlier
    //   lifecycle event in its own in-memory model, now goes to write ─
    await using (var ctxB = CreateDbContext()) {
      var staleModelB = OrderWith(id, "Running", 50m);
      await strategy.UpsertPerspectiveRowAsync(ctxB, "wh_per_order", id, staleModelB, Meta("OrderRunning"), scope);
    }

    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>()
      .AsNoTracking()
      .FirstOrDefaultAsync(r => r.Id == id);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Status)
      .IsEqualTo("Completed")
      .Because("Storage must NOT let a stale concurrent apply regress a row from its terminal state. The production strand is " +
               "exactly this: pod B's earlier-lifecycle write overwrote pod A's terminal write because both wrote with " +
               "NULL commit_sequence and the WHERE clause auto-accepted the regression. Until the framework closes this " +
               "race (optimistic-concurrency, stream-pinning, or a perspective-aware guard), consumers cannot trust " +
               "projection rows to be monotone.");
    await Assert.That(row.Data.Amount)
      .IsEqualTo(200m)
      .Because("Amount tracks with Status — A's terminal write set 200, B's regression set 50. Asserting both sticks " +
               "the row to A's snapshot as a whole, not just the State field.");
  }

  // ────────────────────────────────────────────────────────────────────
  //   RED-3b: order-independence — Running-first, Completed-second
  // ────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Symmetric case to RED-3a. When the writes happen in the OTHER order (Running first,
  /// then Completed second), the storage layer does the right thing today by accident —
  /// the second writer's data wins, and that data happens to be terminal. The race only
  /// strands when the regressor writes second.
  ///
  /// <para>This test locks the "Completed-second is fine" invariant — proves the fix
  /// preserves it. Today: passes. After fix: still passes (regression detection should
  /// be advancement-aware, not order-aware).</para>
  /// </summary>
  [Test]
  public async Task RunningFirstThenCompletedSecond_RowEndsAtCompleted_NoRegressionAsync() {
    EnableAtomicPath();
    var id = TrackedGuid.NewMedo().Value;
    var strategy = new PostgresUpsertStrategy();
    var scope = new PerspectiveScope();

    await using (var ctxA = CreateDbContext()) {
      var modelA = OrderWith(id, "Running", 50m);
      await strategy.UpsertPerspectiveRowAsync(ctxA, "wh_per_order", id, modelA, Meta("OrderRunning"), scope);
    }

    await using (var ctxB = CreateDbContext()) {
      var modelB = OrderWith(id, "Completed", 200m);
      await strategy.UpsertPerspectiveRowAsync(ctxB, "wh_per_order", id, modelB, Meta("OrderCompleted"), scope);
    }

    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>()
      .AsNoTracking()
      .FirstOrDefaultAsync(r => r.Id == id);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Status)
      .IsEqualTo("Completed")
      .Because("Forward-progressing concurrent writes must converge to the most-advanced state regardless of order. " +
               "The fix for RED-3a must preserve this — no over-correction that blocks legitimate forward writes.");
  }

  // ────────────────────────────────────────────────────────────────────
  //   RED-3c: the production strand exact shape on saga_item-like data
  // ────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Pins the production strand we observed on production saga 019ef473: 350 saga_items, 349
  /// at State=Completed (terminal), 1 at State=Running. The stranded item's per-item stream
  /// has BOTH SagaItemStartedEvent and SagaItemCompletedEvent durably committed to wh_event_store,
  /// yet the projection sits at Running because pod B's Started write overwrote pod A's
  /// Completed write.
  ///
  /// <para>Modeled here with the Order surface (Status field stands in for SagaItemState).
  /// The aim is to lock the framework-level invariant: even with NULL metadata on both writes,
  /// the row must not regress to an earlier state if the data is provably earlier. Today: fails.
  /// After fix: passes.</para>
  /// </summary>
  [Test]
  public async Task SlotThree_ThreeFiftyItemStrand_OneItemRegressedAndLeftAtRunningAsync() {
    EnableAtomicPath();
    var id = TrackedGuid.NewMedo().Value;
    var strategy = new PostgresUpsertStrategy();
    var scope = new PerspectiveScope();

    // T1: pod A processes SagaItemCompletedEvent for item-171, writes row Completed.
    await using (var ctxA = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        ctxA, "wh_per_order", id,
        OrderWith(id, "Completed", 1m),
        Meta("SagaItemCompletedEvent"),
        scope);
    }

    // T2: pod B (different pod, stale read from before T1) processes the corresponding
    //     SagaItemStartedEvent and writes row Running. Production race.
    await using (var ctxB = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        ctxB, "wh_per_order", id,
        OrderWith(id, "Running", 1m),
        Meta("SagaItemStartedEvent"),
        scope);
    }

    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>()
      .AsNoTracking()
      .FirstOrDefaultAsync(r => r.Id == id);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Status)
      .IsEqualTo("Completed")
      .Because("production strand exact shape (saga 019ef473, item 171 on 2026-06-23): the Completed event was processed " +
               "first and made it durable; the Started event came in late on a different pod and overwrote the row. " +
               "Both wh_event_store events exist; only the projection lies. The framework — not each consumer — " +
               "must guarantee monotone forward progress at the storage layer.");
    await Assert.That(row.Version)
      .IsEqualTo(2)
      .Because("Two upsert attempts means the row's version moves 1 → 2 even when the second is rejected on a data " +
               "basis. The lock asserts version movement is unchanged so the fix doesn't accidentally also block the " +
               "INSERT/UPDATE round-trip.");
  }
}
