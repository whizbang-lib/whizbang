// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Whizbang.Core.Priority;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2120 (owner decision 2026-11-07: both). An interactive event whose earlier events in its stream are background
/// priority and unclaimed is held back from storing until they are handled (230). Acquisition orders by priority and
/// reads each band through a window of its oldest rows, so on a busy service the background predecessors were never
/// claimed, while the interactive event was claimed again and charged an attempt on every lease until it was
/// dead-lettered.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Acquisition takes a stream's earlier pending events with the rows it picks, whatever their priority, so the
/// held-back event is stored in order with them.</item>
/// <item>A row the claim acquired but the store pass held back (its predecessor is held elsewhere) is not charged the
/// attempt the claim counted: waiting is not failing.</item>
/// </list>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/411_HeldBackStreamsAreClaimedWhole.sql</code-under-test>
/// <docs>fundamentals/work-coordinator/claim-loop#a-held-back-stream-is-claimed-whole</docs>
[Category("Integration")]
[Category("Shard5")]
public class HeldBackStreamsClaimSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Test.Domain.ThingHappened, Test";

  [Test]
  public async Task Claim_AnInteractiveEventBehindBackgroundPredecessorsOutsideTheWindow_TakesThePredecessorsWithItAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    // A background backlog older than the stream's predecessor fills the background band's window (eight batches).
    for (var i = 0; i < 40; i++) {
      await _seedAsync(conn, Guid.CreateVersion7(), Guid.CreateVersion7(), WorkPriority.BACKGROUND, ageMinutes: 100 + i, heldBy: null);
    }
    var stream = Guid.CreateVersion7();
    var (predecessor, interactive) = _ordered(Guid.CreateVersion7(), Guid.CreateVersion7());
    await _seedAsync(conn, predecessor, stream, WorkPriority.BACKGROUND, ageMinutes: 50, heldBy: null);
    await _seedAsync(conn, interactive, stream, WorkPriority.INTERACTIVE, ageMinutes: 5, heldBy: null);

    await _claimAsync(conn, instance, maxRows: 2);

    await Assert.That(await _leasedToAsync(conn, predecessor)).IsEqualTo(instance)
      .Because("the interactive event's background predecessor is claimed with it, though it lies outside the band's window");
    await Assert.That(await _storedAsync(conn, interactive)).IsTrue()
      .Because("with its predecessor held by the same claim nothing holds the interactive event back from storing");
  }

  [Test]
  public async Task Claim_AStreamWithNoEarlierPendingEvent_IsClaimedAsBeforeAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    var stream = Guid.CreateVersion7();
    var only = Guid.CreateVersion7();
    await _seedAsync(conn, only, stream, WorkPriority.INTERACTIVE, ageMinutes: 5, heldBy: null);

    await _claimAsync(conn, instance, maxRows: 2);

    await Assert.That(await _leasedToAsync(conn, only)).IsEqualTo(instance);
    await Assert.That(await _attemptsAsync(conn, only)).IsEqualTo(1).Because("an acquisition that can proceed is charged, as before");
    await Assert.That(await _storedAsync(conn, only)).IsTrue();
  }

  [Test]
  public async Task Claim_ARowHeldBackBehindAPredecessorAPeerHolds_IsNotChargedAnAttemptAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    var peer = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    var stream = Guid.CreateVersion7();
    var (predecessor, waiting) = _ordered(Guid.CreateVersion7(), Guid.CreateVersion7());
    await _seedAsync(conn, predecessor, stream, WorkPriority.BACKGROUND, ageMinutes: 50, heldBy: peer);
    await _seedAsync(conn, waiting, stream, WorkPriority.INTERACTIVE, ageMinutes: 5, heldBy: null, attempts: 3);

    await _claimAsync(conn, instance, maxRows: 2);

    await Assert.That(await _leasedToAsync(conn, waiting)).IsEqualTo(instance).Because("precondition: the claim acquired it");
    await Assert.That(await _storedAsync(conn, waiting)).IsFalse().Because("precondition: the store pass held it back");
    await Assert.That(await _attemptsAsync(conn, waiting)).IsEqualTo(3)
      .Because("a row acquired but not offered because it is held back keeps its attempt count; waiting is not failing");
  }

  [Test]
  public async Task Claim_AHeldBackRowAcquiredByAnEarlierClaim_IsNotRefundedAgainAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    var peer = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    var stream = Guid.CreateVersion7();
    var (predecessor, waiting) = _ordered(Guid.CreateVersion7(), Guid.CreateVersion7());
    await _seedAsync(conn, predecessor, stream, WorkPriority.BACKGROUND, ageMinutes: 50, heldBy: peer);
    await _seedAsync(conn, waiting, stream, WorkPriority.INTERACTIVE, ageMinutes: 5, heldBy: null, attempts: 3);
    await _claimAsync(conn, instance, maxRows: 2);

    await _claimAsync(conn, instance, maxRows: 2);

    await Assert.That(await _attemptsAsync(conn, waiting)).IsEqualTo(3)
      .Because("a claim that did not acquire the row charged nothing, so it refunds nothing");
  }

  // ===== Helpers =====

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@i, 'HeldBackSvc', 'host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    cmd.Parameters.AddWithValue("i", instance);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Seeds one pending inbox event; a non-null <paramref name="heldBy"/> leases it under a live lease.</summary>
  private static async Task _seedAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, int priority, int ageMinutes, Guid? heldBy, int attempts = 0) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        VALUES (@msg, 'TestHandler', @type, '{"p": {}}', '{}', NOW() - (@age * INTERVAL '1 minute'), @stream, true, @priority)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, @attempts, 0,
             @heldBy, CASE WHEN @heldBy::uuid IS NULL THEN NULL ELSE NOW() + INTERVAL '5 minutes' END
      FROM m
      """;
    cmd.Parameters.AddWithValue("msg", messageId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("type", EVENT_TYPE);
    cmd.Parameters.AddWithValue("age", ageMinutes);
    cmd.Parameters.AddWithValue(nameof(priority), priority);
    cmd.Parameters.AddWithValue(nameof(attempts), attempts);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(heldBy), NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)heldBy ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  // The store pass orders a stream by message id as Postgres compares uuids, so a pair is sorted the way it sorts them.
  private static (Guid First, Guid Second) _ordered(Guid a, Guid b) =>
    string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? (a, b) : (b, a);

  private static async Task _claimAsync(NpgsqlConnection conn, Guid instance, int maxRows) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT count(*) FROM claim_work(
        p_instance_id => @i,
        p_service_name => 'HeldBackSvc',
        p_host_name => 'host',
        p_process_id => 1,
        p_max_streams => 10,
        p_partition_count => 10000,
        p_lease_seconds => 300,
        p_max_rows => @rows)
      """;
    cmd.Parameters.AddWithValue("i", instance);
    cmd.Parameters.AddWithValue("rows", maxRows);
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

  private static async Task<bool> _storedAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE event_id = @m)";
    cmd.Parameters.AddWithValue("m", messageId);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }
}
