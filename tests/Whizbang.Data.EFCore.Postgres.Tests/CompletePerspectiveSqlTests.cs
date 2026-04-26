using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>complete_perspective</c> — batched perspective work completion.
/// Combines event-work-row deletion (via process_perspective_event_completions) and
/// cursor advancement (via update_perspective_cursors) in one round-trip.
/// Coalesced flush from the C# PerspectiveCompletionFlushWorker.
/// Phase A of the work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public class CompletePerspectiveSqlTests : EFCoreTestBase {

  [Test]
  public async Task CompletePerspective_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='complete_perspective' AND pronamespace='public'::regnamespace);";
    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task CompletePerspective_DeletesPerspectiveEventRowsForProvidedWorkIdsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var workId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@work, @stream, 'TestPerspective', @eid, 0, 0, NOW())";
      ins.Parameters.AddWithValue("work", workId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("eid", eventId);
      await ins.ExecuteNonQueryAsync();
    }

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT complete_perspective('[]'::jsonb, @ids)";
      call.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { workId } });
      _ = await call.ExecuteScalarAsync();
    }

    await using var verify = connection.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_perspective_events WHERE event_work_id = @work";
    verify.Parameters.AddWithValue("work", workId);
    var remaining = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(remaining).IsEqualTo(0L);
  }
}
