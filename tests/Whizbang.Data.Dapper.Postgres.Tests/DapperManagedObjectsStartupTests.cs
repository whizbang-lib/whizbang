// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper driver's managed-object reconcile, run by the host: at start with this instance's id, the configured
/// settings and the host's logger, and between starts by a maintenance step once per fleet per window, the way the
/// EF Core driver runs it (#1253).
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperManagedSchemaReconcileStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/ManagedSchemaHostPass.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/FleetClaim.cs</code-under-test>
public class DapperManagedObjectsStartupTests {
  private const string TABLE = "wh_per_probe";
  private const string RETIRED = $"ix_{TABLE}_old";
  private const string ENTRY = $"""
    CREATE TABLE IF NOT EXISTS {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_{TABLE}_status ON {TABLE} ((data ->> 'Status'));
    """;

  private static readonly (string Table, string Kind, string Name, string DeclaredBy)[] _declared =
    [(TABLE, "index", $"ix_{TABLE}_status", "ProbeModel")];

  private PerTestDatabase _database;
  private readonly SettableClock _clock = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_mgd_st");
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  private ServiceCollection _services(ListLoggerProvider? logs = null, params (string Key, string Value)[] config) {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
      .AddInMemoryCollection(config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value))).Build());
    services.AddSingleton<TimeProvider>(_clock);
    if (logs is not null) {
      services.AddLogging(b => b.AddProvider(logs));
    }
    services.AddWhizbangPostgres(_database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), true,
      [new KeyValuePair<string, string>("ProbePerspective", ENTRY)], _declared);
    return services;
  }

  /// <summary>One start of an instance: the schema initializer runs to the open gate, then the instance stops.</summary>
  private static async Task _startOnceAsync(ServiceCollection services) {
    await using var provider = await SchemaStartup.StartAsync(services);
  }

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.ExecuteAsync(sql);
  }

  private async Task<bool> _existsAsync(string index) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    return await db.ExecuteScalarAsync<bool>("SELECT to_regclass('public.' || @index) IS NOT NULL", new { index });
  }

  private static async Task _runStepAsync(IServiceProvider provider) {
    await using var scope = provider.CreateAsyncScope();
    var step = scope.ServiceProvider.GetServices<IMaintenanceStep>().Single(s => s.Name == "managed-schema-objects");
    await step.RunAsync(scope.ServiceProvider, CancellationToken.None);
  }

  [Test]
  public async Task AtStart_TheReconcileRecordsWhatThisInstanceDeclares_UnderItsIdAsync() {
    await using var provider = await SchemaStartup.StartAsync(_services());

    var self = provider.GetRequiredService<IServiceInstanceProvider>().InstanceId;
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    var objects = await db.ExecuteScalarAsync<string[]>(
      "SELECT objects FROM wh_managed_object_declarations WHERE instance_id = @self", new { self });
    await Assert.That(objects).IsEquivalentTo([$"{TABLE}:ix_{TABLE}_status"])
      .Because("a peer's fleet gate reads this instance as reported only when its declarations carry its id");
  }

  [Test]
  public async Task AtStart_TheConfiguredModeIsHonoredAsync() {
    await _startOnceAsync(_services());
    await _execAsync($"CREATE INDEX {RETIRED} ON {TABLE} USING gin (data)");
    await _startOnceAsync(_services());

    await _startOnceAsync(_services(config: ("Whizbang:Schema:Reconcile:Mode", "ReportOnly")));

    await Assert.That(await _existsAsync(RETIRED)).IsTrue()
      .Because("ReportOnly reports the drop and makes none");
  }

  [Test]
  public async Task AtStart_ASettingThatIsNotOne_FailsTheStartAsync() {
    await Assert.That(async () => await SchemaStartup.StartAsync(_services(config: ("Whizbang:Schema:Reconcile:Mode", "Sideways"))))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task AtStart_TheReconcileReportsThroughTheHostsLoggerAsync() {
    await _startOnceAsync(_services());
    await _execAsync($"CREATE INDEX {RETIRED} ON {TABLE} USING gin (data)");
    await _startOnceAsync(_services());
    var logs = new ListLoggerProvider();

    await _startOnceAsync(_services(logs));

    await Assert.That(logs.Messages.Any(m => m.Contains($"dropped Index {RETIRED}", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task AtStart_AContributorsObjectIsDeclaredAndKeptAsync() {
    await _startOnceAsync(_services());
    await _execAsync($"CREATE INDEX {RETIRED} ON {TABLE} USING gin (data)");
    await _startOnceAsync(_services());
    var services = _services();
    services.AddSingleton<IManagedSchemaObjectContributor>(new Contributor());

    await _startOnceAsync(services);

    await Assert.That(await _existsAsync(RETIRED)).IsTrue();
  }

  [Test]
  public async Task TheMaintenanceStep_DropsWhatAStartHeldBack_OncePerWindow_WithNoClaimStoreAsync() {
    await using var provider = await SchemaStartup.StartAsync(_services());
    await _execAsync($"CREATE INDEX {RETIRED} ON {TABLE} USING gin (data)");
    await _runStepAsync(provider);   // first sighting: recorded, nothing dropped
    await Assert.That(await _existsAsync(RETIRED)).IsTrue();

    await _runStepAsync(provider);   // same window: the claim is taken
    await Assert.That(await _existsAsync(RETIRED)).IsTrue();

    _clock.Advance(ManagedSchemaHostPass.ClaimWindow);
    await _runStepAsync(provider);

    await Assert.That(await _existsAsync(RETIRED)).IsFalse();
  }

  [Test]
  public async Task TheMaintenanceStep_WithTheReconcileOff_DoesNothingAsync() {
    var services = _services(config: ("Whizbang:Schema:Reconcile:Mode", "Off"));
    await using var provider = await SchemaStartup.StartAsync(services);

    await _runStepAsync(provider);

    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await Assert.That(await db.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_unique_emission_claims")).IsEqualTo(0L);
  }

  [Test]
  public async Task WithoutDeclarations_NoStepIsRegisteredAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPostgres(_database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), true,
      [new KeyValuePair<string, string>("ProbePerspective", ENTRY)], managedObjects: null);
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    await Assert.That(scope.ServiceProvider.GetServices<IMaintenanceStep>().Any(s => s.Name == "managed-schema-objects")).IsFalse();
  }

  /// <summary>A clock the test moves; nothing here waits on time, so no timers are needed.</summary>
  private sealed class SettableClock(DateTimeOffset now) : TimeProvider {
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
  }

  private sealed class Contributor : IManagedSchemaObjectContributor {
    public void Contribute(ManagedSchemaObjectSet objects) => objects.Index(TABLE, RETIRED, "a contributor");
  }

  private sealed class ListLoggerProvider : ILoggerProvider {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages {
      get {
        lock (_lock) {
          return [.. _messages];
        }
      }
    }

    public ILogger CreateLogger(string categoryName) => new ListLogger(this);
    public void Dispose() { }

    private sealed class ListLogger(ListLoggerProvider owner) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) {
        lock (owner._lock) {
          owner._messages.Add(formatter(state, exception));
        }
      }
    }
  }
}
