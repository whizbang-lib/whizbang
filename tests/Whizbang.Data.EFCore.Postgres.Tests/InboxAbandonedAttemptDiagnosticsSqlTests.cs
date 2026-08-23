using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks that an inbox attempt consumed by lease abandonment records WHY.
///
/// <para>
/// <c>claim_orphaned_inbox</c> is the sole source of attempt counting and bumps unconditionally on
/// every claim. <c>process_inbox_failures</c> records <c>error</c>/<c>failure_reason</c> and nulls
/// the lease, but it only runs when dispatch actually reported a failure. When the process is
/// killed mid-dispatch — SIGKILL from a failed liveness probe, container replaced, handler hung
/// past its lease — nothing reports anything: the lease simply expires and the next claim bumps the
/// counter. The row's <c>error</c> stays NULL and <c>failure_reason</c> stays Unknown.
/// </para>
///
/// <para>
/// The retry budget is therefore spendable in total silence. Observed in production as ~54k inbox
/// rows averaging 11 attempts — every single one with <c>error IS NULL</c> and
/// <c>failure_reason = 99</c> — which then dead-lettered as "MaxAttemptsExceeded: attempts=N &gt;
/// max=10", a message that describes the budget running out and says nothing about what consumed
/// it. Operators could not tell a crash-looping host from a genuinely failing handler, and the real
/// cause stayed hidden behind a generic counter for hours.
/// </para>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
[Category("Shard4")]
public class InboxAbandonedAttemptDiagnosticsSqlTests : EFCoreTestBase {

  /// <summary>MessageFailureReason.LeaseExpired.</summary>
  private const int LEASE_EXPIRED = 6;

  [Test]
  public async Task ClaimOrphanedInbox_AbandonedLease_RecordsWhyTheAttemptWasConsumedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var deadInstance = (Guid)TrackedGuid.NewMedo();

    // A row claimed by an instance that then died mid-dispatch: lease is in the past, and nothing
    // ever reported a failure, so error/failure_reason were never written.
    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 3,
      instanceId: deadInstance, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-5));

    await _claimOrphanedInboxAsync(conn, (Guid)TrackedGuid.NewMedo());

    var row = await _readInboxRowAsync(conn, messageId);

    await Assert.That(row.Attempts).IsEqualTo(4)
      .Because("claim_orphaned_inbox is the sole attempt counter and bumps on every re-claim");

    await Assert.That(row.FailureReason).IsEqualTo(LEASE_EXPIRED)
      .Because("an attempt consumed by an expired lease must be attributable — otherwise the retry "
             + "budget drains silently and the eventual dead-letter blames only the counter");

    await Assert.That(row.Error).IsNotNull()
      .Because("the row must say WHAT consumed the attempt, not merely that one was consumed");
    await Assert.That(row.Error!).Contains(deadInstance.ToString())
      .Because("naming the instance that held the lease is what distinguishes a crash-looping host "
             + "from a failing handler");
  }

  [Test]
  public async Task ClaimOrphanedInbox_RecordedFailure_DoesNotOverwriteTheRealErrorAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    // The shape process_inbox_failures leaves behind: lease released, real error recorded.
    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 2,
      instanceId: null, leaseExpiry: null,
      error: "ValidationError: Price must be positive", failureReason: 4);

    await _claimOrphanedInboxAsync(conn, (Guid)TrackedGuid.NewMedo());

    var row = await _readInboxRowAsync(conn, messageId);

    await Assert.That(row.Error).IsEqualTo("ValidationError: Price must be positive")
      .Because("a genuine dispatch failure is the better diagnostic — re-claim must never paper "
             + "over it with a lease-expiry note");
    await Assert.That(row.FailureReason).IsEqualTo(4)
      .Because("the recorded failure reason must survive re-claim");
  }

  [Test]
  public async Task ClaimOrphanedInbox_FreshRow_RecordsNoFailureAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    // Never claimed: no instance, no lease, no error.
    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 0,
      instanceId: null, leaseExpiry: null);

    await _claimOrphanedInboxAsync(conn, (Guid)TrackedGuid.NewMedo());

    var row = await _readInboxRowAsync(conn, messageId);

    await Assert.That(row.Attempts).IsEqualTo(1)
      .Because("attempts is one-based: 1 means the first attempt has started");
    await Assert.That(row.Error).IsNull()
      .Because("a first claim has consumed nothing yet — stamping it would make every healthy "
             + "message look like a casualty");
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
      NpgsqlConnection conn, Guid messageId, Guid streamId, int attempts,
      Guid? instanceId = null, DateTimeOffset? leaseExpiry = null,
      string? error = null, int failureReason = 99) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         stream_id, partition_number, instance_id, lease_expiry, error, failure_reason)
      VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, @att, NOW(),
              @stream, 0, @inst, @lease, @err, @reason)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("att", attempts);
    ins.Parameters.AddWithValue("inst", (object?)instanceId ?? DBNull.Value);
    ins.Parameters.AddWithValue("lease", (object?)leaseExpiry ?? DBNull.Value);
    ins.Parameters.AddWithValue("err", (object?)error ?? DBNull.Value);
    ins.Parameters.AddWithValue("reason", failureReason);
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

  private static async Task<(int Attempts, int FailureReason, string? Error)> _readInboxRowAsync(
      NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT attempts, failure_reason, error FROM wh_inbox WHERE message_id = @msg";
    cmd.Parameters.AddWithValue("msg", messageId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"inbox row {messageId} not found");
    }
    return (
      reader.GetInt32(0),
      reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
      reader.IsDBNull(2) ? null : reader.GetString(2));
  }
}
