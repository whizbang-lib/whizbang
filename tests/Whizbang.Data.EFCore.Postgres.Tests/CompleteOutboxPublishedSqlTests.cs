using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>complete_outbox_published</c>. Two-mode contract:
/// <list type="bullet">
///   <item><description><c>p_debug_mode = FALSE</c> (default, production): DELETE the row from <c>wh_outbox</c>. Row exits the table — claim_work can never re-issue it.</description></item>
///   <item><description><c>p_debug_mode = TRUE</c>: retain the row and stamp <c>published_at = NOW()</c> + <c>processed_at = NOW()</c> + <c>status |= 4</c> (Published bit). Visible in the table for forensics; <c>eligible_outbox</c> filters debug-mode rows via <c>published_at IS NULL</c>.</description></item>
/// </list>
/// This is the legacy <c>process_outbox_completions</c> design (mig 013) restored after the Phase H regression.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
[Category("Shard1")]
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
  public async Task CompleteOutboxPublished_Production_DeletesRowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    await _insertOutboxRowsAsync(connection, ids);

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT complete_outbox_published(@ids)";
      call.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
      _ = await call.ExecuteScalarAsync();
    }

    await using var verify = connection.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids)";
    verify.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
    var remaining = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(remaining).IsEqualTo(0L);
  }

  [Test]
  public async Task CompleteOutboxPublished_DebugMode_RetainsRowAndSetsPublishedAtAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
    await _insertOutboxRowsAsync(connection, ids);

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT complete_outbox_published(@ids, TRUE)";
      call.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
      _ = await call.ExecuteScalarAsync();
    }

    await using var verify = connection.CreateCommand();
    verify.CommandText = @"SELECT count(*) FROM wh_outbox
      WHERE message_id = ANY(@ids)
        AND published_at IS NOT NULL
        AND processed_at IS NOT NULL
        AND (status & 4) = 4";
    verify.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
    var marked = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(marked).IsEqualTo(2L);
  }

  [Test]
  public async Task CompleteOutboxPublished_DebugMode_RetainedRow_NotReturnedByEligibleOutboxAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    await _registerInstanceAsync(connection, instanceId);
    await _insertOutboxRowAsync(connection, messageId, streamId, instanceId);

    // Debug-mode complete: row stays, but published_at is set.
    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT complete_outbox_published(ARRAY[@id]::UUID[], TRUE)";
      call.Parameters.AddWithValue("id", messageId);
      _ = await call.ExecuteScalarAsync();
    }

    // claim_work should NOT return the row — published_at filter excludes it.
    await using var claim = connection.CreateCommand();
    claim.CommandText = @"SELECT count(*) FROM claim_work(@inst, 'test-svc', 'test-host', 1, 1000, 10000, 300) WHERE source = 'outbox' AND work_id = @id";
    claim.Parameters.AddWithValue("inst", instanceId);
    claim.Parameters.AddWithValue("id", messageId);
    var rowsClaimed = (long)(await claim.ExecuteScalarAsync())!;
    await Assert.That(rowsClaimed).IsEqualTo(0L);
  }

  [Test]
  public async Task CompleteOutboxPublished_Production_AcrossManyMessages_TableEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    const int rowCount = 1000;
    var ids = new Guid[rowCount];
    for (var i = 0; i < rowCount; i++) {
      ids[i] = Guid.NewGuid();
    }
    await _insertOutboxRowsAsync(connection, ids);

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT complete_outbox_published(@ids)";
      call.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
      _ = await call.ExecuteScalarAsync();
    }

    await using var verify = connection.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = ANY(@ids)";
    verify.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
    var remaining = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(remaining).IsEqualTo(0L);
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

  private static async Task _insertOutboxRowsAsync(NpgsqlConnection connection, Guid[] ids) {
    foreach (var id in ids) {
      await _insertOutboxRowAsync(connection, id, Guid.NewGuid(), instanceId: null);
    }
  }

  private static async Task _insertOutboxRowAsync(NpgsqlConnection connection, Guid messageId, Guid streamId, Guid? instanceId) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number, instance_id, lease_expiry)
      VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(), @stream, 0, @inst::UUID, CASE WHEN @inst::UUID IS NULL THEN NULL ELSE NOW() + INTERVAL '5 minutes' END)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.Add(new NpgsqlParameter("inst", NpgsqlDbType.Uuid) { Value = (object?)instanceId ?? DBNull.Value });
    await ins.ExecuteNonQueryAsync();
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
