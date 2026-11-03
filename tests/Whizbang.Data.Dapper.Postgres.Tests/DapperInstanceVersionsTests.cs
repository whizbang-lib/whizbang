// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2027 on the Dapper driver: a beat records the application version and the liveness window the instance declares
/// on its row, the same as the EF Core driver (InstanceLivenessRuleSqlTests), and the one liveness rule judges it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
public class DapperInstanceVersionsTests : PostgresTestBase {
  [Test]
  public async Task ABeat_RecordsTheApplicationVersionAndTheWindow_AndTheRuleJudgesItAsync() {
    var coordinator = new DapperWorkCoordinator(ConnectionString,
      new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default }, NullLogger<DapperWorkCoordinator>.Instance);
    var instance = (Guid)TrackedGuid.New();

    var accepted = await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(
      instance, "dapper-versions-svc", "dapper-versions-host", 9, StaleThresholdSeconds: 150, ApplicationVersion: "3.1.0"));

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await Assert.That(accepted).IsTrue();
    await Assert.That(await conn.ExecuteScalarAsync<string>(
      "SELECT application_version FROM wh_service_instances WHERE instance_id = @instance", new { instance })).IsEqualTo("3.1.0");
    await Assert.That(await conn.ExecuteScalarAsync<int>(
      "SELECT liveness_window_seconds FROM wh_service_instances WHERE instance_id = @instance", new { instance })).IsEqualTo(150);
    await Assert.That(await conn.ExecuteScalarAsync<bool>("SELECT wh_instance_is_live(@instance)", new { instance })).IsTrue();
  }
}
