using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks the fresh-attempt grant on every canary flip-to-Pending (found live on the
/// first real deploy: probes bounced straight back to Held). Held rows are spent-budget
/// storm casualties — recovery_attempts already at the policy max. When the canary flips
/// one to Pending to probe, release, or trickle it, the recovery worker's exhaustion check
/// re-holds it before it ever gets an attempt, so the campaign verdict is stuck Pending
/// forever. A canary on a new build is evidence-scoped: it must reset recovery_attempts to
/// 0 — the same principle as the observation-window reset — so the probe genuinely runs.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/127_DlqCanaryCampaigns.sql</code-under-test>
[Category("Shard2")]
public class CanaryFreshAttemptSqlTests : EFCoreTestBase {

  private const int HELD = 2;

  private static async Task<Guid> _seedSpentHeldAsync(NpgsqlConnection conn, string fp, string mt) {
    var id = (Guid)TrackedGuid.NewMedo();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_dead_letters
        (dead_letter_id, source_table, source_id, message_type, envelope, failure_reason,
         attempts_when_dlq, recovery_status, recovery_attempts, generation, error_fingerprint,
         error_fingerprint_version)
      VALUES (@id, 'wh_inbox', @src, @mt, '{}'::jsonb, 5, 3, @st, 3, 'seed/1', @fp, 1)";
    ins.Parameters.AddWithValue("id", id);
    ins.Parameters.AddWithValue("src", (Guid)TrackedGuid.NewMedo());
    ins.Parameters.AddWithValue("mt", mt);
    ins.Parameters.AddWithValue("st", HELD);
    ins.Parameters.AddWithValue("fp", fp);
    await ins.ExecuteNonQueryAsync();
    return id;
  }

  private EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext> _svc(WorkCoordinationDbContext ctx) =>
    new(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<
      EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>>.Instance, null);

  private static async Task<int> _attemptsOfPendingAsync(NpgsqlConnection conn, string fp) {
    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT COALESCE(max(recovery_attempts),-1) FROM wh_dead_letters WHERE error_fingerprint=@fp AND recovery_status=0";
    q.Parameters.AddWithValue("fp", fp);
    return (int)(await q.ExecuteScalarAsync() ?? -1);
  }

  [Test]
  public async Task BeginProbes_GrantsAFreshAttempt_SoTheProbeDoesNotReHoldAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    for (var i = 0; i < 4; i++) { await _seedSpentHeldAsync(conn, fp, "T.A"); }

    await _svc(ctx).BeginCanaryProbesAsync(fp, "gen/1", 4, 3);

    await Assert.That(await _attemptsOfPendingAsync(conn, fp)).IsEqualTo(0)
      .Because("a spent-budget probe left at max attempts is re-held by the exhaustion check "
             + "before it ever runs — a new build's canary deserves a fresh attempt");
  }

  [Test]
  public async Task Release_GrantsAFreshAttempt_SoReleasedRowsDrainAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    for (var i = 0; i < 5; i++) { await _seedSpentHeldAsync(conn, fp, "T.A"); }

    await _svc(ctx).ReleaseHeldCohortAsync(fp, TimeSpan.FromMinutes(30));

    await Assert.That(await _attemptsOfPendingAsync(conn, fp)).IsEqualTo(0)
      .Because("a Pass release that leaves rows at max attempts releases them straight back "
             + "into the exhaustion check — the whole cohort would re-hold");
  }

  [Test]
  public async Task TrickleWave_GrantsAFreshAttemptAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    for (var i = 0; i < 5; i++) { await _seedSpentHeldAsync(conn, fp, "T.A"); }
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = "INSERT INTO wh_dlq_probe_campaigns (fingerprint, generation, probe_ids, verdict) VALUES (@fp, 'g', '{}', 3)";
      ins.Parameters.AddWithValue("fp", fp);
      await ins.ExecuteNonQueryAsync();
    }

    await _svc(ctx).BeginTrickleWaveAsync(fp, "g", 3);

    await Assert.That(await _attemptsOfPendingAsync(conn, fp)).IsEqualTo(0)
      .Because("a trickle wave releases spent-budget rows too — same fresh-attempt grant");
  }
}
