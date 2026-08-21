using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phase B integration tests: <see cref="IWorkCoordinator.RecordHeartbeatAsync"/>
/// on the EFCore Postgres backend. Decoupled-heartbeat path; one of the foundational
/// methods for the work-pump decomposition's separate-timer design.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
[Category("Shard3")]
public class EFCoreRecordHeartbeatTests : EFCoreTestBase {

  [Test]
  public async Task RecordHeartbeatAsync_NewInstance_InsertsRowAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions());

    var instanceId = Guid.NewGuid();
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(
      InstanceId: instanceId,
      ServiceName: "test-svc",
      HostName: "test-host",
      ProcessId: 42));

    await using var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    await using var verify = connection.CreateCommand();
    verify.CommandText = "SELECT service_name, host_name, process_id FROM wh_service_instances WHERE instance_id = @id";
    verify.Parameters.AddWithValue("id", instanceId);
    await using var reader = await verify.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    await Assert.That(reader.GetString(0)).IsEqualTo("test-svc");
    await Assert.That(reader.GetString(1)).IsEqualTo("test-host");
    await Assert.That(reader.GetInt32(2)).IsEqualTo(42);
  }

  [Test]
  public async Task RecordHeartbeatAsync_ExistingInstance_AdvancesLastHeartbeatAtAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions());

    var instanceId = Guid.NewGuid();
    var req = new HeartbeatRequest(instanceId, "test-svc", "test-host", 1);

    await coordinator.RecordHeartbeatAsync(req);

    var c = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (c.State != System.Data.ConnectionState.Open) {
      await c.OpenAsync();
    }

    DateTime first;
    await using (var read = c.CreateCommand()) {
      read.CommandText = "SELECT last_heartbeat_at FROM wh_service_instances WHERE instance_id = @id";
      read.Parameters.AddWithValue("id", instanceId);
      first = Convert.ToDateTime(await read.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
    await using (var sleep = c.CreateCommand()) {
      sleep.CommandText = "SELECT pg_sleep(0.05)";
      _ = await sleep.ExecuteScalarAsync();
    }

    await coordinator.RecordHeartbeatAsync(req);

    DateTime second;
    await using (var read = c.CreateCommand()) {
      read.CommandText = "SELECT last_heartbeat_at FROM wh_service_instances WHERE instance_id = @id";
      read.Parameters.AddWithValue("id", instanceId);
      second = Convert.ToDateTime(await read.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    await Assert.That(second).IsGreaterThan(first);
  }

  [Test]
  public async Task RecordHeartbeatAsync_NullRequest_ThrowsAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions());

    var threw = false;
    try {
      await coordinator.RecordHeartbeatAsync(null!);
    } catch (ArgumentNullException) {
      threw = true;
    }
    await Assert.That(threw).IsTrue();
  }
}
