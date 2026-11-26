// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Priority;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1630 (owner decision 2026-11-21): an inbox row held back behind an earlier event of its stream that is not yet
/// stored (230) records a distinct state operators can see, <see cref="MessageFailureReason.AwaitingStreamHead"/> with
/// an error naming the stream and when the wait began, instead of reading as a fresh row or, once its lease lapsed, as a
/// row whose dispatch was cut off.
/// </summary>
/// <remarks>
/// Every lapse is set on the database clock (<c>NOW() - interval</c>), the clock the claim compares against, and every
/// claim is the real <c>claim_work</c>.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/551_HeldBackRowsShowTheyAwaitTheStreamHead.sql</code-under-test>
/// <docs>fundamentals/work-coordinator/claim-loop#a-row-waiting-for-its-stream-head</docs>
[Category("Integration")]
[Category("Shard5")]
public class AwaitingStreamHeadSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Test.Domain.ThingHappened, Test";
  private const int UNKNOWN = (int)MessageFailureReason.Unknown;

  [Test]
  public async Task Claim_ARowHeldBackBehindAnUnstoredHead_ShowsItAwaitsTheStreamHeadAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    var peer = Guid.CreateVersion7();
    await _heartbeatAsync(conn, instance);
    var stream = Guid.CreateVersion7();
    var (head, waiting) = _ordered(Guid.CreateVersion7(), Guid.CreateVersion7());
    await _seedAsync(conn, head, stream, ageMinutes: 50, heldBy: peer);
    await _seedAsync(conn, waiting, stream, ageMinutes: 5, heldBy: null);

    await _claimAsync(conn, instance);

    await Assert.That(await _storedAsync(conn, waiting)).IsFalse().Because("precondition: the store pass held it back");
    var (reason, error) = await _stateAsync(conn, waiting);
    await Assert.That(reason).IsEqualTo((int)MessageFailureReason.AwaitingStreamHead)
      .Because("a row held back behind its stream's unstored head records that it waits, a state of its own");
    await Assert.That(error).IsNotNull();
    await Assert.That(error!).StartsWith("Waiting for the stream head since ")
      .Because("the error says what the row waits for and since when");
    await Assert.That(error).Contains(stream.ToString()).Because("the error names the stream whose head it waits for");
    await Assert.That(error).DoesNotContain("mid-dispatch").Because("nothing was dispatched");
    var (headReason, headError) = await _stateAsync(conn, head);
    await Assert.That(headReason).IsEqualTo(UNKNOWN).Because("the head itself waits for nothing, and is not marked");
    await Assert.That(headError).IsNull();
  }

  [Test]
  public async Task Claim_AWaitingRowWhoseLeaseLapsed_IsReclaimedStillAwaitingTheHead_NotAsALeaseExpiryAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var first = Guid.CreateVersion7();
    var peer = Guid.CreateVersion7();
    await _heartbeatAsync(conn, first);
    var stream = Guid.CreateVersion7();
    var (head, waiting) = _ordered(Guid.CreateVersion7(), Guid.CreateVersion7());
    await _seedAsync(conn, head, stream, ageMinutes: 50, heldBy: peer);
    await _seedAsync(conn, waiting, stream, ageMinutes: 5, heldBy: null, attempts: 2);
    await _claimAsync(conn, first);
    var (_, firstError) = await _stateAsync(conn, waiting);
    // The first instance is gone and its lease lapsed while the head is still unstored: the next instance re-acquires
    // the row through the claim's lapsed-lease branch, the one that records a cut-off dispatch.
    await _execAsync(conn, "DELETE FROM wh_service_instances WHERE instance_id = @i", ("i", first));
    await _execAsync(conn, "UPDATE wh_inbox_state SET lease_expiry = NOW() - INTERVAL '1 minute' WHERE message_id = @m",
      ("m", waiting));
    await _execAsync(conn, "UPDATE wh_active_streams SET lease_expiry = NOW() - INTERVAL '1 minute' WHERE stream_id = @s",
      ("s", stream));
    var second = Guid.CreateVersion7();
    await _heartbeatAsync(conn, second);

    await _claimAsync(conn, second);

    await Assert.That(await _leasedToAsync(conn, waiting)).IsEqualTo(second).Because("precondition: the lapse was re-acquired");
    var (reason, error) = await _stateAsync(conn, waiting);
    await Assert.That(reason).IsEqualTo((int)MessageFailureReason.AwaitingStreamHead)
      .Because("a lapse while waiting is not a dispatch that ended without an outcome; the row still waits");
    await Assert.That(error).IsEqualTo(firstError)
      .Because("the wait is the same wait: its text, and the time it began, are kept");
    await Assert.That(await _attemptsAsync(conn, waiting)).IsEqualTo(2).Because("waiting is not charged an attempt");
  }

  [Test]
  public async Task Claim_OnceTheHeadIsHeld_TheWaitingRowIsStored_AndNoLongerShowsItWaitsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    var peer = Guid.CreateVersion7();
    await _heartbeatAsync(conn, instance);
    var stream = Guid.CreateVersion7();
    var (head, waiting) = _ordered(Guid.CreateVersion7(), Guid.CreateVersion7());
    await _seedAsync(conn, head, stream, ageMinutes: 50, heldBy: peer);
    await _seedAsync(conn, waiting, stream, ageMinutes: 5, heldBy: null);
    await _claimAsync(conn, instance);
    var (waitingReason, _) = await _stateAsync(conn, waiting);
    await Assert.That(waitingReason).IsEqualTo((int)MessageFailureReason.AwaitingStreamHead).Because("precondition: it waited");
    // The peer lets the head go, so this instance can take it and store the stream in order.
    await _execAsync(conn, "UPDATE wh_inbox_state SET instance_id = NULL, lease_expiry = NULL WHERE message_id = @m",
      ("m", head));

    await _claimAsync(conn, instance);

    await Assert.That(await _storedAsync(conn, waiting)).IsTrue().Because("precondition: the head no longer holds it back");
    var (reason, error) = await _stateAsync(conn, waiting);
    await Assert.That(reason).IsEqualTo(UNKNOWN)
      .Because("a stored row no longer waits, so it reads as any fresh row does, not as one still waiting");
    await Assert.That(error).IsNull();
  }

  [Test]
  public async Task Claim_AHeldBackRowCarryingAnEarlierLeaseExpiry_ShowsItAwaitsTheHeadInsteadAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    var peer = Guid.CreateVersion7();
    await _heartbeatAsync(conn, instance);
    var stream = Guid.CreateVersion7();
    var (head, waiting) = _ordered(Guid.CreateVersion7(), Guid.CreateVersion7());
    await _seedAsync(conn, head, stream, ageMinutes: 50, heldBy: peer);
    // What a waiting row carried before this release: the lapsed-lease branch's reading of a wait as a cut-off dispatch.
    await _seedAsync(conn, waiting, stream, ageMinutes: 5, heldBy: null,
      failureReason: (int)MessageFailureReason.LeaseExpired,
      error: "Attempt 1 ended without a reported outcome: lease held by instance x expired (process terminated mid-dispatch, or the handler outran its lease).");

    await _claimAsync(conn, instance);

    var (reason, error) = await _stateAsync(conn, waiting);
    await Assert.That(reason).IsEqualTo((int)MessageFailureReason.AwaitingStreamHead)
      .Because("an unstored row was never dispatched, so a lease expiry on it was a wait, and is recorded as one");
    await Assert.That(error!).StartsWith("Waiting for the stream head since ");
  }

  [Test]
  public async Task Claim_AHeldBackRowCarryingAnotherRecordedFailure_KeepsItAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    var peer = Guid.CreateVersion7();
    await _heartbeatAsync(conn, instance);
    var stream = Guid.CreateVersion7();
    var (head, waiting) = _ordered(Guid.CreateVersion7(), Guid.CreateVersion7());
    await _seedAsync(conn, head, stream, ageMinutes: 50, heldBy: peer);
    await _seedAsync(conn, waiting, stream, ageMinutes: 5, heldBy: null,
      failureReason: (int)MessageFailureReason.SerializationError, error: "a recorded failure");

    await _claimAsync(conn, instance);

    await Assert.That(await _storedAsync(conn, waiting)).IsFalse().Because("precondition: the store pass held it back");
    var (reason, error) = await _stateAsync(conn, waiting);
    await Assert.That(reason).IsEqualTo((int)MessageFailureReason.SerializationError)
      .Because("a failure someone recorded is the better diagnostic, and the wait does not paper over it");
    await Assert.That(error).IsEqualTo("a recorded failure");
  }

  // ===== Helpers =====

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) =>
    _execAsync(conn, """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@i, 'AwaitHeadSvc', 'host', 1, NOW(), NOW(), '{}'::jsonb)
      """, ("i", instance));

  /// <summary>Seeds one pending inbox event; a non-null <paramref name="heldBy"/> leases it under a live lease.</summary>
  private static async Task _seedAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, int ageMinutes, Guid? heldBy, int attempts = 0,
      int failureReason = UNKNOWN, string? error = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        VALUES (@msg, 'TestHandler', @type, '{"p": {}}', '{}', NOW() - (@age * INTERVAL '1 minute'), @stream, true, @priority)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id,
         lease_expiry, failure_reason, error)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, @attempts, 0,
             @heldBy, CASE WHEN @heldBy::uuid IS NULL THEN NULL ELSE NOW() + INTERVAL '5 minutes' END, @reason, @error
      FROM m
      """;
    cmd.Parameters.AddWithValue("msg", messageId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("type", EVENT_TYPE);
    cmd.Parameters.AddWithValue("age", ageMinutes);
    cmd.Parameters.AddWithValue("priority", WorkPriority.INTERACTIVE);
    cmd.Parameters.AddWithValue(nameof(attempts), attempts);
    cmd.Parameters.AddWithValue("reason", failureReason);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(error), NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter(nameof(heldBy), NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)heldBy ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  // The store pass orders a stream by message id as Postgres compares uuids, so a pair is sorted the way it sorts them.
  private static (Guid First, Guid Second) _ordered(Guid a, Guid b) =>
    string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? (a, b) : (b, a);

  private static async Task _claimAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT count(*) FROM claim_work(
        p_instance_id => @i,
        p_service_name => 'AwaitHeadSvc',
        p_host_name => 'host',
        p_process_id => 1,
        p_max_streams => 10,
        p_partition_count => 10000,
        p_lease_seconds => 300,
        p_max_rows => 10)
      """;
    cmd.Parameters.AddWithValue("i", instance);
    cmd.CommandTimeout = 120;
    await cmd.ExecuteScalarAsync();
  }

  private static async Task<Guid?> _leasedToAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_inbox_state WHERE message_id = @m AND lease_expiry > NOW()";
    cmd.Parameters.AddWithValue("m", messageId);
    return await cmd.ExecuteScalarAsync() as Guid?;
  }

  private static async Task<int> _attemptsAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT attempts FROM wh_inbox_state WHERE message_id = @m";
    cmd.Parameters.AddWithValue("m", messageId);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<(int Reason, string? Error)> _stateAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT failure_reason, error FROM wh_inbox_state WHERE message_id = @m";
    cmd.Parameters.AddWithValue("m", messageId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
  }

  private static async Task<bool> _storedAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE event_id = @m)";
    cmd.Parameters.AddWithValue("m", messageId);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }
}
