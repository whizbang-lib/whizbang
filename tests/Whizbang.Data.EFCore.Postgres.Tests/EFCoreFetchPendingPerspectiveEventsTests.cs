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
/// Tests for the EFCore-side wrapper around <c>fetch_pending_perspective_events</c>
/// (Phase H step 7 slice 2). SQL-level invariants are pinned in
/// <see cref="FetchPendingPerspectiveEventsSqlTests"/>; this suite verifies the
/// C# parameter wiring and row mapping.
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public class EFCoreFetchPendingPerspectiveEventsTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> Coord(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task FetchPendingPerspectiveEventsAsync_NoRows_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);

    var result = await coord.FetchPendingPerspectiveEventsAsync(
      streamId: (Guid)TrackedGuid.NewMedo(),
      perspectiveName: "Empty",
      instanceId: (Guid)TrackedGuid.NewMedo());

    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchPendingPerspectiveEventsAsync_RoundTripsAllColumnsAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    var workId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
        ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
      ins.Parameters.AddWithValue("id", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    // Backing wh_event_store row required by the stamper-lag gate on the SQL fn —
    // INNER JOIN excludes rows without one. Stamper-assigned cs here makes the row
    // visible to the drainer.
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, version, event_type,
           created_at, commit_sequence)
        VALUES (@id, @stream, @stream, 'TestAgg', 1, 'TestEvt',
                NOW(), nextval('wh_commit_seq'))";
      ins.Parameters.AddWithValue("id", eventId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
           partition_number, status, attempts, created_at, claimed_at, processed_at)
        VALUES (@work, @stream, @persp, @event, @inst, NOW() + INTERVAL '5 minutes',
                0, 0, 0, NOW(), NOW(), NULL)";
      ins.Parameters.AddWithValue("work", workId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("persp", perspectiveName);
      ins.Parameters.AddWithValue("event", eventId);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.FetchPendingPerspectiveEventsAsync(streamId, perspectiveName, instanceId);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].EventWorkId).IsEqualTo(workId);
    await Assert.That(rows[0].EventId).IsEqualTo(eventId);
    await Assert.That(rows[0].CommitSequence)
      .IsNotNull()
      .Because("backing event_store row is stamped, so the C# reader's non-null branch must surface the value");
  }

  /// <summary>
  /// Production forensic G6: when wh_event_store has a stamped commit_sequence, the C# reader's
  /// non-null branch must surface it through <see cref="PendingPerspectiveEvent.CommitSequence"/>.
  /// Companion to the no-event-store row test above.
  /// </summary>
  [Test]
  public async Task FetchPendingPerspectiveEventsAsync_StampedRow_SurfacesCommitSequenceAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    var workId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    const long stampedCommitSequence = 234500L;

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
        ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
      ins.Parameters.AddWithValue("id", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, version, event_type,
           created_at, commit_sequence)
        VALUES (@id, @stream, @stream, 'TestAgg', 1, 'TestEvt',
                NOW(), @cs)";
      ins.Parameters.AddWithValue("id", eventId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("cs", stampedCommitSequence);
      await ins.ExecuteNonQueryAsync();
    }

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
           partition_number, status, attempts, created_at, claimed_at, processed_at)
        VALUES (@work, @stream, @persp, @event, @inst, NOW() + INTERVAL '5 minutes',
                0, 0, 0, NOW(), NOW(), NULL)";
      ins.Parameters.AddWithValue("work", workId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("persp", perspectiveName);
      ins.Parameters.AddWithValue("event", eventId);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.FetchPendingPerspectiveEventsAsync(streamId, perspectiveName, instanceId);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].CommitSequence)
      .IsEqualTo(stampedCommitSequence)
      .Because("LEFT JOIN matches → reader's IsDBNullAsync false-branch surfaces stamped commit_sequence");
  }

  /// <summary>
  /// Production forensic G6: same coverage for the atomic claim-and-fetch variant. Locks the non-null
  /// reader branch on <see cref="EFCoreWorkCoordinator{T}.ClaimAndFetchPendingPerspectiveEventsAsync"/>.
  /// </summary>
  [Test]
  public async Task ClaimAndFetchPendingPerspectiveEventsAsync_StampedRow_SurfacesCommitSequenceAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    var workId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    const long stampedCommitSequence = 571800L;

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
        ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
      ins.Parameters.AddWithValue("id", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, version, event_type,
           created_at, commit_sequence)
        VALUES (@id, @stream, @stream, 'TestAgg', 1, 'TestEvt',
                NOW(), @cs)";
      ins.Parameters.AddWithValue("id", eventId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("cs", stampedCommitSequence);
      await ins.ExecuteNonQueryAsync();
    }

    // Unowned so the atomic variant claims it.
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
           partition_number, status, attempts, created_at)
        VALUES (@work, @stream, @persp, @event, NULL, NULL, 0, 0, 0, NOW())";
      ins.Parameters.AddWithValue("work", workId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("persp", perspectiveName);
      ins.Parameters.AddWithValue("event", eventId);
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.ClaimAndFetchPendingPerspectiveEventsAsync(
      streamId, perspectiveName, instanceId, leaseDuration: TimeSpan.FromMinutes(5));

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].CommitSequence).IsEqualTo(stampedCommitSequence);
  }

  [Test]
  public async Task FetchEventsByIdsAsync_EmptyInput_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);

    var result = await coord.FetchEventsByIdsAsync([]);

    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchEventsByIdsAsync_RoundTripsAllColumnsAsync() {
    await using var dbContext = CreateDbContext();
    var coord = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
        VALUES (@evt, @stream, @stream, 'agg', 'My.Type', '{""tenant"":""t1""}'::jsonb, 1, NOW());
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@evt, '{""payload"":42}'::jsonb, '{""hop"":1}'::jsonb)";
      ins.Parameters.AddWithValue("evt", eventId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    var rows = await coord.FetchEventsByIdsAsync([eventId]);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].StreamId).IsEqualTo(streamId);
    await Assert.That(rows[0].EventId).IsEqualTo(eventId);
    await Assert.That(rows[0].EventType).IsEqualTo("My.Type");
    await Assert.That(rows[0].EventData).Contains("\"payload\"");
    await Assert.That(rows[0].EventWorkId).IsEqualTo(Guid.Empty)
      .Because("body fetch does not carry work-id; drainer pairs it from the prefetch tuples");
  }
}
