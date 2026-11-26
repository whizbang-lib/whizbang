// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The gate and the writers key a schema on the name the server gives its database, not the name a connection string
/// asks for (#2518). Behind a pooler the writers' connection string names an alias, and a coordinator can use a
/// connection string of its own; keyed on those names the writer would never see the gate open, keep writing the object
/// form, say so in its heartbeat, and the stored-form rewrite would never start. Here the gate is asked on a direct
/// connection and the writers go through a pgbouncer alias of the same database.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresServerDatabase.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/StrongIdDocumentTarget.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/ServerDatabaseNameInterceptor.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StrongIdPooledDatabaseNameTests : EFCoreTestBase {
  /// <summary>The pinned pooler image, listed in <c>.github/ci-images.txt</c>.</summary>
  private const string PGBOUNCER_IMAGE = "edoburu/pgbouncer:v1.26.1-p0";

  private const int PGBOUNCER_PORT = 6432;

  [Test]
  public async Task AWriterThroughAPoolerAlias_SeesTheGateOpenedOnADirectConnection_AndSaysSoInItsHeartbeatAsync() {
    await using var pooler = await _startPoolerAsync("orders_alias");
    var aliased = _aliasedConnectionString(pooler, "orders_alias");
    await using (var direct = new NpgsqlConnection(ConnectionString)) {
      await direct.OpenAsync();
      await _beatAsync(direct, """{"stored_form_level":2}""");
      await Assert.That((await StrongIdDocumentGate.CheckAsync(direct, "public")).State)
        .IsEqualTo(StrongIdDocumentGateState.Open);
    }
    var (dataSource, options) = CreateContextOptions(aliased);
    await using var _ = dataSource;
    var streamId = (Guid)TrackedGuid.New();
    var instance = (Guid)TrackedGuid.New();

    await using (var context = new WorkCoordinationDbContext(options)) {
      var store = new EFCorePostgresPerspectiveStore<Order>(context, "wh_per_order", new PostgresUpsertStrategy());
      await store.UpsertAsync(streamId, new Order { OrderId = new TestOrderId(streamId), Amount = 5m, Status = "Created" });
    }
    await using (var context = new WorkCoordinationDbContext(options)) {
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        context, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
      await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instance, "svc", "pooled", 1));
    }

    await Assert.That(new NpgsqlConnectionStringBuilder(aliased).Database).IsEqualTo("orders_alias");
    await Assert.That(await _scalarAsync("SELECT data::text FROM wh_per_order WHERE id = @row", streamId))
      .Contains($"\"OrderId\": \"{streamId}\"")
      .Because("the gate is open for the database the alias reaches, whatever the alias is called");
    await Assert.That(await _scalarAsync("SELECT metadata::text FROM wh_service_instances WHERE instance_id = @row", instance))
      .Contains("\"strong_id_document_form\": \"uuid-string\"")
      .Because("the heartbeat says what this process writes, so the stored-form rewrite can start");
  }

  /// <summary>
  /// Starts pgbouncer with one database, <paramref name="alias"/>, that reaches this test's database on the shared
  /// PostgreSQL server under its own name.
  /// </summary>
  private async Task<IContainer> _startPoolerAsync(string alias) {
    var server = new NpgsqlConnectionStringBuilder(ConnectionString);
    var ini = $"""
      [databases]
      {alias} = host=host.docker.internal port={server.Port} dbname={server.Database}

      [pgbouncer]
      listen_addr = 0.0.0.0
      listen_port = {PGBOUNCER_PORT}
      auth_type = plain
      auth_file = /etc/pgbouncer/userlist.txt
      pool_mode = session
      ignore_startup_parameters = extra_float_digits,search_path
      """;
    var users = $"\"{server.Username}\" \"{server.Password}\"\n";
    var container = new ContainerBuilder(PGBOUNCER_IMAGE)
      .WithExtraHost("host.docker.internal", "host-gateway")
      .WithResourceMapping(Encoding.UTF8.GetBytes(ini), "/etc/pgbouncer/pgbouncer.ini")
      .WithResourceMapping(Encoding.UTF8.GetBytes(users), "/etc/pgbouncer/userlist.txt")
      .WithPortBinding(PGBOUNCER_PORT, assignRandomHostPort: true)
      .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(PGBOUNCER_PORT))
      .Build();
    await container.StartAsync();
    return container;
  }

  private string _aliasedConnectionString(IContainer pooler, string alias) =>
    new NpgsqlConnectionStringBuilder(ConnectionString) {
      Host = pooler.Hostname,
      Port = pooler.GetMappedPublicPort(PGBOUNCER_PORT),
      Database = alias,
      Pooling = false,
    }.ConnectionString;

  /// <summary>A live instance of this build, which reads the UUID string.</summary>
  private static async Task _beatAsync(NpgsqlConnection conn, string metadata) {
    await using var cmd = new NpgsqlCommand("SELECT record_heartbeat(@i, 'svc', 'host', 2, @m::jsonb, NULL, '1.0.0')", conn);
    cmd.Parameters.AddWithValue("i", (Guid)TrackedGuid.New());
    cmd.Parameters.AddWithValue("m", metadata);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<string?> _scalarAsync(string sql, Guid row) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue(nameof(row), row);
    return (await cmd.ExecuteScalarAsync())?.ToString();
  }
}
