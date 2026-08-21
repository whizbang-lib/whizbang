using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The poison-outbox loop, reproduced and terminated against the real SQL. A message that can
/// NEVER publish (an envelope over the transport's size cap is the observed producer) fails,
/// gets its lease cleared with a backoff capped at five minutes, is re-claimed by
/// <c>claim_orphaned_outbox</c> — the sole attempt counter — and fails again: the SQL layer alone
/// never terminates this, by design. What bounds it is the drain worker's pre-publish gate
/// (<c>MaxOutboxAttempts</c>, default 10 — locked by <c>V502DefaultsTests</c>): past the cap the
/// row is atomically moved to <c>wh_dead_letters</c> and the loop is terminally over. These tests
/// pin BOTH halves: the loop is real, and the termination genuinely terminates.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/017_ProcessOutboxFailures.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/024_ClaimOrphanedOutbox.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/050_WhDeadLetters.sql</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class PoisonOutboxLoopSqlTests : EFCoreTestBase {

  private const string PERMANENT_ERROR =
    "Failed to publish outbox message to inbox: message exceeds maximum batch message size (Reason: Unknown)";

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private static async Task _insertPoisonRowAsync(NpgsqlConnection conn, Guid messageId, Guid streamId, CancellationToken ct) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number)
      VALUES (@msg, 'topic', 'OversizedEvent', 'TestEnvelope', '{}', '{}', 1, 0, NOW(), @stream, 0)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    await ins.ExecuteNonQueryAsync(ct);
  }

  /// <summary>One turn of the observed loop: re-claim (the sole attempt counter), fail
  /// permanently, rewind the capped backoff — the test's only time machine.</summary>
  private static async Task<bool> _oneLoopCycleAsync(NpgsqlConnection conn, Guid instanceId, Guid messageId, CancellationToken ct) {
    bool claimed;
    await using (var claim = conn.CreateCommand()) {
      claim.CommandText = @"
        SELECT count(*) FROM claim_orphaned_outbox(
          @instance, 0, 1, NOW() + INTERVAL '30 seconds', NOW(), 4, NOW() - INTERVAL '5 minutes')
        WHERE message_id = @msg";
      claim.Parameters.AddWithValue("instance", instanceId);
      claim.Parameters.AddWithValue("msg", messageId);
      claimed = (long)(await claim.ExecuteScalarAsync(ct))! == 1;
    }
    if (!claimed) {
      return false;
    }
    await using (var fail = conn.CreateCommand()) {
      fail.CommandText = "SELECT process_outbox_failures(@failures, NOW())";
      fail.Parameters.Add(new NpgsqlParameter("failures", NpgsqlDbType.Jsonb) {
        Value = $@"[{{""MessageId"":""{messageId}"",""CompletedStatus"":0,""Error"":""{PERMANENT_ERROR}"",""FailureReason"":0}}]",
      });
      await fail.ExecuteNonQueryAsync(ct);
    }
    await using (var rewind = conn.CreateCommand()) {
      rewind.CommandText = "UPDATE wh_outbox SET scheduled_for = NOW() - INTERVAL '1 second' WHERE message_id = @msg";
      rewind.Parameters.AddWithValue("msg", messageId);
      await rewind.ExecuteNonQueryAsync(ct);
    }
    return true;
  }

  private static async Task<(int Attempts, string? Error)> _readRowAsync(NpgsqlConnection conn, Guid messageId, CancellationToken ct) {
    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT attempts, error FROM wh_outbox WHERE message_id = @msg";
    read.Parameters.AddWithValue("msg", messageId);
    await using var reader = await read.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
  }

  [Test]
  [Timeout(60000)]
  public async Task PermanentPublishFailure_TheSqlLoopAloneNeverTerminates_AttemptsJustClimbAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _insertPoisonRowAsync(conn, messageId, (Guid)TrackedGuid.NewMedo(), cancellationToken);

    for (var cycle = 1; cycle <= 12; cycle++) {
      await Assert.That(await _oneLoopCycleAsync(conn, instanceId, messageId, cancellationToken)).IsTrue()
        .Because($"cycle {cycle}: the Failed bit is not terminal — the row is re-claimed every "
               + "backoff interval, exactly the observed every-~5-minutes production loop");
    }

    var (attempts, error) = await _readRowAsync(conn, messageId, cancellationToken);
    await Assert.That(attempts).IsEqualTo(12)
      .Because("claim_orphaned_outbox is the sole attempt counter, so the cap is reachable");
    await Assert.That(error).IsEqualTo(PERMANENT_ERROR)
      .Because("the row carries the real exception text — the forensics the DLQ move will preserve");
  }

  [Test]
  [Timeout(60000)]
  public async Task PoisonRow_PastTheGateCap_TheMoveTerminatesTheLoopForGoodAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var dlqId = (Guid)TrackedGuid.NewMedo();
    await _insertPoisonRowAsync(conn, messageId, (Guid)TrackedGuid.NewMedo(), cancellationToken);

    // Wind the loop past the drain worker's default cap (MaxOutboxAttempts = 10, locked by
    // V502DefaultsTests) — 11 cycles, so attempts > cap and the pre-publish gate fires.
    for (var cycle = 1; cycle <= 11; cycle++) {
      _ = await _oneLoopCycleAsync(conn, instanceId, messageId, cancellationToken);
    }
    var (attempts, error) = await _readRowAsync(conn, messageId, cancellationToken);

    // What the gate does at the cap: the atomic move, carrying the row's REAL error text.
    await using (var move = conn.CreateCommand()) {
      move.CommandText = @"
        SELECT move_to_dead_letters(@dlq, 'wh_outbox', @msg, 5, @err, @instance, 'test-generation')";
      move.Parameters.AddWithValue("dlq", dlqId);
      move.Parameters.AddWithValue("msg", messageId);
      move.Parameters.AddWithValue("err", (object?)error ?? DBNull.Value);
      move.Parameters.AddWithValue("instance", instanceId);
      await move.ExecuteNonQueryAsync(cancellationToken);
    }

    // Terminally over: the source row is gone, nothing is claimable, the loop cannot resume.
    await using (var gone = conn.CreateCommand()) {
      gone.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = @msg";
      gone.Parameters.AddWithValue("msg", messageId);
      await Assert.That((long)(await gone.ExecuteScalarAsync(cancellationToken))!).IsEqualTo(0L)
        .Because("the move DELETEs the source row in the same transaction — 'Failed' is finally terminal");
    }
    await Assert.That(await _oneLoopCycleAsync(conn, instanceId, messageId, cancellationToken)).IsFalse()
      .Because("no further claim cycle can ever pick the message up again");

    await using (var dlq = conn.CreateCommand()) {
      dlq.CommandText = "SELECT attempts_when_dlq, error_text FROM wh_dead_letters WHERE dead_letter_id = @dlq";
      dlq.Parameters.AddWithValue("dlq", dlqId);
      await using var reader = await dlq.ExecuteReaderAsync(cancellationToken);
      await Assert.That(await reader.ReadAsync(cancellationToken)).IsTrue();
      await Assert.That(reader.GetInt32(0)).IsEqualTo(attempts)
        .Because("the DLQ row records how long the loop ran before the gate ended it");
      await Assert.That(reader.GetString(1)).IsEqualTo(PERMANENT_ERROR)
        .Because("operators diagnose from the DLQ row alone — the real root-cause text must survive the move");
    }
  }
}
