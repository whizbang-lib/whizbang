using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks the settled-dead-letter retention semantics fixed for issue #682: retention
/// keys on when the row SETTLED (<c>recovered_at</c>), not on when the message originally
/// failed (<c>dead_lettered_at</c>) — recovering a backlog older than the window must not
/// delete the receipts within one maintenance cycle of the work happening. And a row
/// referenced by an UNRESOLVED canary campaign's probe set is evidence, not clutter: the
/// purge must never destroy it, or the campaign resolves on an empty evidence set.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/124_PurgeRecoveredDeadLetters.sql</code-under-test>
[Category("Shard2")]
public class RecoveredDeadLetterPurgeRetentionTests : EFCoreTestBase {

  private static async Task<Guid> _seedRecoveredAsync(
      NpgsqlConnection conn, string fingerprint,
      string deadLetteredOffset, string? recoveredOffset) {
    var id = (Guid)TrackedGuid.NewMedo();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_dead_letters
        (dead_letter_id, source_table, source_id, message_type, envelope, failure_reason,
         attempts_when_dlq, dead_lettered_at, recovery_status, recovered_at, generation,
         error_fingerprint, error_fingerprint_version)
      VALUES (@id, 'wh_inbox', @src, 'T.A', '{""p"":1}'::jsonb, 5, 3,
              NOW() + @dl::interval, 3,
              CASE WHEN @rec IS NULL THEN NULL ELSE NOW() + @rec::interval END,
              'seed/1', @fp, 1)";
    ins.Parameters.AddWithValue("id", id);
    ins.Parameters.AddWithValue("src", (Guid)TrackedGuid.NewMedo());
    ins.Parameters.AddWithValue("dl", deadLetteredOffset);
    ins.Parameters.AddWithValue("rec", (object?)recoveredOffset ?? DBNull.Value);
    ins.Parameters.AddWithValue("fp", fingerprint);
    await ins.ExecuteNonQueryAsync();
    return id;
  }

  private static async Task<bool> _existsAsync(NpgsqlConnection conn, Guid id) {
    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT EXISTS(SELECT 1 FROM wh_dead_letters WHERE dead_letter_id=@id)";
    q.Parameters.AddWithValue("id", id);
    return (bool)(await q.ExecuteScalarAsync() ?? false);
  }

  private static async Task _runPurgeAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT rows_affected FROM perform_maintenance() WHERE task_name = 'purge_recovered_dead_letters'";
    _ = await cmd.ExecuteScalarAsync();
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    return conn;
  }

  [Test]
  public async Task Purge_OldFailureRecoveredJustNow_SurvivesFullRetentionAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var fp = Guid.NewGuid().ToString("N")[..16];

    // Failed 10 days ago (past the 7-day window), recovered 1 minute ago: the receipt for
    // work that JUST happened. Under dead_lettered_at-keyed retention this was deleted on
    // the first maintenance pass — observed live as recovered-count going BACKWARDS while
    // a backlog drained (issue #682).
    var justRecovered = await _seedRecoveredAsync(conn, fp, "-10 days", "-1 minute");
    // Control: same vintage failure whose recovery is ALSO past retention — genuinely done,
    // eligible for purge. Proves the task ran rather than no-op'd.
    var longSettled = await _seedRecoveredAsync(conn, fp, "-30 days", "-8 days");

    await _runPurgeAsync(conn);

    await Assert.That(await _existsAsync(conn, justRecovered)).IsTrue()
      .Because("retention must key on recovered_at: a freshly recovered row gets its full "
             + "window regardless of how old the original failure was — otherwise draining "
             + "an old backlog leaves no trace and no usable progress metric");
    await Assert.That(await _existsAsync(conn, longSettled)).IsFalse()
      .Because("control: a row settled longer than the retention window is still purged");
  }

  [Test]
  public async Task Purge_ProbeOfUnresolvedCampaign_IsExemptAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var fp = Guid.NewGuid().ToString("N")[..16];

    // A probe row that recovered, with BOTH timestamps past retention — purge-eligible on
    // age by either key. Only its role as live campaign evidence protects it.
    var probe = await _seedRecoveredAsync(conn, fp, "-30 days", "-8 days");
    await using (var camp = conn.CreateCommand()) {
      camp.CommandText = @"
        INSERT INTO wh_dlq_probe_campaigns (fingerprint, generation, started_at, probe_ids, verdict)
        VALUES (@fp, 'gen/1', NOW() - interval '1 hour', ARRAY[@probe]::uuid[], 0)";
      camp.Parameters.AddWithValue("fp", fp);
      camp.Parameters.AddWithValue("probe", probe);
      await camp.ExecuteNonQueryAsync();
    }
    // Control: identical vintage, no campaign reference — must purge.
    var unreferenced = await _seedRecoveredAsync(conn, fp, "-30 days", "-8 days");

    await _runPurgeAsync(conn);

    await Assert.That(await _existsAsync(conn, probe)).IsTrue()
      .Because("a row named in an unresolved (verdict=0) campaign's probe_ids is the evidence "
             + "the verdict arithmetic counts; deleting it resolves the campaign vacuously");
    await Assert.That(await _existsAsync(conn, unreferenced)).IsFalse()
      .Because("control: exemption is scoped to campaign evidence, not the whole fingerprint");
  }

  [Test]
  public async Task Purge_ProbeOfResolvedCampaign_IsNotExemptAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var fp = Guid.NewGuid().ToString("N")[..16];

    var probe = await _seedRecoveredAsync(conn, fp, "-30 days", "-8 days");
    await using (var camp = conn.CreateCommand()) {
      camp.CommandText = @"
        INSERT INTO wh_dlq_probe_campaigns (fingerprint, generation, started_at, probe_ids, verdict, verdict_at)
        VALUES (@fp, 'gen/1', NOW() - interval '2 days', ARRAY[@probe]::uuid[], 1, NOW() - interval '2 days')";
      camp.Parameters.AddWithValue("fp", fp);
      camp.Parameters.AddWithValue("probe", probe);
      await camp.ExecuteNonQueryAsync();
    }

    await _runPurgeAsync(conn);

    await Assert.That(await _existsAsync(conn, probe)).IsFalse()
      .Because("once the campaign carries a terminal verdict its probes are receipts like any "
             + "other settled row — exempting them forever would recreate unbounded growth");
  }
}
