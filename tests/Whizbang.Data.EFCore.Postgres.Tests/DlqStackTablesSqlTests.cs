using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DeadLetters;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks the relational stack layer's persistence (P2 of
/// plans/dlq-stack-intelligence.md): frames dedupe across all dead letters, links preserve
/// order, recording is idempotent, and the backfill fetch returns only unstamped rows —
/// the inverted index that turns "which stacks are failing" into a query.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/128_DlqStackTables.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs</code-under-test>
[Category("Shard2")]
public class DlqStackTablesSqlTests : EFCoreTestBase {

  private EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext> _svc(WorkCoordinationDbContext ctx) =>
    new(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<
      EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>>.Instance, null);

  private static readonly string[] _expectedOrderedFrames = ["My.App.First.RunAsync", "My.App.Second.RunAsync"];

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
  public async Task RecordStack_PersistsFramesLinksInOrder_AndStampsTheRowAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var text = "System.InvalidOperationException: x\n"
      + "   at My.App.First.RunAsync()\n   at My.App.Second.RunAsync()";
    var id = await _seedAsync(conn, text);
    var stack = StackNormalizer.Normalize(text)!;

    await _svc(ctx).RecordStackAsync(id, stack);

    await using var q = conn.CreateCommand();
    q.CommandText = @"
      SELECT f.frame
      FROM wh_stack_links l JOIN wh_stack_frames f ON f.frame_id = l.frame_id
      WHERE l.stack_id = @sid ORDER BY l.position";
    q.Parameters.AddWithValue("sid", stack.SequenceHash);
    var frames = new List<string>();
    await using (var r = await q.ExecuteReaderAsync()) {
      while (await r.ReadAsync()) { frames.Add(r.GetString(0)); }
    }
    await Assert.That(frames).IsEquivalentTo(_expectedOrderedFrames)
      .Because("position preserves throw-site-versus-caller order — the links table is the "
             + "ordered stack, not a bag of frames");

    await using var q2 = conn.CreateCommand();
    q2.CommandText = "SELECT stack_id FROM wh_dead_letters WHERE dead_letter_id = @id";
    q2.Parameters.AddWithValue("id", id);
    await Assert.That((string?)await q2.ExecuteScalarAsync()).IsEqualTo(stack.SequenceHash);
  }

  [Test]
  public async Task RecordStack_SharedFrames_DedupeAcrossStacksAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var shared = "   at My.Shared.Helper.DoAsync()\n";
    var t1 = "System.Exception: a\n" + shared + "   at My.App.CallerOne.RunAsync()";
    var t2 = "System.Exception: b\n" + shared + "   at My.App.CallerTwo.RunAsync()";
    var id1 = await _seedAsync(conn, t1);
    var id2 = await _seedAsync(conn, t2);
    var svc = _svc(ctx);
    await svc.RecordStackAsync(id1, StackNormalizer.Normalize(t1)!);
    await svc.RecordStackAsync(id2, StackNormalizer.Normalize(t2)!);

    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT count(*) FROM wh_stack_frames WHERE frame = 'My.Shared.Helper.DoAsync'";
    await Assert.That((long)(await q.ExecuteScalarAsync() ?? 0L)).IsEqualTo(1L)
      .Because("frames dedupe across ALL stacks — that is what makes 'every stack passing "
             + "through this method' a join instead of a text scan");
  }

  [Test]
  public async Task RecordStack_IsIdempotentAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var text = "System.Exception: x\n   at My.App.Only.RunAsync()";
    var id = await _seedAsync(conn, text);
    var stack = StackNormalizer.Normalize(text)!;
    var svc = _svc(ctx);

    await svc.RecordStackAsync(id, stack);
    await svc.RecordStackAsync(id, stack);

    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT count(*) FROM wh_stack_links WHERE stack_id = @sid";
    q.Parameters.AddWithValue("sid", stack.SequenceHash);
    await Assert.That((long)(await q.ExecuteScalarAsync() ?? 0L)).IsEqualTo(1L)
      .Because("a storm records the same stack thousands of times; idempotence is what "
             + "keeps that a no-op instead of a constraint-violation storm");
  }

  [Test]
  public async Task FetchUnstacked_ReturnsOnlyUnstamped_NewestFirstAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var marker = Guid.NewGuid().ToString("N");
    var stamped = await _seedAsync(conn, "stamped " + marker);
    var pending = await _seedAsync(conn, "pending " + marker);
    var svc = _svc(ctx);
    await svc.RecordStackAsync(stamped, StackNormalizer.Normalize("stamped " + marker)!);

    var unstacked = await svc.FetchUnstackedAsync(10_000);

    var mine = unstacked.Where(u => u.ErrorText.Contains(marker, StringComparison.Ordinal)).ToList();
    await Assert.That(mine.Count).IsEqualTo(1);
    await Assert.That(mine[0].DeadLetterId).IsEqualTo(pending)
      .Because("stamped rows never re-enter the backfill — the bounded batch must spend "
             + "itself on rows that still need work");
  }
}
