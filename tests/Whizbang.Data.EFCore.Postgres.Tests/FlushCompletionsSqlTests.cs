using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>flush_completions</c> — the composite single-round-trip flusher
/// that combines complete_outbox_published + complete_perspective + report_failures
/// into one call when the C# flusher has multiple categories buffered. Single
/// fsync at outer commit covers all sub-operations.
/// Phase A of the work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public class FlushCompletionsSqlTests : EFCoreTestBase {

  [Test]
  public async Task FlushCompletions_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='flush_completions' AND pronamespace='public'::regnamespace);";
    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task FlushCompletions_OutboxAndPerspective_AppliesBothInOneCallAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var outboxMsgId = Guid.NewGuid();
    var perspectiveWorkId = Guid.NewGuid();

    // Seed an outbox row (unprocessed) and a perspective_events row.
    await using (var insOutbox = connection.CreateCommand()) {
      insOutbox.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(), @stream, 0)";
      insOutbox.Parameters.AddWithValue("msg", outboxMsgId);
      insOutbox.Parameters.AddWithValue("stream", Guid.NewGuid());
      await insOutbox.ExecuteNonQueryAsync();
    }
    await using (var insPersp = connection.CreateCommand()) {
      insPersp.CommandText = @"
        INSERT INTO wh_perspective_events
          (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@work, @stream, 'TestPerspective', @eid, 0, 0, NOW())";
      insPersp.Parameters.AddWithValue("work", perspectiveWorkId);
      insPersp.Parameters.AddWithValue("stream", Guid.NewGuid());
      insPersp.Parameters.AddWithValue("eid", Guid.NewGuid());
      await insPersp.ExecuteNonQueryAsync();
    }

    // One round-trip flushing both categories.
    await using (var flush = connection.CreateCommand()) {
      flush.CommandText = "SELECT flush_completions(@outbox_ids, '[]'::jsonb, @persp_ids, '[]'::jsonb)";
      flush.Parameters.Add(new NpgsqlParameter("outbox_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { outboxMsgId } });
      flush.Parameters.Add(new NpgsqlParameter("persp_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { perspectiveWorkId } });
      _ = await flush.ExecuteScalarAsync();
    }

    // Assert both effects applied. Production-mode complete_outbox_published DELETEs the
    // row outright (not UPDATEs processed_at) — structurally immune to claim_work
    // re-issuing it. The row should be gone.
    await using (var verifyOutbox = connection.CreateCommand()) {
      verifyOutbox.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = @msg";
      verifyOutbox.Parameters.AddWithValue("msg", outboxMsgId);
      var remaining = (long)(await verifyOutbox.ExecuteScalarAsync())!;
      await Assert.That(remaining).IsEqualTo(0L);
    }
    await using (var verifyPersp = connection.CreateCommand()) {
      verifyPersp.CommandText = "SELECT count(*) FROM wh_perspective_events WHERE event_work_id = @work";
      verifyPersp.Parameters.AddWithValue("work", perspectiveWorkId);
      var remaining = (long)(await verifyPersp.ExecuteScalarAsync())!;
      await Assert.That(remaining).IsEqualTo(0L);
    }
  }
}
