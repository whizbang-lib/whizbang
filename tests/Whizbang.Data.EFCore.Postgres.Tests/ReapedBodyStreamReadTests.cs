// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The perspective drain's read (<c>get_stream_events_in_stream_order</c> through
/// <see cref="EFCoreWorkCoordinator{TDbContext}.GetStreamEventsAsync"/>) over a batch that holds an ephemeral stream whose
/// bodies the reap has deleted, next to a healthy stream (#2591). The read returns the reaped event's row with no body;
/// it is read as such, never as an <see cref="InvalidCastException"/> that loses every other stream of the batch.
/// </summary>
/// <docs>operations/workers/perspective-worker#unreadable-stored-events</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class ReapedBodyStreamReadTests : EFCoreTestBase {
  private const string PERSPECTIVE = "ActiveSessions+Projection";
  private const int EPHEMERAL = 8;
  private static readonly DateTimeOffset _writtenInJuly = new(2026, 7, 2, 9, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task GetStreamEvents_OverABatchHoldingAStreamWhoseBodiesWereReaped_ReadsEveryRow_AndThrowsNothingAsync() {
    await using var context = CreateDbContext();
    var conn = await _openAsync(context);
    var instanceId = (Guid)TrackedGuid.New();
    await _registerInstanceAsync(conn, instanceId);
    var healthyStream = (Guid)TrackedGuid.New();
    var reapedStream = (Guid)TrackedGuid.New();
    var missingStream = (Guid)TrackedGuid.New();
    var healthy = await _seedLeasedEventAsync(conn, instanceId, healthyStream, version: 1, body: true, flags: 0);
    var reaped = await _seedLeasedEventAsync(conn, instanceId, reapedStream, version: 1, body: false, flags: EPHEMERAL);
    var missing = await _seedLeasedEventAsync(conn, instanceId, missingStream, version: 1, body: false, flags: 0);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());

    List<StreamEventData>? rows = null;
    Exception? thrown = null;
    try {
      rows = await coordinator.GetStreamEventsAsync(instanceId, [healthyStream, reapedStream, missingStream]);
    } catch (Exception ex) {
      thrown = ex;
    }

    await Assert.That(thrown).IsNull()
      .Because($"a reaped body is a row the read names, not a failure of the whole batch's read; it threw {thrown?.GetType().Name}: {thrown?.Message}");
    Guid[] expected = [healthy.WorkId, reaped.WorkId, missing.WorkId];
    await Assert.That(rows!.Select(r => r.EventWorkId)).IsEquivalentTo(expected)
      .Because("the reaped event's row is returned so the worker can fail it, and the healthy stream's row with it");
    await Assert.That(rows!.Single(r => r.EventWorkId == healthy.WorkId).EventData).IsEqualTo("{\"Ordinal\": 1}");
    await Assert.That(rows!.Single(r => r.EventWorkId == healthy.WorkId).UnreadableReason).IsNull();
    var reapedRow = rows!.Single(r => r.EventWorkId == reaped.WorkId);
    await Assert.That(reapedRow.UnreadableReason).IsEqualTo(StreamEventData.BODY_REAPED)
      .Because("an ephemeral event without a body was reaped past its retention, and the row says so");
    await Assert.That(rows!.Single(r => r.EventWorkId == missing.WorkId).UnreadableReason).IsEqualTo(StreamEventData.BODY_MISSING)
      .Because("a Sourced event's body is never reaped, so its absence is named apart");
    await Assert.That(reapedRow.EventData).IsEqualTo(string.Empty);
    await Assert.That(reapedRow.StreamId).IsEqualTo(reapedStream);
    await Assert.That(reapedRow.EventId).IsEqualTo(reaped.EventId);
  }

  // ── helpers ─────────────────────────────────────────────────────────────────────────────────

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext context) {
    var conn = (NpgsqlConnection)context.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  /// <summary>
  /// One stored event, stamped, with its work row leased to the instance. An ephemeral event written in July whose body
  /// is gone when <paramref name="body"/> is false, as the reap leaves it: the pointer row stays, the body row does not.
  /// </summary>
  private static async Task<(Guid EventId, Guid WorkId)> _seedLeasedEventAsync(
      NpgsqlConnection conn, Guid instanceId, Guid streamId, int version, bool body, int flags) {
    var eventId = (Guid)TrackedGuid.New();
    var workId = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at,
                                  commit_sequence, flags)
      VALUES (@eid, @sid, @sid, 'TestAggregate', @ver, 'TestEvent', NULL, @written, nextval('wh_commit_seq'), @flags);
      {(body ? "INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@eid, '{\"Ordinal\": 1}'::jsonb, '{}'::jsonb);" : string.Empty)}
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, created_at)
      VALUES (@work, @sid, @perspective, @eid, @inst, NOW() + INTERVAL '5 minutes', 0, 1, 0, @written);
      """;
    cmd.Parameters.AddWithValue("eid", eventId);
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("ver", version);
    cmd.Parameters.AddWithValue("written", _writtenInJuly);
    cmd.Parameters.AddWithValue(nameof(flags), flags);
    cmd.Parameters.AddWithValue("work", workId);
    cmd.Parameters.AddWithValue("perspective", PERSPECTIVE);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
    return (eventId, workId);
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'reaped-body-svc', 'reaped-body-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = EXCLUDED.last_heartbeat_at
      """;
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }
}
