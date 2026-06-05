using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Slice 3a of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis) — locks
/// the auto-fingerprint invariant for the canonical DLQ storage primitive
/// <c>move_to_dead_letters</c>:
/// every row landing in <c>wh_dead_letters</c> via this function MUST have its
/// <c>error_fingerprint</c> and <c>error_fingerprint_version</c> columns populated
/// from Slice 2's algorithm — for ALL source tables (inbox, outbox,
/// perspective_events). One implementation, three call sites, zero drift between
/// live capture and Slice 6's version-aware aggregation backfill.
///
/// <para>Why this invariant matters: pre-Slice-3a, callers passed
/// <c>(errorText, ...)</c> and the function INSERTed it raw. wh_dead_letters rows
/// landed with NULL fingerprints and triage had to wait for the aggregation job
/// to run (up to 10 min lag). After Slice 3a, every wh_outbox/inbox/perspective
/// row gets a populated fingerprint at the INSERT instant — operators run
/// <c>GROUP BY error_fingerprint</c> immediately.</para>
/// </summary>
/// <docs>operations/dead-letter-queue/outbox-dlq-promotion</docs>
public class MoveToDeadLettersFingerprintSqlTests : EFCoreTestBase {

  private const string _stackForFingerprintA = """
    System.InvalidOperationException: Could not open connection to 'jdx_bff'
       at Whizbang.Data.EFCore.Postgres.Functions.OutboxClaim.LeaseAsync(Guid instanceId)
    """;

  private const string _stackForFingerprintB = """
    System.NullReferenceException: Object reference not set to an instance of an object
       at Whizbang.Data.EFCore.Postgres.Functions.InboxDispatch.ClaimAsync(Guid instanceId)
    """;

  // --- helpers ---

  private async Task<NpgsqlConnection> _openAsync() {
    var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _seedOutboxRowAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_outbox (
        message_id, stream_id, destination, message_type, envelope_type,
        event_data, metadata, status, attempts, partition_number, is_event
      ) VALUES (
        @id, @stream, 'test-topic', 'TestMessage', 'MessageEnvelope',
        '{}'::jsonb, '{}'::jsonb, 1, 11, 0, false
      )
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("stream", (Guid)TrackedGuid.NewMedo());
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<(string? Fingerprint, short? Version, string? ErrorText)> _readDlqRowAsync(
      NpgsqlConnection conn, Guid deadLetterId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT error_fingerprint, error_fingerprint_version, error_text FROM wh_dead_letters WHERE dead_letter_id = @id";
    cmd.Parameters.AddWithValue("id", deadLetterId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException("expected dead-letter row");
    }
    return (
      Fingerprint: reader.IsDBNull(0) ? null : reader.GetString(0),
      Version: reader.IsDBNull(1) ? null : reader.GetInt16(1),
      ErrorText: reader.IsDBNull(2) ? null : reader.GetString(2));
  }

  private static async Task<Guid> _moveToDlqAsync(NpgsqlConnection conn, Guid messageId, string? errorText) {
    var deadLetterId = (Guid)TrackedGuid.NewMedo();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT move_to_dead_letters(@dlq_id, 'wh_outbox', @msg, 99, @err, @inst, 'test-gen')
      """;
    cmd.Parameters.AddWithValue("dlq_id", deadLetterId);
    cmd.Parameters.AddWithValue("msg", messageId);
    cmd.Parameters.Add(new NpgsqlParameter("err", NpgsqlDbType.Text) { Value = (object?)errorText ?? DBNull.Value });
    cmd.Parameters.AddWithValue("inst", (Guid)TrackedGuid.NewMedo());
    await cmd.ExecuteScalarAsync();
    return deadLetterId;
  }

  // --- tests ---

  [Test]
  public async Task MoveToDeadLetters_OutboxSource_PopulatesFingerprintAndVersionAsync() {
    await using var conn = await _openAsync();
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _seedOutboxRowAsync(conn, messageId);

    var deadLetterId = await _moveToDlqAsync(conn, messageId, _stackForFingerprintA);

    var (fingerprint, version, errorText) = await _readDlqRowAsync(conn, deadLetterId);
    await Assert.That(fingerprint).IsNotNull()
      .Because("Slice 3a invariant: move_to_dead_letters MUST populate error_fingerprint at INSERT time so the canonical GROUP BY triage query works without waiting for the aggregation cycle.");
    await Assert.That(fingerprint!.Length).IsEqualTo(16)
      .Because("Algorithm v1 produces a 16-char fingerprint; anything else means a different function ran or a different version landed silently.");
    await Assert.That(version).IsEqualTo((short)1)
      .Because("Slice 6's version-aware backfill keys off this — uninitialized rows would force a full re-hash pass on first aggregation.");
    await Assert.That(errorText).IsEqualTo(_stackForFingerprintA)
      .Because("Pre-existing contract: error_text is stored verbatim. The fingerprint addition must not mutate the stored text.");
  }

  [Test]
  public async Task MoveToDeadLetters_DistinctErrorTexts_DistinctFingerprintsAsync() {
    await using var conn = await _openAsync();
    var msgA = (Guid)TrackedGuid.NewMedo();
    var msgB = (Guid)TrackedGuid.NewMedo();
    await _seedOutboxRowAsync(conn, msgA);
    await _seedOutboxRowAsync(conn, msgB);

    var dlqA = await _moveToDlqAsync(conn, msgA, _stackForFingerprintA);
    var dlqB = await _moveToDlqAsync(conn, msgB, _stackForFingerprintB);

    var (fingerprintA, _, _) = await _readDlqRowAsync(conn, dlqA);
    var (fingerprintB, _, _) = await _readDlqRowAsync(conn, dlqB);
    await Assert.That(fingerprintB).IsNotEqualTo(fingerprintA)
      .Because("Different exception type and different in-app frame MUST produce different fingerprints — otherwise Slice 6's aggregation would collapse distinct root causes into one cluster.");
  }

  [Test]
  public async Task MoveToDeadLetters_NullErrorText_NullFingerprintAsync() {
    await using var conn = await _openAsync();
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _seedOutboxRowAsync(conn, messageId);

    var deadLetterId = await _moveToDlqAsync(conn, messageId, errorText: null);

    var (fingerprint, version, _) = await _readDlqRowAsync(conn, deadLetterId);
    await Assert.That(fingerprint).IsNull()
      .Because("NULL passthrough: compute_dead_letter_fingerprint(NULL) returns NULL, so a NULL error_text MUST yield a NULL fingerprint — no spurious 'all-NULLs' cluster.");
    await Assert.That(version).IsNull()
      .Because("Slice 6's WHERE error_fingerprint_version IS NULL OR < current() picks these rows up on the next maintenance tick if error_text later changes (it shouldn't, but the version-aware path handles the edge).");
  }

  [Test]
  public async Task MoveToDeadLetters_Idempotent_SameFingerprintForSameErrorTextAsync() {
    await using var conn = await _openAsync();
    var msgA = (Guid)TrackedGuid.NewMedo();
    var msgB = (Guid)TrackedGuid.NewMedo();
    await _seedOutboxRowAsync(conn, msgA);
    await _seedOutboxRowAsync(conn, msgB);

    var dlqA = await _moveToDlqAsync(conn, msgA, _stackForFingerprintA);
    var dlqB = await _moveToDlqAsync(conn, msgB, _stackForFingerprintA);

    var (fingerprintA, _, _) = await _readDlqRowAsync(conn, dlqA);
    var (fingerprintB, _, _) = await _readDlqRowAsync(conn, dlqB);
    await Assert.That(fingerprintB).IsEqualTo(fingerprintA)
      .Because("Two rows with identical error_text MUST share a fingerprint — that's the entire point of fingerprinting as a clustering key for Slice 6's aggregation.");
  }
}
