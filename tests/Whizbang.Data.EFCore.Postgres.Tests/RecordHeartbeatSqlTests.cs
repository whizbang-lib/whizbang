using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>record_heartbeat</c> — the decoupled heartbeat function.
/// Separated from claim_work so the heartbeat timer can fire on its own cadence (5s default)
/// independent of the polling interval. Sub-millisecond UPSERT.
/// Phase A of the work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public class RecordHeartbeatSqlTests : EFCoreTestBase {

  [Test]
  public async Task RecordHeartbeat_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='record_heartbeat' AND pronamespace='public'::regnamespace);";
    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task RecordHeartbeat_NewInstance_InsertsRowAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT record_heartbeat(@id, 'test', 'host-1', 1, '{}'::jsonb)";
      call.Parameters.AddWithValue("id", instanceId);
      _ = await call.ExecuteScalarAsync();
    }

    await using var verify = connection.CreateCommand();
    verify.CommandText = "SELECT last_heartbeat_at IS NOT NULL FROM wh_service_instances WHERE instance_id = @id";
    verify.Parameters.AddWithValue("id", instanceId);
    var found = await verify.ExecuteScalarAsync();
    await Assert.That(found).IsNotNull();
    await Assert.That((bool)found!).IsTrue();
  }

  [Test]
  public async Task RecordHeartbeat_ExistingInstance_UpdatesLastHeartbeatAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();

    // First heartbeat creates row.
    await using (var first = connection.CreateCommand()) {
      first.CommandText = "SELECT record_heartbeat(@id, 'test', 'host-1', 1, '{}'::jsonb)";
      first.Parameters.AddWithValue("id", instanceId);
      _ = await first.ExecuteScalarAsync();
    }

    DateTime firstHeartbeat;
    await using (var read = connection.CreateCommand()) {
      read.CommandText = "SELECT last_heartbeat_at FROM wh_service_instances WHERE instance_id = @id";
      read.Parameters.AddWithValue("id", instanceId);
      firstHeartbeat = Convert.ToDateTime(await read.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // Force time to advance enough that NOW() differs.
    await using (var sleep = connection.CreateCommand()) {
      sleep.CommandText = "SELECT pg_sleep(0.05)";
      _ = await sleep.ExecuteScalarAsync();
    }

    // Second heartbeat should advance the timestamp.
    await using (var second = connection.CreateCommand()) {
      second.CommandText = "SELECT record_heartbeat(@id, 'test', 'host-1', 1, '{}'::jsonb)";
      second.Parameters.AddWithValue("id", instanceId);
      _ = await second.ExecuteScalarAsync();
    }

    DateTime secondHeartbeat;
    await using (var read = connection.CreateCommand()) {
      read.CommandText = "SELECT last_heartbeat_at FROM wh_service_instances WHERE instance_id = @id";
      read.Parameters.AddWithValue("id", instanceId);
      secondHeartbeat = Convert.ToDateTime(await read.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    await Assert.That(secondHeartbeat).IsGreaterThan(firstHeartbeat);
  }
}
