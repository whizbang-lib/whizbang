using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>resolve_sync_inquiries</c> — the PerspectiveSyncAwaiter read-only path.
/// Reports pending vs processed event counts per (stream, perspective) pair so the
/// awaiter can wait for cursor advancement. Read-only against wh_event_store +
/// wh_perspective_events; no writes.
/// Phase A of the work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public class ResolveSyncInquiriesSqlTests : EFCoreTestBase {

  [Test]
  public async Task ResolveSyncInquiries_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='resolve_sync_inquiries' AND pronamespace='public'::regnamespace);";
    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task ResolveSyncInquiries_ReturnsPendingAndProcessedCountsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var streamId = Guid.NewGuid();
    var processedEventId = Guid.NewGuid();
    var pendingEventId = Guid.NewGuid();
    var inquiryId = Guid.NewGuid();
    const string perspectiveName = "TestPerspective";

    // Two events in event store on this stream.
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, created_at, scope)
        VALUES
          (@e1, @stream, @stream, 'TestAgg', 1, 'TestEvent', NOW(), 'null'::jsonb),
          (@e2, @stream, @stream, 'TestAgg', 2, 'TestEvent', NOW(), 'null'::jsonb)";
      ins.Parameters.AddWithValue("e1", processedEventId);
      ins.Parameters.AddWithValue("e2", pendingEventId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    // First event is processed by the perspective, second is pending (no row).
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (stream_id, perspective_name, event_id, status, attempts, created_at, processed_at)
        VALUES (@stream, @persp, @eid, 1, 1, NOW(), NOW())";
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("persp", perspectiveName);
      ins.Parameters.AddWithValue("eid", processedEventId);
      await ins.ExecuteNonQueryAsync();
    }
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@stream, @persp, @eid, 0, 0, NOW())";
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("persp", perspectiveName);
      ins.Parameters.AddWithValue("eid", pendingEventId);
      await ins.ExecuteNonQueryAsync();
    }

    var inquiriesJson = $$"""
      [{
        "InquiryId": "{{inquiryId}}",
        "StreamId": "{{streamId}}",
        "PerspectiveName": "{{perspectiveName}}",
        "DiscoverPendingFromOutbox": true,
        "IncludePendingEventIds": false,
        "IncludeProcessedEventIds": false
      }]
      """;

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT inquiry_id, stream_id, pending_count, processed_count FROM resolve_sync_inquiries(@req::jsonb)";
    cmd.Parameters.AddWithValue("req", inquiriesJson);

    var rows = new List<(Guid inquiryId, Guid streamId, int pending, int processed)>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetInt32(3)));
      }
    }

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].inquiryId).IsEqualTo(inquiryId);
    await Assert.That(rows[0].streamId).IsEqualTo(streamId);
    await Assert.That(rows[0].pending).IsEqualTo(1);
    await Assert.That(rows[0].processed).IsEqualTo(1);
  }
}
