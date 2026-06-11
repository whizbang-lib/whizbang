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
/// Phase B integration tests for <see cref="IWorkCoordinator.ClaimWorkAsync"/>.
/// Polling-side method; non-empty mapping lands in Phase C worker integration.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
public class EFCoreClaimWorkTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> Coord(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task ClaimWorkAsync_EmptyQueues_ReturnsEmptyBatchAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
      InstanceId: TrackedGuid.NewMedo(),
      ServiceName: "test-svc",
      HostName: "test-host",
      ProcessId: 1));

    await Assert.That(batch.OutboxWork.Count).IsEqualTo(0);
    await Assert.That(batch.InboxWork.Count).IsEqualTo(0);
    await Assert.That(batch.PerspectiveWork.Count).IsEqualTo(0);
    await Assert.That(batch.PerspectiveStreamIds.Count).IsEqualTo(0);
  }

  /// <summary>
  /// v0.683 regression — the per-inner-function guards in claim_work must
  /// preserve behavior when an inner function would be a no-op anyway. This
  /// test exercises the `_emit_event_store_chain_for_inbox` guard's worst
  /// case: a batch of inbox event rows owned by this instance whose
  /// event_ids are ALREADY in wh_event_store (handler-side delay scenario).
  /// The guard's NOT EXISTS predicate must catch this and skip the function
  /// call entirely; claim_work must still complete cleanly and return the
  /// claimed work.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/claim-loop</docs>
  [Test]
  public async Task ClaimWorkAsync_InboxEventsAlreadyEmitted_SucceedsWithoutDuplicateEmitAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = TrackedGuid.NewMedo();
    var streamId = TrackedGuid.NewMedo();
    var eventId = TrackedGuid.NewMedo();

    // Heartbeat (claim_work uses calculate_instance_rank which reads this).
    await using (var hb = conn.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", (Guid)instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    // Pre-insert into wh_event_store so the inbox row's NOT EXISTS check in
    // _emit_event_store_chain_for_inbox returns false. This simulates the
    // handler-side delay scenario observed during the 2026-06-11 production
    // import: inbox backlog where every event_id is already emitted.
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type,
           event_data, metadata, scope, version, created_at)
        VALUES (@eid, @stream, @stream, 'Test', 'Test', '{}'::jsonb, '{}'::jsonb, NULL, 1, NOW())";
      ins.Parameters.AddWithValue("eid", (Guid)eventId);
      ins.Parameters.AddWithValue("stream", (Guid)streamId);
      await ins.ExecuteNonQueryAsync();
    }

    // Insert the matching inbox event row — owned by this instance, eligible
    // by all the inbox predicates, but its event_id is already in wh_event_store.
    await using (var inbox = conn.CreateCommand()) {
      inbox.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, scope,
           stream_id, instance_id, lease_expiry, processed_at, is_event,
           status, attempts, received_at, partition_number)
        VALUES (@mid, 'TestHandler', 'Test', '{}'::jsonb, '{}'::jsonb, NULL,
                @stream, @inst, NOW() + INTERVAL '5 minutes', NULL, true,
                0, 0, NOW(), 1)";
      inbox.Parameters.AddWithValue("mid", (Guid)eventId);
      inbox.Parameters.AddWithValue("stream", (Guid)streamId);
      inbox.Parameters.AddWithValue("inst", (Guid)instanceId);
      await inbox.ExecuteNonQueryAsync();
    }

    var beforeCount = await _scalarAsync<long>(conn, "SELECT COUNT(*) FROM wh_event_store");

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
      InstanceId: instanceId,
      ServiceName: "test",
      HostName: "test-host",
      ProcessId: 1));

    var afterCount = await _scalarAsync<long>(conn, "SELECT COUNT(*) FROM wh_event_store");

    // Function MUST succeed and NOT re-emit the event (PK conflict is caught by
    // ON CONFLICT DO NOTHING in _emit_event_store_chain_for_inbox either way, but
    // the v0.683 guard should prevent the call entirely).
    await Assert.That(afterCount).IsEqualTo(beforeCount)
      .Because("emit_chain must not re-emit events that are already in wh_event_store; the v0.683 NOT EXISTS guard short-circuits the call.");

    // And the inbox row is now claimed (this is the eligible_inbox RETURN QUERY path).
    await Assert.That(batch.InboxStreamIds).Contains((Guid)streamId)
      .Because("the inbox row is owned + eligible — claim_work's inbox RETURN QUERY must still surface its stream_id even when emit_chain was guarded.");
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var result = await cmd.ExecuteScalarAsync();
    return (T)result!;
  }

  [Test]
  public async Task ClaimWorkAsync_PerspectiveStreamPresent_ReturnsStreamIdAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = TrackedGuid.NewMedo();
    var streamId = TrackedGuid.NewMedo();

    // Heartbeat.
    await using (var hb = conn.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", (Guid)instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    // Insert a perspective_event row that claim_work will discover.
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@stream, 'TestPerspective', @eid, 0, 0, NOW())";
      ins.Parameters.AddWithValue("stream", (Guid)streamId);
      ins.Parameters.AddWithValue("eid", (Guid)TrackedGuid.NewMedo());
      await ins.ExecuteNonQueryAsync();
    }

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
      InstanceId: instanceId,
      ServiceName: "test",
      HostName: "test-host",
      ProcessId: 1));

    await Assert.That(batch.PerspectiveStreamIds).Contains((Guid)streamId);
  }
}
