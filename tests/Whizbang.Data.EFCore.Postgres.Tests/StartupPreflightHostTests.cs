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
      await Assert.That(lines).DoesNotContain(l => l.Contains("40P01", StringComparison.Ordinal)
          || l.Contains("deadlock", StringComparison.OrdinalIgnoreCase))
        .Because("instances starting together must not deadlock their own migration");
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
  /// A second initialization, called while the host's own is still migrating, returns only once that one has
  /// finished: the schema it hands back is the whole schema. The host's initialization is held open after its first
  /// committed step, so "still migrating" is a fact when the second call is made.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task ASecondInitialization_ReturnsOnlyOnceTheFirstHasFinishedAsync(CancellationToken cancellationToken) {
    var hold = new HoldFirstStep();
    var instance = new Instance(_database.ConnectionString, services => services.AddSingleton<ISchemaInitializationObserver>(hold));
    try {
      await instance.Host.StartAsync(cancellationToken);
      await hold.Holding.WaitAsync(_deadline, cancellationToken);

      var second = instance.Host.EnsureWhizbangInitializedAsync(cancellationToken);

      await Assert.That(second.IsCompleted).IsFalse().Because("the first initialization is held mid-pass");
      hold.Release();
      await second.WaitAsync(_deadline, cancellationToken);
      var files = MigrationStepTransactionTests.MigrationFiles();
      await Assert.That(await _scalarAsync<long>(
          "SELECT count(*) FROM public.wh_schema_migrations WHERE owner = 'whizbang' AND status IN (1, 2) AND file_name = ANY($1)",
          (object)files))
        .IsEqualTo((long)files.Length).Because("when the second call returns, every file has been applied");
    } finally {
      hold.Release();
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
