// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.StartupParity;

/// <summary>
/// The startup parity suite: one set of startup scenarios, each run against both Postgres drivers, so "Dapper and
/// EF Core start the same way" is a test rather than a promise. Each scenario composes the driver the way an
/// application does and starts the shared schema initializer; every wait is on a signal (the schema-ready gate, an
/// <see cref="ISchemaInitializationObserver"/> notification, or the initializer's own completion), never on time.
/// </summary>
/// <docs>data/turnkey-initialization#both-drivers</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/WhizbangDatabaseInitializerService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaInitializationLock.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/ISchemaInitializationObserver.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/MigratorDutyStaging.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StartupParityTests {
  private PerTestDatabase _database;
  private readonly List<ServiceProvider> _instances = [];

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("parity");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    foreach (var instance in _instances) {
      await instance.DisposeAsync();
    }
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  /// <summary>Composes one instance of a service on <paramref name="driver"/>; nothing runs until it is started.</summary>
  private ServiceProvider _compose(
      StartupDriver driver, bool blocking = true, ISchemaInitializationObserver? observer = null,
      string? connectionString = null, Action<IServiceCollection>? configure = null, params (string Key, string Value)[] config) {
    var services = new ServiceCollection();
    services.AddLogging();
    // #2126: an exit in a test keeps the test process's own exit code.
    services.AddSingleton<Whizbang.Core.RunControl.IProcessExitCode>(new DiscardedExitCode());
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
      .AddInMemoryCollection(config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value))).Build());
    services.Configure<SchemaInitializationOptions>(o => {
      o.NonBlockingSchemaInit = !blocking;
      o.InitRetryDelay = TimeSpan.Zero;
    });
    if (observer is not null) {
      services.AddSingleton(observer);
    }
    driver.Register(services, connectionString ?? _database.ConnectionString);
    configure?.Invoke(services);
    var provider = services.BuildServiceProvider();
    _instances.Add(provider);
    return provider;
  }

  private sealed class DiscardedExitCode : Whizbang.Core.RunControl.IProcessExitCode {
    public void SetExitCode(int exitCode) { }
  }

  private static WhizbangDatabaseInitializerService _initializer(IServiceProvider instance) =>
    instance.GetRequiredService<WhizbangDatabaseInitializerService>();

  /// <summary>Starts one instance's schema initialization inline: returns once its gate is open.</summary>
  private async Task<ServiceProvider> _startAsync(StartupDriver driver, params (string Key, string Value)[] config) {
    var instance = _compose(driver, config: config);
    await _initializer(instance).StartAsync(CancellationToken.None);
    return instance;
  }

  private async Task<T> _scalarAsync<T>(string sql, params object[] parameters) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    foreach (var parameter in parameters) {
      command.Parameters.AddWithValue(parameter);
    }
    return (T)(await command.ExecuteScalarAsync())!;
  }

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private Task<bool> _existsAsync(string index) => _scalarAsync<bool>("SELECT to_regclass('public.' || $1) IS NOT NULL", index);

  /// <summary>An index Whizbang built that the driver no longer declares, recorded by one start after it appeared.</summary>
  private async Task _retiredIndexSeenAsync(StartupDriver driver) {
    await _execAsync($"CREATE INDEX {driver.Retired} ON {driver.Table} USING gin (data)");
    await _startAsync(driver);
    await Assert.That(await _existsAsync(driver.Retired)).IsTrue()
      .Because("the start that first sees an object records it and drops nothing");
  }

  private static async Task _runReconcileStepAsync(IServiceProvider instance) {
    await using var scope = instance.CreateAsyncScope();
    var step = scope.ServiceProvider.GetServices<IMaintenanceStep>().Single(s => s.Name == "managed-schema-objects");
    await step.RunAsync(scope.ServiceProvider, CancellationToken.None);
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task TheGate_OpensOnceTheSchemaIsMigrated_AndNotBeforeAsync(string driverName) {
    var driver = StartupDriver.For(driverName);
    var instance = _compose(driver);
    var gate = instance.GetRequiredService<ISchemaReadyGate>();
    await Assert.That(gate.IsReady).IsFalse();

    await _initializer(instance).StartAsync(CancellationToken.None);

    await Assert.That(gate.IsReady).IsTrue();
    await Assert.That(await _scalarAsync<bool>($"SELECT to_regclass('public.{driver.Table}') IS NOT NULL")).IsTrue();
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task EveryStartingInstance_RegistersItselfAsync(string driverName) {
    var instance = await _startAsync(StartupDriver.For(driverName));

    var self = instance.GetRequiredService<IServiceInstanceProvider>().InstanceId;
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_service_instances WHERE instance_id = $1", self))
      .IsEqualTo(1L).Because("the instances that start after it see it running, so its declarations count");
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task WithSeveralInstancesRunning_ARetiredObjectIsDroppedAsync(string driverName) {
    var driver = StartupDriver.For(driverName);
    var first = await _startAsync(driver);
    await _retiredIndexSeenAsync(driver);

    await _startAsync(driver);

    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_service_instances")).IsGreaterThanOrEqualTo(3L)
      .Because("every earlier instance is still running");
    var firstDeclared = await _scalarAsync<long>(
      "SELECT count(*) FROM wh_managed_object_declarations WHERE instance_id = $1",
      first.GetRequiredService<IServiceInstanceProvider>().InstanceId);
    await Assert.That(firstDeclared).IsEqualTo(1L).Because("each running instance has reported what it declares");
    await Assert.That(await _existsAsync(driver.Retired)).IsFalse()
      .Because("no running instance declares it, so the fleet gate lets the drop through (#1253)");
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task TheFleetGate_HoldsTheDropWhileALivePeerDeclaresIt_AndThePeriodicReRunDropsItOnceThePeerStopsAsync(string driverName) {
    var driver = StartupDriver.For(driverName);
    await _startAsync(driver);
    await _retiredIndexSeenAsync(driver);
    // An instance of the previous release, still running, which declares the index this release retired.
    var previous = Guid.CreateVersion7();
    await _execAsync($"""
      SELECT register_instance_heartbeat('{previous}', 'parity', 'parity-host', 1, NULL::jsonb, now(), now() + interval '60 seconds');
      INSERT INTO wh_managed_object_declarations (instance_id, objects, reported_at)
        VALUES ('{previous}', ARRAY['{driver.Table}:{driver.Retired}'], now());
      """);

    var current = await _startAsync(driver);

    await Assert.That(await _existsAsync(driver.Retired)).IsTrue()
      .Because("the previous release still runs and still declares it");

    await _execAsync($"UPDATE wh_service_instances SET last_heartbeat_at = now() - interval '1 hour' WHERE instance_id = '{previous}'");
    await _runReconcileStepAsync(current);

    await Assert.That(await _existsAsync(driver.Retired)).IsFalse()
      .Because("the periodic re-run finds the previous release gone and drops what nothing declares");
  }

  /// <summary>Marks the driver's table as one that declares row retention, as the retention sync does.</summary>
  private Task _enrollAsync(StartupDriver driver) =>
    _execAsync($$"""
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name, row_retention_enrolled)
      VALUES ('Parity.RetentionProbe', '{{driver.Table}}', '{}'::jsonb, 'parity-hash', 'parity-retention', TRUE)
      """);

  private Task<long> _updatedAtIndexCountAsync(StartupDriver driver) =>
    _scalarAsync<long>(
      "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = $1 AND indexdef LIKE '%USING btree (updated_at)'",
      driver.Table);

  /// <summary>
  /// #2087: a table that neither declares retention nor declares the index loses the <c>updated_at</c> index an
  /// earlier release built, on both drivers, so its updates can be HOT.
  /// </summary>
  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task TheUpdatedAtIndex_IsRetiredFromATableWithoutRetentionAsync(string driverName) {
    var driver = StartupDriver.For(driverName);
    await _startAsync(driver);
    await _execAsync($"CREATE INDEX IF NOT EXISTS {driver.UpdatedAtIndex} ON {driver.Table} (updated_at)");

    await _startAsync(driver);
    await _startAsync(driver);

    await Assert.That(await _existsAsync(driver.UpdatedAtIndex)).IsFalse()
      .Because("nothing asks for it: no retention and no [PerspectiveIndex(PerspectiveRowColumns.UpdatedAt)]");
    await Assert.That(await _updatedAtIndexCountAsync(driver)).IsEqualTo(0L);
  }

  /// <summary>#2087: a table that declares retention gets the index on both drivers, once, and keeps it.</summary>
  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task TheUpdatedAtIndex_IsBuiltAndKeptForATableThatDeclaresRetentionAsync(string driverName) {
    var driver = StartupDriver.For(driverName);
    await _startAsync(driver);
    await _enrollAsync(driver);

    await _startAsync(driver);
    await Assert.That(await _existsAsync(driver.UpdatedAtIndex)).IsTrue()
      .Because("the reapers range-scan updated_at on an enrolled table");

    await _startAsync(driver);
    await _startAsync(driver);
    await Assert.That(await _updatedAtIndexCountAsync(driver)).IsEqualTo(1L).Because("later starts neither drop nor duplicate it");
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task TheReconcileSettings_AreHonoredAsync(string driverName) {
    var driver = StartupDriver.For(driverName);
    await _startAsync(driver);
    await _retiredIndexSeenAsync(driver);

    await _startAsync(driver, ("Whizbang:Schema:Reconcile:Mode", "ReportOnly"));
    await Assert.That(await _existsAsync(driver.Retired)).IsTrue().Because("ReportOnly drops nothing");

    await _startAsync(driver, ("Whizbang:Schema:Reconcile:Drop:Index", "false"));
    await Assert.That(await _existsAsync(driver.Retired)).IsTrue().Because("index drops are switched off");

    // #2126: a setting that is not one fails the start rather than being ignored: the instance exits through the one exit
    // path as a startup failure (exit code 4), rather than throwing out of host start as an unhandled crash.
    var refused = await _startAsync(driver, ("Whizbang:Schema:Reconcile:Mode", "Sideways"));
    var exit = refused.GetRequiredService<Whizbang.Core.RunControl.IWhizbangInstanceExit>();
    await exit.Completion;
    await Assert.That(exit.Cause).IsEqualTo(Whizbang.Core.RunControl.ExitCause.StartupFailure);
    await Assert.That(exit.Report!.Record.ExceptionType).IsEqualTo("System.InvalidOperationException");

    await _startAsync(driver);
    await Assert.That(await _existsAsync(driver.Retired)).IsFalse();
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task ConcurrentStarts_AreSerializedByTheSchemaLockAsync(string driverName) {
    var driver = StartupDriver.For(driverName);
    var observer = new ParityObserver();
    var instance = _compose(driver, observer: observer);
    var gate = instance.GetRequiredService<ISchemaReadyGate>();
    // Another instance mid-migration: it holds the schema initialization lock.
    await using var migrating = new NpgsqlConnection(_database.ConnectionString);
    await migrating.OpenAsync();
    await using var held = await migrating.BeginTransactionAsync();
    await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", migrating, held)) {
      take.Parameters.AddWithValue(SchemaInitializationLockKey.Compute("public"));
      await take.ExecuteNonQueryAsync();
    }

    var start = _initializer(instance).StartAsync(CancellationToken.None);
    var first = await Task.WhenAny(observer.LockContended.Task, start);

    await Assert.That(ReferenceEquals(first, observer.LockContended.Task)).IsTrue()
      .Because("this start found the lock held and waits instead of migrating alongside");
    await Assert.That(gate.IsReady).IsFalse();

    await held.CommitAsync();
    await start;

    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task ADatabaseThatComesUpLate_IsWaitedFor_ThenTheGateOpensAsync(string driverName) {
    var driver = StartupDriver.For(driverName);
    var late = await PerTestDatabaseFactory.CreateAsync("parity_late");
    await PerTestDatabaseFactory.DropAsync(late.Name);
    var observer = new ParityObserver(holdFirstFailure: true);
    var instance = _compose(driver, blocking: false, observer: observer, connectionString: late.ConnectionString);
    var gate = instance.GetRequiredService<ISchemaReadyGate>();
    try {
      await _initializer(instance).StartAsync(CancellationToken.None);
      await observer.AttemptFailed.Task;
      await Assert.That(gate.IsReady).IsFalse().Because("the database is not there, so nothing is migrated");

      // Under the factory's retry: other tests create databases at the same time, and a create that loses the race
      // for the template fails outright (55006).
      await PerTestDatabaseFactory.RunWithRetryAsync(
        async () => {
          await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
          await admin.OpenAsync();
          await using var create = new NpgsqlCommand($"CREATE DATABASE {late.Name}", admin);
          await create.ExecuteNonQueryAsync();
        },
        PerTestDatabaseFactory.IsAlreadyCreated,
        TimeProvider.System, // review-checks:allow: only the contention backoff every per-test database create uses; nothing here waits on it
        CancellationToken.None);
      observer.ReleaseFailure();

      await gate.WaitForReadyAsync(CancellationToken.None);
      await Assert.That(gate.IsReady).IsTrue();
    } finally {
      await _initializer(instance).StopAsync(CancellationToken.None);
      NpgsqlConnection.ClearAllPools();
      await PerTestDatabaseFactory.DropAsync(late.Name);
    }
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task ThePeriodicReRun_IsRegisteredAsync(string driverName) {
    var instance = _compose(StartupDriver.For(driverName));
    await using var scope = instance.CreateAsyncScope();

    await Assert.That(scope.ServiceProvider.GetServices<IMaintenanceStep>().Count(s => s.Name == "managed-schema-objects"))
      .IsEqualTo(1);
  }

  // ── #1959: both drivers handle the rebuild command ──────────────────────────────────────────────────────────

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task EachDriver_HandlesTheRebuildCommand_AndRunsItsBackgroundRebuildsAsync(string driverName) {
    IServiceCollection? composed = null;
    _ = _compose(StartupDriver.For(driverName), configure: s => composed = s);

    // The registrar runtime-registers the rebuild command's receptor; the worker runs the blue-green rebuilds it queues.
    // The Dapper driver registered neither, so a Dapper host could rebuild only by calling the rebuilder itself.
    await Assert.That(_hostedCount<RebuildCommandReceptorRegistrar>(composed!)).IsEqualTo(1)
      .Because("RebuildPerspectiveCommand is handled the same way on both drivers");
    await Assert.That(_hostedCount<RebuildBackgroundWorker>(composed!)).IsEqualTo(1);
    await Assert.That(composed!.Count(d => d.ServiceType == typeof(RebuildBackgroundQueue))).IsEqualTo(1);
  }

  private static int _hostedCount<T>(IServiceCollection services) where T : Microsoft.Extensions.Hosting.IHostedService =>
    services.Count(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
      && (d.ImplementationType == typeof(T) || d.ImplementationFactory is Func<IServiceProvider, T>));

  // ── #1992: the Assess verdict binds startup, on both drivers ─────────────────────────────────────────────────

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  public async Task EachDriver_SuppliesAnAssessor_ThatReadsTheVersionRecordAsync(string driverName) {
    var instance = _compose(StartupDriver.For(driverName));
    var assessor = instance.GetRequiredService<IStartupAssessor>();

    await Assert.That(assessor.IsConfigured).IsTrue()
      .Because("Dapper behaves like EF Core: without an assessor a stood-down Dapper instance would migrate");
    var fresh = await assessor.AssessAsync(CancellationToken.None);
    await Assert.That(fresh.Verdict).IsEqualTo(StartupVerdict.Migrate)
      .Because("a brand-new database has no version record, and nothing newer can exist: migrate");
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  [Timeout(300000)]
  public async Task ARestartOnACurrentSchema_TakesNoSchemaLock_AndSkipsElectAndMigrateAsync(
      string driverName, CancellationToken cancellationToken) {
    var driver = StartupDriver.For(driverName);
    // Two starts: the first migrates, the second settles what the first converted.
    await _startAsync(driver);
    await _startAsync(driver);
    var stages = new StageLog();
    var restart = _compose(driver, configure: s => s.AddSingleton<IStartupStageReporter>(stages));
    var assessment = await restart.GetRequiredService<IStartupAssessor>().AssessAsync(cancellationToken);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Current).Because(assessment.Reason);

    // Another session holds the schema lock for the whole restart. A start that took it would wait here forever.
    await using var holder = new NpgsqlConnection(_database.ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock($1)", holder)) {
      take.Parameters.AddWithValue(SchemaInitializationLockKey.Compute("public"));
      await take.ExecuteNonQueryAsync(cancellationToken);
    }

    await _initializer(restart).StartAsync(cancellationToken);

    await Assert.That(restart.GetRequiredService<ISchemaReadyGate>().IsReady).IsTrue()
      .Because("Current skips the election and the migration entirely: no schema lock, so the held one cannot stall it");
    await Assert.That(stages.Calls).Contains("Skip Elect: the schema is current; no migrator is needed");
    await Assert.That(stages.Calls).Contains("Skip Migrate: the schema is current");
    await Assert.That(stages.Calls).Contains("Enter Coordinate: recomputing partitions before the schema-ready gate opens")
      .Because("Converge and Coordinate still run on a current schema");
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  [Timeout(300000)]
  public async Task ANewerSchema_StandsDown_AppliesNothing_AndKeepsTheGateClosed_WhileVisibleAsync(
      string driverName, CancellationToken cancellationToken) {
    var driver = StartupDriver.For(driverName);
    await _startAsync(driver);
    await _recordNewerVersionAsync("999.0.0");
    var before = await _ledgerAsync();
    var stages = new StageLog();
    var standingDown = _compose(driver, configure: s => s.AddSingleton<IStartupStageReporter>(stages));
    var initializer = _initializer(standingDown);

    await initializer.StartAsync(cancellationToken);
    await stages.Registered.Task.WaitAsync(cancellationToken);
    await initializer.StopAsync(cancellationToken);
    await initializer.StandDownTask!;

    await Assert.That(standingDown.GetRequiredService<ISchemaReadyGate>().IsReady).IsFalse()
      .Because("the gate stays closed: no data plane, readiness withheld, the process alive");
    await Assert.That(await _ledgerAsync()).IsEqualTo(before)
      .Because("a stood-down instance applies nothing: no bootstrap, no pass, no ledger write");
    await Assert.That(stages.Calls.Any(c => c.StartsWith("Enter Preflight: standing down: ", StringComparison.Ordinal)
      && c.Contains("999.0.0", StringComparison.Ordinal))).IsTrue()
      .Because("the recorded state names the reason: the schema's version against the binary's");
    await Assert.That(stages.Calls).Contains("Phase StandingBy");
    var self = standingDown.GetRequiredService<IServiceInstanceProvider>().InstanceId;
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_service_instances WHERE instance_id = $1", self))
      .IsEqualTo(1L).Because("it registers at once, without a bootstrap: the tables exist because the database is newer");
  }

  [Test]
  [Timeout(300000)]
  public async Task TheTurnkeyCall_OnAStoodDownInstance_IsRefused_AndAppliesNothingAsync(CancellationToken cancellationToken) {
    var driver = StartupDriver.For("efcore");
    await _startAsync(driver);
    await _recordNewerVersionAsync("999.0.0");
    var before = await _ledgerAsync();
    var instance = _compose(driver);

    await Assert.That(async () => await DbContextInitializationRegistry.InitializeAllAsync(instance, null, cancellationToken))
      .Throws<SchemaStandDownException>()
      .Because("no start path skips Assess: the direct initialization reads the same verdict and refuses");
    await Assert.That(await _ledgerAsync()).IsEqualTo(before);
  }

  // ── the incompatibility override (owner decision, #2022) ─────────────

  private const string OVERRIDE = "Whizbang:Startup:OverrideIncompatibility";

  /// <summary>Every log line at Warning or above, with its event id, from every category.</summary>
  private sealed class WarningLog : ILoggerProvider {
    private readonly Lock _lock = new();
    private readonly List<(int Id, LogLevel Level, string Message)> _lines = [];
    public List<(int Id, LogLevel Level, string Message)> Lines { get { lock (_lock) { return [.. _lines]; } } }
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    public void Dispose() { /* nothing held */ }

    private sealed class Sink(WarningLog owner, string category) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        if (IsEnabled(logLevel)) {
          lock (owner._lock) {
            owner._lines.Add((eventId.Id, logLevel, category + ": " + formatter(state, exception)));
          }
        }
      }
    }
  }

  private Task<object> _overrideRecordAsync(IServiceProvider instance) => _scalarAsync<object>(
    "SELECT incompatibility_override FROM wh_service_instances WHERE instance_id = $1",
    instance.GetRequiredService<IServiceInstanceProvider>().InstanceId);

  private static List<(int Id, LogLevel Level, string Message)> _overrideLines(WarningLog log) =>
    [.. log.Lines.Where(l => l.Message.StartsWith("Whizbang.Core.Startup.StartupAssessmentSource: ", StringComparison.Ordinal)
      && l.Message.Contains(OVERRIDE, StringComparison.Ordinal))];

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  [Timeout(300000)]
  public async Task TheIncompatibilityOverride_StartsWhatTheGateRefuses_LogsIt_RecordsIt_AndAppliesNothingAsync(
      string driverName, CancellationToken cancellationToken) {
    var driver = StartupDriver.For(driverName);
    await _startAsync(driver);
    await _recordNewerVersionAsync("999.0.0");
    var before = await _ledgerAsync();
    var log = new WarningLog();
    var instance = _compose(driver, configure: s => s.AddSingleton<ILoggerProvider>(log), config: (OVERRIDE, "true"));

    await _initializer(instance).StartAsync(cancellationToken);

    await Assert.That(instance.GetRequiredService<ISchemaReadyGate>().IsReady).IsTrue()
      .Because("the override lets this build serve");
    await Assert.That(await _ledgerAsync()).IsEqualTo(before)
      .Because("it only serves: no migration runs, backward or otherwise");
    var lines = _overrideLines(log);
    await Assert.That(lines.Count).IsEqualTo(1).Because("logged once per start, loudly");
    await Assert.That(lines[0].Level).IsEqualTo(LogLevel.Error);
    await Assert.That(lines[0].Id).IsEqualTo(3);
    await Assert.That(lines[0].Message).Contains("StandDown");
    await Assert.That(lines[0].Message).Contains("(applied by library 999.0.0, application unknown)")
      .Because("the full reason, as the stand-down error carries it");
    var recorded = await _overrideRecordAsync(instance) as string;
    await Assert.That(recorded).IsNotNull().Because("operators and peers see it on the instance row");
    await Assert.That(recorded).Contains("(applied by library 999.0.0, application unknown)");
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  [Timeout(300000)]
  public async Task TheIncompatibilityOverride_OnACompatibleInstance_LogsNothing_AndRecordsNothingAsync(
      string driverName, CancellationToken cancellationToken) {
    var driver = StartupDriver.For(driverName);
    await _startAsync(driver);
    var log = new WarningLog();
    var instance = _compose(driver, configure: s => s.AddSingleton<ILoggerProvider>(log), config: (OVERRIDE, "true"));

    await _initializer(instance).StartAsync(cancellationToken);

    await Assert.That(instance.GetRequiredService<ISchemaReadyGate>().IsReady).IsTrue();
    await Assert.That(_overrideLines(log)).IsEmpty();
    await Assert.That(await _overrideRecordAsync(instance)).IsEqualTo(DBNull.Value);
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  [Timeout(300000)]
  public async Task TheIncompatibilityOverride_DoesNotOverrideANewerVersionOnlineAsync(
      string driverName, CancellationToken cancellationToken) {
    var driver = StartupDriver.For(driverName);
    await _startAsync(driver);
    await _execAsync($$"""
      SELECT record_heartbeat('{{Whizbang.Core.ValueObjects.TrackedGuid.New()}}'::uuid, 'svc', 'host', 1, '{}'::jsonb,
        'Running', '0.0.0-0', 90, 'pooled', '2.0.0')
      """);
    var stages = new StageLog();
    // An application host registers its version with AddWhizbang; the driver alone does not, so the scenario does.
    var instance = _compose(driver, configure: s => {
      s.AddSingleton<IStartupStageReporter>(stages);
      s.AddSingleton<Whizbang.Core.Versioning.IApplicationVersionProvider>(new Whizbang.Core.Versioning.ApplicationVersionProvider(
        Whizbang.Core.Versioning.ApplicationVersion.FromRecorded("1.0.0")));
    }, config: (OVERRIDE, "true"));
    var initializer = _initializer(instance);

    await initializer.StartAsync(cancellationToken);
    await stages.Registered.Task.WaitAsync(cancellationToken);
    await initializer.StopAsync(cancellationToken);
    await initializer.StandDownTask!;

    await Assert.That(instance.GetRequiredService<ISchemaReadyGate>().IsReady).IsFalse()
      .Because("a newer version online is the cluster-wide stand-down, not an incompatibility: the override never lifts it");
    await Assert.That(stages.Calls.Any(c => c.Contains("is online with application version 2.0.0", StringComparison.Ordinal))).IsTrue();
  }

  /// <summary>
  /// Records a release newer than this binary as having applied one framework migration: a changed content hash, so the
  /// ledger records it as the applier (#2022). A pass that only re-stamped the row would have applied nothing.
  /// </summary>
  private async Task _recordNewerVersionAsync(string version) => await _execAsync($"""
    WITH v AS (INSERT INTO wh_schema_versions (library_version) VALUES ('{version}') RETURNING id)
    UPDATE wh_schema_migrations SET version_id = (SELECT id FROM v), content_hash = 'applied-by-{version}', status = 2
      WHERE file_name = (SELECT min(file_name) FROM wh_schema_migrations WHERE file_name ~ '^[0-9]');
    """);

  /// <summary>Every ledger row, as one string, so "nothing was written" is one comparison.</summary>
  private Task<string> _ledgerAsync() => _scalarAsync<string>(
    "SELECT string_agg(file_name || ':' || content_hash || ':' || coalesce(version_id::text, '-') || ':' || status::text "
    + "|| ':' || coalesce(applied_at::text, '-'), ',' ORDER BY file_name) FROM wh_schema_migrations");

  /// <summary>Every stage call in order, and a signal when the instance's row is first written.</summary>
  private sealed class StageLog : IStartupStageReporter {
    private readonly Lock _lock = new();
    private readonly List<string> _calls = [];
    private StartupStageSnapshot? _current;

    public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Calls { get { lock (_lock) { return [.. _calls]; } } }
    public StartupStageSnapshot? Current { get { lock (_lock) { return _current; } } }

    public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _calls.Add($"Enter {stage}: {message}");
        _current = new StartupStageSnapshot(stage, message, false, DateTimeOffset.UnixEpoch);
      }
      return ValueTask.CompletedTask;
    }

    public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _calls.Add($"Skip {stage}: {reason}");
        _current = new StartupStageSnapshot(stage, "skipped: " + reason, true, DateTimeOffset.UnixEpoch);
      }
      return ValueTask.CompletedTask;
    }

    public ValueTask EnterPhaseAsync(Whizbang.Core.RunControl.LifecyclePhase phase, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _calls.Add($"Phase {phase}");
      }
      return ValueTask.CompletedTask;
    }

    public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) {
      lock (_lock) {
        _calls.Add("Registered");
      }
      Registered.TrySetResult();
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Signals the transitions the scenarios wait on; can hold the first failed attempt until released.</summary>
  private sealed class ParityObserver(bool holdFirstFailure = false) : ISchemaInitializationObserver {
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource LockContended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AttemptFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseFailure() => _release.TrySetResult();

    public ValueTask OnSchemaLockContendedAsync(string schema, CancellationToken cancellationToken) {
      LockContended.TrySetResult();
      return ValueTask.CompletedTask;
    }

    public async ValueTask OnAttemptFailedAsync(int attempt, Exception exception, CancellationToken cancellationToken) {
      AttemptFailed.TrySetResult();
      if (holdFirstFailure && attempt == 1) {
        await _release.Task.WaitAsync(cancellationToken);
      }
    }
  }
}
