// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <c>wh_drop_exhausted_inbox_rows</c> through the EF Core coordinator (#2117): it drops only the given rows that are
/// still unprocessed and past the ceiling, and keeps them in debug mode with their processed time stamped.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/350_ExhaustedInboxRowsAreDropped.sql</code-under-test>
/// <docs>operations/observability/stuck-row-sentinel#exhausted-rows-are-concluded</docs>
[Category("Shard5")]
public class ExhaustedInboxDropSqlTests : EFCoreTestBase {

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task<Guid> _seedAsync(NpgsqlConnection conn, int attempts, bool processed = false) {
    var messageId = Guid.CreateVersion7();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event)
        VALUES (@msg, 'TestHandler', 'Whizbang.Core.Messaging.IntegrityCheckpoint, Whizbang.Core', '{"p": {}}', '{}', NOW(),
                @stream, FALSE)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts, processed_at)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, @attempts,
             CASE WHEN @processed THEN NOW() ELSE NULL END
      FROM m
      """;
    cmd.Parameters.AddWithValue("msg", messageId);
    cmd.Parameters.AddWithValue("stream", Guid.CreateVersion7());
    cmd.Parameters.AddWithValue(nameof(attempts), attempts);
    cmd.Parameters.AddWithValue(nameof(processed), processed);
    await cmd.ExecuteNonQueryAsync();
    return messageId;
  }

  private static async Task<(bool Exists, bool Processed)> _stateAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT EXISTS (SELECT 1 FROM wh_inbox WHERE message_id = @m),
             COALESCE((SELECT processed_at IS NOT NULL FROM wh_inbox_state WHERE message_id = @m), FALSE)
      """;
    cmd.Parameters.AddWithValue("m", messageId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetBoolean(0), reader.GetBoolean(1));
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Deliberate: the maintenance worker calls through the interface, so a coordinator that does not implement the member reaches its inert default here as it does there.")]
  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task Drop_RemovesOnlyTheGivenUnprocessedRowsPastTheCeilingAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var exhausted = await _seedAsync(conn, attempts: 11);
    var atCeiling = await _seedAsync(conn, attempts: 10);
    var processed = await _seedAsync(conn, attempts: 30, processed: true);
    var notGiven = await _seedAsync(conn, attempts: 30);

    var dropped = await _coordinator(ctx).DropExhaustedInboxRowsAsync([exhausted, atCeiling, processed], maxAttempts: 10);

    await Assert.That(dropped).IsEqualTo(1);
    await Assert.That((await _stateAsync(conn, exhausted)).Exists).IsFalse()
      .Because("a row still unprocessed and past the ceiling is dropped with its state row");
    await Assert.That((await _stateAsync(conn, atCeiling)).Exists).IsTrue()
      .Because("a row at the ceiling still has its last attempt; the drop re-checks the ceiling");
    await Assert.That((await _stateAsync(conn, processed)).Exists).IsTrue()
      .Because("a row that completed meanwhile is not the drop's to touch");
    await Assert.That((await _stateAsync(conn, notGiven)).Exists).IsTrue()
      .Because("only the rows asked for are dropped");
  }

  [Test]
  public async Task Drop_InDebugMode_KeepsTheRowWithItsProcessedTimeStampedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES "
        + "('debug_mode', 'true', 'boolean', 'test') ON CONFLICT (setting_key) DO UPDATE SET setting_value = 'true'";
      await cmd.ExecuteNonQueryAsync();
    }
    var exhausted = await _seedAsync(conn, attempts: 11);

    var dropped = await _coordinator(ctx).DropExhaustedInboxRowsAsync([exhausted], maxAttempts: 10);

    await Assert.That(dropped).IsEqualTo(1);
    await Assert.That(await _stateAsync(conn, exhausted)).IsEqualTo((true, true))
      .Because("debug mode keeps a concluded row for forensics, as the completion functions keep one");
  }

  [Test]
  public async Task Drop_WithNoIds_IsANoOpAsync() {
    await using var ctx = CreateDbContext();
    var dropped = await _coordinator(ctx).DropExhaustedInboxRowsAsync([], maxAttempts: 10);
    await Assert.That(dropped).IsEqualTo(0);
  }
}
