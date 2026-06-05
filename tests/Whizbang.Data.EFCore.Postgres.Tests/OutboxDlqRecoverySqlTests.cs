using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 4 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis) —
/// regression-lock on the outbox-source recovery path. The SQL function
/// <c>recover_dead_letter</c> in migration 051 already supports
/// <c>source_table = 'wh_outbox'</c>, but pre-Slice-3 no outbox-source row had
/// ever existed in <c>wh_dead_letters</c>, so this branch had never been
/// exercised in production. Slice 3 produces real outbox-source rows; Slice 4
/// proves recovery works for them and locks the behavior so future schema
/// edits can't silently regress it.
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>A wh_dead_letters row with source_table=wh_outbox can be
/// recovered: <c>recover_dead_letter</c> returns true, the wh_outbox row
/// reappears with attempts=0, the DLQ row is marked Recovered (status=3),
/// <c>recovered_at</c> is non-NULL.</description></item>
/// <item><description>The original generation tag is appended to
/// <c>retried_on_generations</c> on the DLQ row so the generation-replay sweep
/// never double-replays a recovered row.</description></item>
/// <item><description>The forensic fingerprint stays on the recovered DLQ row
/// — operators can still query "what failure mode did this row hit?" after
/// recovery.</description></item>
/// <item><description>Calling <c>recover_dead_letter</c> twice (race against
/// another worker) is idempotent — the second call returns false, the wh_outbox
/// row's attempts stay at 0.</description></item>
/// </list>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public class OutboxDlqRecoverySqlTests : EFCoreTestBase {

  private const string _stack = """
    System.InvalidOperationException: Could not open connection to 'jdx_bff'
       at Whizbang.Data.EFCore.Postgres.Functions.OutboxClaim.LeaseAsync(Guid instanceId)
    """;

  // --- helpers ---

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = (NpgsqlConnection)CreateDbContext().Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _seedOutboxAsync(NpgsqlConnection conn, Guid messageId, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_outbox (
        message_id, stream_id, destination, message_type, envelope_type,
        event_data, metadata, status, attempts, partition_number, is_event
      ) VALUES (
        @id, @stream, 'test-topic', 'TestMessage', 'MessageEnvelope',
        @event_data::jsonb, '{}'::jsonb, 1, 11, 0, false
      )
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("event_data", """{"hello":"world"}""");
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _moveToDlqAsync(NpgsqlConnection conn, Guid messageId, string generation) {
    var deadLetterId = (Guid)TrackedGuid.NewMedo();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT move_to_dead_letters(@dlq, 'wh_outbox', @msg, 99, @err, @inst, @gen)";
    cmd.Parameters.AddWithValue("dlq", deadLetterId);
    cmd.Parameters.AddWithValue("msg", messageId);
    cmd.Parameters.AddWithValue("err", _stack);
    cmd.Parameters.AddWithValue("inst", (Guid)TrackedGuid.NewMedo());
    cmd.Parameters.AddWithValue("gen", generation);
    await cmd.ExecuteScalarAsync();
    return deadLetterId;
  }

  private static async Task<bool> _recoverAsync(NpgsqlConnection conn, Guid deadLetterId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT recover_dead_letter(@id)";
    cmd.Parameters.AddWithValue("id", deadLetterId);
    var raw = await cmd.ExecuteScalarAsync();
    return raw is true;
  }

  private static async Task<int?> _outboxAttemptsAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT attempts FROM wh_outbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    var raw = await cmd.ExecuteScalarAsync();
    return raw is null or DBNull ? null : (int)raw;
  }

  private static async Task<(int Status, DateTimeOffset? RecoveredAt, string[] RetriedGenerations, string? Fingerprint)> _dlqRowAsync(
      NpgsqlConnection conn, Guid deadLetterId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT recovery_status, recovered_at, retried_on_generations, error_fingerprint
      FROM wh_dead_letters WHERE dead_letter_id = @id
      """;
    cmd.Parameters.AddWithValue("id", deadLetterId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException("missing DLQ row");
    }
    var recoveredAt = reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1);
    var generations = reader.IsDBNull(2) ? [] : reader.GetFieldValue<string[]>(2);
    var fingerprint = reader.IsDBNull(3) ? null : reader.GetString(3);
    return (reader.GetInt32(0), recoveredAt, generations, fingerprint);
  }

  // --- tests ---

  [Test]
  public async Task RecoverDeadLetter_OutboxSource_ReturnsToOutboxAsync() {
    await using var conn = await _openAsync();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedOutboxAsync(conn, messageId, streamId);

    var deadLetterId = await _moveToDlqAsync(conn, messageId, generation: "test-gen-1");
    var fingerprintBeforeRecovery = (await _dlqRowAsync(conn, deadLetterId)).Fingerprint;

    var recovered = await _recoverAsync(conn, deadLetterId);

    await Assert.That(recovered).IsTrue()
      .Because("recover_dead_letter MUST succeed for wh_outbox-source rows — the SQL supports it, and Slice 3 now produces real rows that need this path.");

    var attempts = await _outboxAttemptsAsync(conn, messageId);
    await Assert.That(attempts).IsEqualTo(0)
      .Because("Recovery resets attempts to 0 so the row gets a fresh retry budget — the prior failure had its forensic record preserved in wh_dead_letters.");

    var (status, recoveredAt, generations, fingerprintAfterRecovery) = await _dlqRowAsync(conn, deadLetterId);
    await Assert.That(status).IsEqualTo(3)
      .Because("recovery_status=3 == Recovered; locks the terminal state of a successful recovery so future-status enum drift doesn't silently mismatch.");
    await Assert.That(recoveredAt).IsNotNull()
      .Because("recovered_at being non-NULL is what fetch_dead_letters_due filters on — without it, the row would re-enter the recovery loop on the next tick.");
    await Assert.That(generations).Contains("test-gen-1")
      .Because("Original generation appended to retried_on_generations so the generation-replay sweep never double-replays this row.");
    await Assert.That(fingerprintAfterRecovery).IsEqualTo(fingerprintBeforeRecovery)
      .Because("Slice 4 explicit invariant: forensic fingerprint stays on the recovered DLQ row so post-recovery 'what failure mode hit this row' triage queries still work.");
  }

  [Test]
  public async Task RecoverDeadLetter_OutboxSource_PreservesEventDataAsync() {
    await using var conn = await _openAsync();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedOutboxAsync(conn, messageId, streamId);

    var deadLetterId = await _moveToDlqAsync(conn, messageId, generation: "test-gen-1");
    await _recoverAsync(conn, deadLetterId);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_data::text FROM wh_outbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    var eventData = (string?)await cmd.ExecuteScalarAsync();

    await Assert.That(eventData).IsNotNull()
      .Because("Recovered wh_outbox row MUST have event_data — recovery without payload would silently lose the original message.");
    await Assert.That(eventData!).Contains("hello")
      .Because("The original event_data ({\"hello\":\"world\"}) survives the move→recover roundtrip via the envelope JSONB snapshot in wh_dead_letters.");
  }

  [Test]
  public async Task RecoverDeadLetter_OutboxSource_SecondCallIsIdempotentAsync() {
    await using var conn = await _openAsync();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _seedOutboxAsync(conn, messageId, streamId);

    var deadLetterId = await _moveToDlqAsync(conn, messageId, generation: "test-gen-1");
    var firstRecover = await _recoverAsync(conn, deadLetterId);
    var secondRecover = await _recoverAsync(conn, deadLetterId);

    await Assert.That(firstRecover).IsTrue();
    await Assert.That(secondRecover).IsFalse()
      .Because("Double-recovery race: the second call must return false rather than crashing or duplicating the source row — the atomic UPDATE in recover_dead_letter is gated on recovery_status NOT IN (1,2,3,4).");

    var attempts = await _outboxAttemptsAsync(conn, messageId);
    await Assert.That(attempts).IsEqualTo(0)
      .Because("Idempotent: the wh_outbox row's attempts MUST stay at 0 across the duplicate recovery attempt — the ON CONFLICT DO NOTHING in recover_dead_letter prevents both row corruption and accidental attempts bump.");
  }
}
