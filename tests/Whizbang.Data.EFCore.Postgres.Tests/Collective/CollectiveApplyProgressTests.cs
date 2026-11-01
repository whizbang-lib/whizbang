// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// The shared half of a resumable collective apply (#1969): the batch statement both drivers run, its parameters, and the
/// loop that runs batches until the record says the apply is finished. No database: the loop is driven by a fake batch.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Collective/CollectiveApplyProgress.cs</code-under-test>
[Category("Collective")]
[Category("Shard4")]
public class CollectiveApplyProgressTests {
  private static readonly DateTimeOffset _appliedAt = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

  private static CollectiveApplyBatch _batch(bool serialized) => new() {
    Table = "wh_per_job",
    UpdateTarget = "\"svc\".\"wh_per_job\"",
    ProgressTable = "\"svc\".wh_collective_apply_progress",
    Where = "scope ->> 't' = @where0",
    SetList = "data = jsonb_set(data, @path0, @p0::jsonb)",
    BatchSize = 250,
    SerializedByTheApplyLock = serialized,
  };

  [Test]
  public async Task Render_WhenTheApplyLockSerializesTheApplies_DoesNotLockTheRecordItReadsAsync() {
    await Assert.That(CollectiveApplyProgress.Render(_batch(serialized: true))).DoesNotContain("FOR UPDATE")
      .Because("the apply lock already serializes the appliers, and the statement takes no lock it does not need");
  }

  [Test]
  public async Task Render_WhenTheAppliesAreNotSerialized_LocksTheRecordItReadsAsync() {
    await Assert.That(CollectiveApplyProgress.Render(_batch(serialized: false)))
      .Contains("WHERE event_id = @wb_event_id AND entry_key = @wb_entry_key FOR UPDATE)")
      .Because("without the apply lock a second applier waits on the record instead of computing a batch it would lose");
  }

  [Test]
  public async Task Render_MovesTheRecord_OnlyFromTheThroughIdTheBatchStartedAfterAsync() {
    await Assert.That(CollectiveApplyProgress.Render(_batch(serialized: true)))
      .Contains("WHERE NOT wb_p.completed AND wb_p.through_id IS NOT DISTINCT FROM (SELECT through_id FROM wb_start)")
      .Because("the move is compare-and-set, so two appliers cannot both apply one range");
  }

  [Test]
  public async Task Render_UsesTheTablesAndTheBatchSize_ItIsGivenAsync() {
    var sql = CollectiveApplyProgress.Render(_batch(serialized: true));

    await Assert.That(sql).Contains("SELECT id FROM wh_per_job WHERE (scope ->> 't' = @where0)");
    await Assert.That(sql).Contains("ORDER BY id LIMIT 250");
    await Assert.That(sql).Contains("UPDATE \"svc\".\"wh_per_job\" SET data = jsonb_set(data, @path0, @p0::jsonb)");
    await Assert.That(sql).Contains("INSERT INTO \"svc\".wh_collective_apply_progress AS wb_p");
  }

  [Test]
  public async Task Render_RefusesANullBatchAsync() {
    await Assert.That(() => CollectiveApplyProgress.Render(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Bind_AddsTheRecordsIdentity_TheStart_AndTheApplysTimestampAsync() {
    var eventId = Guid.NewGuid();
    var from = Guid.NewGuid();
    await using var command = new NpgsqlCommand();

    CollectiveApplyProgress.Bind(command, new CollectiveApplyCursor(eventId, "H.Apply:M", from, _appliedAt));

    await Assert.That(command.Parameters["wb_event_id"].Value).IsEqualTo(eventId);
    await Assert.That(command.Parameters["wb_entry_key"].Value).IsEqualTo("H.Apply:M");
    await Assert.That(command.Parameters["wb_from"].Value).IsEqualTo(from);
    await Assert.That(command.Parameters["wb_applied_at"].Value).IsEqualTo(_appliedAt);
  }

  [Test]
  public async Task Bind_WithNoStart_BindsATypedNull_SoTheStatementReadsTheRecordAsync() {
    await using var command = new NpgsqlCommand();

    CollectiveApplyProgress.Bind(command, new CollectiveApplyCursor(Guid.NewGuid(), "H.Apply:M", null, _appliedAt));

    await Assert.That(command.Parameters["wb_from"].Value).IsEqualTo(DBNull.Value);
    await Assert.That(command.Parameters["wb_from"].DbType).IsEqualTo(System.Data.DbType.Guid);
  }

  [Test]
  public async Task Bind_RefusesNullArgumentsAsync() {
    await using var command = new NpgsqlCommand();
    await Assert.That(() => CollectiveApplyProgress.Bind(null!, new CollectiveApplyCursor(Guid.Empty, "k", null, _appliedAt)))
      .Throws<ArgumentNullException>();
    await Assert.That(() => CollectiveApplyProgress.Bind(command, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Parameters_AreTheFourBindValues_AMissingStartAsDbNullAsync() {
    var eventId = Guid.NewGuid();
    var from = Guid.NewGuid();

    var withStart = CollectiveApplyProgress.Parameters(new CollectiveApplyCursor(eventId, "k", from, _appliedAt));
    var withoutStart = CollectiveApplyProgress.Parameters(new CollectiveApplyCursor(eventId, "k", null, _appliedAt));

    await Assert.That(withStart.Select(p => p.Key)).IsEquivalentTo(["wb_event_id", "wb_entry_key", "wb_from", "wb_applied_at"]);
    await Assert.That(withStart.Single(p => p.Key == "wb_from").Value).IsEqualTo(from);
    await Assert.That(withoutStart.Single(p => p.Key == "wb_from").Value).IsEqualTo(DBNull.Value);
    await Assert.That(() => CollectiveApplyProgress.Parameters(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task RunAsync_GoesOnFromWhereEachBatchLeftTheRecord_UntilItIsFinishedAsync() {
    var first = Guid.NewGuid();
    var starts = new List<Guid?>();
    var reports = 0;
    var results = new Queue<CollectiveApplyBatchResult>([
      new(2, Recorded: true, first, Completed: false),
      new(1, Recorded: true, Guid.NewGuid(), Completed: true),
    ]);

    var (rows, batches) = await CollectiveApplyProgress.RunAsync(
      from => { starts.Add(from); return Task.FromResult(results.Dequeue()); },
      _ => { reports++; return ValueTask.CompletedTask; },
      CancellationToken.None);

    await Assert.That(rows).IsEqualTo(3);
    await Assert.That(batches).IsEqualTo(2);
    await Assert.That(reports).IsEqualTo(2).Because("the lease is renewed after every batch that applied");
    await Assert.That(starts).IsEquivalentTo(new Guid?[] { null, first }, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the first batch reads the record; each later one expects it where the previous batch left it");
  }

  [Test]
  public async Task RunAsync_ABatchThatLostTheRecord_AppliesNothing_AndTheNextReadsTheRecordAgainAsync() {
    var expected = Guid.NewGuid();
    var starts = new List<Guid?>();
    var results = new Queue<CollectiveApplyBatchResult>([
      new(2, Recorded: true, expected, Completed: false),
      new(0, Recorded: false, null, Completed: false),
      new(1, Recorded: true, Guid.NewGuid(), Completed: true),
    ]);

    var (rows, batches) = await CollectiveApplyProgress.RunAsync(
      from => { starts.Add(from); return Task.FromResult(results.Dequeue()); }, onBatchApplied: null, CancellationToken.None);

    await Assert.That(rows).IsEqualTo(3);
    await Assert.That(batches).IsEqualTo(2).Because("the batch that lost the compare-and-set applied nothing");
    await Assert.That(starts).IsEquivalentTo(new Guid?[] { null, expected, null }, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("after a lost compare-and-set the next batch reads where the other applier got to");
  }

  [Test]
  public async Task RunAsync_AnApplyFinishedBefore_RunsOneBatch_ThatAppliesNothingAsync() {
    var calls = 0;
    var reports = 0;

    var (rows, batches) = await CollectiveApplyProgress.RunAsync(
      _ => { calls++; return Task.FromResult(new CollectiveApplyBatchResult(0, Recorded: false, null, Completed: true)); },
      _ => { reports++; return ValueTask.CompletedTask; },
      CancellationToken.None);

    await Assert.That((rows, batches, calls, reports)).IsEqualTo((0, 0, 1, 0))
      .Because("a finished record skips the apply, and a skipped apply renews nothing");
  }

  [Test]
  public async Task RunAsync_RefusesANullBatchAsync() {
    await Assert.That(async () => await CollectiveApplyProgress.RunAsync(null!, null, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }
}
