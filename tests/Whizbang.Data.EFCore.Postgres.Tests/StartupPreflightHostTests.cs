// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Security;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Data.EFCore.Postgres.Tests.Migrations;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Startup against a real server through real hosts (#1636): several instances started together on a fresh database
/// all reach Running with the migration applied once and no deadlock; no instance votes for a role or runs its
/// partition assigner before Coordinate, and none of its workers asks the server for an object that does not exist
/// yet; and a second initialization returns only once the first has finished.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here stands in for the startup path: each host is composed the way a consumer composes one, and runs its
/// own initializer, election, workers and lifecycle. The one decoration is the duty elector, wrapped to record the
/// stage each vote was cast in and then cast it through the real one.
/// </para>
/// <para>
/// Every wait is a signal: the stage observer for the stage asserted, the schema-step observer for a step held
/// open. The deadline on each only turns a stage that is never reached into a failure that says so.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageGate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageGatedService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CatalogCheckedDdl.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/DbContextInitializationRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs</code-under-test>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard5")]
public sealed partial class StartupPreflightHostTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(180);

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("preflight");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  /// <summary>
  /// Three instances start together on an empty database: each reaches Running, every migration file is applied
  /// exactly once across the three, the ledger holds every file, and no instance logs a deadlock.
  /// </summary>
  [Test]
  [Timeout(600000)]
  public async Task SeveralInstancesOnAFreshDatabase_AllReachRunning_TheMigrationAppliesOnce_AndNothingDeadlocksAsync(
      CancellationToken cancellationToken) {
    var instances = Enumerable.Range(0, 3).Select(_ => new Instance(_database.ConnectionString)).ToList();
    try {
      await Task.WhenAll(instances.Select(i => i.Host.StartAsync(cancellationToken)));
      foreach (var instance in instances) {
        await instance.Stages.Reached(StartupStage.Running).WaitAsync(_deadline, cancellationToken);
      }

      var lines = instances.SelectMany(i => i.Log.Lines).ToList();
      var deadlocks = lines.Where(l => !l.StartsWith("Information", StringComparison.Ordinal)
        && (l.Contains("40P01", StringComparison.Ordinal) || l.Contains("deadlock", StringComparison.OrdinalIgnoreCase))).ToList();
      await Assert.That(deadlocks).IsEmpty()
        .Because("instances starting together must not deadlock their own migration; logged: " + string.Join(" | ", deadlocks));
      var files = MigrationStepTransactionTests.MigrationFiles();
      foreach (var file in files) {
        await Assert.That(lines.Count(l => _succeeded(file).IsMatch(l))).IsEqualTo(1)
          .Because($"{file} is applied once across the fleet, by whichever instance took its step");
      }
      await Assert.That(await _scalarAsync<long>(
          "SELECT count(*) FROM public.wh_schema_migrations WHERE owner = 'whizbang' AND status IN (1, 2) AND file_name = ANY($1)",
          (object)files))
        .IsEqualTo((long)files.Length).Because("the ledger holds every file");
    } finally {
      foreach (var host in instances.Select(i => i.Host)) {
        await host.StopAsync(CancellationToken.None);
        host.Dispose();
      }
    }
  }

  /// <summary>
  /// Nothing early: the duty holder and the partition assigner cast no vote before this instance reaches
  /// Coordinate (the migrator election is the one vote Elect exists for), and no worker logs a missing table or
  /// function while the instance starts on an empty database.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task NothingVotesBeforeCoordinate_AndNoWorkerAsksForAnObjectThatDoesNotExistYetAsync(CancellationToken cancellationToken) {
    var instance = new Instance(_database.ConnectionString);
    try {
      await instance.Host.StartAsync(cancellationToken);
      await instance.Stages.Reached(StartupStage.Running).WaitAsync(_deadline, cancellationToken);
      await instance.Elector.RoleVoteCast.WaitAsync(_deadline, cancellationToken);

      var votes = instance.Elector.Votes;
      await Assert.That(votes.Where(v => v.Role != StartupDuties.MIGRATOR && v.Stage < StartupStage.Coordinate))
        .IsEmpty().Because($"roles are voted for from Coordinate on; votes cast: {string.Join(", ", votes)}");
      await Assert.That(votes.Where(v => v.Role == StartupDuties.MIGRATOR).Select(v => v.Stage))
        .All(s => s == StartupStage.Elect).Because("the migrator election is the one vote cast in Elect");
      await Assert.That(instance.Log.Lines.Where(l =>
          (l.StartsWith("Warning", StringComparison.Ordinal) || l.StartsWith("Error", StringComparison.Ordinal))
          && (l.Contains("42P01", StringComparison.Ordinal) || l.Contains("42883", StringComparison.Ordinal))))
        .IsEmpty().Because("no worker reads an object before the stage that creates it");
    } finally {
      await instance.Host.StopAsync(CancellationToken.None);
      instance.Host.Dispose();
    }
  }

  /// <summary>
  /// A second initialization, called while the host's own is still migrating, joins it and returns only once it has
  /// finished: the schema it hands back is the whole schema. The host's initialization is held open after its first
  /// committed step, so "still migrating" is a fact when the second call is made. The host is not awaited first,
  /// because its start does not return before the schema is ready.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task ASecondInitialization_ReturnsOnlyOnceTheFirstHasFinishedAsync(CancellationToken cancellationToken) {
    var hold = new HoldFirstStep();
    var instance = new Instance(_database.ConnectionString, services => services.AddSingleton<ISchemaInitializationObserver>(hold));
    var starting = instance.Host.StartAsync(cancellationToken);
    try {
      await hold.Holding.WaitAsync(_deadline, cancellationToken);

      var second = instance.Host.EnsureWhizbangInitializedAsync(cancellationToken);

      await Assert.That(second.IsCompleted).IsFalse().Because("the first initialization is held mid-pass");
      await Assert.That(instance.Log.Lines).Contains("Information Whizbang database initialization is in progress; waiting for it to finish")
        .Because("the second call joins the host's initialization rather than running one of its own");
      hold.Release();
      await second.WaitAsync(_deadline, cancellationToken);
      var files = MigrationStepTransactionTests.MigrationFiles();
      await Assert.That(await _scalarAsync<long>(
          "SELECT count(*) FROM public.wh_schema_migrations WHERE owner = 'whizbang' AND status IN (1, 2) AND file_name = ANY($1)",
          (object)files))
        .IsEqualTo((long)files.Length).Because("when the second call returns, every file has been applied");
      await starting.WaitAsync(_deadline, cancellationToken);
    } finally {
      hold.Release();
      await instance.Host.StopAsync(CancellationToken.None);
      instance.Host.Dispose();
    }
  }

  /// <summary>
  /// A bootstrap closure that fails for a reason that is not transient fails the initialization attempt, naming what
  /// failed, at Error (#1636): the instance does not go on to migrate under the schema lock as though nothing happened.
  /// The closure's record table is stood up in a shape the bootstrap cannot read, which no retry can fix.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task ABootstrapThatCannotBeApplied_FailsTheInitialization_NamingWhatFailedAsync(CancellationToken cancellationToken) {
    await _executeAsync("CREATE TABLE public.wh_bootstrap_closure (unrelated int)");
    var failures = new AttemptFailures();
    var instance = new Instance(_database.ConnectionString, services => services.AddSingleton<ISchemaInitializationObserver>(failures));
    // Not awaited: the host's start does not return before the schema is ready, and here it never is. Canceling this
    // token ends the start, so the host can be stopped.
    using var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var starting = instance.Host.StartAsync(start.Token);
    try {
      var first = await Task.WhenAny(failures.First, instance.Stages.Reached(StartupStage.Running)).WaitAsync(_deadline, cancellationToken);
      await Assert.That(first == failures.First).IsTrue()
        .Because("a bootstrap that cannot be applied stops the start in Preflight; it is not skipped on the way to Running");
      var failed = _chain(await failures.First).OfType<SchemaBootstrapException>().FirstOrDefault();
      await Assert.That(failed).IsNotNull().Because("the attempt fails with the bootstrap's own failure, not a later symptom");
      await Assert.That(failed!.Step).IsEqualTo("<closure record>");
      await Assert.That(instance.Log.Lines.Any(l => l.StartsWith("Error", StringComparison.Ordinal)
          && l.Contains("<closure record>", StringComparison.Ordinal)))
        .IsTrue().Because("what failed is said at Error");
      await Assert.That(instance.Log.Lines.Any(l => l.Contains("Staged startup could not be arranged", StringComparison.Ordinal)))
        .IsFalse().Because("the failure is not swallowed into a lost election");
    } finally {
      await start.CancelAsync();
      await Task.WhenAny(starting);
      await instance.Host.StopAsync(CancellationToken.None);
      instance.Host.Dispose();
    }
  }

  /// <summary>
  /// The instance row is registered at the end of Preflight (#1636), after the bootstrap closure and the instance and
  /// role table DDL, and before Elect: the first stage the row records is Preflight's last step, written as the row is
  /// registered, and every stage line before it was log-only. A first instance migrates the database, so the stage
  /// columns exist when the second registers.
  /// </summary>
  [Test]
  [Timeout(600000)]
  public async Task TheInstanceRow_IsRegisteredAtTheEndOfPreflight_BeforeElectAsync(CancellationToken cancellationToken) {
    var migrator = new Instance(_database.ConnectionString);
    try {
      await migrator.Host.StartAsync(cancellationToken);
      await migrator.Stages.Reached(StartupStage.Running).WaitAsync(_deadline, cancellationToken);
    } finally {
      await migrator.Host.StopAsync(CancellationToken.None);
      migrator.Host.Dispose();
    }

    var recorder = new RowAtFirstRecord(_database.ConnectionString);
    var instance = new Instance(_database.ConnectionString, services => services.AddSingleton<IStartupStageObserver>(recorder));
    recorder.InstanceId = instance.Host.Services.GetRequiredService<Whizbang.Core.Observability.IServiceInstanceProvider>().InstanceId;
    try {
      await instance.Host.StartAsync(cancellationToken);
      await instance.Stages.Reached(StartupStage.Running).WaitAsync(_deadline, cancellationToken);

      var changes = recorder.Changes;
      var first = changes.FindIndex(c => c.Recorded);
      await Assert.That(first).IsGreaterThanOrEqualTo(0);
      await Assert.That(changes[first].Snapshot.Stage).IsEqualTo(StartupStage.Preflight)
        .Because("the row is registered at the end of Preflight, so Preflight's last step is the first stage it records; "
          + "changes: " + string.Join(" | ", changes.Select(c => $"{c.Snapshot.Stage}:{c.Snapshot.Message}:{c.Recorded}")));
      await Assert.That(changes[first].Snapshot.Message)
        .IsEqualTo("the bootstrap closure is in place; registering this instance");
      await Assert.That(changes.Take(first).All(c => c.Snapshot.Stage == StartupStage.Preflight && !c.Recorded)).IsTrue()
        .Because("every stage before the row exists is log-only, and none of them is past Preflight");
      await Assert.That(recorder.StageOnTheRow).IsEqualTo((int)StartupStage.Preflight)
        .Because("the row exists, holding Preflight, when its first stage is recorded");
    } finally {
      await instance.Host.StopAsync(CancellationToken.None);
      instance.Host.Dispose();
    }
  }

  // ─────────────────────────────────────────────────────────────────────────

  private static Regex _succeeded(string file) =>
    new($@"^Information Migration \d+ of \d+ - Succeeded : {Regex.Escape(file)} \(", RegexOptions.None, TimeSpan.FromSeconds(5));

  private async Task<T> _scalarAsync<T>(string sql, params object[] parameters) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var parameter in parameters) {
      command.Parameters.AddWithValue(parameter);
    }
    return (T)(await command.ExecuteScalarAsync())!;
  }

  private async Task _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
  }

  private static IEnumerable<Exception> _chain(Exception exception) {
    for (Exception? current = exception; current is not null; current = current.InnerException) {
      yield return current;
    }
  }

  /// <summary>Every stage change and whether the row recorded it; reads the row's stage at the first one it did.</summary>
  private sealed class RowAtFirstRecord(string connectionString) : IStartupStageObserver {
    private readonly Lock _lock = new();
    private readonly List<(StartupStageSnapshot Snapshot, bool Recorded)> _changes = [];

    public Guid InstanceId { get; set; }

    public int? StageOnTheRow { get; private set; }

    public List<(StartupStageSnapshot Snapshot, bool Recorded)> Changes {
      get {
        lock (_lock) {
          return [.. _changes];
        }
      }
    }

    public async ValueTask OnStageChangedAsync(StartupStageSnapshot snapshot, bool recorded, CancellationToken cancellationToken) {
      bool firstRecorded;
      lock (_lock) {
        firstRecorded = recorded && !_changes.Exists(c => c.Recorded);
        _changes.Add((snapshot, recorded));
      }
      if (firstRecorded) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var read = new NpgsqlCommand(
          "SELECT startup_stage FROM public.wh_service_instances WHERE instance_id = $1", connection);
        read.Parameters.AddWithValue(InstanceId);
        StageOnTheRow = await read.ExecuteScalarAsync(cancellationToken) is short stage ? stage : null;
      }
    }
  }

  /// <summary>Completes with the first initialization attempt's failure.</summary>
  private sealed class AttemptFailures : ISchemaInitializationObserver {
    private readonly TaskCompletionSource<Exception> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<Exception> First => _first.Task;

    public ValueTask OnAttemptFailedAsync(int attempt, Exception exception, CancellationToken cancellationToken) {
      _first.TrySetResult(exception);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>One instance of a service, composed as a consumer composes it, on the shared database.</summary>
  private sealed class Instance {
    public Instance(string connectionString, Action<IServiceCollection>? configure = null) {
      var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
      builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:db"] = connectionString,
      });
      builder.Logging.ClearProviders();
      builder.Logging.AddProvider(Log);
      builder.Logging.SetMinimumLevel(LogLevel.Information);
      builder.Services.AddSingleton<ITransport>(new InProcessTransport());
      builder.Services.AddSingleton<IStartupStageObserver>(Stages);
      configure?.Invoke(builder.Services);
      _ = builder.Services.AddWhizbang().WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
      builder.Services.Replace(ServiceDescriptor.Singleton(new MessageSecurityOptions { AllowAnonymous = true }));
      builder.Services.RemoveAll<IDutyElector>();
      builder.Services.AddSingleton<IDutyElector>(sp => Elector.Over(
        sp.GetRequiredService<PgRoleElector>(), sp.GetRequiredService<IStartupStageReporter>()));
      Host = builder.Build();
    }

    public IHost Host { get; }
    public HostLog Log { get; } = new();
    public StageSignals Stages { get; } = new();
    public RecordingElector Elector { get; } = new();
  }

  /// <summary>One signal per stage reached.</summary>
  private sealed class StageSignals : IStartupStageObserver {
    private readonly Dictionary<StartupStage, TaskCompletionSource> _reached = Enum.GetValues<StartupStage>()
      .ToDictionary(s => s, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public Task Reached(StartupStage stage) => _reached[stage].Task;

    public ValueTask OnStageChangedAsync(StartupStageSnapshot snapshot, bool recorded, CancellationToken cancellationToken) {
      _reached[snapshot.Stage].TrySetResult();
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>One vote: the role, and the stage this instance was in when it was cast.</summary>
  private sealed record Vote(string Role, StartupStage? Stage);

  /// <summary>The real elector, recording the stage each vote is cast in before casting it.</summary>
  private sealed class RecordingElector : IDutyElector {
    private readonly Lock _lock = new();
    private readonly List<Vote> _votes = [];
    private readonly TaskCompletionSource _roleVoteCast = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDutyElector? _inner;
    private IStartupStageReporter? _stages;

    public List<Vote> Votes {
      get {
        lock (_lock) {
          return [.. _votes];
        }
      }
    }

    /// <summary>Completes once a role other than the migrator has been voted for.</summary>
    public Task RoleVoteCast => _roleVoteCast.Task;

    public RecordingElector Over(IDutyElector inner, IStartupStageReporter stages) {
      _inner = inner;
      _stages = stages;
      return this;
    }

    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      _record([duty]);
      return _inner!.TryAcquireAsync(duty, cancellationToken);
    }

    public Task<IReadOnlyList<DutyAttempt>> TryAcquireManyAsync(IReadOnlyList<string> duties, CancellationToken cancellationToken) {
      _record(duties);
      return _inner!.TryAcquireManyAsync(duties, cancellationToken);
    }

    private void _record(IEnumerable<string> roles) {
      var stage = _stages!.Current?.Stage;
      lock (_lock) {
        foreach (var role in roles) {
          _votes.Add(new Vote(role, stage));
          if (role != StartupDuties.MIGRATOR) {
            _roleVoteCast.TrySetResult();
          }
        }
      }
    }
  }

  /// <summary>Holds the initialization that commits the first schema step until released.</summary>
  private sealed class HoldFirstStep : ISchemaInitializationObserver {
    private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Holding => _holding.Task;

    public void Release() => _released.TrySetResult();

    public async ValueTask OnSchemaStepCommittedAsync(string schema, string stepName, CancellationToken cancellationToken) {
      if (_holding.TrySetResult()) {
        await _released.Task.WaitAsync(cancellationToken);
      }
    }
  }

  /// <summary>Every line the host logs at Information and above, with its level and any exception's message.</summary>
  private sealed class HostLog : ILoggerProvider {
    private readonly Lock _lock = new();
    private readonly List<string> _lines = [];

    public List<string> Lines {
      get {
        lock (_lock) {
          return [.. _lines];
        }
      }
    }

    public ILogger CreateLogger(string categoryName) => new Writer(this);

    public void Dispose() { }

    private sealed class Writer(HostLog log) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) {
        var line = exception is null
          ? $"{logLevel} {formatter(state, exception)}"
          : $"{logLevel} {formatter(state, exception)} [{exception.GetType().Name}: {exception.Message}]";
        lock (log._lock) {
          log._lines.Add(line);
        }
      }
    }
  }
}
