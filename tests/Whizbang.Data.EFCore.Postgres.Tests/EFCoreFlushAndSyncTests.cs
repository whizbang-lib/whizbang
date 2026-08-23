using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phase B integration tests for the last two coordinator methods:
/// FlushCompletionsAsync (composite multi-category flush) and ResolveSyncInquiriesAsync.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
[Category("Shard2")]
public class EFCoreFlushAndSyncTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> Coord(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task FlushCompletionsAsync_OutboxAndPerspective_AppliesBothInOneCallAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var outboxMsgId = TrackedGuid.NewMedo();
    var perspectiveWorkId = TrackedGuid.NewMedo();

    await using (var insOutbox = conn.CreateCommand()) {
      insOutbox.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(), @stream, 0)";
      insOutbox.Parameters.AddWithValue("msg", (Guid)outboxMsgId);
      insOutbox.Parameters.AddWithValue("stream", (Guid)TrackedGuid.NewMedo());
      await insOutbox.ExecuteNonQueryAsync();
    }
    await using (var insPersp = conn.CreateCommand()) {
      insPersp.CommandText = @"
        INSERT INTO wh_perspective_events
          (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@work, @stream, 'TestPerspective', @eid, 0, 0, NOW())";
      insPersp.Parameters.AddWithValue("work", (Guid)perspectiveWorkId);
      insPersp.Parameters.AddWithValue("stream", (Guid)TrackedGuid.NewMedo());
      insPersp.Parameters.AddWithValue("eid", (Guid)TrackedGuid.NewMedo());
      await insPersp.ExecuteNonQueryAsync();
    }

    await coordinator.FlushCompletionsAsync(new FlushCompletionsRequest(
      OutboxIds: [outboxMsgId],
      PerspectiveEventWorkIds: [perspectiveWorkId]));

    // Production-mode complete_outbox_published DELETEs the row outright; assert it's gone.
    await using (var v = conn.CreateCommand()) {
      v.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = @m";
      v.Parameters.AddWithValue("m", (Guid)outboxMsgId);
      await Assert.That((long)(await v.ExecuteScalarAsync())!).IsEqualTo(0L);
    }
    await using (var v = conn.CreateCommand()) {
      v.CommandText = "SELECT count(*) FROM wh_perspective_events WHERE event_work_id = @w";
      v.Parameters.AddWithValue("w", (Guid)perspectiveWorkId);
      await Assert.That((long)(await v.ExecuteScalarAsync())!).IsEqualTo(0L);
    }
  }

  [Test]
  public async Task ResolveSyncInquiriesAsync_ReturnsPendingAndProcessedCountsAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var streamId = TrackedGuid.NewMedo();
    var processedEventId = TrackedGuid.NewMedo();
    var pendingEventId = TrackedGuid.NewMedo();
    const string perspectiveName = "TestPerspective";

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, created_at, scope)
        VALUES
          (@e1, @stream, @stream, 'TestAgg', 1, 'TestEvent', NOW(), 'null'::jsonb),
          (@e2, @stream, @stream, 'TestAgg', 2, 'TestEvent', NOW(), 'null'::jsonb)";
      ins.Parameters.AddWithValue("e1", (Guid)processedEventId);
      ins.Parameters.AddWithValue("e2", (Guid)pendingEventId);
      ins.Parameters.AddWithValue("stream", (Guid)streamId);
      await ins.ExecuteNonQueryAsync();
    }
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (stream_id, perspective_name, event_id, status, attempts, created_at, processed_at)
        VALUES (@stream, @persp, @eid, 1, 1, NOW(), NOW())";
      ins.Parameters.AddWithValue("stream", (Guid)streamId);
      ins.Parameters.AddWithValue("persp", perspectiveName);
      ins.Parameters.AddWithValue("eid", (Guid)processedEventId);
      await ins.ExecuteNonQueryAsync();
    }
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@stream, @persp, @eid, 0, 0, NOW())";
      ins.Parameters.AddWithValue("stream", (Guid)streamId);
      ins.Parameters.AddWithValue("persp", perspectiveName);
      ins.Parameters.AddWithValue("eid", (Guid)pendingEventId);
      await ins.ExecuteNonQueryAsync();
    }

    var results = await coordinator.ResolveSyncInquiriesAsync([
      new SyncInquiry {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        DiscoverPendingFromOutbox = true
      }
    ]);

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].StreamId).IsEqualTo((Guid)streamId);
    await Assert.That(results[0].PendingCount).IsEqualTo(1);
    await Assert.That(results[0].ProcessedCount).IsEqualTo(1);
  }

  [Test]
  public async Task ResolveSyncInquiriesAsync_EmptyList_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var results = await coordinator.ResolveSyncInquiriesAsync([]);
    await Assert.That(results.Count).IsEqualTo(0);
  }
}
