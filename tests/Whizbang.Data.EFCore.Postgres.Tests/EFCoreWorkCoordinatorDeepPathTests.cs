using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// DB round-trip tests for deep coordinator paths not exercised by the slice-1 janitor
/// suite: <c>DeregisterInstanceAsync</c> (mig 036), <c>RecordLifecycleCompletionAsync</c>
/// (mig 035), <c>RecomputePartitionNumbersAsync</c> (mig 041), cursor-bearing
/// <c>CompletePerspectiveAsync</c> (serializes non-empty PerspectiveCursorCompletion[]),
/// <c>FlushCompletionsAsync</c> with per-category failures, multi-inquiry
/// <c>ResolveSyncInquiriesAsync</c> with EventIds/EventTypeFilter, and the guard-clause
/// early returns on the batched flusher surface.
/// Each test seeds real rows via raw SQL, calls the coordinator method, and asserts both
/// the returned data AND the resulting table state.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public class EFCoreWorkCoordinatorDeepPathTests : EFCoreTestBase {
  private static readonly string[] _typeAAndUnknownFilter = ["Type.A", "Type.Unknown"];

  // --------------------------------------------------------------------------
  // DeregisterInstanceAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task DeregisterInstanceAsync_RegisteredInstanceWithLease_ReleasesLeaseAndRemovesInstanceRowAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var coordinator = _createCoordinator(dbContext);

    var instanceId = Guid.NewGuid();
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "test-svc", "test-host", 1234));

    var registered = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_service_instances WHERE instance_id = @id", ("id", instanceId));
    await Assert.That(registered).IsEqualTo(1L);

    // Unprocessed outbox row leased by the instance — graceful shutdown must release it.
    var leasedMsgId = Guid.NewGuid();
    await _insertOutboxRowAsync(connection, leasedMsgId, Guid.NewGuid(), instanceId: instanceId);

    await coordinator.DeregisterInstanceAsync(instanceId);

    var remaining = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_service_instances WHERE instance_id = @id", ("id", instanceId));
    await Assert.That(remaining).IsEqualTo(0L);

    var released = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_outbox WHERE message_id = @msg AND instance_id IS NULL AND lease_expiry IS NULL",
      ("msg", leasedMsgId));
    await Assert.That(released).IsEqualTo(1L);

    // Shutdown is audited to wh_log with the instance id as message_id.
    var audited = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_log WHERE source = 'shutdown' AND message_id = @id", ("id", instanceId));
    await Assert.That(audited).IsEqualTo(1L);
  }

  // --------------------------------------------------------------------------
  // RecordLifecycleCompletionAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task RecordLifecycleCompletionAsync_CalledTwiceForSameEvent_InsertsSingleIdempotentMarkerAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var coordinator = _createCoordinator(dbContext);

    var eventId = (Guid)TrackedGuid.NewMedo();

    await coordinator.RecordLifecycleCompletionAsync(eventId);
    // ON CONFLICT DO NOTHING — the second call must be a silent no-op.
    await coordinator.RecordLifecycleCompletionAsync(eventId);

    var markers = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_lifecycle_completions WHERE event_id = @id", ("id", eventId));
    await Assert.That(markers).IsEqualTo(1L);
  }

  // --------------------------------------------------------------------------
  // RecomputePartitionNumbersAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task RecomputePartitionNumbersAsync_MismatchedRows_RecomputesAllThreeTablesAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var coordinator = _createCoordinator(dbContext);

    const int partitionCount = 8;
    // partition_number 999 can never equal compute_partition(stream, 8) — every row is stale.
    await _insertOutboxRowAsync(connection, Guid.NewGuid(), Guid.NewGuid(), partitionNumber: 999);
    await _insertInboxRowAsync(connection, Guid.NewGuid(), "TestEvent", Guid.NewGuid(), partitionNumber: 999);
    await _insertActiveStreamAsync(connection, Guid.NewGuid(), partitionNumber: 999);

    var result = await coordinator.RecomputePartitionNumbersAsync(partitionCount);

    await Assert.That(result.OutboxRowsRecomputed).IsEqualTo(1L);
    await Assert.That(result.InboxRowsRecomputed).IsEqualTo(1L);
    await Assert.That(result.ActiveStreamsRowsRecomputed).IsEqualTo(1L);

    // All partition numbers must now fall inside the canonical [0, partitionCount) range.
    var staleRows = await _countAsync(connection, @"
      SELECT
        (SELECT COUNT(*) FROM wh_outbox WHERE partition_number NOT BETWEEN 0 AND 7)
      + (SELECT COUNT(*) FROM wh_inbox WHERE partition_number NOT BETWEEN 0 AND 7)
      + (SELECT COUNT(*) FROM wh_active_streams WHERE partition_number NOT BETWEEN 0 AND 7)");
    await Assert.That(staleRows).IsEqualTo(0L);
  }

  // --------------------------------------------------------------------------
  // CompletePerspectiveAsync — cursor-bearing path
  // --------------------------------------------------------------------------

  [Test]
  public async Task CompletePerspectiveAsync_WithCursorCompletions_AdvancesCursorFromProcessedEventsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var coordinator = _createCoordinator(dbContext);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var processedEventId = (Guid)TrackedGuid.NewMedo();
    var pendingEventId = (Guid)TrackedGuid.NewMedo();
    var pendingWorkId = Guid.NewGuid();
    const string perspectiveName = "P.CursorAdvance";

    // The cursor's last_event_id has a FK to wh_event_store, so both events must exist there.
    await _insertEventStoreRowAsync(connection, processedEventId, streamId, "P.Type", version: 1);
    await _insertEventStoreRowAsync(connection, pendingEventId, streamId, "P.Type", version: 2);

    // One already-processed event row and one pending row whose work id we complete.
    await _insertPerspectiveEventAsync(
      connection, Guid.NewGuid(), streamId, perspectiveName, processedEventId, processedAt: DateTimeOffset.UtcNow);
    await _insertPerspectiveEventAsync(
      connection, pendingWorkId, streamId, perspectiveName, pendingEventId, processedAt: null);

    await coordinator.CompletePerspectiveAsync(
      cursors: [
        new PerspectiveCursorCompletion {
          StreamId = streamId,
          PerspectiveName = perspectiveName,
          LastEventId = processedEventId,
          Status = PerspectiveProcessingStatus.Completed
        }
      ],
      eventWorkIds: [pendingWorkId],
      debugMode: false);

    // The pending work row is deleted first, so the cursor is created from the sole
    // remaining processed event with is_complete = true (status 2).
    var pendingRemains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_perspective_events WHERE event_work_id = @work", ("work", pendingWorkId));
    await Assert.That(pendingRemains).IsEqualTo(0L);

    var cursorAdvanced = await _countAsync(connection, @"
      SELECT COUNT(*) FROM wh_perspective_cursors
      WHERE stream_id = @stream AND perspective_name = @name
        AND last_event_id = @last AND status = 2",
      ("stream", streamId), ("name", perspectiveName), ("last", processedEventId));
    await Assert.That(cursorAdvanced).IsEqualTo(1L);
  }

  [Test]
  public async Task CompletePerspectiveAsync_EmptyCursorsAndWorkIds_IsNoOpAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var coordinator = _createCoordinator(dbContext);

    var workId = Guid.NewGuid();
    await _insertPerspectiveEventAsync(
      connection, workId, Guid.NewGuid(), "P.Untouched", Guid.NewGuid(), processedAt: null);

    await coordinator.CompletePerspectiveAsync(cursors: [], eventWorkIds: [], debugMode: false);

    // Early return before any SQL — the seeded work row must be untouched.
    var remains = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_perspective_events WHERE event_work_id = @work", ("work", workId));
    await Assert.That(remains).IsEqualTo(1L);
  }

  // --------------------------------------------------------------------------
  // FlushCompletionsAsync — failures-by-category path
  // --------------------------------------------------------------------------

  [Test]
  public async Task FlushCompletionsAsync_FailuresInTwoCategories_RecordsErrorsOnBothTablesAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var coordinator = _createCoordinator(dbContext);

    var outboxMsgId = Guid.NewGuid();
    var inboxMsgId = Guid.NewGuid();
    await _insertOutboxRowAsync(connection, outboxMsgId, Guid.NewGuid());
    await _insertInboxRowAsync(connection, inboxMsgId, "TestEvent", Guid.NewGuid());

    // Two categories in one composite flush — exercises the per-category JSON
    // builder loop including the separator between category objects.
    await coordinator.FlushCompletionsAsync(new FlushCompletionsRequest(
      FailuresByCategory: [
        new CategoryFailures(WorkCategory.Outbox, [
          new MessageFailure {
            MessageId = outboxMsgId,
            CompletedStatus = MessageProcessingStatus.Stored,
            Error = "outbox transport exploded",
            Reason = MessageFailureReason.Unknown
          }
        ]),
        new CategoryFailures(WorkCategory.Inbox, [
          new MessageFailure {
            MessageId = inboxMsgId,
            CompletedStatus = MessageProcessingStatus.Stored,
            Error = "inbox handler exploded",
            Reason = MessageFailureReason.Unknown
          }
        ])
      ]));

    var outboxFailed = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_outbox WHERE message_id = @msg AND error = 'outbox transport exploded'",
      ("msg", outboxMsgId));
    var inboxFailed = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_inbox WHERE message_id = @msg AND error = 'inbox handler exploded'",
      ("msg", inboxMsgId));
    await Assert.That(outboxFailed).IsEqualTo(1L);
    await Assert.That(inboxFailed).IsEqualTo(1L);
  }

  // --------------------------------------------------------------------------
  // ResolveSyncInquiriesAsync — EventIds / EventTypeFilter / multi-inquiry
  // --------------------------------------------------------------------------

  [Test]
  public async Task ResolveSyncInquiriesAsync_MultipleInquiriesWithFilters_AppliesFiltersPerInquiryAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var coordinator = _createCoordinator(dbContext);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var processedEventId = (Guid)TrackedGuid.NewMedo();
    var pendingEventId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "P.Sync";

    await _insertEventStoreRowAsync(connection, processedEventId, streamId, "Type.A", version: 1);
    await _insertEventStoreRowAsync(connection, pendingEventId, streamId, "Type.B", version: 2);
    await _insertPerspectiveEventAsync(
      connection, Guid.NewGuid(), streamId, perspectiveName, processedEventId, processedAt: DateTimeOffset.UtcNow);

    var filtered = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      // Both events pass the EventIds filter, but only Type.A survives the type filter.
      EventIds = [processedEventId, pendingEventId],
      EventTypeFilter = _typeAAndUnknownFilter
    };
    var unfiltered = new SyncInquiry {
      StreamId = streamId,
      PerspectiveName = perspectiveName
    };

    var results = await coordinator.ResolveSyncInquiriesAsync([filtered, unfiltered]);

    await Assert.That(results).Count().IsEqualTo(2);

    var filteredResult = results.Single(r => r.InquiryId == filtered.InquiryId);
    await Assert.That(filteredResult.StreamId).IsEqualTo(streamId);
    await Assert.That(filteredResult.ProcessedCount).IsEqualTo(1);
    await Assert.That(filteredResult.PendingCount).IsEqualTo(0);

    var unfilteredResult = results.Single(r => r.InquiryId == unfiltered.InquiryId);
    await Assert.That(unfilteredResult.ProcessedCount).IsEqualTo(1);
    await Assert.That(unfilteredResult.PendingCount).IsEqualTo(1);
  }

  // --------------------------------------------------------------------------
  // Guard-clause early returns
  // --------------------------------------------------------------------------

  [Test]
  public async Task ReportFailuresAsync_EmptyList_LeavesRowsUntouchedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var coordinator = _createCoordinator(dbContext);

    var msgId = Guid.NewGuid();
    await _insertOutboxRowAsync(connection, msgId, Guid.NewGuid());

    await coordinator.ReportFailuresAsync(WorkCategory.Outbox, []);

    var pristine = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_outbox WHERE message_id = @msg AND error IS NULL AND attempts = 0",
      ("msg", msgId));
    await Assert.That(pristine).IsEqualTo(1L);
  }

  [Test]
  public async Task RenewLeasesAsync_EmptyList_ReturnsZeroAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _createCoordinator(dbContext);

    var renewed = await coordinator.RenewLeasesAsync(WorkCategory.Outbox, []);

    await Assert.That(renewed).IsEqualTo(0);
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_EmptyArray_StoresNothingAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var coordinator = _createCoordinator(dbContext);

    await coordinator.StoreOutboxMessagesAsync([], partitionCount: 100);

    var stored = await _countAsync(connection, "SELECT COUNT(*) FROM wh_outbox");
    await Assert.That(stored).IsEqualTo(0L);
  }

  [Test]
  public async Task GetPerspectiveCursorsBatchAsync_EmptyStreamIds_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _createCoordinator(dbContext);

    var cursors = await coordinator.GetPerspectiveCursorsBatchAsync([]);

    await Assert.That(cursors).Count().IsEqualTo(0);
  }

  [Test]
  public async Task CleanupCompletedStreamsAsync_NullStreamList_ReturnsZeroAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _createCoordinator(dbContext);

    IReadOnlyList<Guid>? nullStreams = null;
    var evicted = await coordinator.CleanupCompletedStreamsAsync(nullStreams!);

    await Assert.That(evicted).IsEqualTo(0);
  }

  // --------------------------------------------------------------------------
  // Helpers
  // --------------------------------------------------------------------------

  private static async Task<NpgsqlConnection> _openConnectionAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _createCoordinator(WorkCoordinationDbContext dbContext) {
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
  }

  private static async Task<long> _countAsync(
      NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _insertOutboxRowAsync(
      NpgsqlConnection connection, Guid messageId, Guid streamId,
      Guid? instanceId = null, int partitionNumber = 0) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{""payload"":1}', '{""hop"":1}', 1, 0,
              NOW(), @stream, @partition, @inst, @lease)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("partition", partitionNumber);
    ins.Parameters.AddWithValue("inst", (object?)instanceId ?? DBNull.Value);
    ins.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) {
      Value = instanceId.HasValue ? DateTimeOffset.UtcNow.AddMinutes(5) : DBNull.Value
    });
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertInboxRowAsync(
      NpgsqlConnection connection, Guid messageId, string messageType, Guid streamId,
      int partitionNumber = 0) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         stream_id, partition_number)
      VALUES (@msg, 'TestHandler', @type, '{""payload"":1}', '{""hop"":1}', 1, 0, NOW(),
              @stream, @partition)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("type", messageType);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("partition", partitionNumber);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertActiveStreamAsync(
      NpgsqlConnection connection, Guid streamId, int partitionNumber = 0) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_active_streams (stream_id, partition_number)
      VALUES (@stream, @partition)";
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("partition", partitionNumber);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertEventStoreRowAsync(
      NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType, long version = 1) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
      VALUES (@evt, @stream, @stream, 'agg', @type, '{}'::jsonb, @version, NOW())";
    ins.Parameters.AddWithValue("evt", eventId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("type", eventType);
    ins.Parameters.AddWithValue("version", version);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertPerspectiveEventAsync(
      NpgsqlConnection connection, Guid eventWorkId, Guid streamId, string perspectiveName,
      Guid eventId, DateTimeOffset? processedAt) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at, processed_at)
      VALUES (@work, @stream, @name, @eid, 0, 0, NOW(), @processed)";
    ins.Parameters.AddWithValue("work", eventWorkId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("name", perspectiveName);
    ins.Parameters.AddWithValue("eid", eventId);
    ins.Parameters.Add(new NpgsqlParameter("processed", NpgsqlDbType.TimestampTz) {
      Value = (object?)processedAt ?? DBNull.Value
    });
    await ins.ExecuteNonQueryAsync();
  }
}
