// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper driver initializes its schema the way the EF Core driver does: registration only records what to
/// initialize, and the shared schema initializer does the work at host start, under the schema lock, registers this
/// instance, and opens the schema-ready gate.
/// </summary>
/// <docs>data/turnkey-initialization</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperSchemaInitializationRunner.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaInitializationLock.cs</code-under-test>
public class DapperSchemaStartupTests {
  private const string TABLE = "wh_per_startup_probe";
  private static readonly KeyValuePair<string, string>[] _entries = [
    new("StartupProbe", $"CREATE TABLE IF NOT EXISTS {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL);"),
  ];

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_startup");
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  private IServiceCollection _services(bool initializeSchema = true, string? connectionString = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    services.AddWhizbangPostgres(
      connectionString ?? _database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema, _entries,
      managedObjects: null);
    return services;
  }

  private static IHostedService _initializer(IServiceProvider provider) =>
    provider.GetServices<IHostedService>().OfType<WhizbangDatabaseInitializerService>().Single();

  private async Task<bool> _tableExistsAsync(string table) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    return await db.ExecuteScalarAsync<bool>("SELECT to_regclass(@t) IS NOT NULL", new { t = $"public.{table}" });
  }

  [Test]
  public async Task Registration_DoesNotTouchTheDatabaseAsync() {
    // A database that does not exist: any connection attempt at registration fails at once.
    var missing = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = $"{_database.Name}_absent" };

    var services = _services(connectionString: missing.ConnectionString);

    await Assert.That(services.Any(d => d.ServiceType == typeof(ISchemaInitializationRunner))).IsTrue()
      .Because("registration records what to initialize; the initializer does it at host start");
  }

  [Test]
  public async Task HostStart_MigratesTheSchema_ThenOpensTheGateAsync() {
    await using var provider = _services().BuildServiceProvider();
    var gate = provider.GetRequiredService<ISchemaReadyGate>();
    await Assert.That(await _tableExistsAsync("wh_event_store")).IsFalse();

    await _initializer(provider).StartAsync(CancellationToken.None);

    await Assert.That(gate.IsReady).IsTrue();
    await Assert.That(await _tableExistsAsync("wh_event_store")).IsTrue();
    await Assert.That(await _tableExistsAsync(TABLE)).IsTrue();
  }

  [Test]
  public async Task HostStart_RegistersThisInstanceAsync() {
    await using var provider = _services().BuildServiceProvider();

    await _initializer(provider).StartAsync(CancellationToken.None);

    var self = provider.GetRequiredService<IServiceInstanceProvider>().InstanceId;
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await db.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_service_instances WHERE instance_id = @self", new { self })).IsEqualTo(1L);
  }

  [Test]
  public async Task Registration_RegistersAnInstanceIdentity_AndKeepsOneAlreadyRegisteredAsync() {
    await using (var provider = _services().BuildServiceProvider()) {
      await Assert.That(provider.GetService<IServiceInstanceProvider>()).IsNotNull();
    }

    var mine = new FixedInstance();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(mine);
    services.AddWhizbangPostgres(_database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), true, _entries,
      managedObjects: null);
    await using var withMine = services.BuildServiceProvider();
    await Assert.That(withMine.GetRequiredService<IServiceInstanceProvider>()).IsSameReferenceAs(mine);
  }

  [Test]
  public async Task HostStart_WithoutSchemaInitialization_OpensTheGateAndMigratesNothingAsync() {
    await using var provider = _services(initializeSchema: false).BuildServiceProvider();

    await _initializer(provider).StartAsync(CancellationToken.None);

    await Assert.That(provider.GetRequiredService<ISchemaReadyGate>().IsReady).IsTrue()
      .Because("a schema provisioned out of band has nothing to wait for");
    await Assert.That(await _tableExistsAsync("wh_event_store")).IsFalse();
  }

  [Test]
  public async Task HostStart_WaitsForTheSchemaLock_WhileAnotherSessionHoldsItAsync() {
    var observer = new LockObserver();
    var services = _services();
    services.AddSingleton<ISchemaInitializationObserver>(observer);
    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<ISchemaReadyGate>();

    await using var holder = new NpgsqlConnection(_database.ConnectionString);
    await holder.OpenAsync();
    await using var held = await holder.BeginTransactionAsync();
    await holder.ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", new { key = SchemaInitializationLockKey.Compute("public") }, held);

    var start = _initializer(provider).StartAsync(CancellationToken.None);
    var first = await Task.WhenAny(observer.Contended.Task, start);

    await Assert.That(first).IsSameReferenceAs(observer.Contended.Task)
      .Because("another session holds the schema lock, so this start reports it and waits instead of migrating");
    await Assert.That(gate.IsReady).IsFalse();
    await Assert.That(await observer.Contended.Task).IsEqualTo("public");

    await held.CommitAsync();
    await start;

    await Assert.That(gate.IsReady).IsTrue();
    await Assert.That(await _tableExistsAsync("wh_event_store")).IsTrue();
  }

  private sealed class LockObserver : ISchemaInitializationObserver {
    public TaskCompletionSource<string> Contended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask OnSchemaLockContendedAsync(string schema, CancellationToken cancellationToken) {
      Contended.TrySetResult(schema);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FixedInstance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "startup-probe";
    public string HostName => "startup-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }
}
