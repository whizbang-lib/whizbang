using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks the jurisdiction split between generation replay and canary campaigns
/// (found live on the first deploy of the full canary stack): replay re-offers PENDING
/// rows on a new build, campaigns probe HELD cohorts. Before this fix the replay flipped
/// Held rows back to Pending wholesale — 55,342 rows re-offered blind — emptying the held
/// cohorts in the same startup that was about to canary-probe them: the campaign found
/// nothing, and the fleet re-adjudicated the spent-budget rows right back to Held instead
/// of probing a stratified sample.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/051_DeadLetterRecovery.sql</code-under-test>
[Category("Shard2")]
public class GenerationReplayHeldJurisdictionSqlTests : EFCoreTestBase {

  private static async Task<Guid> _seedAsync(NpgsqlConnection conn, int status) {
    var id = (Guid)TrackedGuid.NewMedo();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_dead_letters
        (dead_letter_id, source_table, source_id, message_type, envelope, failure_reason,
         attempts_when_dlq, recovery_status, generation, retried_on_generations)
      VALUES (@id, 'wh_inbox', @src, 'T.A', '{}'::jsonb, 5, 3, @st, 'old/1', '{}')";
    ins.Parameters.AddWithValue("id", id);
    ins.Parameters.AddWithValue("src", (Guid)TrackedGuid.NewMedo());
    ins.Parameters.AddWithValue("st", status);
    await ins.ExecuteNonQueryAsync();
    return id;
  }

  [Test]
  public async Task Replay_SchedulesPending_LeavesHeldToTheCampaignsAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var pending = await _seedAsync(conn, status: 0);
    var held = await _seedAsync(conn, status: 2);
    var gen = "new/" + Guid.NewGuid().ToString("N")[..8];

    var svc = new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(
      ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<
        EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>>.Instance, null);
    await svc.ResetForGenerationAsync(gen, 0);

    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT recovery_status FROM wh_dead_letters WHERE dead_letter_id = @id";
    q.Parameters.AddWithValue("id", held);
    await Assert.That((int)(await q.ExecuteScalarAsync() ?? -1)).IsEqualTo(2)
      .Because("held rows are the canary campaign's jurisdiction: a blind mass re-offer on "
             + "deploy empties the cohorts in the same startup that was about to probe them, "
             + "and spent-budget rows just bounce back to Held through policy churn");

    await using var q2 = conn.CreateCommand();
    q2.CommandText = "SELECT recovery_status, next_recovery_at <= NOW() FROM wh_dead_letters WHERE dead_letter_id = @id";
    q2.Parameters.AddWithValue("id", pending);
    await using var r = await q2.ExecuteReaderAsync();
    await r.ReadAsync();
    await Assert.That(r.GetInt32(0)).IsEqualTo(0);
    await Assert.That(r.GetBoolean(1)).IsTrue()
      .Because("pending rows keep the pre-canary contract: a new build re-offers them once");
  }

  [Test]
  public async Task Replay_Staggered_SpreadsReoffersAcrossTheWindowAsync() {
    // #669: a deploy's generation replay re-offered every eligible row due-NOW — one flood
    // competing with live traffic on the same queues and database. Staggered, the same
    // replay drains as a paced stream through the bounded scans.
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var gen = "new/" + Guid.NewGuid().ToString("N")[..8];
    for (var i = 0; i < 40; i++) { await _seedAsync(conn, status: 0); }

    var svc = new EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>(
      ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<
        EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>>.Instance, null);
    var scheduled = await svc.ResetForGenerationAsync(gen, 30);

    await Assert.That(scheduled).IsEqualTo(40);
    await using var q = conn.CreateCommand();
    q.CommandText = @"
      SELECT count(DISTINCT next_recovery_at),
             count(*) FILTER (WHERE next_recovery_at > NOW() + INTERVAL '31 minutes'),
             count(*) FILTER (WHERE next_recovery_at < NOW() - INTERVAL '1 minute')
      FROM wh_dead_letters WHERE generation = 'old/1' AND recovery_status = 0";
    await using var r = await q.ExecuteReaderAsync();
    await r.ReadAsync();
    await Assert.That(r.GetInt64(0)).IsGreaterThan(10L)
      .Because("random staggering across a 30-minute window must actually spread the "
             + "due-times — 40 rows collapsing to one instant is the flood this removes");
    await Assert.That(r.GetInt64(1)).IsEqualTo(0L)
      .Because("nothing schedules past the window — the replay finishes, paced");
    await Assert.That(r.GetInt64(2)).IsEqualTo(0L);
  }

}
