using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round tests for <see cref="EFCoreDeadLetterRecoveryService{TDbContext}"/>'s
/// closed-connection auto-open guard on <c>GetPassedCampaignFingerprintsAsync</c> and
/// <c>MarkDiscardedAsync</c> -- two of the many identical "if closed, open it" copies in this
/// class that <see cref="EFCoreDeadLetterRecoveryServiceTests"/>'s
/// <c>EveryReadPath_OpensAClosedConnectionItselfAsync</c> does not drive (it exercises four other
/// methods only). Requires a live PostgreSQL database: both methods call the real recovery SQL
/// functions from migration 051/127/134, which only exist against a real server.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDeadLetterRecoveryService.cs</code-under-test>
[Category("Shard1")]
public class EFCoreDeadLetterRecoveryServiceCoverageTests : EFCoreTestBase {

  // A connection-reuse mistake that skips reopening a closed connection throws on the very
  // first command; the recovery worker would then treat the whole DLQ subsystem as unavailable
  // instead of just missing this one guard. This locks both that the guard reopens the
  // connection for itself AND that the query it then runs finds the real row -- a fingerprint
  // whose canary campaign already passed must be re-driven instead of re-quarantined
  // (issue #681, documented in 134_PassedCampaignFingerprints.sql).
  [Test]
  public async Task GetPassedCampaignFingerprintsAsync_ClosedConnection_ReopensAndReturnsThePassedFingerprintAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var svc = _newService(ctx);
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    var generation = $"v0.coverage-{Guid.NewGuid():N}";
    var fingerprint = Guid.NewGuid().ToString("N")[..16];
    await _seedPassedCampaignAsync(conn, fingerprint, generation);

    if (conn.State != System.Data.ConnectionState.Closed) {
      await conn.CloseAsync();
    }
    // Without this the test could pass having proved nothing: if the connection were still
    // open, the guard under test would be skipped and the call would succeed for the ordinary
    // reason.
    await Assert.That(conn.State).IsEqualTo(System.Data.ConnectionState.Closed)
      .Because("the point of this test is the state the service is handed, not whatever the fixture left it in");

    var fingerprints = await svc.GetPassedCampaignFingerprintsAsync(generation, cancellationToken);

    await Assert.That(fingerprints).Contains(fingerprint)
      .Because("a closed connection must be reopened by the method itself, and the seeded Pass "
        + "verdict must actually come back -- proving both the guard and the underlying query ran for real");
  }

  // Discarding must actually settle the row (Recovered + note), never silently no-op behind a
  // guard that appeared to run. A discard that left the row untouched would leave a
  // disabled-subsystem message cycling through HoldForReview / re-attempt forever instead of
  // aging out through the normal retention purge.
  [Test]
  public async Task MarkDiscardedAsync_ClosedConnection_ReopensAndSettlesTheRowAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var svc = _newService(ctx);
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    var dlqId = await _seedDlqAsync(conn);

    if (conn.State != System.Data.ConnectionState.Closed) {
      await conn.CloseAsync();
    }
    await Assert.That(conn.State).IsEqualTo(System.Data.ConnectionState.Closed)
      .Because("the point of this test is the state the service is handed, not whatever the fixture left it in");

    await svc.MarkDiscardedAsync(dlqId, "subsystem disabled", cancellationToken);

    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var (status, notes) = await _getStatusAndNotesAsync(conn, dlqId);
    await Assert.That(status).IsEqualTo((int)DeadLetterRecoveryStatus.Recovered)
      .Because("discarding must settle the row as Recovered so it ages out through retention -- "
        + "never left mid-flight, and never picked up for another attempt");
    await Assert.That(notes).IsNotNull();
    await Assert.That(notes!).Contains("subsystem disabled");
  }

  // ===== Helpers =====

  // The shipped evaluate_canary_campaign always returns exactly one row -- the unknown-campaign
  // branch answers 0,0,0,0 rather than returning empty -- so the reader guard is defensive. It is
  // still worth pinning, because the value it must produce is PENDING and not a terminal verdict.
  // Pending means "evaluate again next scan"; Pass would release a held cohort on evidence that
  // was never read, and Fail would hold a healthy one forever. Without the guard the call throws
  // out of the recovery scan instead, from a reader that was never positioned.
  [Test]
  public async Task EvaluateCampaignAsync_WhenTheFunctionYieldsNoRow_ReadsAsPendingAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }

    // Same declared signature, no rows. EFCoreTestBase gives this test its own database, so the
    // crippled function is invisible to every other test.
    await using (var replace = conn.CreateCommand()) {
      replace.CommandText = """
        CREATE OR REPLACE FUNCTION evaluate_canary_campaign(
          p_fingerprint VARCHAR(16), p_generation TEXT)
        RETURNS TABLE(verdict INTEGER, probes_succeeded INTEGER, probes_failed INTEGER, probes_outstanding INTEGER)
        AS $$ BEGIN RETURN; END; $$ LANGUAGE plpgsql;
        """;
      await replace.ExecuteNonQueryAsync(cancellationToken);
    }

    var verdict = await _newService(ctx).EvaluateCampaignAsync(
      "abcdef0123456789", "v0.no-row", cancellationToken);

    await Assert.That(verdict.Kind).IsEqualTo(CanaryVerdictKind.Pending)
      .Because("a campaign that answered nothing has produced no evidence either way; Pass would "
             + "release a held cohort and Fail would hold a healthy one, both off a reading that "
             + "was never taken");
    await Assert.That(verdict.ProbesSucceeded).IsEqualTo(0);
    await Assert.That(verdict.ProbesFailed).IsEqualTo(0);
    await Assert.That(verdict.ProbesOutstanding).IsEqualTo(0)
      .Because("no probe arithmetic was read, so none may be reported as fact");
  }

  private static EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext> _newService(WorkCoordinationDbContext ctx) =>
    new(ctx, NullLogger<EFCoreDeadLetterRecoveryService<WorkCoordinationDbContext>>.Instance);

  private static async Task _seedPassedCampaignAsync(NpgsqlConnection conn, string fingerprint, string generation) {
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_dlq_probe_campaigns (fingerprint, generation, probe_ids, verdict)
      VALUES (@fp, @gen, '{}'::uuid[], 1)";
    cmd.Parameters.AddWithValue("fp", fingerprint);
    cmd.Parameters.AddWithValue("gen", generation);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _seedDlqAsync(NpgsqlConnection conn) {
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    var dlqId = (Guid)TrackedGuid.NewMedo();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_dead_letters
        (dead_letter_id, source_table, source_id, message_type, envelope, failure_reason,
         attempts_when_dlq, dead_lettered_at, recovery_status, generation, error_fingerprint,
         error_fingerprint_version)
      VALUES (@id, 'wh_inbox', @src, 'Test.Event', '{""p"":1}'::jsonb, 5, 3,
              NOW() - INTERVAL '1 hour', 0, 'v0.coverage', 'fp-coverage', 1)";
    ins.Parameters.AddWithValue("id", dlqId);
    ins.Parameters.AddWithValue("src", (Guid)TrackedGuid.NewMedo());
    await ins.ExecuteNonQueryAsync();
    return dlqId;
  }

  private static async Task<(int Status, string? Notes)> _getStatusAndNotesAsync(NpgsqlConnection conn, Guid dlqId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT recovery_status, operator_notes FROM wh_dead_letters WHERE dead_letter_id = @id";
    cmd.Parameters.AddWithValue("id", dlqId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
  }
}
