// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
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
/// The Dapper driver and the strong-id document gate (#2518). The Dapper store writes a strongly-typed id as its UUID
/// string whatever the gate says, and its heartbeat says so from the first beat, so a schema with Entity Framework and
/// Dapper instances reaches the stored-form rewrite rather than holding it for the Dapper ones. The driver also asks
/// the gate at host start and each maintenance cycle, as the Entity Framework driver does, which latches the gate,
/// opens the process's form for any Entity Framework writer it hosts, and reports instances that cannot read it.
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

  private ServiceProvider _provider(JsonSerializerOptions? jsonOptions = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    services.AddWhizbangPostgres(
      _database.ConnectionString, jsonOptions ?? JsonContextRegistry.CreateCombinedOptions(), initializeSchema: true,
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

  private async Task<string?> _metadataAsync(Guid instance) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    return await connection.ExecuteScalarAsync<string?>(
      "SELECT metadata::text FROM wh_service_instances WHERE instance_id = @instance", new { instance });
  }

  /// <summary>
  /// The Dapper store writes a strongly-typed id as its UUID string whatever the gate says, so its heartbeat says so from
  /// its first beat, before anything has asked the gate, with no form forced.
  /// </summary>
  [Test]
  public async Task TheDapperHeartbeat_SaysItWritesTheUuidString_FromItsFirstBeatAsync() {
    await using var provider = _provider();
    var self = provider.GetRequiredService<IServiceInstanceProvider>().InstanceId;
    using var scope = provider.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await _startAsync(provider);
    StrongIdDocumentForms.Hold(_database.Name, "public");

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(self, "dapper-svc", "dapper-host", 2));

    await Assert.That(await _metadataAsync(self)).Contains("\"strong_id_document_form\": \"uuid-string\"")
      .Because("the Dapper store writes the UUID string even while this process holds the schema for its EF writers");
  }

  /// <summary>
  /// A Dapper store built with an application's own serializer options, without Whizbang's id converters, writes a
  /// strongly-typed id as the object <c>{"Value": ...}</c>, so its heartbeat does not say it writes the UUID string, and the
  /// stored-form rewrite waits for it (#2518).
  /// </summary>
  [Test]
  public async Task TheDapperHeartbeat_WithAnApplicationsOwnOptions_SaysWhatThoseOptionsWriteAsync() {
    var applications = new JsonSerializerOptions(JsonContextRegistry.CreateCombinedOptions());
    applications.Converters.Clear();
    applications.TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
    await Assert.That(JsonSerializer.Serialize(DapperProbeId.New(), applications)).StartsWith("{");
    await using var provider = _provider(applications);
    var self = provider.GetRequiredService<IServiceInstanceProvider>().InstanceId;
    using var scope = provider.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await _startAsync(provider);

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(self, "dapper-svc", "dapper-host", 2));

    await Assert.That(await _metadataAsync(self)).DoesNotContain("strong_id_document_form")
      .Because("these options write the object form, so the heartbeat must not let the rewrite start");
  }

  /// <summary>
  /// A schema with an Entity Framework instance and a Dapper instance reaches the rewrite, driven through the real Dapper
  /// coordinator and the real maintenance step: the Dapper heartbeat says it writes the UUID string, and the step's
  /// check, which every instance in the fleet reads, opens the gate.
  /// </summary>
  [Test]
  public async Task AMixedFleet_ReachesTheRewrite_ThroughTheDapperCoordinatorAndStepAsync() {
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

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(self, "dapper-svc", "dapper-host", 2));
    var answer = await step.CheckAsync(scope.ServiceProvider, CancellationToken.None);
    var hold = await _rewriteHoldAsync();

    await Assert.That(answer.State).IsEqualTo(StrongIdDocumentGateState.Open).Because("the check that latches the gate answers open");
    await Assert.That(StrongIdDocumentForms.IsOpen(_database.Name, "public")).IsTrue()
      .Because("the step opened the process's write form for this schema");
    await Assert.That(hold).IsNull().Because("every live instance, the Dapper one included, writes the UUID string");
  }
}

/// <summary>A strongly-typed id registered in this test assembly, so the Dapper heartbeat has one to ask about (#2518).</summary>
[Whizbang.Core.WhizbangId]
public readonly partial struct DapperProbeId;
