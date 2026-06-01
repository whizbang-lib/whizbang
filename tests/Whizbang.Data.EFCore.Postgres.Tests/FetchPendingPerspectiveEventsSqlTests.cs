using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>fetch_pending_perspective_events</c> — Phase H step 7 slice 1.
/// The drainer's cheap ID-only prefetch query: returns (event_work_id, event_id) tuples
/// for unprocessed rows leased to the caller, ordered by event_id ASC. Replaces the
/// in-memory cursor-inversion check with a SQL-side filter that also feeds the cooldown
/// cache and the conditional body fetch.
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public class FetchPendingPerspectiveEventsSqlTests : EFCoreTestBase {

  [Test]
  public async Task FetchPendingPerspectiveEvents_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='fetch_pending_perspective_events' AND pronamespace='public'::regnamespace);";
    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task FetchPendingPerspectiveEvents_ReturnsOrderedByEventIdAscAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(connection, instanceId);

    // UUIDv7 is time-ordered, so creating in temporal order produces lex-ordered ids.
    var event1 = (Guid)TrackedGuid.NewMedo();
    await Task.Delay(2);
    var event2 = (Guid)TrackedGuid.NewMedo();
    await Task.Delay(2);
    var event3 = (Guid)TrackedGuid.NewMedo();

    var workId1 = (Guid)TrackedGuid.NewMedo();
    var workId2 = (Guid)TrackedGuid.NewMedo();
    var workId3 = (Guid)TrackedGuid.NewMedo();

    // Insert in reverse order to prove sorting by event_id, not insert order.
    await _insertPerspectiveEventAsync(connection, workId3, streamId, perspectiveName, event3, instanceId);
    await _insertPerspectiveEventAsync(connection, workId1, streamId, perspectiveName, event1, instanceId);
    await _insertPerspectiveEventAsync(connection, workId2, streamId, perspectiveName, event2, instanceId);

    var fetched = await _fetchAsync(connection, streamId, perspectiveName, instanceId);

    await Assert.That(fetched.Count).IsEqualTo(3);
    await Assert.That(fetched[0].EventId).IsEqualTo(event1);
    await Assert.That(fetched[1].EventId).IsEqualTo(event2);
    await Assert.That(fetched[2].EventId).IsEqualTo(event3);
    await Assert.That(fetched[0].EventWorkId).IsEqualTo(workId1);
    await Assert.That(fetched[1].EventWorkId).IsEqualTo(workId2);
    await Assert.That(fetched[2].EventWorkId).IsEqualTo(workId3);
  }

  [Test]
  public async Task FetchPendingPerspectiveEvents_FiltersOtherInstancesRowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var meId = (Guid)TrackedGuid.NewMedo();
    var otherId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(connection, meId);
    await _registerInstanceAsync(connection, otherId);

    var mineEvent = (Guid)TrackedGuid.NewMedo();
    var theirEvent = (Guid)TrackedGuid.NewMedo();
    var mineWork = (Guid)TrackedGuid.NewMedo();
    var theirWork = (Guid)TrackedGuid.NewMedo();
    await _insertPerspectiveEventAsync(connection, mineWork, streamId, perspectiveName, mineEvent, meId);
    await _insertPerspectiveEventAsync(connection, theirWork, streamId, perspectiveName, theirEvent, otherId);

    var fetched = await _fetchAsync(connection, streamId, perspectiveName, meId);

    await Assert.That(fetched.Count).IsEqualTo(1);
    await Assert.That(fetched[0].EventId).IsEqualTo(mineEvent);
  }

  [Test]
  public async Task FetchPendingPerspectiveEvents_FiltersProcessedRowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(connection, instanceId);

    var pendingEvent = (Guid)TrackedGuid.NewMedo();
    var doneEvent = (Guid)TrackedGuid.NewMedo();
    var pendingWork = (Guid)TrackedGuid.NewMedo();
    var doneWork = (Guid)TrackedGuid.NewMedo();
    await _insertPerspectiveEventAsync(connection, pendingWork, streamId, perspectiveName, pendingEvent, instanceId);
    await _insertPerspectiveEventAsync(connection, doneWork, streamId, perspectiveName, doneEvent, instanceId, processedAt: DateTimeOffset.UtcNow);

    var fetched = await _fetchAsync(connection, streamId, perspectiveName, instanceId);

    await Assert.That(fetched.Count).IsEqualTo(1);
    await Assert.That(fetched[0].EventId).IsEqualTo(pendingEvent);
  }

  [Test]
  public async Task FetchPendingPerspectiveEvents_FiltersByPerspectiveNameAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(connection, instanceId);

    var event1 = (Guid)TrackedGuid.NewMedo();
    var event2 = (Guid)TrackedGuid.NewMedo();
    await _insertPerspectiveEventAsync(connection, (Guid)TrackedGuid.NewMedo(), streamId, "Projection.A", event1, instanceId);
    await _insertPerspectiveEventAsync(connection, (Guid)TrackedGuid.NewMedo(), streamId, "Projection.B", event2, instanceId);

    var fetchedA = await _fetchAsync(connection, streamId, "Projection.A", instanceId);
    var fetchedB = await _fetchAsync(connection, streamId, "Projection.B", instanceId);

    await Assert.That(fetchedA.Count).IsEqualTo(1);
    await Assert.That(fetchedA[0].EventId).IsEqualTo(event1);
    await Assert.That(fetchedB.Count).IsEqualTo(1);
    await Assert.That(fetchedB[0].EventId).IsEqualTo(event2);
  }

  [Test]
  public async Task FetchPendingPerspectiveEvents_FiltersByStreamIdAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(connection, instanceId);

    var eventA = (Guid)TrackedGuid.NewMedo();
    var eventB = (Guid)TrackedGuid.NewMedo();
    await _insertPerspectiveEventAsync(connection, (Guid)TrackedGuid.NewMedo(), streamA, perspectiveName, eventA, instanceId);
    await _insertPerspectiveEventAsync(connection, (Guid)TrackedGuid.NewMedo(), streamB, perspectiveName, eventB, instanceId);

    var fetched = await _fetchAsync(connection, streamA, perspectiveName, instanceId);

    await Assert.That(fetched.Count).IsEqualTo(1);
    await Assert.That(fetched[0].EventId).IsEqualTo(eventA);
  }

  [Test]
  public async Task ClaimAndFetch_AlreadyLeasedToMe_DoesNotReExtendLeaseAsync() {
    // Slice 28: when rows are already leased to the caller with a valid lease, the
    // function MUST NOT re-UPDATE them. pg_stat_user_tables on JDX after run 19 showed
    // 5.7M UPDATEs vs 800k inserts (7x bloat) directly caused by the prior behavior of
    // bumping every fetch. LeaseRenewalWorker handles in-flight renewals independently,
    // so the per-fetch re-extend is redundant.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(conn, instanceId);

    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    await _insertPerspectiveEventAsync(conn, workId, streamId, perspectiveName, eventId, instanceId);

    var beforeLease = await _readLeaseExpiryAsync(conn, workId);
    var beforeAttempts = await _readAttemptsAsync(conn, workId);

    // Wait briefly so any spurious re-UPDATE would produce a visibly different lease_expiry.
    await Task.Delay(50);

    // Call claim_and_fetch with a "different" lease expiry — if the function re-updates,
    // we'll see this new value. If it correctly skips (slice 28), the original stays.
    var passedLease = beforeLease.AddMinutes(10);
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT * FROM claim_and_fetch_pending_perspective_events(@p_stream_id, @p_perspective_name, @p_instance_id, @p_lease_expiry, NOW())";
      cmd.Parameters.AddWithValue("p_stream_id", streamId);
      cmd.Parameters.AddWithValue("p_perspective_name", perspectiveName);
      cmd.Parameters.AddWithValue("p_instance_id", instanceId);
      cmd.Parameters.AddWithValue("p_lease_expiry", passedLease);
      await cmd.ExecuteNonQueryAsync();
    }

    var afterLease = await _readLeaseExpiryAsync(conn, workId);
    var afterAttempts = await _readAttemptsAsync(conn, workId);

    await Assert.That(afterLease).IsEqualTo(beforeLease)
      .Because("row already leased to caller — UPDATE must be skipped, lease_expiry must not change");
    await Assert.That(afterAttempts).IsEqualTo(beforeAttempts)
      .Because("re-fetching own row does not bump attempts");
  }

  [Test]
  public async Task ClaimAndFetch_LeaseExpired_DoesReExtendLeaseAsync() {
    // Counterpart to the previous test: rows whose lease has EXPIRED must still be
    // re-claimed (lease_expiry bumped, attempts incremented). This is the original
    // semantics for orphan recovery on the same-instance retry path.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(conn, instanceId);

    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    // Insert with EXPIRED lease (already past).
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
           partition_number, status, attempts, created_at, claimed_at, processed_at)
        VALUES (@work, @stream, @persp, @event, @inst, NOW() - INTERVAL '1 minute',
                0, 0, 3, NOW(), NOW(), NULL)";
      ins.Parameters.AddWithValue("work", workId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("persp", perspectiveName);
      ins.Parameters.AddWithValue("event", eventId);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    var beforeAttempts = await _readAttemptsAsync(conn, workId);
    // Truncate to microsecond precision: PG TIMESTAMPTZ stores 6 fractional-second digits
    // while .NET DateTimeOffset stores 7 (100-ns ticks). Without this, the round-trip drops
    // sub-microsecond ticks and a direct IsEqualTo fails by <1µs in CI builds even though
    // the SQL function set the value verbatim.
    var freshLease = new DateTimeOffset(
      (DateTimeOffset.UtcNow.AddMinutes(5).UtcTicks / TimeSpan.TicksPerMicrosecond) * TimeSpan.TicksPerMicrosecond,
      TimeSpan.Zero);

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT * FROM claim_and_fetch_pending_perspective_events(@p_stream_id, @p_perspective_name, @p_instance_id, @p_lease_expiry, NOW())";
      cmd.Parameters.AddWithValue("p_stream_id", streamId);
      cmd.Parameters.AddWithValue("p_perspective_name", perspectiveName);
      cmd.Parameters.AddWithValue("p_instance_id", instanceId);
      cmd.Parameters.AddWithValue("p_lease_expiry", freshLease);
      await cmd.ExecuteNonQueryAsync();
    }

    var afterLease = await _readLeaseExpiryAsync(conn, workId);
    var afterAttempts = await _readAttemptsAsync(conn, workId);

    await Assert.That(afterLease).IsEqualTo(freshLease)
      .Because("expired lease must be re-extended even though instance_id is unchanged");
    await Assert.That(afterAttempts).IsEqualTo(beforeAttempts + 1)
      .Because("expired-then-re-claimed bumps attempts");
  }

  /// <summary>
  /// Slot-3 regression (G6): the prefetch tuple must carry <c>wh_event_store.commit_sequence</c>
  /// so the drainer's inversion detector can compare against the cached cursor's commit_sequence
  /// directly, with no separate round-trip. Without this column, the detector either falls back
  /// to event_id (the same UUIDv7 inversion that slot-3 hit) or pays N extra GetCommitSequence
  /// queries per drain cycle.
  /// </summary>
  [Test]
  public async Task FetchPendingPerspectiveEvents_ReturnsCommitSequenceFromEventStoreAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(connection, instanceId);

    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    const long stampedCommitSequence = 234500L;
    await _insertEventStoreRowAsync(connection, eventId, streamId, stampedCommitSequence);
    await _insertPerspectiveEventAsync(connection, workId, streamId, perspectiveName, eventId, instanceId);

    var fetched = await _fetchAsync(connection, streamId, perspectiveName, instanceId);

    await Assert.That(fetched.Count).IsEqualTo(1);
    await Assert.That(fetched[0].EventId).IsEqualTo(eventId);
    await Assert.That(fetched[0].CommitSequence)
      .IsEqualTo(stampedCommitSequence)
      .Because("the prefetch must JOIN wh_event_store and project commit_sequence so the drainer's inversion detector has it without an extra round-trip");
  }

  /// <summary>
  /// Null-branch coverage for G6: when the stamper hasn't caught up to the row (or there's
  /// no matching wh_event_store entry for the perspective_event yet), the LEFT JOIN yields
  /// NULL and the C# reader must surface that as <c>CommitSequence = null</c>. Callers fall
  /// back to event_id compare in that case.
  /// </summary>
  [Test]
  public async Task FetchPendingPerspectiveEvents_NoEventStoreRow_ReturnsNullCommitSequenceAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(connection, instanceId);

    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    // No wh_event_store row inserted — LEFT JOIN yields NULL.
    await _insertPerspectiveEventAsync(connection, workId, streamId, "MyApp.Test+Projection", eventId, instanceId);

    var fetched = await _fetchAsync(connection, streamId, "MyApp.Test+Projection", instanceId);

    await Assert.That(fetched.Count).IsEqualTo(1);
    await Assert.That(fetched[0].CommitSequence)
      .IsNull()
      .Because("missing event_store row → LEFT JOIN NULL → reader's IsDBNullAsync branch must surface null");
  }

  /// <summary>Companion to the test above for the atomic claim variant.</summary>
  [Test]
  public async Task ClaimAndFetchPendingPerspectiveEvents_ReturnsCommitSequenceFromEventStoreAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(connection, instanceId);

    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    const long stampedCommitSequence = 571800L;
    await _insertEventStoreRowAsync(connection, eventId, streamId, stampedCommitSequence);
    // Unowned so the atomic variant claims it before returning.
    await _insertUnownedPerspectiveEventAsync(connection, workId, streamId, perspectiveName, eventId);

    var fetched = await _claimAndFetchAsync(connection, streamId, perspectiveName, instanceId);

    await Assert.That(fetched.Count).IsEqualTo(1);
    await Assert.That(fetched[0].CommitSequence).IsEqualTo(stampedCommitSequence);
  }

  [Test]
  public async Task FetchPendingPerspectiveEvents_EmptyResult_WhenNoPendingAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(connection, instanceId);

    var fetched = await _fetchAsync(connection, streamId, "Projection.Empty", instanceId);

    await Assert.That(fetched.Count).IsEqualTo(0);
  }

  // --- helpers ---

  private static async Task<List<PendingRow>> _fetchAsync(
      NpgsqlConnection connection, Guid streamId, string perspectiveName, Guid instanceId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT * FROM fetch_pending_perspective_events(@p_stream_id, @p_perspective_name, @p_instance_id)";
    cmd.Parameters.AddWithValue("p_stream_id", streamId);
    cmd.Parameters.AddWithValue("p_perspective_name", perspectiveName);
    cmd.Parameters.AddWithValue("p_instance_id", instanceId);

    return await _readRowsAsync(cmd);
  }

  private static async Task<List<PendingRow>> _claimAndFetchAsync(
      NpgsqlConnection connection, Guid streamId, string perspectiveName, Guid instanceId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_and_fetch_pending_perspective_events(@p_stream_id, @p_perspective_name, @p_instance_id, @p_lease_expiry, @p_now)";
    cmd.Parameters.AddWithValue("p_stream_id", streamId);
    cmd.Parameters.AddWithValue("p_perspective_name", perspectiveName);
    cmd.Parameters.AddWithValue("p_instance_id", instanceId);
    cmd.Parameters.AddWithValue("p_lease_expiry", DateTime.UtcNow.AddMinutes(5));
    cmd.Parameters.AddWithValue("p_now", DateTime.UtcNow);

    return await _readRowsAsync(cmd);
  }

  private static async Task<List<PendingRow>> _readRowsAsync(NpgsqlCommand cmd) {
    var rows = new List<PendingRow>();
    await using var reader = await cmd.ExecuteReaderAsync();
    var hasCommitSeqColumn = reader.FieldCount >= 3;
    while (await reader.ReadAsync()) {
      rows.Add(new PendingRow {
        EventWorkId = reader.GetGuid(0),
        EventId = reader.GetGuid(1),
        CommitSequence = hasCommitSeqColumn && !await reader.IsDBNullAsync(2)
          ? reader.GetInt64(2)
          : null
      });
    }
    return rows;
  }

  private sealed class PendingRow {
    public Guid EventWorkId { get; init; }
    public Guid EventId { get; init; }
    public long? CommitSequence { get; init; }
  }

  private static async Task _insertEventStoreRowAsync(
      NpgsqlConnection connection, Guid eventId, Guid streamId, long commitSequence) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type,
         event_data, metadata, created_at, commit_sequence)
      VALUES (@id, @stream, @stream, 'TestAgg', 1, 'TestEvt',
              '{}'::jsonb, '{}'::jsonb, NOW(), @cs)";
    ins.Parameters.AddWithValue("id", eventId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("cs", commitSequence);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertUnownedPerspectiveEventAsync(
      NpgsqlConnection connection,
      Guid eventWorkId,
      Guid streamId,
      string perspectiveName,
      Guid eventId) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at)
      VALUES (@work, @stream, @persp, @event, NULL, NULL, 0, 0, 0, NOW())";
    ins.Parameters.AddWithValue("work", eventWorkId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("persp", perspectiveName);
    ins.Parameters.AddWithValue("event", eventId);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertPerspectiveEventAsync(
      NpgsqlConnection connection,
      Guid eventWorkId,
      Guid streamId,
      string perspectiveName,
      Guid eventId,
      Guid instanceId,
      DateTimeOffset? processedAt = null) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at, claimed_at, processed_at)
      VALUES (@work, @stream, @persp, @event, @inst, NOW() + INTERVAL '5 minutes',
              0, 0, 0, NOW(), NOW(), @processed)";
    ins.Parameters.AddWithValue("work", eventWorkId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("persp", perspectiveName);
    ins.Parameters.AddWithValue("event", eventId);
    ins.Parameters.AddWithValue("inst", instanceId);
    ins.Parameters.Add(new NpgsqlParameter("processed", NpgsqlDbType.TimestampTz) { Value = (object?)processedAt ?? DBNull.Value });
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task<DateTimeOffset> _readLeaseExpiryAsync(NpgsqlConnection conn, Guid workId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT lease_expiry FROM wh_perspective_events WHERE event_work_id = @id";
    cmd.Parameters.AddWithValue("id", workId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return reader.GetFieldValue<DateTimeOffset>(0);
  }

  private static async Task<int> _readAttemptsAsync(NpgsqlConnection conn, Guid workId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT attempts FROM wh_perspective_events WHERE event_work_id = @id";
    cmd.Parameters.AddWithValue("id", workId);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }
}
