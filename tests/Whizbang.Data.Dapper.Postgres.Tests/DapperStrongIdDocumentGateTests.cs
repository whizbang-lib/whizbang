// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper driver asks the strong-id document gate as the Entity Framework driver does (#2518): at host start and
/// each maintenance cycle. Asking opens the process's write form once every live instance reads the UUID string, and
/// the heartbeat then says this instance writes it, so a schema with Entity Framework and Dapper instances reaches the
/// stored-form rewrite rather than holding it for the Dapper ones.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/StrongIdDocumentGateStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperSchemaInitializationRunner.cs</code-under-test>
[Category("Integration")]
public class DapperStrongIdDocumentGateTests {
  private const string WRITES = """{"stored_form_level":2,"strong_id_document_form":"uuid-string"}""";

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_sid_gate");
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  private ServiceProvider _provider() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    services.AddWhizbangPostgres(
      _database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: true,
      perspectiveEntries: [], managedObjects: null);
    return services.BuildServiceProvider();
  }

  private static async Task _startAsync(ServiceProvider provider) =>
    await provider.GetServices<IHostedService>().OfType<WhizbangDatabaseInitializerService>().Single()
      .StartAsync(CancellationToken.None);

  private async Task<string?> _rewriteHoldAsync() {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    return await StrongIdDocumentGate.RewriteHoldAsync(connection, "public");
  }

  /// <summary>Host start asks the gate, so the process knows the schema from its first write.</summary>
  [Test]
  public async Task HostStart_AsksTheGate_SoTheProcessKnowsTheSchemaAsync() {
    await using var provider = _provider();

    await _startAsync(provider);

    var known = StrongIdDocumentForms.IsOpen(_database.Name, "public") || StrongIdDocumentForms.IsHeld(_database.Name, "public");
    await Assert.That(known).IsTrue();
  }

  /// <summary>
  /// A schema with an Entity Framework instance and a Dapper instance: once the Dapper driver's step asks the gate and
  /// its heartbeat says it writes the UUID string, the rewrite runs. Before, the Dapper instance never asked, never said
  /// so, and held the rewrite for as long as it was live.
  /// </summary>
  [Test]
  public async Task AMixedFleet_ReachesTheRewrite_OnceTheDapperInstanceAsksTheGateAsync() {
    await using var provider = _provider();
    await _startAsync(provider);
    var self = provider.GetRequiredService<IServiceInstanceProvider>().InstanceId;
    await using (var db = new NpgsqlConnection(_database.ConnectionString)) {
      await db.ExecuteAsync("SELECT record_heartbeat(@i, 'ef-svc', 'ef-host', 1, @m::jsonb, NULL, '0.4522.0')",
        new { i = Guid.CreateVersion7(), m = WRITES });
    }
    using var scope = provider.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var step = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<StrongIdDocumentGateStep>().Single();
    var heartbeat = new HeartbeatRequest(self, "dapper-svc", "dapper-host", 2);

    // The Dapper instance as it beats before its form is open: it reads the UUID string but does not yet write it.
    using (StrongIdDocumentForms.ScopeTo(uuidString: false)) {
      await coordinator.RecordHeartbeatAsync(heartbeat);
    }
    var answer = await step.CheckAsync(scope.ServiceProvider, CancellationToken.None);
    var heldForDapper = await _rewriteHoldAsync();
    // Its form is open now, so its heartbeat says it writes the UUID string.
    using (StrongIdDocumentForms.ScopeTo(uuidString: true)) {
      await coordinator.RecordHeartbeatAsync(heartbeat);
    }
    var released = await _rewriteHoldAsync();

    await Assert.That(answer.State).IsEqualTo(StrongIdDocumentGateState.Open);
    await Assert.That(StrongIdDocumentForms.IsOpen(_database.Name, "public")).IsTrue()
      .Because("the step opened the process's write form for this schema");
    await Assert.That(heldForDapper).IsEqualTo("strong-id document gate open (1 of 2 live instances write strong ids as UUID strings)");
    await Assert.That(released).IsNull().Because("every live instance, the Dapper one included, writes the UUID string");
  }
}
