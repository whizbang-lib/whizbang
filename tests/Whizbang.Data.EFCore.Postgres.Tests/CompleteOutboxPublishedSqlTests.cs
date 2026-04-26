using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>complete_outbox_published</c> — fire-and-forget batched UPDATE that
/// marks outbox rows as processed after the transport publish succeeds. Coalesced
/// flush from the C# OutboxCompletionFlushWorker.
/// Phase A of the work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public class CompleteOutboxPublishedSqlTests : EFCoreTestBase {

  [Test]
  public async Task CompleteOutboxPublished_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='complete_outbox_published' AND pronamespace='public'::regnamespace);";
    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task CompleteOutboxPublished_MarksAllProvidedIdsProcessedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    foreach (var id in ids) {
      await using var ins = connection.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", id);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT complete_outbox_published(@ids)";
      call.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
      _ = await call.ExecuteScalarAsync();
    }

    await using var verify = connection.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids) AND processed_at IS NOT NULL";
    verify.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
    var processedCount = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(processedCount).IsEqualTo(3L);
  }

  [Test]
  public async Task CompleteOutboxPublished_UnknownIdsSilentlyIgnoredAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var ghosts = new[] { Guid.NewGuid(), Guid.NewGuid() };

    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT complete_outbox_published(@ids)";
    call.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ghosts });

    // Should not throw.
    _ = await call.ExecuteScalarAsync();
  }
}
