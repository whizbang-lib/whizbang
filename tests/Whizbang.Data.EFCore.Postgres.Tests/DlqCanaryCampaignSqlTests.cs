using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks the held-cohort campaign persistence (P1 of plans/dlq-stack-intelligence.md):
/// the grandfather purge gate (a held row whose envelope is the JSON null literal — NOT SQL
/// null — can never be re-driven), fingerprint cohort listing, idempotent stratified canary
/// probes, verdict arithmetic including the re-dead-lettered-probe failure signal, and
/// staggered release.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/127_DlqCanaryCampaigns.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs</code-under-test>
[Category("Shard2")]
public class DlqCanaryCampaignSqlTests : EFCoreTestBase {

  private const int HELD = 2;      // DeadLetterRecoveryStatus.HoldForReview
  private const int PERMANENT = 4; // DeadLetterRecoveryStatus.PermanentlyFailed

  private static async Task<Guid> _seedHeldAsync(
      NpgsqlConnection conn, string fingerprint, string messageType,
      string envelope = "{\"p\":1}", int status = HELD, Guid? sourceId = null,
      string offset = "-1 hour") {
    var id = (Guid)TrackedGuid.NewMedo();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_dead_letters
        (dead_letter_id, source_table, source_id, message_type, envelope, failure_reason,
         attempts_when_dlq, dead_lettered_at, recovery_status, generation, error_fingerprint,
         error_fingerprint_version)
      VALUES (@id, 'wh_inbox', @src, @mt, @env::jsonb, 5, 3,
              NOW() + @off::interval, @st, 'seed/1', @fp, 1)";
    ins.Parameters.AddWithValue("id", id);
    ins.Parameters.AddWithValue("src", sourceId ?? (Guid)TrackedGuid.NewMedo());
    ins.Parameters.AddWithValue("mt", messageType);
    ins.Parameters.AddWithValue("env", envelope);
    ins.Parameters.AddWithValue("st", status);
    ins.Parameters.AddWithValue("fp", fingerprint);
    ins.Parameters.AddWithValue("off", offset);
    await ins.ExecuteNonQueryAsync();
    return id;
  }

  private EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext> _svc(WorkCoordinationDbContext ctx) =>
    new(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<
      EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>>.Instance, null);

  private static async Task<(int Status, DateTimeOffset? Next)> _rowAsync(NpgsqlConnection conn, Guid id) {
    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT recovery_status, next_recovery_at FROM wh_dead_letters WHERE dead_letter_id=@id";
    q.Parameters.AddWithValue("id", id);
    await using var r = await q.ExecuteReaderAsync();
    await r.ReadAsync();
    return (r.GetInt32(0), r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1));
  }

  [Test]
  public async Task Purge_MarksJsonNullEnvelopes_AndOnlyThoseAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    // The trap this encodes: 'null'::jsonb satisfies NOT NULL and IS NULL misses it.
    var undeliverable = await _seedHeldAsync(conn, fp, "T.A", envelope: "null");
    var deliverable = await _seedHeldAsync(conn, fp, "T.A");

    var purged = await _svc(ctx).PurgeUndeliverableHeldAsync();

    await Assert.That(purged).IsGreaterThanOrEqualTo(1);
    await Assert.That((await _rowAsync(conn, undeliverable)).Status).IsEqualTo(PERMANENT)
      .Because("a held row with a JSON-null envelope can never be re-driven by any campaign "
             + "— it is marked PermanentlyFailed for the operator ledger, not silently skipped");
    await Assert.That((await _rowAsync(conn, deliverable)).Status).IsEqualTo(HELD)
      .Because("rows the machinery CAN re-drive are exactly what campaigns exist for");
  }

  [Test]
  public async Task ListHeldCohorts_GroupsByFingerprint_HeldOnlyAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp1 = Guid.NewGuid().ToString("N")[..16];
    var fp2 = Guid.NewGuid().ToString("N")[..16];
    await _seedHeldAsync(conn, fp1, "T.A");
    await _seedHeldAsync(conn, fp1, "T.B");
    await _seedHeldAsync(conn, fp2, "T.A");
    await _seedHeldAsync(conn, fp2, "T.A", status: 0); // pending — not a held cohort member

    var cohorts = await _svc(ctx).ListHeldCohortsAsync();

    var c1 = cohorts.FirstOrDefault(c => c.Fingerprint == fp1);
    var c2 = cohorts.FirstOrDefault(c => c.Fingerprint == fp2);
    await Assert.That(c1 is not null && c1.RowCount == 2 && c1.MessageTypeCount == 2).IsTrue();
    await Assert.That(c2 is not null && c2.RowCount == 1).IsTrue()
      .Because("only HELD rows form cohorts — pending rows are the live queue, not campaign material");
  }

  [Test]
  public async Task BeginProbes_StratifiesAcrossTypes_FlipsPending_IdempotentAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    for (var i = 0; i < 6; i++) { await _seedHeldAsync(conn, fp, "T.A"); }
    for (var i = 0; i < 6; i++) { await _seedHeldAsync(conn, fp, "T.B"); }
    for (var i = 0; i < 6; i++) { await _seedHeldAsync(conn, fp, "T.C"); }

    var svc = _svc(ctx);
    var probes = await svc.BeginCanaryProbesAsync(fp, "gen/1", 6, 3);
    await Assert.That(probes).IsEqualTo(6);

    await using (var q = conn.CreateCommand()) {
      q.CommandText = @"SELECT message_type, count(*) FROM wh_dead_letters
        WHERE error_fingerprint=@fp AND recovery_status=0 GROUP BY 1";
      q.Parameters.AddWithValue("fp", fp);
      var perType = new Dictionary<string, long>();
      await using var r = await q.ExecuteReaderAsync();
      while (await r.ReadAsync()) { perType[r.GetString(0)] = r.GetInt64(1); }
      await Assert.That(perType.Count).IsEqualTo(3)
        .Because("probes stratify across message types — a cohort can genuinely split by "
               + "type, and an all-one-type probe set would hide it");
      await Assert.That(perType.Values.All(v => v == 2)).IsTrue();
    }

    var again = await svc.BeginCanaryProbesAsync(fp, "gen/1", 6, 3);
    await Assert.That(again).IsEqualTo(0)
      .Because("idempotent per (fingerprint, generation): a pod restart mid-campaign "
             + "resumes evaluation, it must not mint a second probe set");
  }

  [Test]
  public async Task Evaluate_RecoveredProbes_PassAndPersistAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    for (var i = 0; i < 4; i++) { await _seedHeldAsync(conn, fp, "T.A"); }
    var svc = _svc(ctx);
    await svc.BeginCanaryProbesAsync(fp, "gen/1", 2, 3);

    // Probes "recover": mark the two Pending rows recovered, as the paced scan would.
    await using (var up = conn.CreateCommand()) {
      up.CommandText = @"UPDATE wh_dead_letters SET recovered_at=NOW(), recovery_status=3
        WHERE error_fingerprint=@fp AND recovery_status=0";
      up.Parameters.AddWithValue("fp", fp);
      await up.ExecuteNonQueryAsync();
    }

    var verdict = await svc.EvaluateCampaignAsync(fp, "gen/1");
    await Assert.That(verdict.Kind).IsEqualTo(CanaryVerdictKind.Pass);
    await Assert.That(verdict.ProbesSucceeded).IsEqualTo(2);

    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT verdict FROM wh_dlq_probe_campaigns WHERE fingerprint=@fp AND generation='gen/1'";
    q.Parameters.AddWithValue("fp", fp);
    await Assert.That((int)(await q.ExecuteScalarAsync() ?? -1)).IsEqualTo((int)CanaryVerdictKind.Pass)
      .Because("the campaign row is the durable record a restarted pod resumes from");
  }

  [Test]
  public async Task Evaluate_ProbeThatDeadLettersAgain_FailsOrMixesAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    var srcA = (Guid)TrackedGuid.NewMedo();
    var srcB = (Guid)TrackedGuid.NewMedo();
    await _seedHeldAsync(conn, fp, "T.A", sourceId: srcA);
    await _seedHeldAsync(conn, fp, "T.B", sourceId: srcB);
    var svc = _svc(ctx);
    await svc.BeginCanaryProbesAsync(fp, "gen/1", 2, 3);

    // Probe A recovers; probe B's message dead-letters AGAIN (newer unrecovered row, same source).
    await using (var up = conn.CreateCommand()) {
      up.CommandText = @"UPDATE wh_dead_letters SET recovered_at=NOW(), recovery_status=3
        WHERE error_fingerprint=@fp AND recovery_status=0";
      up.Parameters.AddWithValue("fp", fp);
      await up.ExecuteNonQueryAsync();
    }
    await _seedHeldAsync(conn, fp, "T.B", sourceId: srcB, status: 0, offset: "+1 second");

    var verdict = await svc.EvaluateCampaignAsync(fp, "gen/1");
    await Assert.That(verdict.Kind).IsEqualTo(CanaryVerdictKind.Mixed)
      .Because("one probe recovered and one re-dead-lettered: the cohort spans more than "
             + "one real behavior and must not auto-release");
    await Assert.That(verdict.ProbesFailed).IsEqualTo(1);
  }

  [Test]
  public async Task Evaluate_OutstandingProbes_StayPendingAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    await _seedHeldAsync(conn, fp, "T.A");
    await _seedHeldAsync(conn, fp, "T.A");
    var svc = _svc(ctx);
    await svc.BeginCanaryProbesAsync(fp, "gen/1", 2, 3);

    var verdict = await svc.EvaluateCampaignAsync(fp, "gen/1");
    await Assert.That(verdict.Kind).IsEqualTo(CanaryVerdictKind.Pending)
      .Because("probes not yet re-driven are outstanding, and a verdict from silence "
             + "would be a verdict from no evidence");
    await Assert.That(verdict.ProbesOutstanding).IsEqualTo(2);
  }

  [Test]
  public async Task BeginProbes_GenerationBudgetSpent_ReturnsMinusOne_TouchesNothingAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    await _seedHeldAsync(conn, fp, "T.A");
    // Two FAILED campaigns on distinct generations already recorded.
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_dlq_probe_campaigns (fingerprint, generation, probe_ids, verdict)
        VALUES (@fp, 'old/1', '{}', 2), (@fp, 'old/2', '{}', 2)";
      ins.Parameters.AddWithValue("fp", fp);
      await ins.ExecuteNonQueryAsync();
    }

    var result = await _svc(ctx).BeginCanaryProbesAsync(fp, "new/3", 5, generationBudget: 2);

    await Assert.That(result).IsEqualTo(-1)
      .Because("two distinct generations already re-tested this cohort and failed — the "
             + "budget says a third build does not get to try automatically");
    var (status, _) = await _rowAsync(conn,
      (await _heldIdAsync(conn, fp))!.Value);
    await Assert.That(status).IsEqualTo(HELD)
      .Because("budget refusal touches nothing: no probes, no state change");
  }

  [Test]
  public async Task BeginProbes_ResetsTheProbedMessagesObservationWindowsAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    var src = (Guid)TrackedGuid.NewMedo();
    await _seedHeldAsync(conn, fp, "T.A", sourceId: src);
    // The message sits AT the observation bound — without a reset, its first probe
    // redelivery would re-cross the bound and instantly requarantine: auto-failed probe.
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_message_deduplication (message_id, first_seen_at, observation_count)
        VALUES (@id, NOW() - INTERVAL '1 day', 10)
        ON CONFLICT (message_id) DO UPDATE SET observation_count = 10";
      ins.Parameters.AddWithValue("id", src);
      await ins.ExecuteNonQueryAsync();
    }

    await _svc(ctx).BeginCanaryProbesAsync(fp, "gen/1", 5, 3);

    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT observation_count FROM wh_message_deduplication WHERE message_id = @id";
    q.Parameters.AddWithValue("id", src);
    await Assert.That((int)(await q.ExecuteScalarAsync() ?? -1)).IsEqualTo(0)
      .Because("observation counts are evidence about a build generation, exactly like "
             + "attempt budgets — a new generation's probe starts a fresh window under "
             + "the same bound");
  }

  [Test]
  public async Task TrickleWave_ReleasesBounded_StampsWaveState_AndCountsWashbackAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    for (var i = 0; i < 12; i++) { await _seedHeldAsync(conn, fp, "T.A"); }
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"INSERT INTO wh_dlq_probe_campaigns (fingerprint, generation, probe_ids, verdict)
        VALUES (@fp, 'gen/1', '{}', 3)"; // Mixed
      ins.Parameters.AddWithValue("fp", fp);
      await ins.ExecuteNonQueryAsync();
    }
    var svc = _svc(ctx);

    var wave1 = await svc.BeginTrickleWaveAsync(fp, "gen/1", 5);
    await Assert.That(wave1).IsEqualTo(5)
      .Because("a wave releases exactly its bounded size — the doubling happens between "
             + "waves, never inside one");

    await Assert.That(await svc.CountWaveRequarantinesAsync(fp, "gen/1")).IsEqualTo(0)
      .Because("nothing has washed back yet — a clean wave");

    // One released row "comes back": a NEW unrecovered dead letter with the fingerprint.
    await _seedHeldAsync(conn, fp, "T.A", status: 0, offset: "+2 seconds");
    await Assert.That(await svc.CountWaveRequarantinesAsync(fp, "gen/1")).IsEqualTo(1)
      .Because("a new dead letter with the cohort's fingerprint after the wave started IS "
             + "the wave washing back — the halt signal");

    var wave2 = await svc.BeginTrickleWaveAsync(fp, "gen/1", 100);
    await Assert.That(wave2).IsEqualTo(7)
      .Because("the second wave takes whatever remains held when fewer than the doubled "
             + "size are left");
    var wave3 = await svc.BeginTrickleWaveAsync(fp, "gen/1", 100);
    await Assert.That(wave3).IsEqualTo(0)
      .Because("zero is the drained signal: the washback row is Pending, not held, so it "
             + "is the live queue's problem — the trickle only ever releases HELD rows");
  }

  private static async Task<Guid?> _heldIdAsync(NpgsqlConnection conn, string fp) {
    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT dead_letter_id FROM wh_dead_letters WHERE error_fingerprint=@fp LIMIT 1";
    q.Parameters.AddWithValue("fp", fp);
    return (Guid?)await q.ExecuteScalarAsync();
  }

  [Test]
  public async Task Release_FlipsHeldToPending_StaggeredWithinWindowAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    var ids = new List<Guid>();
    for (var i = 0; i < 20; i++) { ids.Add(await _seedHeldAsync(conn, fp, "T.A")); }

    var released = await _svc(ctx).ReleaseHeldCohortAsync(fp, TimeSpan.FromMinutes(30));
    await Assert.That(released).IsEqualTo(20);

    var lower = DateTimeOffset.UtcNow.AddMinutes(-1);
    var upper = DateTimeOffset.UtcNow.AddMinutes(31);
    foreach (var id in ids) {
      var (status, next) = await _rowAsync(conn, id);
      await Assert.That(status).IsEqualTo(0);
      await Assert.That(next is not null && next > lower && next < upper).IsTrue()
        .Because("release is staggered eligibility across the window — one giant due-set "
               + "arriving at once is exactly the storm shape the arbitration exists to prevent");
    }
  }

  // ==== #682: a verdict must never be reached on destroyed evidence ====

  [Test]
  public async Task Evaluate_AllProbeRowsDeleted_StaysPendingNotPassAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    for (var i = 0; i < 4; i++) { await _seedHeldAsync(conn, fp, "T.A"); }
    var svc = _svc(ctx);
    await svc.BeginCanaryProbesAsync(fp, "gen/1", 2, 3);

    // The retention purge deletes the probe rows out from under the live campaign
    // (issue #682: retention keyed on dead_lettered_at made this the NORMAL case for
    // cohorts older than the window, not an edge case).
    await using (var del = conn.CreateCommand()) {
      del.CommandText = "DELETE FROM wh_dead_letters WHERE error_fingerprint=@fp AND recovery_status=0";
      del.Parameters.AddWithValue("fp", fp);
      await del.ExecuteNonQueryAsync();
    }

    var verdict = await svc.EvaluateCampaignAsync(fp, "gen/1");
    await Assert.That(verdict.Kind).IsNotEqualTo(CanaryVerdictKind.Pass)
      .Because("zero surviving probes is zero evidence — 0 succeeded / 0 failed must not "
             + "satisfy the failed=0 branch and release an entire cohort vacuously");
    await Assert.That(verdict.Kind).IsEqualTo(CanaryVerdictKind.Pending)
      .Because("evidence loss resolves to Pending so the worker re-probes rather than concluding");

    await using var q = conn.CreateCommand();
    q.CommandText = "SELECT verdict FROM wh_dlq_probe_campaigns WHERE fingerprint=@fp AND generation='gen/1'";
    q.Parameters.AddWithValue("fp", fp);
    await Assert.That((int)(await q.ExecuteScalarAsync() ?? -1)).IsEqualTo((int)CanaryVerdictKind.Pending)
      .Because("no terminal verdict may persist from an empty evidence set");
  }

  [Test]
  public async Task BeginProbes_EvidenceLost_RefreshesProbeSetFromSurvivorsAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    for (var i = 0; i < 6; i++) { await _seedHeldAsync(conn, fp, "T.A"); }
    var svc = _svc(ctx);
    var first = await svc.BeginCanaryProbesAsync(fp, "gen/1", 2, 3);
    await Assert.That(first).IsEqualTo(2);

    // Purge destroys the minted probes (they flipped to Pending=0); held rows survive.
    await using (var del = conn.CreateCommand()) {
      del.CommandText = "DELETE FROM wh_dead_letters WHERE error_fingerprint=@fp AND recovery_status=0";
      del.Parameters.AddWithValue("fp", fp);
      await del.ExecuteNonQueryAsync();
    }

    var refreshed = await svc.BeginCanaryProbesAsync(fp, "gen/1", 2, 3);
    await Assert.That(refreshed).IsEqualTo(2)
      .Because("a campaign whose entire probe set was destroyed must re-mint from the "
             + "surviving held rows instead of resuming as a zero-evidence campaign; "
             + "idempotent-resume (return 0) is only correct while probe rows still exist");

    await using var q = conn.CreateCommand();
    q.CommandText = @"SELECT COUNT(*) FROM wh_dead_letters d
      JOIN wh_dlq_probe_campaigns c ON d.dead_letter_id = ANY(c.probe_ids)
      WHERE c.fingerprint=@fp AND c.generation='gen/1' AND d.recovery_status=0";
    q.Parameters.AddWithValue("fp", fp);
    await Assert.That((long)(await q.ExecuteScalarAsync() ?? 0L)).IsEqualTo(2L)
      .Because("the refreshed probe_ids must reference live Pending rows the evaluator can count");
  }

  [Test]
  public async Task BeginProbes_ProbesStillPresent_RemainsIdempotentAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    for (var i = 0; i < 4; i++) { await _seedHeldAsync(conn, fp, "T.A"); }
    var svc = _svc(ctx);
    var first = await svc.BeginCanaryProbesAsync(fp, "gen/1", 2, 3);
    await Assert.That(first).IsEqualTo(2);

    var resumed = await svc.BeginCanaryProbesAsync(fp, "gen/1", 2, 3);
    await Assert.That(resumed).IsEqualTo(0)
      .Because("control: while the probe rows survive, a restart resumes the campaign "
             + "rather than minting a second probe set — the refresh path must not regress this");
  }


  // ==== #681: Pass verdicts are standing, generation-scoped evidence ====

  [Test]
  public async Task PassedFingerprints_ReturnsOnlyPassVerdicts_ForTheGenerationAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fpPass = Guid.NewGuid().ToString("N")[..16];
    var fpFail = Guid.NewGuid().ToString("N")[..16];
    var fpOtherGen = Guid.NewGuid().ToString("N")[..16];
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_dlq_probe_campaigns (fingerprint, generation, probe_ids, verdict) VALUES
          (@a, 'gen/1', '{}'::uuid[], 1),
          (@b, 'gen/1', '{}'::uuid[], 2),
          (@c, 'gen/2', '{}'::uuid[], 1)";
      ins.Parameters.AddWithValue("a", fpPass);
      ins.Parameters.AddWithValue("b", fpFail);
      ins.Parameters.AddWithValue("c", fpOtherGen);
      await ins.ExecuteNonQueryAsync();
    }
    var svc = _svc(ctx);

    var passed = await svc.GetPassedCampaignFingerprintsAsync("gen/1");

    await Assert.That(passed).Contains(fpPass);
    await Assert.That(passed.Contains(fpFail)).IsFalse()
      .Because("a Fail verdict is evidence AGAINST the cohort — never a retry grant");
    await Assert.That(passed.Contains(fpOtherGen)).IsFalse()
      .Because("verdicts are generation-scoped: a pass on another build proves nothing here");
  }

  [Test]
  public async Task FetchDue_CarriesTheErrorFingerprintAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var fp = Guid.NewGuid().ToString("N")[..16];
    await _seedHeldAsync(conn, fp, "T.A", status: 0);
    var svc = _svc(ctx);

    var due = await svc.FetchDueAsync(500);

    var mine = due.FirstOrDefault(e => e.ErrorFingerprint == fp);
    await Assert.That(mine).IsNotNull()
      .Because("the worker's #681 pass-verdict bypass keys on the fingerprint; a fetch that "
             + "drops it would silently disable the bypass for every row");
  }

}
