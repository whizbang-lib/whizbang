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
using Whizbang.Core.Health;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The startup stages (#1636), through a real host on a real server: a service started the way a consumer starts it
/// logs every stage it enters as <c>Stage X of 7: &lt;name&gt; - &lt;message&gt;</c>, records each on its own instance
/// row from the moment the row can hold it, is eligible for work exactly from Accept commands, reports the stage on
/// readiness, skips Migrate on a restart against a current schema, and records Draining before it stops.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here stands in for the startup path: the host's own lifecycle, its initializer service, the generated schema
/// pass, the election, the lifecycle driver and the shutdown service all run. The log lines are read from the host's
/// own logging pipeline, as an operator reads them.
/// </para>
/// <para>
/// Every wait is on the observer signal for the exact stage asserted, and each recorded row is read inside the
/// observer, while the reporter holds the stage, so no later write can change what is read.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageReporter.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageHostLifecycle.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageRunControl.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaInitializationStages.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/231_StartupStageRecord.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public sealed partial class StartupStageHostTests {
  /// <summary>Bounds each signal wait, so a stage never reached fails as that rather than as the test's timeout.</summary>
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(120);

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("stages");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  /// <summary>
  /// A fresh database: every startup stage is entered in order and logged once as it is entered; the row records each
  /// stage from the first one after the migration that adds the columns, with its message, entered-at time and the
  /// phase it maps to; the instance is eligible for work exactly from Accept commands; readiness reports the stage.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task AFreshDatabase_EntersEveryStageInOrder_LogsEach_AndRecordsEachTheRowCanHoldAsync(CancellationToken cancellationToken) {
    var log = new StageLog();
    var watch = new StageWatch(_database.ConnectionString, readinessAt: StartupStage.Migrate);
    using var host = _buildHost(log, watch);

    await host.StartAsync(cancellationToken);
    await watch.Reached(StartupStage.Running).WaitAsync(_deadline, cancellationToken);

    var transitions = log.Lines.Select(l => _stagePrefix().Match(l).Value).Distinct().ToList();
    await Assert.That(transitions).IsEquivalentTo([
      "Information Stage 1 of 7: Preflight", "Information Stage 2 of 7: Elect", "Information Stage 3 of 7: Migrate",
      "Information Stage 4 of 7: Converge", "Information Stage 5 of 7: Coordinate",
      "Information Stage 6 of 7: Accept commands", "Information Stage 7 of 7: Running",
   ], TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("each stage is entered once, in order, and logged as it is");
    await Assert.That(log.Lines).Contains("Information Stage 1 of 7: Preflight - host starting");
    await Assert.That(log.Lines).Contains("Information Stage 2 of 7: Elect - joining the registry and voting for the migrator duty");
    await Assert.That(log.Lines).Contains("Information Stage 2 of 7: Elect - this instance migrates the schema");
    await Assert.That(log.Lines).Contains(l => _migrateLine().IsMatch(l));
    await Assert.That(log.Lines).Contains("Information Stage 4 of 7: Converge - analyzing new indexes and reconciling managed objects");
    await Assert.That(log.Lines).Contains("Information Stage 5 of 7: Coordinate - recomputing partitions before the schema-ready gate opens");
    await Assert.That(log.Lines).Contains(
      "Information Stage 6 of 7: Accept commands - the schema is ready; commands are accepted while the read models catch up");
    await Assert.That(log.Lines[^1]).IsEqualTo("Information Stage 7 of 7: Running - reads and writes are served");

    var recorded = watch.Recorded;
    await Assert.That(recorded.Select(r => (int)r.Stage)).IsEquivalentTo([40, 50, 60, 70],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the columns arrive with migration 231, so the stages before the pass that adds them are in the log only");
    await Assert.That(recorded.Select(r => r.Message)).IsEquivalentTo([
      "analyzing new indexes and reconciling managed objects",
      "recomputing partitions before the schema-ready gate opens",
      "the schema is ready; commands are accepted while the read models catch up",
      "reads and writes are served",
   ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(recorded.Select(r => r.Phase)).IsEquivalentTo([
      "Migrating", "Migrating", "AcceptingCommands", "Running",
   ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(recorded.Zip(recorded.Skip(1)).All(p => p.First.EnteredAt <= p.Second.EnteredAt)).IsTrue()
      .Because("each stage is entered after the one before it");
    await Assert.That(recorded.Select(r => r.Eligible)).IsEquivalentTo([false, false, true, true],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("an instance becomes eligible for work exactly when it records Accept commands");

    await Assert.That(watch.ReadinessStage).IsEqualTo(StartupStage.Migrate);
    await Assert.That(watch.ReadinessStartupDetail).StartsWith("Stage 3 of 7: Migrate - ")
      .Because("readiness and the startup component report the stage the instance is in while it is in it");
    var running = await host.Services.GetRequiredService<WhizbangHealthAggregator>()
      .EvaluateAsync(HealthProbe.Readiness, cancellationToken);
    await Assert.That(running.Stage!.Stage).IsEqualTo(StartupStage.Running);
    await Assert.That(running.Components.Single(c => c.Component == "startup").Detail)
      .StartsWith("Stage 7 of 7: Running - reads and writes are served; ");

    await host.StopAsync(cancellationToken);
  }

  /// <summary>
  /// A restart against a current schema: the held Elect stage is written when the election registers the row, Migrate
  /// is skipped because the schema is already current, and every stage after it is recorded.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task ARestartOnACurrentDatabase_SkipsMigrate_AndRecordsFromRegistrationAsync(CancellationToken cancellationToken) {
    var firstWatch = new StageWatch(_database.ConnectionString);
    using (var first = _buildHost(new StageLog(), firstWatch)) {
      await first.StartAsync(cancellationToken);
      await firstWatch.Reached(StartupStage.Running).WaitAsync(_deadline, cancellationToken);
      await first.StopAsync(cancellationToken);
    }
    var log = new StageLog();
    var watch = new StageWatch(_database.ConnectionString);
    using var host = _buildHost(log, watch);

    await host.StartAsync(cancellationToken);
    await watch.Reached(StartupStage.Running).WaitAsync(_deadline, cancellationToken);

    await Assert.That(log.Lines).Contains("Information Stage 3 of 7: Migrate - skipped: the schema is already current")
      .Because("a restart against a current schema runs no pass, and says so");
    await Assert.That(log.Lines).DoesNotContain(l => _migrateLine().IsMatch(l));
    var recorded = watch.Recorded;
    await Assert.That(recorded.Select(r => $"{r.Stage} {r.Message}")).IsEquivalentTo([
      "20 joining the registry and voting for the migrator duty",
      "20 this instance migrates the schema",
      "30 skipped: the schema is already current",
      "40 analyzing new indexes and reconciling managed objects",
      "50 recomputing partitions before the schema-ready gate opens",
      "60 the schema is ready; commands are accepted while the read models catch up",
      "70 reads and writes are served",
   ], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("Elect, held until the election registered the row, is written at registration; every stage after it as it is entered");
    await Assert.That(recorded[0].EnteredAt).IsEqualTo(recorded[1].EnteredAt)
      .Because("the election's outcome is progress within Elect, not a new stage");

    await host.StopAsync(cancellationToken);
  }

  /// <summary>
  /// A graceful stop: Draining is recorded on the row before any worker stops, and the instance stops being eligible
  /// for work at once; Stopped follows once every hosted service has stopped. Both are logged as shutdown stages.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task AGracefulStop_RecordsDraining_ThenStoppedAsync(CancellationToken cancellationToken) {
    var log = new StageLog();
    var watch = new StageWatch(_database.ConnectionString);
    using var host = _buildHost(log, watch);
    await host.StartAsync(cancellationToken);
    await watch.Reached(StartupStage.Running).WaitAsync(_deadline, cancellationToken);

    await host.StopAsync(cancellationToken);
    await watch.Reached(StartupStage.Stopped).WaitAsync(_deadline, cancellationToken);

    await Assert.That(log.Lines.TakeLast(2)).IsEquivalentTo([
      "Information Shutdown stage: Draining - host stopping, finishing in-flight work",
      "Information Shutdown stage: Stopped - host stopped",
   ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    var draining = watch.Recorded.Single(r => r.Stage == 80);
    await Assert.That(draining.Message).IsEqualTo("host stopping, finishing in-flight work");
    await Assert.That(draining.Eligible).IsFalse()
      .Because("a draining instance's share goes to its peers the moment Draining is recorded, not when its heartbeat lapses");
    await Assert.That(watch.Seen.Select(s => s.Stage)).Contains(StartupStage.Stopped);
  }

  // ─────────────────────────────────────────────────────────────────────────

  [GeneratedRegex(@"^Information (Stage \d of 7: [A-Za-z]+?|Shutdown stage: \w+)(?= - )")]
  private static partial Regex _stagePrefix();

  [GeneratedRegex(@"^Information Stage 3 of 7: Migrate - \d+ migration steps? to apply$")]
  private static partial Regex _migrateLine();

  private IHost _buildHost(StageLog log, StageWatch watch) {
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:db"] = _database.ConnectionString,
    });
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(log);
    builder.Logging.SetMinimumLevel(LogLevel.Information);
    builder.Services.AddSingleton<ITransport>(new InProcessTransport());
    builder.Services.AddSingleton<IStartupStageObserver>(watch);
    _ = builder.Services.AddWhizbang().WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    builder.Services.Replace(ServiceDescriptor.Singleton(new MessageSecurityOptions { AllowAnonymous = true }));
    var host = builder.Build();
    watch.Attach(host.Services);
    return host;
  }

  /// <summary>One recorded stage, as the instance's row held it when the stage was written.</summary>
  private sealed record RecordedStage(short Stage, string Message, DateTime EnteredAt, string Phase, bool Eligible);

  /// <summary>
  /// Watches the stage: completes a signal per stage reached, reads the row each time a stage is written, and at one
  /// chosen stage evaluates readiness while the instance is still in it.
  /// </summary>
  private sealed class StageWatch(string connectionString, StartupStage? readinessAt = null) : IStartupStageObserver {
    private readonly Lock _lock = new();
    private readonly List<RecordedStage> _recorded = [];
    private readonly List<StartupStageSnapshot> _seen = [];
    private readonly Dictionary<StartupStage, TaskCompletionSource> _reached = Enum.GetValues<StartupStage>()
      .ToDictionary(s => s, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    private IServiceProvider? _services;

    public StartupStage? ReadinessStage { get; private set; }
    public string? ReadinessStartupDetail { get; private set; }

    public List<RecordedStage> Recorded {
      get {
        lock (_lock) {
          return [.. _recorded];
        }
      }
    }

    public List<StartupStageSnapshot> Seen {
      get {
        lock (_lock) {
          return [.. _seen];
        }
      }
    }

    public void Attach(IServiceProvider services) => _services = services;

    public Task Reached(StartupStage stage) => _reached[stage].Task;

    public async ValueTask OnStageChangedAsync(StartupStageSnapshot snapshot, bool recorded, CancellationToken cancellationToken) {
      lock (_lock) {
        _seen.Add(snapshot);
      }
      if (recorded) {
        var row = await _readRowAsync(cancellationToken);
        lock (_lock) {
          _recorded.Add(row);
        }
      }
      if (snapshot.Stage == readinessAt && ReadinessStage is null && _services is not null) {
        var readiness = await _services.GetRequiredService<WhizbangHealthAggregator>()
          .EvaluateAsync(HealthProbe.Readiness, cancellationToken);
        ReadinessStage = readiness.Stage?.Stage;
        ReadinessStartupDetail = readiness.Components.Single(c => c.Component == "startup").Detail;
      }
      _reached[snapshot.Stage].TrySetResult();
    }

    private async Task<RecordedStage> _readRowAsync(CancellationToken cancellationToken) {
      var instanceId = _services!.GetRequiredService<IServiceInstanceProvider>().InstanceId;
      await using var connection = new NpgsqlConnection(connectionString);
      await connection.OpenAsync(cancellationToken);
      await using var command = new NpgsqlCommand("""
        SELECT startup_stage, startup_stage_message, startup_stage_entered_at, lifecycle_phase,
               wh_instance_is_eligible_for_work(si)
        FROM wh_service_instances si WHERE instance_id = @id
        """, connection);
      command.Parameters.AddWithValue("id", instanceId);
      await using var reader = await command.ExecuteReaderAsync(cancellationToken);
      if (!await reader.ReadAsync(cancellationToken)) {
        throw new InvalidOperationException($"a stage was reported written, but instance {instanceId} has no row");
      }
      return new RecordedStage(
        reader.GetInt16(0), reader.GetString(1), reader.GetDateTime(2), reader.GetString(3), reader.GetBoolean(4));
    }
  }

  /// <summary>The host's stage lines as its logging pipeline formats them.</summary>
  private sealed class StageLog : ILoggerProvider {
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

    private sealed class Writer(StageLog log) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) {
        var line = $"{logLevel} {formatter(state, exception)}";
        if (!_stagePrefix().IsMatch(line)) {
          return;
        }
        lock (log._lock) {
          log._lines.Add(line);
        }
      }
    }
  }
}
