// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The set-based write sink (#2246) against real Postgres: a batch of prepared rows is written with one statement
/// per table and shape, each row's outcome is told apart exactly as the per-stream write tells it, a row the
/// database rejects fails alone, and the stored documents are the bytes the per-stream write stores.
/// </summary>
/// <remarks>
/// Statements are counted by a statement-level trigger on the table, which fires once per statement whatever its row
/// count, so a count is a property of the SQL sent and never of timing.
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes</docs>
[Category("Integration")]
[Category("Shard3")]
public class SetBasedPerspectiveWriteSinkTests : EFCoreTestBase {
  private const string ACTION_TABLE = "wh_per_action_test";
  private const string VERSIONED_TABLE = "wh_per_versioned_item";
  private const string EXTRACTED_TABLE = "wh_per_init_only_extracted";
  private const string SPLIT_TABLE = "wh_per_init_only_split";

  // A fixed event time, so stored metadata and business time are the same on every run.
  private static readonly DateTime _eventAt = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
  private static readonly Guid _olderEventId = Guid.Parse("019f0000-0000-7aaa-8000-000000000001");
  private static readonly Guid _newerEventId = Guid.Parse("019f0001-0000-7aaa-8000-000000000001");

  // ── Inserts ──────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Insert_NNewRows_AreWrittenWithOneInsertStatementAsync() {
    await _countStatementsAsync(ACTION_TABLE);
    await using var context = CreateDbContext();
    var ids = Enumerable.Range(0, 25).Select(_ => Guid.CreateVersion7()).ToList();
    var writes = ids.Select((id, i) => _action(context, id, $"name-{i}", i, PerspectiveRowVersion.Absent)).ToList();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync(writes);

    await Assert.That(outcomes.Select(o => o.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteStatus.Written));
    await Assert.That(outcomes.Count).IsEqualTo(25);
    await Assert.That(await _statementsAsync(ACTION_TABLE, "INSERT")).IsEqualTo(1)
      .Because("N new streams are one insert statement for their table, not N");
    await Assert.That(await _countRowsAsync(ACTION_TABLE, ids)).IsEqualTo(25);
    var row = await _rowAsync(ACTION_TABLE, ids[7]);
    await Assert.That(row.Data).Contains("\"name-7\"");
    await Assert.That(row.Version).IsEqualTo(1);
  }

  [Test]
  public async Task Insert_OfARowThatAppearedSinceTheRead_IsAConflictOnItsVersionAsync() {
    var present = Guid.CreateVersion7();
    var fresh = Guid.CreateVersion7();
    await _seedActionAsync(present, "seeded", 1, commitSequence: 1);
    var actual = (await _rowAsync(ACTION_TABLE, present)).Xmin;
    await using var context = CreateDbContext();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      _action(context, present, "late", 2, PerspectiveRowVersion.Absent),
      _action(context, fresh, "new", 3, PerspectiveRowVersion.Absent),
    ]);

    await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Conflict);
    await Assert.That(outcomes[0].ActualVersion).IsEqualTo(PerspectiveRowVersion.Of(actual));
    await Assert.That(outcomes[1].Status).IsEqualTo(PerspectiveWriteStatus.Written);
    await Assert.That((await _rowAsync(ACTION_TABLE, present)).Data).Contains("\"seeded\"")
      .Because("an insert of a row the apply read as absent never overwrites one that appeared");
  }

  // ── Upserts ──────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Upsert_NewAndExistingRows_AreBothWrittenAsync() {
    var existing = Guid.CreateVersion7();
    var fresh = Guid.CreateVersion7();
    await _seedActionAsync(existing, "before", 1, commitSequence: 1);
    await using var context = CreateDbContext();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      _action(context, existing, "after", 2, PerspectiveRowVersion.Unchecked, commitSequence: 2),
      _action(context, fresh, "new", 3, PerspectiveRowVersion.Unchecked, commitSequence: 2),
    ]);

    await Assert.That(outcomes.Select(o => o.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteStatus.Written));
    var updated = await _rowAsync(ACTION_TABLE, existing);
    await Assert.That(updated.Data).Contains("\"after\"");
    await Assert.That(updated.Version).IsEqualTo(2);
    await Assert.That((await _rowAsync(ACTION_TABLE, fresh)).Data).Contains("\"new\"");
  }

  [Test]
  public async Task Upsert_WithAnOlderCommitSequence_IsRefusedAndLeavesTheRowAsync() {
    var id = Guid.CreateVersion7();
    await _seedActionAsync(id, "newer", 1, commitSequence: 10);
    await using var context = CreateDbContext();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      _action(context, id, "older", 2, PerspectiveRowVersion.Unchecked, commitSequence: 5),
    ]);

    await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Refused);
    await Assert.That((await _rowAsync(ACTION_TABLE, id)).Data).Contains("\"newer\"");
  }

  [Test]
  public async Task Upsert_OfTheSameRowTwiceInOneBatch_WritesBothInBatchOrderAsync() {
    var id = Guid.CreateVersion7();
    await _countStatementsAsync(ACTION_TABLE);
    await using var context = CreateDbContext();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      _action(context, id, "first", 1, PerspectiveRowVersion.Unchecked, commitSequence: 1),
      _action(context, Guid.CreateVersion7(), "other", 1, PerspectiveRowVersion.Unchecked, commitSequence: 1),
      _action(context, id, "second", 2, PerspectiveRowVersion.Unchecked, commitSequence: 2),
    ]);

    await Assert.That(outcomes.Select(o => o.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteStatus.Written));
    await Assert.That((await _rowAsync(ACTION_TABLE, id)).Data).Contains("\"second\"");
    await Assert.That(await _statementsAsync(ACTION_TABLE, "INSERT")).IsEqualTo(2)
      .Because("one statement cannot affect a row twice, so the second write of a row goes in a second round");
  }

  [Test]
  public async Task AScopeEvent_ReplacesTheStoredScope_AndOtherwiseItIsKept_OnUpsertAndUpdateAsync() {
    var forced = Guid.CreateVersion7();
    var kept = Guid.CreateVersion7();
    var forcedOnVersion = Guid.CreateVersion7();
    var keptOnVersion = Guid.CreateVersion7();
    foreach (var id in new[] { forced, kept, forcedOnVersion, keptOnVersion }) {
      await _seedActionAsync(id, "a", 1, commitSequence: 1);
    }
    var forcedVersion = (await _rowAsync(ACTION_TABLE, forcedOnVersion)).Xmin;
    var keptVersion = (await _rowAsync(ACTION_TABLE, keptOnVersion)).Xmin;
    await using var context = CreateDbContext();
    var scope = new PerspectiveScope { TenantId = "tenant-moved" };

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      _action(context, forced, "a2", 2, PerspectiveRowVersion.Unchecked, commitSequence: 2, scope: scope, forceUpdateScope: true),
      _action(context, kept, "a2", 2, PerspectiveRowVersion.Unchecked, commitSequence: 2, scope: scope),
      _action(context, forcedOnVersion, "a2", 2, PerspectiveRowVersion.Of(forcedVersion), commitSequence: 2, scope: scope, forceUpdateScope: true),
      _action(context, keptOnVersion, "a2", 2, PerspectiveRowVersion.Of(keptVersion), commitSequence: 2, scope: scope),
    ]);

    await Assert.That(outcomes.Select(o => o.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteStatus.Written));
    await Assert.That((await _rowAsync(ACTION_TABLE, forced)).Scope).Contains("tenant-moved");
    await Assert.That((await _rowAsync(ACTION_TABLE, kept)).Scope).DoesNotContain("tenant-moved");
    await Assert.That((await _rowAsync(ACTION_TABLE, forcedOnVersion)).Scope).Contains("tenant-moved");
    await Assert.That((await _rowAsync(ACTION_TABLE, keptOnVersion)).Scope).DoesNotContain("tenant-moved");
  }

  // ── Updates on a read version ────────────────────────────────────────────────────────────────

  [Test]
  public async Task Update_OnTheReadVersion_IsWrittenWithOneUpdateStatementAsync() {
    var ids = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToList();
    foreach (var id in ids) {
      await _seedActionAsync(id, "before", 1, commitSequence: 1);
    }
    var versions = new List<long>();
    foreach (var id in ids) {
      versions.Add((await _rowAsync(ACTION_TABLE, id)).Xmin);
    }
    await _countStatementsAsync(ACTION_TABLE);
    await using var context = CreateDbContext();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync(
      [.. ids.Select((id, i) => _action(context, id, $"after-{i}", 2, PerspectiveRowVersion.Of(versions[i]), commitSequence: 2))]);

    await Assert.That(outcomes.Select(o => o.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteStatus.Written));
    await Assert.That(await _statementsAsync(ACTION_TABLE, "UPDATE")).IsEqualTo(1);
    var row = await _rowAsync(ACTION_TABLE, ids[2]);
    await Assert.That(row.Data).Contains("\"after-2\"");
    await Assert.That(row.Version).IsEqualTo(2);
  }

  [Test]
  public async Task Update_OnAStaleVersion_IsAConflict_AndTheRestOfTheBatchIsWrittenAsync() {
    var moved = Guid.CreateVersion7();
    var steady = Guid.CreateVersion7();
    await _seedActionAsync(moved, "before", 1, commitSequence: 1);
    await _seedActionAsync(steady, "before", 1, commitSequence: 1);
    var readVersion = (await _rowAsync(ACTION_TABLE, moved)).Xmin;
    var steadyVersion = (await _rowAsync(ACTION_TABLE, steady)).Xmin;
    // Another writer moves the row after the apply read it.
    await _executeAsync($"UPDATE {ACTION_TABLE} SET data = jsonb_set(data, '{{Name}}', '\"collective\"') WHERE id = @id", moved);
    var actual = (await _rowAsync(ACTION_TABLE, moved)).Xmin;
    await using var context = CreateDbContext();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      _action(context, moved, "stale", 2, PerspectiveRowVersion.Of(readVersion), commitSequence: 2),
      _action(context, steady, "fresh", 2, PerspectiveRowVersion.Of(steadyVersion), commitSequence: 2),
    ]);

    await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Conflict);
    await Assert.That(outcomes[0].ActualVersion).IsEqualTo(PerspectiveRowVersion.Of(actual));
    await Assert.That((await _rowAsync(ACTION_TABLE, moved)).Data).Contains("\"collective\"");
    await Assert.That(outcomes[1].Status).IsEqualTo(PerspectiveWriteStatus.Written);
  }

  [Test]
  public async Task Update_OfARowThatVanished_IsAConflictWithNoRowAsync() {
    var id = Guid.CreateVersion7();
    await _seedActionAsync(id, "before", 1, commitSequence: 1);
    var readVersion = (await _rowAsync(ACTION_TABLE, id)).Xmin;
    await _executeAsync($"DELETE FROM {ACTION_TABLE} WHERE id = @id", id);
    await using var context = CreateDbContext();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      _action(context, id, "after", 2, PerspectiveRowVersion.Of(readVersion), commitSequence: 2),
    ]);

    await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Conflict);
    await Assert.That(outcomes[0].ActualVersion).IsEqualTo(PerspectiveRowVersion.Absent);
  }

  [Test]
  public async Task Update_WithAnOlderCommitSequence_OnTheReadVersion_IsRefusedAsync() {
    var id = Guid.CreateVersion7();
    await _seedActionAsync(id, "newer", 1, commitSequence: 10);
    var readVersion = (await _rowAsync(ACTION_TABLE, id)).Xmin;
    await using var context = CreateDbContext();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      _action(context, id, "older", 2, PerspectiveRowVersion.Of(readVersion), commitSequence: 5),
    ]);

    await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Refused)
      .Because("the row is still on the version the apply read, so the ordering guard refused it, as the per-stream write reports");
    await Assert.That((await _rowAsync(ACTION_TABLE, id)).Data).Contains("\"newer\"");
  }

  // ── The event-id guard ──────────────────────────────────────────────────────────────────────

  [Test]
  public async Task VersionedTarget_AnOlderEventId_IsRefusedOnEveryShapeAsync() {
    var upserted = Guid.CreateVersion7();
    var updated = Guid.CreateVersion7();
    foreach (var id in new[] { upserted, updated }) {
      await using var seed = CreateDbContext();
      await new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(seed, VERSIONED_TABLE, id,
        new VersionedItem { Id = id, State = "Completed", Value = 2 }, _meta(null, _newerEventId), new PerspectiveScope());
    }
    var readVersion = (await _rowAsync(VERSIONED_TABLE, updated)).Xmin;
    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      strategy.PreparePendingWrite(context, new BaseUpsertStrategy.UpsertRowArgs<VersionedItem>(VERSIONED_TABLE, upserted, new VersionedItem { Id = upserted, State = "Running", Value = 1 },
        _meta(null, _olderEventId), new PerspectiveScope(), null, false, PerspectiveRowVersion.Unchecked))!,
      strategy.PreparePendingWrite(context, new BaseUpsertStrategy.UpsertRowArgs<VersionedItem>(VERSIONED_TABLE, updated, new VersionedItem { Id = updated, State = "Running", Value = 1 },
        _meta(null, _olderEventId), new PerspectiveScope(), null, false, PerspectiveRowVersion.Of(readVersion)))!,
    ]);

    await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Refused);
    await Assert.That(outcomes[1].Status).IsEqualTo(PerspectiveWriteStatus.Refused);
    await Assert.That((await _rowAsync(VERSIONED_TABLE, upserted)).Data).Contains("\"Completed\"");
    await Assert.That((await _rowAsync(VERSIONED_TABLE, updated)).Data).Contains("\"Completed\"");
  }

  // ── Purges ───────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Purge_DeletesTheRows_WithOneStatement_AndAMissingRowIsWrittenAsync() {
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();
    await _seedActionAsync(a, "a", 1, commitSequence: 1);
    await _seedActionAsync(b, "b", 1, commitSequence: 1);
    await _countStatementsAsync(ACTION_TABLE);
    await using var context = CreateDbContext();
    var table = context.Model.FindEntityType(typeof(PerspectiveRow<ActionTestModel>))!.GetSchema() is { } schema
      ? $"\"{schema}\".{ACTION_TABLE}"
      : ACTION_TABLE;

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      PendingPerspectiveWrite.Purge(table, a),
      PendingPerspectiveWrite.Purge(table, b),
      PendingPerspectiveWrite.Purge(table, Guid.CreateVersion7()),
    ]);

    await Assert.That(outcomes.Select(o => o.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteStatus.Written));
    await Assert.That(await _countRowsAsync(ACTION_TABLE, [a, b])).IsEqualTo(0);
    await Assert.That(await _statementsAsync(ACTION_TABLE, "DELETE")).IsEqualTo(1);
  }

  // ── Physical fields ─────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task PhysicalFields_Extracted_AreWrittenToTheirColumns_OnInsertAndUpdateAsync() {
    var id = Guid.CreateVersion7();
    await using (var context = CreateDbContext()) {
      var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
        _extracted(context, id, "active", 7, PerspectiveRowVersion.Absent, commitSequence: 1),
      ]);
      await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Written);
    }
    await Assert.That(await _columnsAsync(EXTRACTED_TABLE, id)).IsEqualTo(("active", 7));

    var readVersion = (await _rowAsync(EXTRACTED_TABLE, id)).Xmin;
    await using (var context = CreateDbContext()) {
      var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
        _extracted(context, id, null, 9, PerspectiveRowVersion.Of(readVersion), commitSequence: 2),
      ]);
      await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Written);
    }
    await Assert.That(await _columnsAsync(EXTRACTED_TABLE, id)).IsEqualTo(((string?)null, 9))
      .Because("an update writes a null into its column too, typed by the column's store type");
  }

  [Test]
  public async Task PhysicalFields_OnAnUpsertOfAnExistingRow_AreWrittenToTheirColumnsAsync() {
    var id = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      _extracted(context, id, "active", 7, PerspectiveRowVersion.Absent, commitSequence: 1),
    ]);

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      _extracted(context, id, "moved", 12, PerspectiveRowVersion.Unchecked, commitSequence: 2),
    ]);

    await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Written);
    await Assert.That(await _columnsAsync(EXTRACTED_TABLE, id)).IsEqualTo(("moved", 12));
  }

  [Test]
  public async Task PhysicalFields_Split_AreWrittenToTheirColumns_OnInsertAndUpdateAsync() {
    var id = Guid.CreateVersion7();
    await using (var context = CreateDbContext()) {
      await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
        _split(context, id, "active", 7, "first", PerspectiveRowVersion.Absent, commitSequence: 1),
      ]);
    }
    await Assert.That(await _columnsAsync(SPLIT_TABLE, id)).IsEqualTo(("active", 7));

    var readVersion = (await _rowAsync(SPLIT_TABLE, id)).Xmin;
    await using (var context = CreateDbContext()) {
      var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
        _split(context, id, "done", 8, "second", PerspectiveRowVersion.Of(readVersion), commitSequence: 2),
      ]);
      await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Written);
    }
    await Assert.That(await _columnsAsync(SPLIT_TABLE, id)).IsEqualTo(("done", 8));
    await Assert.That((await _rowAsync(SPLIT_TABLE, id)).Data).Contains("\"second\"");
  }

  [Test]
  public async Task PhysicalFields_WithNoStoreType_AreStillWrittenOnUpdateAsync() {
    var id = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      _extracted(context, id, "active", 7, PerspectiveRowVersion.Absent, commitSequence: 1),
    ]);
    var readVersion = (await _rowAsync(EXTRACTED_TABLE, id)).Xmin;
    var write = _extracted(context, id, "typed", 11, PerspectiveRowVersion.Of(readVersion), commitSequence: 2);
    var untyped = _withRow(write, write.Row! with {
      PhysicalValues = [.. write.Row!.PhysicalValues.Select(v => v with { StoreType = null })],
    });

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([untyped]);

    await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Written);
    await Assert.That(await _columnsAsync(EXTRACTED_TABLE, id)).IsEqualTo(("typed", 11));
  }

  [Test]
  public async Task AStoreTypeThatIsNotAPlainTypeName_FailsThatWriteAloneAsync() {
    await using var context = CreateDbContext();
    var bad = _extracted(context, Guid.CreateVersion7(), "x", 1, PerspectiveRowVersion.Of(1), commitSequence: 1);
    bad = _withRow(bad, bad.Row! with {
      PhysicalValues = [bad.Row!.PhysicalValues[0] with { StoreType = "text; DROP TABLE x" }, bad.Row!.PhysicalValues[1]],
    });
    var good = Guid.CreateVersion7();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      bad,
      _extracted(context, good, "ok", 2, PerspectiveRowVersion.Absent, commitSequence: 1),
    ]);

    await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Failed);
    await Assert.That(outcomes[0].Error).IsTypeOf<ArgumentException>();
    await Assert.That(outcomes[1].Status).IsEqualTo(PerspectiveWriteStatus.Written);
  }

  // ── Expiry, activity and version decisions ───────────────────────────────────────────────────

  [Test]
  public async Task Expiry_IsWritten_OnInsertUpsertAndUpdateAsync() {
    var inserted = Guid.CreateVersion7();
    var upserted = Guid.CreateVersion7();
    var updated = Guid.CreateVersion7();
    await _seedActionAsync(upserted, "u", 1, commitSequence: 1);
    await _seedActionAsync(updated, "v", 1, commitSequence: 1);
    var readVersion = (await _rowAsync(ACTION_TABLE, updated)).Xmin;
    var expiresAt = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
    await using var context = CreateDbContext();
    PendingPerspectiveWrite expiring(PendingPerspectiveWrite write) => _withRow(write, write.Row! with { ExpiresAt = expiresAt });

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      expiring(_action(context, inserted, "i", 1, PerspectiveRowVersion.Absent, commitSequence: 2)),
      expiring(_action(context, upserted, "u2", 2, PerspectiveRowVersion.Unchecked, commitSequence: 2)),
      expiring(_action(context, updated, "v2", 2, PerspectiveRowVersion.Of(readVersion), commitSequence: 2)),
    ]);

    await Assert.That(outcomes.Select(o => o.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteStatus.Written));
    foreach (var id in new[] { inserted, upserted, updated }) {
      await Assert.That(await _expiresAtAsync(id)).IsEqualTo(expiresAt);
    }
  }

  [Test]
  public async Task ANonActivityWrite_KeepsBusinessTimeAndVersion_OnUpsertAndUpdateAsync() {
    var upserted = Guid.CreateVersion7();
    var updated = Guid.CreateVersion7();
    await _seedActionAsync(upserted, "u", 1, commitSequence: 1);
    await _seedActionAsync(updated, "v", 1, commitSequence: 1);
    var before = await _rowAsync(ACTION_TABLE, upserted);
    var readVersion = (await _rowAsync(ACTION_TABLE, updated)).Xmin;
    await using var context = CreateDbContext();
    PendingPerspectiveWrite quiet(PendingPerspectiveWrite write) => _withRow(write, write.Row! with {
      SuppressActivity = true,
      BumpVersion = false,
      UpdatedAt = _eventAt.AddDays(3),
    });

    await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      quiet(_action(context, upserted, "u2", 2, PerspectiveRowVersion.Unchecked, commitSequence: 2)),
      quiet(_action(context, updated, "v2", 2, PerspectiveRowVersion.Of(readVersion), commitSequence: 2)),
    ]);

    foreach (var id in new[] { upserted, updated }) {
      var row = await _rowAsync(ACTION_TABLE, id);
      await Assert.That(row.UpdatedAt).IsEqualTo(before.UpdatedAt);
      await Assert.That(row.Version).IsEqualTo(1);
      await Assert.That(row.Data).Contains("2\"");
    }
  }

  // ── Mixed batches ────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task AMixedBatchAcrossTwoTables_IsOneStatementPerTableAndOperationAsync() {
    await _countStatementsAsync(ACTION_TABLE);
    await _countStatementsAsync(VERSIONED_TABLE);
    var existing = Guid.CreateVersion7();
    await _seedActionAsync(existing, "before", 1, commitSequence: 1);
    var readVersion = (await _rowAsync(ACTION_TABLE, existing)).Xmin;
    await _executeAsync("TRUNCATE sink_statements", Guid.Empty);
    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var writes = new List<PendingPerspectiveWrite> {
      _action(context, existing, "after", 2, PerspectiveRowVersion.Of(readVersion), commitSequence: 2),
    };
    for (var i = 0; i < 4; i++) {
      var actionId = Guid.CreateVersion7();
      var itemId = Guid.CreateVersion7();
      writes.Add(_action(context, actionId, $"a{i}", i, PerspectiveRowVersion.Absent, commitSequence: 1));
      writes.Add(strategy.PreparePendingWrite(context, new BaseUpsertStrategy.UpsertRowArgs<VersionedItem>(VERSIONED_TABLE, itemId, new VersionedItem { Id = itemId, State = $"s{i}", Value = i },
        _meta(1, Guid.CreateVersion7()), new PerspectiveScope(), null, false, PerspectiveRowVersion.Absent))!);
    }

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync(writes);

    await Assert.That(outcomes.Select(o => o.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteStatus.Written));
    await Assert.That(await _statementsAsync(ACTION_TABLE, "INSERT")).IsEqualTo(1);
    await Assert.That(await _statementsAsync(ACTION_TABLE, "UPDATE")).IsEqualTo(1);
    await Assert.That(await _statementsAsync(VERSIONED_TABLE, "INSERT")).IsEqualTo(1);
  }

  // ── Failure isolation ────────────────────────────────────────────────────────────────────────

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task ARowTheDatabaseRejects_FailsAlone_AndTheOthersAreWrittenAsync(bool inTransaction) {
    await _executeAsync($"ALTER TABLE {EXTRACTED_TABLE} ADD CONSTRAINT ck_sink_priority CHECK (priority >= 0)", Guid.Empty);
    var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToList();
    await using var context = CreateDbContext();
    var writes = ids.Select((id, i) => _extracted(context, id, $"s{i}", i == 3 ? -1 : i, PerspectiveRowVersion.Absent, commitSequence: 1)).ToList();

    IReadOnlyList<PerspectiveWriteOutcome> outcomes;
    if (inTransaction) {
      await using var transaction = await context.Database.BeginTransactionAsync();
      outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync(writes);
      await transaction.CommitAsync();
    } else {
      outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync(writes);
    }

    await Assert.That(outcomes[3].Status).IsEqualTo(PerspectiveWriteStatus.Failed);
    await Assert.That(outcomes[3].Error).IsTypeOf<PostgresException>();
    await Assert.That(((PostgresException)outcomes[3].Error!).SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
    foreach (var i in new[] { 0, 1, 2, 4 }) {
      await Assert.That(outcomes[i].Status).IsEqualTo(PerspectiveWriteStatus.Written);
    }
    await Assert.That(await _countRowsAsync(EXTRACTED_TABLE, ids)).IsEqualTo(4)
      .Because("the rejected row is isolated by splitting its statement; every other row is written and committed");
  }

  [Test]
  public async Task AValueNpgsqlCannotBind_FailsThatRowAloneAsync() {
    await using var context = CreateDbContext();
    var bad = _extracted(context, Guid.CreateVersion7(), "x", 1, PerspectiveRowVersion.Absent, commitSequence: 1);
    bad = _withRow(bad, bad.Row! with {
      PhysicalValues = [bad.Row!.PhysicalValues[0] with { Value = new UnbindableValue() }, bad.Row!.PhysicalValues[1]],
    });
    var good = Guid.CreateVersion7();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([
      bad,
      _extracted(context, good, "ok", 2, PerspectiveRowVersion.Absent, commitSequence: 1),
    ]);

    await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Failed);
    await Assert.That(outcomes[0].Error).IsTypeOf<InvalidCastException>();
    await Assert.That(outcomes[1].Status).IsEqualTo(PerspectiveWriteStatus.Written);
  }

  [Test]
  public async Task ACanceledWrite_Propagates_RatherThanFailingRowsAsync() {
    await using var context = CreateDbContext();
    await context.Database.OpenConnectionAsync();
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(async () => await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync(
        [_action(context, Guid.CreateVersion7(), "x", 1, PerspectiveRowVersion.Absent)], canceled.Token))
      .Throws<OperationCanceledException>();
  }

  // ── The one writer rule ──────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Documents_AreByteIdenticalToTheSingleRowWrite_OnEveryShapeAsync() {
    var id = Guid.CreateVersion7();
    var (Inserted, Updated) = await _writeTwiceAsync(id, useSink: false);
    await _executeAsync($"DELETE FROM {SPLIT_TABLE} WHERE id = @id", id);
    var sink = await _writeTwiceAsync(id, useSink: true);

    await Assert.That(sink.Inserted).IsEqualTo(Inserted);
    await Assert.That(sink.Updated).IsEqualTo(Updated);
  }

  /// <summary>Inserts a row and then updates it on its read version, by one path, and reads the row after each.</summary>
  private async Task<(RowSnapshot Inserted, RowSnapshot Updated)> _writeTwiceAsync(Guid id, bool useSink) {
    var strategy = new PostgresUpsertStrategy();
    async Task writeAsync(InitOnlySplitModel model, long sequence, PerspectiveRowVersion expected) {
      await using var context = CreateDbContext();
      var physical = new Dictionary<string, object?> { ["status"] = model.Status, ["priority"] = model.Priority };
      if (useSink) {
        var write = strategy.PreparePendingWrite(context, new BaseUpsertStrategy.UpsertRowArgs<InitOnlySplitModel>(SPLIT_TABLE, id, model, _meta(sequence, null), _scope, physical, false, expected))!;
        var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([write]);
        await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Written);
      } else {
        await Assert.That(await strategy.TryUpsertPerspectiveRowWithPhysicalFieldsAsync(
          context, SPLIT_TABLE, id, model, _meta(sequence, null), _scope, physical, false, expected)).IsTrue();
      }
    }

    await writeAsync(new InitOnlySplitModel { Id = id, Status = "active", Priority = 3, Note = "café ✓ \"quoted\"" }, 1, PerspectiveRowVersion.Absent);
    var inserted = await _snapshotAsync(id);
    await writeAsync(new InitOnlySplitModel { Id = id, Status = null, Priority = 4, Note = "second" }, 2, PerspectiveRowVersion.Of(inserted.Xmin));
    return (inserted with { Xmin = 0 }, (await _snapshotAsync(id)) with { Xmin = 0 });
  }

  private static readonly PerspectiveScope _scope = new() { TenantId = "tenant-parity", UserId = "user-parity", AllowedPrincipals = ["group:a"] };

  private sealed record RowSnapshot(string Data, string Metadata, string Scope, long Xmin, int Version, DateTime CreatedAt, DateTime UpdatedAt, string? Status, int Priority);

  private async Task<RowSnapshot> _snapshotAsync(Guid id) {
    var row = await _rowAsync(SPLIT_TABLE, id);
    var (status, priority) = await _columnsAsync(SPLIT_TABLE, id);
    return new RowSnapshot(row.Data, row.Metadata, row.Scope, row.Xmin, row.Version, row.CreatedAt, row.UpdatedAt, status, priority);
  }

  // ── The sink's own contract ──────────────────────────────────────────────────────────────────

  [Test]
  public async Task Sink_RefusesANullContextOrBatch_AndAnEmptyBatchWritesNothingAsync() {
    await Assert.That(() => new EFCorePostgresPerspectiveWriteSink(null!)).Throws<ArgumentNullException>();
    await using var context = _inMemoryContext();
    var sink = new EFCorePostgresPerspectiveWriteSink(context);

    await Assert.That(async () => await sink.WriteAsync(null!)).Throws<ArgumentNullException>();
    await Assert.That(await sink.WriteAsync([])).IsEmpty();
  }

  [Test]
  public async Task Sink_OnAProviderOtherThanNpgsql_RefusesToWriteAsync() {
    await using var context = _inMemoryContext();

    await Assert.That(async () => await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync(
        [PendingPerspectiveWrite.Purge(ACTION_TABLE, Guid.CreateVersion7())]))
      .Throws<InvalidOperationException>().WithMessageContaining("writes through Npgsql");
  }

  [Test]
  public async Task PreparePendingWrite_DeclinesWhereTheAtomicStatementDoesNotApplyAsync() {
    await using var context = CreateDbContext();
    await using var inMemory = _inMemoryContext();
    var id = Guid.CreateVersion7();
    var model = new ActionTestModel { Id = id, Name = "x", Value = 1 };

    await Assert.That(UpsertWritePath.Strategy(atomic: false).PreparePendingWrite(context, new BaseUpsertStrategy.UpsertRowArgs<ActionTestModel>(ACTION_TABLE, id, model,
      _meta(1, null), new PerspectiveScope(), null, false, PerspectiveRowVersion.Absent))).IsNull();
    await Assert.That(new PostgresUpsertStrategy().PreparePendingWrite(inMemory, new BaseUpsertStrategy.UpsertRowArgs<ActionTestModel>(ACTION_TABLE, id, model,
      _meta(1, null), new PerspectiveScope(), null, false, PerspectiveRowVersion.Absent))).IsNull();
  }

  [Test]
  public async Task Writer_SplitsAGroupToItsParameterBudgetAsync() {
    await _countStatementsAsync(ACTION_TABLE);
    await using var context = CreateDbContext();
    var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToList();
    var writes = ids.Select((id, i) => _action(context, id, $"n{i}", i, PerspectiveRowVersion.Absent)).ToList();
    var budget = PerspectiveWriteSql.GROUP_PARAMETERS + (2 * PerspectiveWriteSql.ROW_PARAMETERS);
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    var outcomes = await PostgresPerspectiveWriter.WriteAsync(connection, null, writes, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None, budget);

    await Assert.That(outcomes.Select(o => o.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteStatus.Written));
    await Assert.That(await _statementsAsync(ACTION_TABLE, "INSERT")).IsEqualTo(3)
      .Because("two rows fit the budget, so five rows take three statements");
  }

  [Test]
  public async Task Writer_RefusesANullConnectionOrBatchAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);

    await Assert.That(async () => await PostgresPerspectiveWriter.WriteAsync(null!, null, [], new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await PostgresPerspectiveWriter.WriteAsync(connection, null, null!, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  [Test]
  [Arguments("integer", true)]
  [Arguments("character varying(200)", true)]
  [Arguments("timestamp with time zone", true)]
  [Arguments("numeric(10,2)", true)]
  [Arguments("vector(3)", true)]
  [Arguments("text[]", true)]
  [Arguments("public.citext", true)]
  [Arguments("", false)]
  [Arguments(" ", false)]
  [Arguments("text; DROP TABLE x", false)]
  [Arguments("text'", false)]
  public async Task IsPlainStoreType_AcceptsTypeNamesOnlyAsync(string storeType, bool expected) {
    await Assert.That(PerspectiveWriteSql.IsPlainStoreType(storeType)).IsEqualTo(expected);
  }

  [Test]
  public async Task PhysicalParameter_ANullDocument_IsASqlNullAsync() {
    var parameter = PerspectiveWriteSql.PhysicalParameter("p", new PerspectivePhysicalValue("c", null, "jsonb", IsJsonDocument: true));

    await Assert.That(parameter.Value).IsEqualTo(DBNull.Value);
  }

  // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

  private static PerspectiveMetadata _meta(long? commitSequence, Guid? eventId) => new() {
    EventType = "SinkTestEvent",
    EventId = (eventId ?? Guid.Parse("019f0002-0000-7aaa-8000-000000000001")).ToString("D"),
    Timestamp = _eventAt,
    CommitSequence = commitSequence,
  };

  private static PendingPerspectiveWrite _action(
      DbContext context, Guid id, string name, int value, PerspectiveRowVersion expected, long? commitSequence = null,
      PerspectiveScope? scope = null, bool forceUpdateScope = false) =>
    new PostgresUpsertStrategy().PreparePendingWrite(context, new BaseUpsertStrategy.UpsertRowArgs<ActionTestModel>(ACTION_TABLE, id, new ActionTestModel { Id = id, Name = name, Value = value },
      _meta(commitSequence, null), scope ?? new PerspectiveScope(), null, forceUpdateScope, expected))!;

  private static PendingPerspectiveWrite _extracted(
      DbContext context, Guid id, string? status, int priority, PerspectiveRowVersion expected, long commitSequence) =>
    new PostgresUpsertStrategy().PreparePendingWrite(context, new BaseUpsertStrategy.UpsertRowArgs<InitOnlyExtractedModel>(EXTRACTED_TABLE, id,
      new InitOnlyExtractedModel { Id = id, Status = status, Priority = priority }, _meta(commitSequence, null), new PerspectiveScope(),
      new Dictionary<string, object?> { ["status"] = status, ["priority"] = priority }, false, expected))!;

  private static PendingPerspectiveWrite _split(
      DbContext context, Guid id, string? status, int priority, string note, PerspectiveRowVersion expected, long commitSequence) =>
    new PostgresUpsertStrategy().PreparePendingWrite(context, new BaseUpsertStrategy.UpsertRowArgs<InitOnlySplitModel>(SPLIT_TABLE, id,
      new InitOnlySplitModel { Id = id, Status = status, Priority = priority, Note = note }, _meta(commitSequence, null), new PerspectiveScope(),
      new Dictionary<string, object?> { ["status"] = status, ["priority"] = priority }, false, expected))!;

  private static PendingPerspectiveWrite _withRow(PendingPerspectiveWrite write, PerspectiveRowValues row) =>
    PendingPerspectiveWrite.Write(write.Table, write.StreamId, write.ExpectedVersion, row);

  private async Task _seedActionAsync(Guid id, string name, int value, long commitSequence) {
    await using var context = CreateDbContext();
    await new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(context, ACTION_TABLE, id,
      new ActionTestModel { Id = id, Name = name, Value = value }, _meta(commitSequence, null), new PerspectiveScope());
  }

  private static ProviderOnlyContext _inMemoryContext() =>
    new(new DbContextOptionsBuilder<ProviderOnlyContext>().UseInMemoryDatabase(Guid.CreateVersion7().ToString()).Options);

  /// <summary>A context on a provider other than Npgsql, with nothing mapped: only its provider is under test.</summary>
  private sealed class ProviderOnlyContext(DbContextOptions<ProviderOnlyContext> options) : DbContext(options);

  /// <summary>A value of a type no Npgsql type handler writes.</summary>
  private sealed class UnbindableValue;

  private sealed record StoredRow(string Data, string Metadata, string Scope, long Xmin, int Version, DateTime CreatedAt, DateTime UpdatedAt);

  private async Task<StoredRow> _rowAsync(string table, Guid id) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
      $"SELECT data::text, metadata::text, scope::text, xmin::text::bigint, version, created_at, updated_at FROM {table} WHERE id = @id", connection);
    command.Parameters.AddWithValue(nameof(id), id);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"No row {id} in {table}.");
    }
    return new StoredRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4),
      reader.GetDateTime(5), reader.GetDateTime(6));
  }

  private async Task<(string? Status, int Priority)> _columnsAsync(string table, Guid id) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand($"SELECT status, priority FROM {table} WHERE id = @id", connection);
    command.Parameters.AddWithValue(nameof(id), id);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (await reader.IsDBNullAsync(0) ? null : reader.GetString(0), reader.GetInt32(1));
  }

  private async Task<DateTime?> _expiresAtAsync(Guid id) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand($"SELECT expires_at FROM {ACTION_TABLE} WHERE id = @id", connection);
    command.Parameters.AddWithValue(nameof(id), id);
    return await command.ExecuteScalarAsync() is DateTime value ? value : null;
  }

  private async Task<long> _countRowsAsync(string table, IReadOnlyList<Guid> ids) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE id = ANY(@ids)", connection);
    command.Parameters.AddWithValue(nameof(ids), ids.ToArray());
    return (long)(await command.ExecuteScalarAsync())!;
  }

  private async Task _executeAsync(string sql, Guid id) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue(nameof(id), id);
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>Records every statement that inserts, updates or deletes on the table, once per statement.</summary>
  private async Task _countStatementsAsync(string table) {
    await _executeAsync($"""
      CREATE TABLE IF NOT EXISTS sink_statements (tbl text NOT NULL, op text NOT NULL);
      CREATE OR REPLACE FUNCTION sink_count() RETURNS trigger LANGUAGE plpgsql AS $$
      BEGIN
        INSERT INTO sink_statements VALUES (TG_TABLE_NAME, TG_OP);
        RETURN NULL;
      END $$;
      CREATE TRIGGER sink_count_{table} AFTER INSERT OR UPDATE OR DELETE ON {table}
        FOR EACH STATEMENT EXECUTE FUNCTION sink_count();
      """, Guid.Empty);
  }

  private async Task<long> _statementsAsync(string table, string operation) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand("SELECT count(*) FROM sink_statements WHERE tbl = @t AND op = @op", connection);
    command.Parameters.AddWithValue("t", table);
    command.Parameters.AddWithValue("op", operation);
    return (long)(await command.ExecuteScalarAsync())!;
  }
}
