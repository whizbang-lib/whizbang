// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Data.EFCore.Postgres.Dispatch;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The maintenance step reconciles between starts, so a drop a start held back lands without another deploy;
/// once per fleet per claim window, and only for a context that registered its manifest.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects#every-later-start</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class ManagedSchemaReconcileStepTests {
  private const string TABLE = "wh_per_document_index_opted_out";
  private const string RETIRED = "idx_document_index_opted_out_data_gin";
  private static readonly DateTimeOffset _now = new(2026, 10, 9, 12, 5, 0, TimeSpan.Zero);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("mgd_step");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await using var context = _context();
    await context.EnsureWhizbangDatabaseInitializedAsync();
    // What an earlier release left, recorded as pending retirement by the start after it appeared.
    await _execAsync($"CREATE INDEX {RETIRED} ON {TABLE} USING gin (data)");
    await context.EnsureWhizbangDatabaseInitializedAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private DocumentIndexesDbContext _context() => new(
    new DbContextOptionsBuilder<DocumentIndexesDbContext>()
      .UseNpgsql(_connectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<bool> _existsAsync(string index) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand("SELECT to_regclass('public.' || $1) IS NOT NULL", db);
    command.Parameters.AddWithValue(index);
    return await command.ExecuteScalarAsync() is true;
  }

  private async Task _runAsync(bool withManifest = true, bool withClaimStore = true, params (string Key, string Value)[] config) {
    await using var context = _context();
    var services = new ServiceCollection();
    services.AddSingleton(context);
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
      .AddInMemoryCollection(config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value))).Build());
    if (withManifest) {
      services.AddKeyedSingleton<ManagedSchemaManifest>(typeof(DocumentIndexesDbContext), new ManagedSchemaManifest(
        typeof(DocumentIndexesDbContext), "public", DocumentIndexesDbContextSchemaExtensions.GetManagedSchemaObjects));
    }
    if (withClaimStore) {
      services.AddSingleton<IClaimedEmissionStore>(new EFCoreClaimedEmissionStore(context));
    }
    await using var provider = services.BuildServiceProvider();
    await new ManagedSchemaReconcileStep(typeof(DocumentIndexesDbContext), timeProvider: new FakeTimeProvider(_now))
      .RunAsync(provider, CancellationToken.None);
  }

  [Test]
  public async Task ARun_DropsWhatAStartHeldBackAsync() {
    await _runAsync();

    await Assert.That(await _existsAsync(RETIRED)).IsFalse();
  }

  [Test]
  public async Task ASecondRunInTheSameWindow_ReconcilesNothingAsync() {
    await _execAsync($"SELECT public.wh_pin_object('{TABLE}', '{RETIRED}', 'held for the first run')");
    await _runAsync();
    await _execAsync($"SELECT public.wh_unpin_object('{TABLE}', '{RETIRED}')");

    await _runAsync();

    await Assert.That(await _existsAsync(RETIRED)).IsTrue()
      .Because("the window's claim is taken, so this run belongs to an instance that stands down");
  }

  [Test]
  public async Task WithoutAClaimStore_TheStepStandsDownAsync() {
    await _runAsync(withClaimStore: false);

    await Assert.That(await _existsAsync(RETIRED)).IsTrue();
  }

  [Test]
  public async Task AContextWithNoManifest_IsLeftAloneAsync() {
    await _runAsync(withManifest: false);

    await Assert.That(await _existsAsync(RETIRED)).IsTrue();
  }

  [Test]
  public async Task WithTheReconcileOff_TheStepDoesNothingAsync() {
    await _runAsync(config: ("Whizbang:Schema:Reconcile:Mode", "Off"));

    await Assert.That(await _existsAsync(RETIRED)).IsTrue();
  }

  [Test]
  public async Task TheStep_IsNamedForTheLog_AndNeedsItsContextTypeAsync() {
    await Assert.That(new ManagedSchemaReconcileStep(typeof(DocumentIndexesDbContext)).Name).IsEqualTo("managed-schema-objects");
    await Assert.That(() => new ManagedSchemaReconcileStep(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task WithoutAConnectionFactory_TheContextsOwnConnectionIsBorrowed_AndContributorsAndTheInstanceAreUsedAsync() {
    await using var context = _context();
    var instance = Guid.CreateVersion7();
    // This instance, live in the registry, so its declarations are kept for the rest of the fleet to read.
    await _execAsync($"INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id) VALUES ('{instance}', 'tests', 'localhost', 1)");
    var services = new ServiceCollection()
      .AddSingleton<IManagedSchemaObjectContributor>(new PinningContributor())
      .AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(new FixedInstance(instance))
      .BuildServiceProvider();
    var manifest = new ManagedSchemaManifest(
      typeof(DocumentIndexesDbContext), "public", DocumentIndexesDbContextSchemaExtensions.GetManagedSchemaObjects);

    var report = await ManagedSchemaReconcile.RunAsync(
      context, manifest, ManagedSchemaReconcile.Settings(services), connectionFactory: null, services, logger: null,
      CancellationToken.None);

    await Assert.That(report.Kept.Select(k => k.Name)).Contains(RETIRED)
      .Because("the contributor's pin keeps it");
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM wh_managed_object_declarations WHERE instance_id = '{instance}'"))
      .IsEqualTo("1");
  }

  [Test]
  public async Task WithNoServices_TheReconcileStillRunsAsync() {
    await using var context = _context();
    var manifest = new ManagedSchemaManifest(
      typeof(DocumentIndexesDbContext), "public", DocumentIndexesDbContextSchemaExtensions.GetManagedSchemaObjects);

    var report = await ManagedSchemaReconcile.RunAsync(
      context, manifest, ManagedSchemaReconcile.Settings(null), connectionFactory: null, services: null, logger: null,
      CancellationToken.None);

    await Assert.That(report.Dropped.Select(d => d.Name)).IsEquivalentTo([RETIRED]);
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }

  private sealed class PinningContributor : IManagedSchemaObjectContributor {
    public void Contribute(ManagedSchemaObjectSet objects) =>
      objects.Pin(TABLE, RETIRED, "a contributor keeps it", "test contributor");
  }

  private sealed class FixedInstance(Guid id) : Whizbang.Core.Observability.IServiceInstanceProvider {
    public Guid InstanceId => id;
    public string ServiceName => "managed-objects-tests";
    public string HostName => "localhost";
    public int ProcessId => 1;
    public Whizbang.Core.Observability.ServiceInstanceInfo ToInfo() => new() {
      InstanceId = id,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }
}
