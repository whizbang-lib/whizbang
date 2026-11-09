// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What a receiver pays to ring doorbells while it absorbs work, most visibly repair answers arriving in bulk (365).
/// A handler commit that stores inbox children rings through the store's own rule and nothing more: once for a stream
/// with no pending work, never for a child already seen, never for a stream whose owner already has work queued. And a
/// doorbell judges liveness once per call however many owners it rings: the rule reads <c>pg_locks</c> and, called per
/// owner, was planned and run again for every one.
/// </summary>
/// <docs>fundamentals/work-coordinator/handler-commit</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/365_RepairAnswersAreAbsorbedCheaply.sql</code-under-test>
[Category("Shard5")]
public class ReceiveDoorbellCostSqlTests : EFCoreTestBase {

  // --- commit_handler_result -----------------------------------------------------------------------

  [Test]
  public async Task CommitHandlerResult_AChildOnAStreamWithNoPendingWork_RingsItsOwnerOnceAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance);

    await using var tx = await conn.BeginTransactionAsync();
    await _commitChildAsync(conn, tx, instance, (Guid)TrackedGuid.New(), (Guid)TrackedGuid.New());
    var rings = await _doorbellsAsync(conn, instance, tx);
    await tx.RollbackAsync();

    await Assert.That(rings).IsEqualTo(1L)
      .Because("the store rings the stream's owner for the child it stored; the commit ringing every stream again doubled it");
  }

  [Test]
  public async Task CommitHandlerResult_AChildAlreadySeen_RingsNoOneAsync() {
    var instance = (Guid)TrackedGuid.New();
    var child = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance);
    await _executeAsync(conn, $"INSERT INTO wh_message_deduplication (message_id, first_seen_at) VALUES ('{child}', NOW())");

    await using var tx = await conn.BeginTransactionAsync();
    await _commitChildAsync(conn, tx, instance, child, (Guid)TrackedGuid.New());
    var rings = await _doorbellsAsync(conn, instance, tx);
    await tx.RollbackAsync();

    await Assert.That(rings).IsEqualTo(0L)
      .Because("a redelivered child the store skips as a duplicate is no new work, so nothing is rung for it");
  }

  [Test]
  public async Task CommitHandlerResult_AChildOnAStreamWithPendingWork_RingsNoOneAsync() {
    var instance = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance);
    await _commitChildAsync(conn, transaction: null, instance, (Guid)TrackedGuid.New(), stream);
    await _executeAsync(conn, "DELETE FROM wh_doorbell_queue");   // the first child's rings, already counted above
    await _executeAsync(conn, "DELETE FROM wh_notify_state");     // and its debounce state, so nothing is suppressed

    await using var tx = await conn.BeginTransactionAsync();
    await _commitChildAsync(conn, tx, instance, (Guid)TrackedGuid.New(), stream);
    var rings = await _doorbellsAsync(conn, instance, tx);
    await tx.RollbackAsync();

    await Assert.That(rings).IsEqualTo(0L)
      .Because("the stream already has pending work its owner was rung for (114); a second child adds no reason to wake it");
  }

  // --- notify_instance_owners_with_payload ---------------------------------------------------------

  [Test]
  public async Task NotifyOwners_ManyOwners_JudgesLivenessOnceNotPerOwnerAsync() {
    Guid[] owners = [(Guid)TrackedGuid.New(), (Guid)TrackedGuid.New(), (Guid)TrackedGuid.New()];
    await using var conn = await _openAsync();
    var streams = new List<Guid>();
    foreach (var owner in owners) {
      await _registerAsync(conn, owner);
      var stream = (Guid)TrackedGuid.New();
      streams.Add(stream);
      await _executeAsync(conn,
        $"INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id) VALUES ('{stream}', 0, '{owner}')");
    }

    await using var tx = await conn.BeginTransactionAsync();
    await _executeAsync(conn, "SET LOCAL track_functions = 'all'", tx);
    await _executeAsync(conn,
      $"SELECT notify_instance_owners_with_payload('inbox', 'inbox', ARRAY['{string.Join("','", streams)}']::uuid[])", tx);
    var perOwnerChecks = await _scalarAsync<long>(conn,
      "SELECT COALESCE(SUM(calls), 0) FROM pg_stat_xact_user_functions WHERE funcname = 'wh_instance_is_live'", tx);
    long rung = 0;
    foreach (var owner in owners) {
      rung += await _doorbellsAsync(conn, owner, tx);
    }
    await tx.RollbackAsync();

    await Assert.That(rung).IsEqualTo(3L).Because("precondition: every owner is rung");
    await Assert.That(perOwnerChecks).IsEqualTo(0L)
      .Because("liveness is read once for the call; asking the rule per owner planned and ran its pg_locks read each time");
  }

  [Test]
  public async Task NotifyOwners_ALiveOwnerDraining_IsSuppressed_ButADeadOneIsRungAsync() {
    var live = (Guid)TrackedGuid.New();
    var dead = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, live);
    await _registerAsync(conn, dead, TimeSpan.FromMinutes(10));
    var liveStream = (Guid)TrackedGuid.New();
    var deadStream = (Guid)TrackedGuid.New();
    foreach (var (owner, stream) in new[] { (live, liveStream), (dead, deadStream) }) {
      await _executeAsync(conn,
        $"INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id) VALUES ('{stream}', 0, '{owner}')");
      // A fresh found-work watermark: a live owner is draining, so its doorbell is suppressed.
      await _executeAsync(conn, $"""
        INSERT INTO wh_notify_state (instance_id, payload_kind, last_work_at, last_attempt_at, rapid_run, effective_window_ms, fired_count, suppressed_count)
        VALUES ('{owner}', 'inbox', clock_timestamp(), NULL, 0, 0, 0, 0)
        """);
    }

    await using var tx = await conn.BeginTransactionAsync();
    await _executeAsync(conn,
      $"SELECT notify_instance_owners_with_payload('inbox', 'inbox', ARRAY['{liveStream}', '{deadStream}']::uuid[])", tx);
    var toLive = await _doorbellsAsync(conn, live, tx);
    var toDead = await _doorbellsAsync(conn, dead, tx);
    await tx.RollbackAsync();

    await Assert.That(toLive).IsEqualTo(0L).Because("a live owner that found work within the window needs no doorbell");
    await Assert.That(toDead).IsEqualTo(1L)
      .Because("only a live target may be suppressed: a dead owner's fresh watermark must not strand its work (130)");
  }

  // ------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>A pooled instance whose heartbeat is <paramref name="age"/> old (live within its 90 second window).</summary>
  private static Task _registerAsync(NpgsqlConnection conn, Guid instance, TimeSpan? age = null) =>
    _executeAsync(conn, $"""
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode, liveness_window_seconds)
      VALUES ('{instance}', 'test', 'test-host', 1, NOW() - INTERVAL '{(int)(age ?? TimeSpan.Zero).TotalSeconds} seconds',
              NOW() - INTERVAL '2 hours', '{InstanceConnectionModes.POOLED}', 90)
      """);

  /// <summary>A handler commit by <paramref name="instance"/> that stores one inbox child on <paramref name="stream"/>.</summary>
  private static Task _commitChildAsync(NpgsqlConnection conn, NpgsqlTransaction? transaction, Guid instance, Guid child, Guid stream) =>
    _executeAsync(conn, $$"""
      SELECT commit_handler_result('{
        "instance_id": "{{instance}}",
        "new_inbox_messages": [{
          "MessageId": "{{child}}", "HandlerName": "TestHandler", "MessageType": "TestEvent",
          "Envelope": {}, "Metadata": {}, "Scope": null, "StreamId": "{{stream}}", "IsEvent": true
        }]
      }'::jsonb)
      """, transaction);

  private static Task<long> _doorbellsAsync(NpgsqlConnection conn, Guid instance, NpgsqlTransaction tx) =>
    _scalarAsync<long>(conn, $"SELECT count(*) FROM wh_doorbell_queue WHERE channel = 'wh_work_i_{instance}'", tx);

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Transaction = tx;
    return (T)Convert.ChangeType(await cmd.ExecuteScalarAsync(), typeof(T), CultureInfo.InvariantCulture)!;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Transaction = tx;
    await cmd.ExecuteNonQueryAsync();
  }
}
