using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks that a claim a worker never dispatched costs no retry budget.
///
/// <para>
/// <c>claim_orphaned_inbox</c> charges an attempt on every claim. That is deliberate and must stay:
/// it is the only fail-safe that survives a process vanishing mid-dispatch, because a dead process
/// reports nothing. The cost of that choice is that a worker which claims more rows than it can
/// dispatch inside the lease window pays an attempt for every untouched row, every cycle — so a
/// backlog larger than one worker's throughput burns its own retry budget and dead-letters healthy
/// messages as <c>MaxAttemptsExceeded</c> having never been handed to a receptor.
/// </para>
///
/// <para>
/// The resolution is a REFUND, not a smaller charge: the claim stays optimistic, and a worker that
/// finishes a cycle with rows it never touched says so explicitly. Only an UNGRACEFUL exit — where
/// nothing is released because the process is gone — leaves the charge standing. The database cannot
/// distinguish "never dispatched" from "dispatched and died"; only the worker can, so the worker has
/// to tell it.
/// </para>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
[Category("Shard4")]
public class InboxGracefulReleaseSqlTests : EFCoreTestBase {

  [Test]
  public async Task ReleaseUnprocessed_RefundsTheClaimAttemptAndClearsTheLeaseAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = TrackedGuid.NewMedo().Value;
    var streamId = TrackedGuid.NewMedo().Value;
    var instance = TrackedGuid.NewMedo().Value;

    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 0);
    await _claimOrphanedInboxAsync(conn, instance);

    var claimed = await _readInboxRowAsync(conn, messageId);
    await Assert.That(claimed.Attempts).IsEqualTo(1)
      .Because("the claim charges optimistically — that fail-safe is what this fix must NOT remove");

    await _releaseUnprocessedAsync(conn, instance, [messageId]);

    var released = await _readInboxRowAsync(conn, messageId);
    await Assert.That(released.Attempts).IsEqualTo(0)
      .Because("the worker never dispatched this row, so the optimistic charge must be refunded — "
             + "otherwise a backlog larger than one worker's throughput destroys its own budget");
    await Assert.That(released.InstanceId).IsNull()
      .Because("a released row must be claimable again immediately, not wait out its lease");
    await Assert.That(released.LeaseExpiry).IsNull()
      .Because("leaving the lease set would keep the row invisible until it expired");
  }

  /// <summary>
  /// The guard that keeps the fail-safe intact. If releasing refunded unconditionally, a caller
  /// could quietly grant messages infinite retries — trading a budget-burn bug for an unbounded-retry
  /// bug, which is worse because nothing would ever dead-letter.
  /// </summary>
  [Test]
  public async Task ConsumerVanishesWithoutReleasing_StillPaysTheAttemptAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = TrackedGuid.NewMedo().Value;
    var streamId = TrackedGuid.NewMedo().Value;

    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 0);

    // Two claim cycles with NO release between them — the process died each time.
    await _claimOrphanedInboxAsync(conn, TrackedGuid.NewMedo().Value);
    await _expireLeaseAsync(conn, messageId);
    await _claimOrphanedInboxAsync(conn, TrackedGuid.NewMedo().Value);

    var row = await _readInboxRowAsync(conn, messageId);
    await Assert.That(row.Attempts).IsEqualTo(2)
      .Because("a process that vanishes reports nothing, so its charge must stand — this is the "
             + "property that stops a crash loop from retrying forever");
  }

  /// <summary>
  /// The actual production shape: a worker repeatedly claims far more than it dispatches. Without a
  /// refund the untouched rows climb one attempt per cycle and die at the cap; with it they stay
  /// flat no matter how many cycles pass.
  /// </summary>
  [Test]
  public async Task RepeatedOverClaim_DoesNotAccumulateAttemptsOnUntouchedRowsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var untouched = TrackedGuid.NewMedo().Value;
    var streamId = TrackedGuid.NewMedo().Value;

    await _insertInboxRowAsync(conn, untouched, streamId, attempts: 0);

    // Five cycles of "claim it, never get to it, hand it back".
    for (var cycle = 0; cycle < 5; cycle++) {
      var instance = TrackedGuid.NewMedo().Value;
      await _claimOrphanedInboxAsync(conn, instance);
      await _releaseUnprocessedAsync(conn, instance, [untouched]);
    }

    var row = await _readInboxRowAsync(conn, untouched);
    await Assert.That(row.Attempts).IsEqualTo(0)
      .Because("five cycles of claim-and-hand-back is five dispatches never attempted; charging for "
             + "them is what converts a backlog into permanent message loss");
  }

  [Test]
  public async Task ReleaseUnprocessed_NeverDrivesAttemptsNegativeAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = TrackedGuid.NewMedo().Value;
    var streamId = TrackedGuid.NewMedo().Value;
    var instance = TrackedGuid.NewMedo().Value;

    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 0);
    await _claimOrphanedInboxAsync(conn, instance);
    await _releaseUnprocessedAsync(conn, instance, [messageId]);
    // A duplicated release — retry, at-least-once flush, double shutdown path.
    await _releaseUnprocessedAsync(conn, instance, [messageId]);

    var row = await _readInboxRowAsync(conn, messageId);
    await Assert.That(row.Attempts).IsEqualTo(0)
      .Because("release must be idempotent; a negative budget would make the row effectively "
             + "un-dead-letterable");
  }

  /// <summary>
  /// Releasing is scoped to the caller's own claim. A worker must never be able to refund — or
  /// unlock — a row another instance is actively dispatching.
  /// </summary>
  [Test]
  public async Task ReleaseUnprocessed_DoesNotTouchAnotherInstancesClaimAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = TrackedGuid.NewMedo().Value;
    var streamId = TrackedGuid.NewMedo().Value;
    var owner = TrackedGuid.NewMedo().Value;
    var stranger = TrackedGuid.NewMedo().Value;

    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 0);
    await _claimOrphanedInboxAsync(conn, owner);
    var afterClaim = await _readInboxRowAsync(conn, messageId);

    await _releaseUnprocessedAsync(conn, stranger, [messageId]);

    var row = await _readInboxRowAsync(conn, messageId);
    await Assert.That(row.Attempts).IsEqualTo(afterClaim.Attempts)
      .Because("a release from an instance that does not hold the claim must be a no-op");
    await Assert.That(row.InstanceId).IsNotNull()
      .Because("stealing the lease out from under the real owner would let two workers dispatch the "
             + "same message concurrently");
  }

  /// <summary>
  /// Exercises the COORDINATOR method rather than the SQL function beneath it. The two are separate
  /// failure surfaces: schema resolution, parameter typing and the array marshalling all live in the
  /// C# wrapper, so a green SQL test says nothing about whether the path a worker actually calls
  /// works. Testing only the function is how this method reached production with no coverage at all.
  /// </summary>
  [Test]
  public async Task Coordinator_ReleaseUnprocessedInbox_RefundsThroughTheRealCallPathAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = TrackedGuid.NewMedo().Value;
    var streamId = TrackedGuid.NewMedo().Value;
    var instance = TrackedGuid.NewMedo().Value;

    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 0);
    await _claimOrphanedInboxAsync(conn, instance);

    var coordinator = _coordinator(dbContext);
    var released = await coordinator.ReleaseUnprocessedInboxAsync(instance, [messageId]);

    await Assert.That(released).IsEqualTo(1)
      .Because("the coordinator must report what it actually released — a worker sizing its next "
             + "claim on that number needs it to be true");
    var row = await _readInboxRowAsync(conn, messageId);
    await Assert.That(row.Attempts).IsEqualTo(0)
      .Because("the refund must survive the wrapper, not just the function");
    await Assert.That(row.InstanceId).IsNull();
  }

  [Test]
  public async Task Coordinator_ReleaseUnprocessedInbox_EmptyList_IsANoOpAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);

    var released = await coordinator.ReleaseUnprocessedInboxAsync(TrackedGuid.NewMedo().Value, []);

    await Assert.That(released).IsEqualTo(0)
      .Because("an empty hand-back must not open a connection or issue a statement — the shutdown "
             + "path calls this whenever a loop ends, most often with nothing to release");
  }

  // ==================== helpers ====================

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

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

  private static async Task<(int Attempts, Guid? InstanceId, DateTimeOffset? LeaseExpiry)>
      _readInboxRowAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      "SELECT attempts, instance_id, lease_expiry FROM wh_inbox WHERE message_id = @msg";
    cmd.Parameters.AddWithValue("msg", messageId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"inbox row {messageId} not found");
    }
    return (
      reader.GetInt32(0),
      reader.IsDBNull(1) ? null : reader.GetGuid(1),
      reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
  }
}
