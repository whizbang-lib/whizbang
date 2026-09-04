using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DeadLetters;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks the rolling stack-history log (P4 of plans/dlq-stack-intelligence.md): a
/// bounded daily rollup (<c>wh_stack_daily</c>, one row per stack per day) plus
/// <c>wh_stacks.last_seen</c>, so "which failure shapes are trending over time" survives
/// the purge/archival of the underlying dead letters — decoupling long-term trend from DLQ
/// retention. A configurable rolling window prunes old days; a non-positive retention keeps
/// the log forever.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/129_StackHistory.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs</code-under-test>
[Category("Shard2")]
public class StackHistorySqlTests : EFCoreTestBase {

  private EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext> _svc(WorkCoordinationDbContext ctx) =>
    new(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<
      EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>>.Instance, null);

  private static async Task<Guid> _seedAsync(NpgsqlConnection conn, string errorText) {
    var id = (Guid)TrackedGuid.NewMedo();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_dead_letters
        (dead_letter_id, source_table, source_id, message_type, envelope, failure_reason,
         attempts_when_dlq, recovery_status, generation, error_text)
      VALUES (@id, 'wh_inbox', @src, 'T.A', '{}'::jsonb, 5, 3, 2, 'seed/1', @err)";
    ins.Parameters.AddWithValue("id", id);
    ins.Parameters.AddWithValue("src", (Guid)TrackedGuid.NewMedo());
    ins.Parameters.AddWithValue("err", errorText);
    await ins.ExecuteNonQueryAsync();
    return id;
  }

  [Test]
  public async Task RecordStack_IncrementsTodaysDailyCount_AndBumpsLastSeenAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var text = "System.Exception: x\n   at My.App.Only.RunAsync()";
    var stack = StackNormalizer.Normalize(text)!;
    var svc = _svc(ctx);
    await svc.RecordStackAsync(await _seedAsync(conn, text), stack);
    await svc.RecordStackAsync(await _seedAsync(conn, text), stack);

    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT occurrences FROM wh_stack_daily WHERE stack_id=@sid AND day=CURRENT_DATE";
    q.Parameters.AddWithValue("sid", stack.SequenceHash);
    await Assert.That((long)(await q.ExecuteScalarAsync() ?? 0L)).IsEqualTo(2L)
      .Because("the daily rollup counts occurrences per stack per day — the rolling history "
             + "that survives the dead letters being purged");

    await using var q2 = conn.CreateCommand();
    q2.CommandText = "SELECT last_seen IS NOT NULL FROM wh_stacks WHERE stack_id=@sid";
    q2.Parameters.AddWithValue("sid", stack.SequenceHash);
    await Assert.That((bool)(await q2.ExecuteScalarAsync() ?? false)).IsTrue()
      .Because("last_seen is the cheap always-there summary next to first_seen");
  }

  [Test]
  public async Task RecordStack_MaintainsRunningTotalOccurrencesAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var text = "System.Exception: y\n   at My.App.Only.RunAsync()";
    var stack = StackNormalizer.Normalize(text)!;
    var svc = _svc(ctx);
    for (var i = 0; i < 4; i++) { await svc.RecordStackAsync(await _seedAsync(conn, text), stack); }

    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT total_occurrences FROM wh_stacks WHERE stack_id=@sid";
    q.Parameters.AddWithValue("sid", stack.SequenceHash);
    await Assert.That((long)(await q.ExecuteScalarAsync() ?? 0L)).IsEqualTo(4L)
      .Because("total_occurrences on wh_stacks answers how-many in one cheap row-read, next "
             + "to first_seen/last_seen — no scan of the daily table");
  }

  [Test]
  public async Task RecordStacksBatch_StampsEveryRow_InOneCallAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var entries = new List<(Guid, StackIdentity)>();
    for (var i = 0; i < 5; i++) {
      var text = $"System.Exception: z{i}\n   at My.App.Frame{i}.RunAsync()";
      entries.Add((await _seedAsync(conn, text), StackNormalizer.Normalize(text)!));
    }

    await _svc(ctx).RecordStacksAsync(entries);

    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT count(*) FROM wh_dead_letters WHERE stack_id IS NOT NULL AND dead_letter_id = ANY(@ids)";
    q.Parameters.AddWithValue("ids", entries.Select(e => e.Item1).ToArray());
    await Assert.That((long)(await q.ExecuteScalarAsync() ?? 0L)).IsEqualTo(5L)
      .Because("the batch collapses a storm-sized backfill from N round trips to one — every "
             + "entry is still stamped and rolled");
  }

  [Test]
  public async Task Prune_RemovesDaysOlderThanRetention_KeepsRecentAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var sid = Guid.NewGuid().ToString("N")[..16];
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_stacks (stack_id, frame_count, is_prose) VALUES (@sid, 0, true);
        INSERT INTO wh_stack_daily (stack_id, day, occurrences) VALUES
          (@sid, CURRENT_DATE - 200, 5),
          (@sid, CURRENT_DATE - 100, 5),
          (@sid, CURRENT_DATE - 10, 5)";
      ins.Parameters.AddWithValue("sid", sid);
      await ins.ExecuteNonQueryAsync();
    }

    var pruned = await _svc(ctx).PruneStackHistoryAsync(retentionDays: 90);

    await Assert.That(pruned).IsEqualTo(2)
      .Because("a 90-day window prunes the 200- and 100-day-old rows, keeps the 10-day-old");
    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT count(*) FROM wh_stack_daily WHERE stack_id=@sid";
    q.Parameters.AddWithValue("sid", sid);
    await Assert.That((long)(await q.ExecuteScalarAsync() ?? 0L)).IsEqualTo(1L);
  }

  [Test]
  public async Task Prune_NonPositiveRetention_IsDisabled_KeepsEverythingAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var sid = Guid.NewGuid().ToString("N")[..16];
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_stacks (stack_id, frame_count, is_prose) VALUES (@sid, 0, true);
        INSERT INTO wh_stack_daily (stack_id, day, occurrences) VALUES (@sid, CURRENT_DATE - 9999, 5)";
      ins.Parameters.AddWithValue("sid", sid);
      await ins.ExecuteNonQueryAsync();
    }

    var pruned = await _svc(ctx).PruneStackHistoryAsync(retentionDays: 0);

    await Assert.That(pruned).IsEqualTo(0)
      .Because("retention <= 0 disables the rolling cleanup entirely — the log is kept "
             + "forever, and a very old row is untouched");
    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT count(*) FROM wh_stack_daily WHERE stack_id=@sid";
    q.Parameters.AddWithValue("sid", sid);
    await Assert.That((long)(await q.ExecuteScalarAsync() ?? 0L)).IsEqualTo(1L);
  }
}
