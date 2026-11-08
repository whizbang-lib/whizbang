// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

  /// <summary>Records a release newer than this binary as the writer of one framework migration.</summary>
  private async Task _recordNewerVersionAsync(string version) => await _execAsync($"""
    WITH v AS (INSERT INTO wh_schema_versions (library_version) VALUES ('{version}') RETURNING id)
    UPDATE wh_schema_migrations SET version_id = (SELECT id FROM v)
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
