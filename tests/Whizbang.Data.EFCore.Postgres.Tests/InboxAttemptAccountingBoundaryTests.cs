using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Guards the boundary the attempt-accounting fix must not erode.
///
/// <para>
/// Two changes reduce what the retry budget is spent on: a claim a worker never dispatched can be
/// handed back (refunding its attempt), and the claim batch narrows when work is being re-claimed
/// rather than finished. Both are aimed at the same failure — a backlog larger than one worker's
/// throughput consuming its own budget and dead-lettering healthy messages as
/// <c>MaxAttemptsExceeded</c> having never reached a receptor.
/// </para>
///
/// <para>
/// The risk in that direction is over-correction. If a row could stop accruing attempts entirely,
/// nothing would ever dead-letter and a genuinely poisonous message would be retried forever —
/// strictly worse than the bug being fixed, because it is silent and unbounded. These tests pin the
/// cases where the budget MUST still be spent, and are expected to pass both before and after the
/// fix: they exist to fail if a future change makes the refund unconditional.
/// </para>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public class InboxAttemptAccountingBoundaryTests : EFCoreTestBase {

  /// <summary>
  /// The fail-safe. A worker that vanishes mid-dispatch releases nothing, so its charge stands and
  /// repeated crashes still converge on the cap. This is the property that makes claim-time
  /// charging the right default despite its cost.
  /// </summary>
  [Test]
  public async Task RepeatedCrashesWithoutRelease_ConvergeOnTheCapAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = TrackedGuid.NewMedo().Value;
    var streamId = TrackedGuid.NewMedo().Value;

    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 0);

    for (var crash = 0; crash < 6; crash++) {
      await _claimOrphanedInboxAsync(conn, TrackedGuid.NewMedo().Value);
      await _expireLeaseAsync(conn, messageId);
    }

    var attempts = await _readAttemptsAsync(conn, messageId);
    await Assert.That(attempts).IsEqualTo(6)
      .Because("nothing released these claims, so every one must still be charged — otherwise a "
             + "crash-looping host would retry the same message forever and never dead-letter");
  }

  /// <summary>
  /// A release only ever refunds the single attempt its own claim charged. It cannot reach back and
  /// erase budget spent by earlier, genuinely-consumed attempts.
  /// </summary>
  [Test]
  public async Task ReleasingDoesNotErasePreviouslySpentBudgetAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = TrackedGuid.NewMedo().Value;
    var streamId = TrackedGuid.NewMedo().Value;

    // Three attempts genuinely consumed by crashes before this worker ever sees the row.
    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 3);

    var instance = TrackedGuid.NewMedo().Value;
    await _claimOrphanedInboxAsync(conn, instance);          // 3 -> 4
    await _releaseUnprocessedAsync(conn, instance, [messageId]);  // 4 -> 3

    var attempts = await _readAttemptsAsync(conn, messageId);
    await Assert.That(attempts).IsEqualTo(3)
      .Because("the refund is scoped to this claim only; letting it unwind history would hand a "
             + "poisonous message an unlimited budget one release at a time");
  }

  // ==================== helpers ====================

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _insertInboxRowAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         stream_id, partition_number, instance_id, lease_expiry, error, failure_reason)
      VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, @att, NOW(),
              @stream, 0, NULL, NULL, NULL, 99)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("att", attempts);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _claimOrphanedInboxAsync(NpgsqlConnection conn, Guid claimingInstance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT * FROM claim_orphaned_inbox(
        @inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 1, NOW() - INTERVAL '10 minutes')";
    cmd.Parameters.AddWithValue("inst", claimingInstance);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { }
  }

  private static async Task _releaseUnprocessedAsync(
      NpgsqlConnection conn, Guid instanceId, Guid[] messageIds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT release_unprocessed_inbox(@inst, @ids)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("ids", messageIds);
    await cmd.ExecuteScalarAsync();
  }

  private static async Task _expireLeaseAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      "UPDATE wh_inbox SET lease_expiry = NOW() - INTERVAL '1 minute' WHERE message_id = @msg";
    cmd.Parameters.AddWithValue("msg", messageId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> _readAttemptsAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT attempts FROM wh_inbox WHERE message_id = @msg";
    cmd.Parameters.AddWithValue("msg", messageId);
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }
}
