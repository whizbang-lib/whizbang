// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2211: where a stream-level walk of an audit window stopped at the consumer's page budget is kept per (origin, window
/// start), so the next drill-down of the window resumes after it. A cursor is taken once; keeping one replaces the last
/// for its window; and the cursors of windows the seal has passed (never audited again) are forgotten.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/382_LargeWindowsAreWalkedAcrossAudits.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#a-large-window-is-walked-across-audits</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityDrillCursorSqlTests : EFCoreTestBase {
  private static readonly DateTimeOffset _t0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
  private readonly List<WorkCoordinationDbContext> _contexts = [];

  [Test]
  public async Task Take_AKeptCursor_ReturnsItOnce_ThenNothingAsync() {
    IWorkCoordinator coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var cursor = TrackedGuid.New().Value;

    await coordinator.IntegritySaveDrillCursorAsync(origin, 100, cursor, _t0);
    var first = await coordinator.IntegrityTakeDrillCursorAsync(origin, 100);
    var second = await coordinator.IntegrityTakeDrillCursorAsync(origin, 100);

    await Assert.That(first).IsEqualTo(cursor);
    await Assert.That(second).IsNull().Because("a cursor is used once, so a walk that ends or is dropped leaves none");
  }

  [Test]
  public async Task Take_WithNothingKept_IsNullAsync() {
    IWorkCoordinator coordinator = _coordinator();

    await Assert.That(await coordinator.IntegrityTakeDrillCursorAsync(TrackedGuid.New().Value, 100)).IsNull();
  }

  [Test]
  public async Task Save_AgainForTheSameWindow_KeepsTheLaterCursorAsync() {
    IWorkCoordinator coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var later = TrackedGuid.New().Value;

    await coordinator.IntegritySaveDrillCursorAsync(origin, 100, TrackedGuid.New().Value, _t0);
    await coordinator.IntegritySaveDrillCursorAsync(origin, 100, later, _t0.AddMinutes(1));

    await Assert.That(await coordinator.IntegrityTakeDrillCursorAsync(origin, 100)).IsEqualTo(later);
  }

  [Test]
  public async Task Cursors_OfOtherWindowsAndOrigins_AreKeptApartAsync() {
    IWorkCoordinator coordinator = _coordinator();
    var (origin, other) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var (a, b, c) = (TrackedGuid.New().Value, TrackedGuid.New().Value, TrackedGuid.New().Value);

    await coordinator.IntegritySaveDrillCursorAsync(origin, 100, a, _t0);
    await coordinator.IntegritySaveDrillCursorAsync(origin, 600, b, _t0);
    await coordinator.IntegritySaveDrillCursorAsync(other, 100, c, _t0);

    await Assert.That(await coordinator.IntegrityTakeDrillCursorAsync(origin, 600)).IsEqualTo(b);
    await Assert.That(await coordinator.IntegrityTakeDrillCursorAsync(other, 100)).IsEqualTo(c);
    await Assert.That(await coordinator.IntegrityTakeDrillCursorAsync(origin, 100)).IsEqualTo(a);
  }

  [Test]
  public async Task Save_ForgetsTheOriginsCursorsOfWindowsBelowItsSealAsync() {
    IWorkCoordinator coordinator = _coordinator();
    var (origin, other) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var kept = TrackedGuid.New().Value;
    await coordinator.IntegritySaveDrillCursorAsync(origin, 100, TrackedGuid.New().Value, _t0);
    await coordinator.IntegritySaveDrillCursorAsync(other, 100, kept, _t0);
    await coordinator.AdvanceIntegritySealAsync(origin, 500);

    await coordinator.IntegritySaveDrillCursorAsync(origin, 600, TrackedGuid.New().Value, _t0.AddMinutes(1));

    await Assert.That(await coordinator.IntegrityTakeDrillCursorAsync(origin, 100)).IsNull()
      .Because("the seal passed that window, so it is never audited again and its cursor is dead");
    await Assert.That(await coordinator.IntegrityTakeDrillCursorAsync(other, 100)).IsEqualTo(kept)
      .Because("another origin's seal says nothing about this one's windows");
  }

  [Test]
  public async Task Cursor_UnreachableStore_TakesNothing_AndKeepingDoesNotThrowAsync() {
    IWorkCoordinator coordinator = _coordinator();
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await using var drop = conn.CreateCommand();
      drop.CommandText = "DROP TABLE wh_integrity_drill_cursors";
      await drop.ExecuteNonQueryAsync();
    }
    var origin = TrackedGuid.New().Value;

    await coordinator.IntegritySaveDrillCursorAsync(origin, 100, TrackedGuid.New().Value, _t0);
    var taken = await coordinator.IntegrityTakeDrillCursorAsync(origin, 100);

    await Assert.That(taken).IsNull()
      .Because("a failure is logged and the drill-down starts from the first stream, as it did before the cursor existed");
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }
}
