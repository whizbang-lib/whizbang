// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// The schema-taking helpers of the startup path accept an empty schema as "the default schema" and
/// a quoted name as the same name unquoted. Both spellings must reach the same objects: a helper that
/// treated the empty string literally would address a schema named "" and report the registry as
/// missing on a database where it is present.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/FleetVersions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaBootstrapPhase.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/DoorbellRinger.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class DefaultSchemaBranchTests : EFCoreTestBase {

  [Test]
  [Timeout(60000)]
  [Arguments("")]
  [Arguments("\"public\"")]
  public async Task OtherLiveVersions_EmptyOrQuotedDefaultSchema_FindsTheOtherReleaseAsync(string schema, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using (var insert = new NpgsqlCommand(@"
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at, metadata)
        VALUES (@id, 'fleet-svc', 'fleet-host', 1, NOW(), NOW(), '{""Version"":""9.9.9""}'::jsonb);", conn)) {
      insert.Parameters.AddWithValue("id", Guid.NewGuid());
      await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    var others = await FleetVersions.OtherLiveVersionsAsync(
      conn, schema, Guid.NewGuid(), "1.0.0", TimeSpan.FromMinutes(5), cancellationToken);

    await Assert.That(others.Count).IsEqualTo(1);
    await Assert.That(others[0]).IsEqualTo("9.9.9");
  }

  [Test]
  [Timeout(60000)]
  [Arguments("")]
  [Arguments("\"public\"")]
  public async Task CanElect_EmptyOrQuotedDefaultSchema_SeesTheMigratedElectionObjectsAsync(string schema, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    var canElect = await SchemaBootstrapPhase.CanElectAsync(conn, schema, cancellationToken);

    await Assert.That(canElect).IsTrue()
      .Because("the test database is fully migrated in the default schema");
  }

  [Test]
  [Timeout(60000)]
  [Arguments("")]
  [Arguments("\"public\"")]
  public async Task RegisterInstance_EmptyOrQuotedDefaultSchema_RegistersInTheDefaultSchemaAsync(string schema, CancellationToken cancellationToken) {
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "register-svc", "register-host", processId: 7);

    await SchemaBootstrapPhase.RegisterInstanceAsync(
      () => new NpgsqlConnection(ConnectionString), schema, instance, cancellationToken: cancellationToken);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM public.wh_service_instances WHERE instance_id = @id", conn);
    cmd.Parameters.AddWithValue("id", instance.InstanceId);
    await Assert.That((long)(await cmd.ExecuteScalarAsync(cancellationToken))!).IsEqualTo(1L);
  }

  // A ring function that answers NULL (nothing was queued, in a variant that reports it that way)
  // means zero notifications sent, not a failed ring (-1) and not an exception.
  [Test]
  [Timeout(60000)]
  public async Task Ring_WhenTheFunctionAnswersNull_ReportsZeroSentAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using (var ddl = new NpgsqlCommand(
        "CREATE OR REPLACE FUNCTION branch_ring_null() RETURNS integer LANGUAGE sql AS 'SELECT NULL::integer';", conn)) {
      await ddl.ExecuteNonQueryAsync(cancellationToken);
    }

    var sent = await DoorbellRinger.RingAsync(conn, "branch_ring_null", NullLogger.Instance, cancellationToken);

    await Assert.That(sent).IsEqualTo(0);
  }
}
