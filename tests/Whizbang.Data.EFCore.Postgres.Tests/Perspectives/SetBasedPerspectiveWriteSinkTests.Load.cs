// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The sink's bulk load (#2248), the write path of a blue-green rebuild's staging load: new rows are copied into the
/// table with <c>COPY</c> rather than inserted, with exactly the values the insert writes, and anything the copy cannot
/// take is written by the set-based statements, which isolate a row the database refuses.
/// </summary>
public partial class SetBasedPerspectiveWriteSinkTests {
  [Test]
  public async Task Load_CopiesNewRows_WithTheValuesTheInsertWrites_AndIssuesNoInsertAsync() {
    var id = Guid.CreateVersion7();
    var model = new InitOnlySplitModel { Id = id, Status = "active", Priority = 3, Note = "café ✓ \"quoted\"" };
    await using (var context = CreateDbContext()) {
      var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([_splitWrite(context, model)]);
      await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Written);
    }
    var inserted = (await _snapshotAsync(id)) with { Xmin = 0 };
    await _executeAsync($"DELETE FROM {SPLIT_TABLE} WHERE id = @id", id);

    using var log = new NpgsqlStatementLog(ConnectionString);
    await using (var context = CreateDbContext()) {
      var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).LoadAsync([_splitWrite(context, model)]);
      await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Written);
    }

    await Assert.That((await _snapshotAsync(id)) with { Xmin = 0 }).IsEqualTo(inserted)
      .Because("a row a rebuild loads is the row the live write would have written: one writer rule");
    await Assert.That(log.Matching("INSERT", SPLIT_TABLE)).IsEmpty()
      .Because("the row reached the table by COPY, not by an insert statement");
  }

  [Test]
  public async Task Load_CopiesTheExpiry_AndManyRowsAsync() {
    var expiresAt = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
    await using var context = CreateDbContext();
    var ids = Enumerable.Range(0, 30).Select(_ => Guid.CreateVersion7()).ToList();
    var writes = ids.Select((id, i) => _withRow(_action(context, id, $"n{i}", i, PerspectiveRowVersion.Absent, commitSequence: 1),
      _action(context, id, $"n{i}", i, PerspectiveRowVersion.Absent, commitSequence: 1).Row! with { ExpiresAt = expiresAt })).ToList();
    using var log = new NpgsqlStatementLog(ConnectionString);

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).LoadAsync(writes);

    await Assert.That(outcomes.Select(o => o.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteStatus.Written));
    await Assert.That(await _countRowsAsync(ACTION_TABLE, ids)).IsEqualTo(30);
    await Assert.That(await _expiresAtAsync(ids[12])).IsEqualTo(expiresAt);
    await Assert.That((await _rowAsync(ACTION_TABLE, ids[12])).Version).IsEqualTo(1);
    await Assert.That(log.Matching("INSERT", ACTION_TABLE)).IsEmpty();
  }

  [Test]
  public async Task Load_ARowTheTableRefuses_FailsAlone_AndTheOthersAreWrittenAsync() {
    await _executeAsync($"ALTER TABLE {ACTION_TABLE} ADD CONSTRAINT ck_load_value CHECK ((data->>'Value')::int < 100)", Guid.Empty);
    await using var context = CreateDbContext();
    var (first, refused, last) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).LoadAsync([
      _action(context, first, "a", 1, PerspectiveRowVersion.Absent),
      _action(context, refused, "b", 500, PerspectiveRowVersion.Absent),
      _action(context, last, "c", 3, PerspectiveRowVersion.Unchecked),
    ]);

    await Assert.That(outcomes.Select(o => o.Status)).IsEquivalentTo(
      [PerspectiveWriteStatus.Written, PerspectiveWriteStatus.Failed, PerspectiveWriteStatus.Written],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(outcomes[1].Error!.Message).Contains("ck_load_value");
    await Assert.That(await _countRowsAsync(ACTION_TABLE, [first, refused, last])).IsEqualTo(2);
  }

  [Test]
  public async Task Load_InsideATransaction_ARefusedCopy_LeavesTheTransactionUsableAsync() {
    await _executeAsync($"ALTER TABLE {ACTION_TABLE} ADD CONSTRAINT ck_load_tx CHECK ((data->>'Value')::int < 100)", Guid.Empty);
    await using var context = CreateDbContext();
    var (kept, refused) = (Guid.CreateVersion7(), Guid.CreateVersion7());

    await using (var transaction = await context.Database.BeginTransactionAsync()) {
      var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).LoadAsync([
        _action(context, kept, "a", 1, PerspectiveRowVersion.Absent),
        _action(context, refused, "b", 500, PerspectiveRowVersion.Absent),
      ]);
      await Assert.That(outcomes[1].Status).IsEqualTo(PerspectiveWriteStatus.Failed);
      await transaction.CommitAsync();
    }

    await Assert.That(await _countRowsAsync(ACTION_TABLE, [kept, refused])).IsEqualTo(1)
      .Because("the copy the table refused was undone to its savepoint, and the transaction committed the rest");
  }

  [Test]
  public async Task Load_AValueTheCopyCannotSend_OrAColumnTheTableLacks_FailsThatRowAloneAsync() {
    await using var context = CreateDbContext();
    var unbindable = _extracted(context, Guid.CreateVersion7(), "x", 1, PerspectiveRowVersion.Absent, commitSequence: 1);
    unbindable = _withRow(unbindable, unbindable.Row! with {
      PhysicalValues = [unbindable.Row!.PhysicalValues[0] with { Value = new UnbindableValue() }, unbindable.Row!.PhysicalValues[1]],
    });
    var missingColumn = _extracted(context, Guid.CreateVersion7(), "y", 1, PerspectiveRowVersion.Absent, commitSequence: 1);
    missingColumn = _withRow(missingColumn, missingColumn.Row! with {
      PhysicalValues = [.. missingColumn.Row!.PhysicalValues, new PerspectivePhysicalValue("no_such_column", 1, "integer", false)],
    });
    var good = Guid.CreateVersion7();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).LoadAsync([
      unbindable,
      _extracted(context, good, "ok", 2, PerspectiveRowVersion.Absent, commitSequence: 1),
      missingColumn,
    ]);

    await Assert.That(outcomes[0].Status).IsEqualTo(PerspectiveWriteStatus.Failed);
    await Assert.That(outcomes[1].Status).IsEqualTo(PerspectiveWriteStatus.Written);
    await Assert.That(outcomes[2].Status).IsEqualTo(PerspectiveWriteStatus.Failed);
  }

  [Test]
  public async Task Load_AnUpdateOrAPurge_IsWrittenByTheSetBasedStatementsAsync() {
    var (updated, purged, fresh) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    await _seedActionAsync(updated, "before", 1, commitSequence: 1);
    await _seedActionAsync(purged, "gone", 1, commitSequence: 1);
    var readVersion = (await _rowAsync(ACTION_TABLE, updated)).Xmin;
    await using var context = CreateDbContext();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).LoadAsync([
      _action(context, updated, "after", 2, PerspectiveRowVersion.Of(readVersion), commitSequence: 2),
      _action(context, fresh, "new", 3, PerspectiveRowVersion.Absent),
      PendingPerspectiveWrite.Purge(ACTION_TABLE, purged),
    ]);

    await Assert.That(outcomes.Select(o => o.Status)).All().Satisfy(s => s.IsEqualTo(PerspectiveWriteStatus.Written));
    await Assert.That((await _rowAsync(ACTION_TABLE, updated)).Data).Contains("\"after\"");
    await Assert.That(await _countRowsAsync(ACTION_TABLE, [purged, fresh])).IsEqualTo(1);
  }

  [Test]
  public async Task Load_RefusesANullBatch_AnEmptyOneLoadsNothing_AndAnotherProviderIsRefusedAsync() {
    await using var inMemory = _inMemoryContext();
    await using var context = CreateDbContext();

    await Assert.That(async () => await new EFCorePostgresPerspectiveWriteSink(context).LoadAsync(null!)).Throws<ArgumentNullException>();
    await Assert.That(await new EFCorePostgresPerspectiveWriteSink(context).LoadAsync([])).IsEmpty();
    await Assert.That(async () => await new EFCorePostgresPerspectiveWriteSink(inMemory).LoadAsync(
        [PendingPerspectiveWrite.Purge(ACTION_TABLE, Guid.CreateVersion7())]))
      .Throws<InvalidOperationException>().WithMessageContaining("writes through Npgsql");
  }

  private static PendingPerspectiveWrite _splitWrite(DbContext context, InitOnlySplitModel model) =>
    new PostgresUpsertStrategy().PreparePendingWrite(context, new BaseUpsertStrategy.UpsertRowArgs<InitOnlySplitModel>(SPLIT_TABLE, model.Id,
      model, _meta(1, null), _scope, new Dictionary<string, object?> { ["status"] = model.Status, ["priority"] = model.Priority },
      false, PerspectiveRowVersion.Absent))!;
}
