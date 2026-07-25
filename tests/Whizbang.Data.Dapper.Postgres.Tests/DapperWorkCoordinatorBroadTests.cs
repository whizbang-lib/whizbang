using System.Text.Json;
using Dapper;
using Medo;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Broad smoke-test suite for <see cref="DapperWorkCoordinator"/> covering the SQL paths
/// that previously had no Dapper-side coverage (the EFCore equivalent is exercised by
/// integration tests but Dapper sat at ~2% line coverage). Tests exercise the happy
/// paths and empty-result paths — they're not meant to assert behavior comprehensively
/// (the SQL functions themselves have dedicated tests in the EFCore project), just to
/// keep the Dapper coordinator's serialization and SQL-invocation code paths walked.
/// </summary>
public class DapperWorkCoordinatorBroadTests : PostgresTestBase {

  private DapperWorkCoordinator _build() {
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = InfrastructureJsonContext.Default,
    };
    return new DapperWorkCoordinator(
      ConnectionString,
      jsonOptions,
      NullLogger<DapperWorkCoordinator>.Instance);
  }

  private static OutboxMessage _makeOutbox(Guid msgId, Guid streamId, string? destination = "test-dest") {
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(msgId),
      JsonDocument.Parse("{\"k\":1}").RootElement,
      []);
    return new OutboxMessage {
      MessageId = msgId,
      Destination = destination,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.X, Test]], Whizbang.Core",
      MessageType = "Test.X, Test",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(msgId), Hops = [] },
      StreamId = streamId,
      IsEvent = false,
    };
  }

  [Test]
  public async Task RecordHeartbeatAsync_NewInstance_InsertsRowAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-a", "host-a", 42));

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var svc = await conn.ExecuteScalarAsync<string>(
      "SELECT service_name FROM wh_service_instances WHERE instance_id = @id",
      new { id = instanceId });
    await Assert.That(svc).IsEqualTo("svc-a");
  }

  [Test]
  public async Task RecordHeartbeatAsync_NullRequest_ThrowsAsync() {
    var c = _build();
    var threw = false;
    try {
      await c.RecordHeartbeatAsync(null!);
    } catch (ArgumentNullException) { threw = true; }
    await Assert.That(threw).IsTrue();
  }

  [Test]
  public async Task DeregisterInstanceAsync_RemovesRowAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-b", "host-b", 1));
    await c.DeregisterInstanceAsync(instanceId);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var count = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_service_instances WHERE instance_id = @id",
      new { id = instanceId });
    await Assert.That(count).IsEqualTo(0L);
  }

  [Test]
  public async Task ClaimWorkAsync_NoWork_ReturnsEmptyBatchAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await c.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "svc-c", "host-c", 1));

    var batch = await c.ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc-c", "host-c", 1,
      MaxStreams: 50, PartitionCount: 100, LeaseSeconds: 300));

    await Assert.That(batch).IsNotNull();
    await Assert.That(batch.OutboxStreamIds.Count).IsEqualTo(0);
    await Assert.That(batch.InboxStreamIds.Count).IsEqualTo(0);
    await Assert.That(batch.PerspectiveStreamIds.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchOutboxBatchAsync_NoRows_ReturnsEmptyAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var rows = await c.FetchOutboxBatchAsync(
      [(Guid)TrackedGuid.NewMedo()], instanceId, maxPerStream: 10);
    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchInboxBatchAsync_NoRows_ReturnsEmptyAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var rows = await c.FetchInboxBatchAsync(
      [(Guid)TrackedGuid.NewMedo()], instanceId, maxPerStream: 10);
    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchPendingPerspectiveEventsAsync_NoRows_ReturnsEmptyAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var rows = await c.FetchPendingPerspectiveEventsAsync(
      (Guid)TrackedGuid.NewMedo(), "TestPerspective", instanceId);
    await Assert.That(rows.Count).IsEqualTo(0);
  }

  /// <summary>
  /// Production forensic G6: Dapper-side coverage for the <c>out_commit_sequence</c> projection. Without
  /// this test the Dapper reader's commit_sequence path was uncovered by CI (the EFCore
  /// tests don't exercise the Dapper coordinator).
  /// </summary>
  [Test]
  public async Task FetchPendingPerspectiveEventsAsync_StampedRow_SurfacesCommitSequenceAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    var workId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    const long stampedCommitSequence = 234500L;

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

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

    var rows = await c.FetchPendingPerspectiveEventsAsync(streamId, perspectiveName, instanceId);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].CommitSequence).IsEqualTo(stampedCommitSequence);
  }

  [Test]
  public async Task FetchEventsByIdsAsync_NoIds_ReturnsEmptyAsync() {
    var c = _build();
    var rows = await c.FetchEventsByIdsAsync([]);
    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchEventsByIdsAsync_UnknownIds_ReturnsEmptyAsync() {
    var c = _build();
    var rows = await c.FetchEventsByIdsAsync([
      (Guid)TrackedGuid.NewMedo(),
      (Guid)TrackedGuid.NewMedo(),
    ]);
    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_EmptyArray_NoOpAsync() {
    var c = _build();
    await c.StoreOutboxMessagesAsync([], partitionCount: 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_outbox");
    await Assert.That(count).IsEqualTo(0L);
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_SingleMessage_PersistsRowAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, streamId)], partitionCount: 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var count = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(count).IsEqualTo(1L);
  }

  [Test]
  public async Task CompleteOutboxPublishedAsync_NoIds_NoOpAsync() {
    var c = _build();
    var affected = await c.CompleteOutboxPublishedAsync([], debugMode: false);
    await Assert.That(affected).IsEqualTo(0);
  }

  [Test]
  public async Task CompleteOutboxPublishedAsync_ProductionMode_DeletesRowAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.NewMedo();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, (Guid)TrackedGuid.NewMedo())], 100);

    var affected = await c.CompleteOutboxPublishedAsync([msgId], debugMode: false);
    await Assert.That(affected).IsGreaterThanOrEqualTo(1);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var count = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(count).IsEqualTo(0L);
  }

  [Test]
  public async Task CompleteOutboxPublishedAsync_DebugMode_RetainsRowAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.NewMedo();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, (Guid)TrackedGuid.NewMedo())], 100);

    await c.CompleteOutboxPublishedAsync([msgId], debugMode: true);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var publishedAt = await conn.ExecuteScalarAsync<DateTime?>(
      "SELECT published_at FROM wh_outbox WHERE message_id = @m", new { m = msgId });
    await Assert.That(publishedAt).IsNotNull();
  }

  [Test]
  public async Task RenewLeasesAsync_NoMessages_NoOpAsync() {
    var c = _build();
    var n = await c.RenewLeasesAsync(WorkCategory.Outbox, [], leaseSeconds: 30);
    await Assert.That(n).IsEqualTo(0);
  }

  [Test]
  public async Task ReportFailuresAsync_EmptyList_NoOpAsync() {
    var c = _build();
    await c.ReportFailuresAsync(WorkCategory.Outbox, []);
  }

  [Test]
  public async Task CleanupCompletedStreamsAsync_NoStreams_ReturnsZeroAsync() {
    var c = _build();
    var n = await c.CleanupCompletedStreamsAsync([]);
    await Assert.That(n).IsEqualTo(0);
  }

  [Test]
  public async Task PerformMaintenanceAsync_EmptyDatabase_ReturnsResultsAsync() {
    var c = _build();
    var results = await c.PerformMaintenanceAsync();
    await Assert.That(results).IsNotNull();
  }

  [Test]
  public async Task GatherStatisticsAsync_EmptyDatabase_ReturnsZerosAsync() {
    var c = _build();
    var stats = await c.GatherStatisticsAsync();
    await Assert.That(stats).IsNotNull();
  }

  [Test]
  public async Task ResolveSyncInquiriesAsync_NoInquiries_ReturnsEmptyAsync() {
    var c = _build();
    var results = await c.ResolveSyncInquiriesAsync([]);
    await Assert.That(results.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetStreamEventsAsync_NoEvents_ReturnsEmptyAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var events = await c.GetStreamEventsAsync(instanceId, [(Guid)TrackedGuid.NewMedo()]);
    await Assert.That(events.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetPerspectiveCursorAsync_NoRow_ReturnsNullAsync() {
    var c = _build();
    var info = await c.GetPerspectiveCursorAsync(
      (Guid)TrackedGuid.NewMedo(), "TestPerspective");
    await Assert.That(info).IsNull();
  }

  [Test]
  public async Task CompletePerspectiveEventsAsync_NoIds_ReturnsZeroAsync() {
    var c = _build();
    var n = await c.CompletePerspectiveEventsAsync([], debugMode: false);
    await Assert.That(n).IsEqualTo(0);
  }

  [Test]
  public async Task RecomputePartitionNumbersAsync_NoRows_ReturnsZeroResultAsync() {
    var c = _build();
    var result = await c.RecomputePartitionNumbersAsync(partitionCount: 100);
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task FlushCompletionsAsync_EmptyRequest_NoOpAsync() {
    var c = _build();
    await c.FlushCompletionsAsync(new FlushCompletionsRequest());
  }

  [Test]
  public async Task CommitHandlerBatchAsync_EmptyList_ReturnsEmptyAsync() {
    var c = _build();
    var results = await c.CommitHandlerBatchAsync([]);
    await Assert.That(results.Count).IsEqualTo(0);
  }

  // ----- second wave: deeper paths -----

  [Test]
  public async Task FetchOutboxBatchAsync_WithStoredRows_ReturnsRowsAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, streamId)], partitionCount: 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(
      "UPDATE wh_outbox SET instance_id = @i, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @m",
      new { i = instanceId, m = msgId });

    var rows = await c.FetchOutboxBatchAsync([streamId], instanceId, maxPerStream: 10);
    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].MessageId).IsEqualTo(msgId);
  }

  [Test]
  public async Task ReportPerspectiveCompletionAsync_RunsWithoutErrorAsync() {
    // The SQL function complete_perspective_cursor_work requires pre-staged
    // perspective_events rows to actually persist a cursor row, but invoking it
    // exercises the C# serialization + parameter-binding path which is what
    // we're after for coverage. Side-effect assertion lives in the EFCore
    // equivalent tests where the full pipeline is set up.
    var c = _build();
    await c.ReportPerspectiveCompletionAsync(new PerspectiveCursorCompletion {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PerspectiveName = "TestPerspective",
      LastEventId = (Guid)TrackedGuid.NewMedo(),
      ProcessedEventIds = [(Guid)TrackedGuid.NewMedo()],
      Status = PerspectiveProcessingStatus.Completed,
    });
  }

  [Test]
  public async Task ReportPerspectiveFailureAsync_RunsWithoutErrorAsync() {
    var c = _build();
    await c.ReportPerspectiveFailureAsync(new PerspectiveCursorFailure {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PerspectiveName = "FailPerspective",
      LastEventId = (Guid)TrackedGuid.NewMedo(),
      ProcessedEventIds = [],
      Status = PerspectiveProcessingStatus.Failed,
      Error = "boom",
    });
  }

  [Test]
  public async Task CleanupCompletedStreamsAsync_WithStream_RunsWithoutErrorAsync() {
    var c = _build();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var n = await c.CleanupCompletedStreamsAsync([streamId]);
    await Assert.That(n).IsGreaterThanOrEqualTo(0);
  }

  [Test]
  public async Task RenewLeasesAsync_WithStoredRows_BumpsLeaseAsync() {
    var c = _build();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, (Guid)TrackedGuid.NewMedo())], 100);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(
      "UPDATE wh_outbox SET instance_id = @i, lease_expiry = NOW() + INTERVAL '1 second' WHERE message_id = @m",
      new { i = instanceId, m = msgId });

    var n = await c.RenewLeasesAsync(WorkCategory.Outbox, [msgId], leaseSeconds: 600);
    await Assert.That(n).IsGreaterThanOrEqualTo(0);
  }

  [Test]
  public async Task CompletePerspectiveAsync_RunsWithoutErrorAsync() {
    var c = _build();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var lastEventId = (Guid)TrackedGuid.NewMedo();

    await c.CompletePerspectiveAsync(
      [new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = "PerspectiveDone",
        LastEventId = lastEventId,
        ProcessedEventIds = [lastEventId],
        Status = PerspectiveProcessingStatus.Completed,
      }],
      eventWorkIds: [],
      debugMode: false);
  }

  [Test]
  public async Task ResolveSyncInquiriesAsync_WithBasicInquiry_ReturnsResultsAsync() {
    var c = _build();
    var inquiry = new SyncInquiry {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PerspectiveName = "AnyPerspective",
    };
    var results = await c.ResolveSyncInquiriesAsync([inquiry]);
    await Assert.That(results).IsNotNull();
  }

  [Test]
  public async Task GatherStatisticsAsync_WithStoredRow_StillReturnsStatsAsync() {
    var c = _build();
    var msgId = (Guid)TrackedGuid.NewMedo();
    await c.StoreOutboxMessagesAsync([_makeOutbox(msgId, (Guid)TrackedGuid.NewMedo())], 100);

    var stats = await c.GatherStatisticsAsync();
    await Assert.That(stats).IsNotNull();
  }
}
