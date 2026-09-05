using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The cold-boot journey, end to end: a genuinely unmigrated database, the REAL generated
/// dispatcher, real lens registration, real gates, the real lifecycle ladder — composed the way a
/// booting pod composes them. Covers the three claims the unit tests prove only in isolation:
/// (1) both data-plane seams REFUSE while Migrate is under way, through the real entry points;
/// (2) at <c>AcceptingCommands</c> the write side is live — a publish lands DURABLY in the outbox —
/// while the read side keeps refusing; (3) the ladder walks Connecting → Migrating →
/// AcceptingCommands → Running in order, and the fleet row reports it. The moment the read gate
/// releases is owned by <c>ReadModelsReadyDriverTests</c> (schema gate + perspective startup scan);
/// this journey drives that moment explicitly and asserts everything downstream of it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/RunControl/LifecyclePhaseWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/RunControl/LifecyclePhase.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/ReadModelsReadyGate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineHosting.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public class ColdBootJourneyE2ETests {

  private string? _databaseName;
  private NpgsqlDataSource? _dataSource;
  private DbContextOptions<WorkCoordinationDbContext> _dbOptions = null!;
  private string _connectionString = null!;

  private sealed class ProbeModel {
    public Guid Id { get; set; }
  }

  private sealed class _pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "coldboot-svc";
    public string HostName => "coldboot-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class _recordingRunControl : IWhizbangRunControl {
    private readonly List<LifecyclePhase> _seen = [];
    private readonly Lock _lock = new();
    public string Component => "journey-recorder";
    public IReadOnlyList<LifecyclePhase> Seen {
      get {
        lock (_lock) {
          return [.. _seen];
        }
      }
    }
    public ValueTask OnPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      lock (_lock) {
        _seen.Add(phase);
      }
      return default;
    }
  }

  [Before(Test)]
  public async Task SetupColdDatabaseAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"coldboot_{Guid.NewGuid():N}";

    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = admin.CreateCommand();
      create.CommandText = $"CREATE DATABASE {_databaseName}";
      await create.ExecuteNonQueryAsync();
    }

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true,
    };
    _connectionString = builder.ConnectionString;

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
    dataSourceBuilder.ConfigureJsonOptions(JsonContextRegistry.CreateCombinedOptions());
    dataSourceBuilder.EnableDynamicJson();
    dataSourceBuilder.UseVector();
    _dataSource = dataSourceBuilder.Build();

    var optionsBuilder = new DbContextOptionsBuilder<WorkCoordinationDbContext>();
    optionsBuilder.UseNpgsql(_dataSource)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    _dbOptions = optionsBuilder.Options;
    // Deliberately NO migration here — the database stays COLD until the test decides to migrate.
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_dataSource is not null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }
    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await using var terminate = admin.CreateCommand();
        terminate.CommandText = $@"
          SELECT pg_terminate_backend(pid) FROM pg_stat_activity
          WHERE datname = '{_databaseName}' AND pid <> pg_backend_pid()";
        await terminate.ExecuteNonQueryAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
      } catch {
        // container teardown reclaims stragglers
      }
      _databaseName = null;
    }
  }

  private WorkCoordinationDbContext _createDbContext() => new(_dbOptions);

  /// <summary>One booting pod: the REAL generated dispatcher, real coordinator, real gates, real
  /// lens registration, real lifecycle machine with the real instance-state participant.</summary>
  private ServiceProvider _buildPodServices(
      _pod pod, SchemaReadyGate schemaGate, ReadModelsReadyGate readGate, _recordingRunControl recorder) {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    services.AddSingleton<IServiceInstanceProvider>(pod);
    services.AddScoped(_ => _createDbContext());
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging();
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddOptions<Whizbang.Core.Configuration.WhizbangCoreOptions>();

    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddScoped<IWorkCoordinatorStrategy>(sp => new ScopedWorkCoordinatorStrategy(
      sp.GetRequiredService<IWorkCoordinator>(), pod, workChannelWriter: null,
      new WorkCoordinatorOptions { LeaseSeconds = 30, AbandonStaleInstanceThresholdSeconds = 300, PartitionCount = 4 },
      sp.GetService<ILogger<ScopedWorkCoordinatorStrategy>>()));

    // The write seam: the real generated dispatcher resolves this gate lazily.
    services.AddSingleton<ISchemaReadyGate>(schemaGate);
    // The read seam: real lens registration inherits the barrier at resolution.
    services.AddSingleton<IReadModelsReadyGate>(readGate);
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services, typeof(WorkCoordinationDbContext), typeof(ProbeModel), "probe_model",
      new PostgresUpsertStrategy());

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var lifecycleOptions = new WhizbangLifecycleOptions();
    services.AddSingleton<IWhizbangLifecycleState>(sp => new WhizbangLifecycleState(
      new WhizbangLifecycleCoordinator(
        [recorder, new InstanceStateRunControl(
          sp.GetRequiredService<IServiceScopeFactory>(), pod,
          new LibraryVersionProvider("999.9.9"))],
        lifecycleOptions),
      lifecycleOptions));

    return services.BuildServiceProvider();
  }

  private static async Task _awaitPhaseAsync(
      IWhizbangLifecycleState lifecycle, LifecyclePhase phase, CancellationToken ct) {
    while (lifecycle.Phase != phase) {
      ct.ThrowIfCancellationRequested();
      await Task.Delay(25, ct);
    }
  }

  private async Task _migrateForRealAsync() {
    await using var ctx = _createDbContext();
    await ctx.EnsureWhizbangDatabaseInitializedAsync();
  }

  private async Task<long> _countOutboxRowsAsync(CancellationToken ct) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_outbox";
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
  }

  [Test]
  [Timeout(120000)]
  public async Task ColdBoot_TheSeamsRefuse_ThenTheLadderOpensInOrderAsync(CancellationToken cancellationToken) {
    var pod = new _pod();
    var schemaGate = new SchemaReadyGate();
    var readGate = new ReadModelsReadyGate();
    var recorder = new _recordingRunControl();
    await using var provider = _buildPodServices(pod, schemaGate, readGate, recorder);

    var lifecycle = provider.GetRequiredService<IWhizbangLifecycleState>();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    // Boot: the real ladder driver walks the phases off the real gates.
    var phaseWorker = new LifecyclePhaseWorker(lifecycle, schemaGate, readGate);
    await phaseWorker.StartAsync(cancellationToken);
    await _awaitPhaseAsync(lifecycle, LifecyclePhase.Migrating, cancellationToken);

    // (1) While Migrate is under way, BOTH seams refuse — through the real entry points,
    // against a database where the tables genuinely do not exist yet.
    await Assert.ThrowsAsync<WhizbangNotReadyException>(async () =>
      await dispatcher.PublishAsync(new LocalEventStorageTests.OutboxRoutedEvent(Guid.CreateVersion7(), "early")));
    await using (var earlyScope = provider.CreateAsyncScope()) {
      await Assert.ThrowsAsync<WhizbangNotReadyException>(async () => {
        _ = earlyScope.ServiceProvider.GetRequiredService<ILensQuery<ProbeModel>>();
        await Task.CompletedTask;
      });
    }

    // The REAL migration — the exact work the initializer performs before it marks the gate.
    await _migrateForRealAsync();
    schemaGate.MarkReady();
    await _awaitPhaseAsync(lifecycle, LifecyclePhase.AcceptingCommands, cancellationToken);

    // Identify happens after Migrate: the instance registers once the tables exist.
    await using (var scope = provider.CreateAsyncScope()) {
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
      await coordinator.RecordHeartbeatAsync(
        new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), cancellationToken);
      await coordinator.RecordInstanceStateAsync(
        pod.InstanceId, nameof(LifecyclePhase.AcceptingCommands), "999.9.9", cancellationToken);
    }

    // (2) AcceptingCommands: the WRITE side is live — the same publish that was refused now
    // lands DURABLY in the outbox — while the READ side keeps refusing.
    await dispatcher.PublishAsync(new LocalEventStorageTests.OutboxRoutedEvent(Guid.CreateVersion7(), "window"));
    await using (var flushScope = provider.CreateAsyncScope()) {
      await flushScope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>()
        .FlushAsync(WorkBatchOptions.None, cancellationToken);
    }
    await Assert.That(await _countOutboxRowsAsync(cancellationToken)).IsGreaterThan(0)
      .Because("accepted means DURABLE: the promise lives in wh_outbox before the read side opens");
    await using (var windowScope = provider.CreateAsyncScope()) {
      await Assert.ThrowsAsync<WhizbangNotReadyException>(async () => {
        _ = windowScope.ServiceProvider.GetRequiredService<ILensQuery<ProbeModel>>();
        await Task.CompletedTask;
      });
    }
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.AcceptingCommands);

    // (3) The read side releases (the driver's moment — owned by ReadModelsReadyDriverTests);
    // the ladder settles at Running and the lens serves.
    readGate.MarkReady();
    await _awaitPhaseAsync(lifecycle, LifecyclePhase.Running, cancellationToken);
    await using (var openScope = provider.CreateAsyncScope()) {
      var lens = openScope.ServiceProvider.GetRequiredService<ILensQuery<ProbeModel>>();
      await Assert.That(lens).IsNotNull()
        .Because("Running means the read side serves too");
    }

    // The ladder was walked in order — no rung skipped, none out of place.
    var ladder = string.Join(" -> ", recorder.Seen
      .Where(p => p is LifecyclePhase.Connecting or LifecyclePhase.Migrating
                    or LifecyclePhase.AcceptingCommands or LifecyclePhase.Running));
    await Assert.That(ladder).IsEqualTo("Connecting -> Migrating -> AcceptingCommands -> Running")
      .Because("the CQRS split is a ladder, not a toggle: the write side opens strictly before the read side");

    // And the fleet can see it: the instance row reports the settled phase for peers to read.
    var fleet = new EFCorePostgresStartupFleetStatusSource(
      provider.GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext));
    var instances = await fleet.GetFleetAsync(cancellationToken);
    var mine = instances.Single(i => i.InstanceId == pod.InstanceId);
    await Assert.That(mine.LifecyclePhase).IsEqualTo(nameof(LifecyclePhase.Running))
      .Because("the instance-state participant records each settled transition on the row");

    await phaseWorker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(120000)]
  public async Task ColdBoot_TwoPods_RealInitializersRace_BothPipelinesReachReadyAsync(CancellationToken cancellationToken) {
    // Two pods, each its own pipeline (real Assess over the real ledger, real Migrate over its own
    // gate), racing the REAL generated initializer against one cold database — the in-database
    // advisory lock is the only referee, exactly as in production.
    var podA = new _pod();
    var podB = new _pod();
    var gateA = new SchemaReadyGate();
    var gateB = new SchemaReadyGate();
    await using var providerA = _buildPodServices(podA, gateA, new ReadModelsReadyGate(), new _recordingRunControl());
    await using var providerB = _buildPodServices(podB, gateB, new ReadModelsReadyGate(), new _recordingRunControl());

    StartupPipelineRunner _runnerFor(ServiceProvider provider, SchemaReadyGate gate, StartupPipelineState state) {
      var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
      var assessor = new EFCorePostgresStartupAssessor(
        scopeFactory, typeof(WorkCoordinationDbContext), new LibraryVersionProvider("999.9.9"));
      return new StartupPipelineRunner(
        [new AssessStartupStep(assessor), new MigrateStartupStep(gate)], [state]);
    }

    var stateA = new StartupPipelineState();
    var stateB = new StartupPipelineState();
    var runnerA = _runnerFor(providerA, gateA, stateA);
    var runnerB = _runnerFor(providerB, gateB, stateB);

    async Task _bootAsync(SchemaReadyGate gate) {
      // The real initializer: both pods run it concurrently; the advisory lock elects the
      // migrator server-side, the loser re-checks hashes and no-ops.
      await _migrateForRealAsync();
      gate.MarkReady();
    }

    var results = await Task.WhenAll(
      runnerA.RunAsync(cancellationToken),
      runnerB.RunAsync(cancellationToken),
      Task.Run(async () => { await _bootAsync(gateA); return (IReadOnlyList<StartupStepResult>)[]; }, cancellationToken),
      Task.Run(async () => { await _bootAsync(gateB); return (IReadOnlyList<StartupStepResult>)[]; }, cancellationToken));

    foreach (var result in new[] { results[0], results[1] }) {
      await Assert.That(result.All(r => r.Outcome == StartupStepOutcome.Completed)).IsTrue()
        .Because("Assess reads the cold ledger (verdict: migrate) and Migrate completes on the gate");
    }
    await Assert.That(stateA.IsReady).IsTrue();
    await Assert.That(stateB.IsReady).IsTrue();

    // The race left one WORKING schema: every ledger row records success, and the data plane
    // it provisioned actually serves — a heartbeat through the real coordinator works.
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_schema_migrations WHERE status = -1";   // MigrationStatus.Failed
    await Assert.That((long)(await cmd.ExecuteScalarAsync(cancellationToken))!).IsEqualTo(0L)
      .Because("the advisory lock made the initializers take turns: no half-applied file survived the race");
    await using var postRaceScope = providerA.CreateAsyncScope();
    var postRaceCoordinator = postRaceScope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await postRaceCoordinator.RecordHeartbeatAsync(
      new HeartbeatRequest(podA.InstanceId, podA.ServiceName, podA.HostName, 1), cancellationToken);
  }
}
