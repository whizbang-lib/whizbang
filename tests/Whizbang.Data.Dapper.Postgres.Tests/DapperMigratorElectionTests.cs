// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper driver elects one schema migrator, as the EF Core driver does, through the one shared election (#2561):
/// one instance migrates, every other instance waits on the migrator's duty and then serves without a pass of its own,
/// and a waiter whose migrator dies mid-migration takes the work over.
/// </summary>
/// <remarks>
/// Real database, no real clock in the test: each host's clock is a <see cref="FakeTimeProvider"/> nobody advances, so
/// the migrator's keepalive never renews and never stalls, and the migrator's lease is an hour, so only what a test does
/// (the migrator finishing, or its death judged by the one liveness rule on the database clock) ends its holding. The
/// migrator is held mid-pass on the step observer, the seam both drivers report each committed step through.
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/MigratorElection.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperSchemaInitializationRunner.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
[Category("Integration")]
public class DapperMigratorElectionTests {
  private const string TABLE = "wh_per_election_probe";
  private static readonly KeyValuePair<string, string>[] _entries = [
    new("ElectionProbe", $"CREATE TABLE IF NOT EXISTS {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL);"),
  ];

  /// <summary>A step late enough in the pass that every function the liveness rule and the reap use exists.</summary>
  private const string HOLD_FROM_STEP = "400";

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_election");
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  /// <summary>One instance's connection string: its own application name, so its sessions can be found and ended.</summary>
  private string _connectionFor(string pod) =>
    new NpgsqlConnectionStringBuilder(_database.ConnectionString) { ApplicationName = pod }.ConnectionString;

  /// <summary>One instance, as a host builds it: the Dapper driver, a frozen clock, an hour's migrator lease, an observer.</summary>
  private ServiceProvider _instance(string pod, StepObserver observer) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<TimeProvider>(new FakeTimeProvider());
    services.AddSingleton<ISchemaInitializationObserver>(observer);
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    services.Configure<RoleAssignmentOptions>(o => o.RoleLeases[StartupDuties.MIGRATOR] = TimeSpan.FromHours(1));
    services.AddWhizbangPostgres(
      _connectionFor(pod), JsonContextRegistry.CreateCombinedOptions(), initializeSchema: true, _entries, managedObjects: null);
    return services.BuildServiceProvider();
  }

  private static WhizbangDatabaseInitializerService _initializer(IServiceProvider provider) =>
    provider.GetServices<IHostedService>().OfType<WhizbangDatabaseInitializerService>().Single();

  private async Task<Guid?> _migratorHolderAsync() {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    return await db.ExecuteScalarAsync<Guid?>(
      "SELECT holder_instance_id FROM wh_role_assignments WHERE role = @role", new { role = StartupDuties.MIGRATOR });
  }

  private async Task<bool> _tableExistsAsync(string table) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    return await db.ExecuteScalarAsync<bool>("SELECT to_regclass(@t) IS NOT NULL", new { t = $"public.{table}" });
  }

  /// <summary>
  /// Two instances starting together: the first is elected migrator and is held mid-pass; the second registers, votes,
  /// loses, and waits on the migrator's duty with its gate shut. Once the migrator finishes, the second serves the
  /// schema the migrator built, having applied no step of its own, and nobody holds the duty any more.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task TwoInstancesStarting_OneMigrates_TheOtherWaitsForIt_ThenServesAsync(CancellationToken cancellationToken) {
    var migratorSteps = new StepObserver(holdFrom: HOLD_FROM_STEP);
    var waiterSteps = new StepObserver(holdFrom: null);
    await using var migrator = _instance("pod-migrator", migratorSteps);
    await using var waiter = _instance("pod-waiter", waiterSteps);

    var migrating = _initializer(migrator).StartAsync(cancellationToken);
    await migratorSteps.Held.WaitAsync(cancellationToken);
    await Assert.That(await _migratorHolderAsync())
      .IsEqualTo(migrator.GetRequiredService<IServiceInstanceProvider>().InstanceId)
      .Because("the first instance on an empty database is elected migrator through the bootstrap closure");

    var waiting = _initializer(waiter).StartAsync(cancellationToken);
    await waiterSteps.Contended.WaitAsync(cancellationToken);
    await Assert.That(waiter.GetRequiredService<ISchemaReadyGate>().IsReady).IsFalse()
      .Because("a waiter never serves a schema its migrator has not finished");

    migratorSteps.Release();
    await migrating;
    await waiting;

    await Assert.That(migrator.GetRequiredService<ISchemaReadyGate>().IsReady).IsTrue();
    await Assert.That(waiter.GetRequiredService<ISchemaReadyGate>().IsReady).IsTrue();
    await Assert.That(migratorSteps.Committed.Count).IsGreaterThan(0);
    await Assert.That(waiterSteps.Committed).IsEmpty()
      .Because("the waiter waited for the migrator and then served: it took no schema lock and applied no step");
    await Assert.That(await _tableExistsAsync(TABLE)).IsTrue();
    await Assert.That(await _migratorHolderAsync()).IsNull()
      .Because("the migrator gives the duty back when its initialization returns");
  }

  /// <summary>
  /// The migrator dies mid-migration: its sessions end (which releases the schema lock it held) and the one liveness
  /// rule reaps it, which voids its duty however long its lease. The waiter notices, votes for the duty, wins it,
  /// finishes the pass itself and serves; the dead migrator never opens its gate.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task AMigratorThatDiesMidMigration_HandsTheWorkToTheWaiterAsync(CancellationToken cancellationToken) {
    var migratorSteps = new StepObserver(holdFrom: HOLD_FROM_STEP);
    var waiterSteps = new StepObserver(holdFrom: null);
    await using var migrator = _instance("pod-dying", migratorSteps);
    await using var waiter = _instance("pod-survivor", waiterSteps);
    using var migratorLife = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var migrating = _initializer(migrator).StartAsync(migratorLife.Token);
    await migratorSteps.Held.WaitAsync(cancellationToken);
    var waiting = _initializer(waiter).StartAsync(cancellationToken);
    await waiterSteps.Contended.WaitAsync(cancellationToken);
    var dead = migrator.GetRequiredService<IServiceInstanceProvider>().InstanceId;
    await Assert.That(await _migratorHolderAsync()).IsEqualTo(dead)
      .Because("the waiter is waiting on an elected migrator, not merely on the schema lock");

    // The migrator's pod dies: every session it had ends, and the fleet's reap judges it dead on the database clock.
    await using (var killer = new NpgsqlConnection(_database.ConnectionString)) {
      await killer.ExecuteAsync(
        "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'pod-dying' AND datname = current_database()");
      await killer.ExecuteAsync(
        "UPDATE wh_service_instances SET last_heartbeat_at = now() - INTERVAL '1 hour' WHERE instance_id = @dead",
        new { dead });
      await killer.ExecuteScalarAsync<long>("SELECT count(*) FROM cleanup_stale_instances(now())");
    }

    await waiting;

    await Assert.That(waiter.GetRequiredService<ISchemaReadyGate>().IsReady).IsTrue();
    await Assert.That(waiterSteps.Committed.Count).IsGreaterThan(0)
      .Because("the survivor took over the steps the dead migrator left outstanding");
    await Assert.That(await _tableExistsAsync(TABLE)).IsTrue();
    await Assert.That(await _migratorHolderAsync()).IsNull()
      .Because("the survivor held the duty it took over only until its initialization returned");

    // The dead pod's process, still parked on its observer, is stopped; it never served.
    await migratorLife.CancelAsync();
    var outcome = await Assert.That(async () => await migrating).ThrowsException();
    await Assert.That(outcome).IsNotNull();
    await Assert.That(migrator.GetRequiredService<ISchemaReadyGate>().IsReady).IsFalse();
  }

  /// <summary>
  /// The duty a schema initialization holds is created on the host's clock and role options, and holds nothing until an
  /// election decides: with no host at all it reads the defaults.
  /// </summary>
  [Test]
  public async Task CreateDuty_ReadsTheHostsClockAndRoleOptions_AndHoldsNothingAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<TimeProvider>(new FakeTimeProvider());
    services.Configure<RoleAssignmentOptions>(o => o.MigratorStallLimit = TimeSpan.FromMinutes(3));
    await using var host = services.BuildServiceProvider();

    await using var hosted = MigratorElection.CreateDuty(host, 600);
    await using var bare = MigratorElection.CreateDuty(null, 600);

    foreach (var duty in new[] { hosted, bare }) {
      await Assert.That(duty.Stage).IsEqualTo(SchemaStage.Unstaged);
      await Assert.That(duty.Grant).IsNull();
      await Assert.That(duty.IsKeptAlive).IsFalse().Because("nothing is renewed before an election grants a duty");
    }
  }

  /// <summary>The election as a driver calls it, over this test's database, with the given closure.</summary>
  private MigratorElectionRequest _request(IReadOnlyList<(string Name, string Sql)> closure) => new(
    () => new NpgsqlConnection(_database.ConnectionString), "public", SchemaInitializationLockKey.Compute("public"), closure,
    600, (_, _) => Task.FromResult(false));

  /// <summary>The whole closure the Dapper driver applies, rendered for <c>public</c>.</summary>
  private async Task<IReadOnlyList<(string Name, string Sql)>> _closureAsync() =>
    (await new PostgresSchemaInitializer(_database.ConnectionString).ElectionClosureAsync(CancellationToken.None)).Scripts;

  private static ServiceProvider _host(IServiceInstanceProvider identity, IStartupStageReporter? stages = null) {
    var services = new ServiceCollection();
    services.AddSingleton(identity);
    if (stages is not null) {
      services.AddSingleton(stages);
    }
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// A closure that leaves the database unable to hold an election (here, only migration 000's region) is not a
  /// failure: the instance is unstaged, migrates under the schema lock as before any election existed, and Elect is
  /// reported skipped.
  /// </summary>
  [Test]
  public async Task AClosureThatCannotElect_LeavesTheInstanceUnstaged_AndElectSkippedAsync() {
    var closure = (await _closureAsync()).Where(s => s.Name.StartsWith("000", StringComparison.Ordinal)).ToList();
    var stages = new RecordedStages();
    await using var host = _host(new Identity("svc"), stages);
    await using var duty = MigratorElection.CreateDuty(host, 600);

    var staging = await MigratorElection.StageAsync(duty, _request(closure), host, logger: null, CancellationToken.None);

    await Assert.That(closure).Count().IsEqualTo(1).Because("a positive control: the closure is cut to migration 000's region");
    await Assert.That(staging.Stage).IsEqualTo(SchemaStage.Unstaged);
    await Assert.That(stages.Calls).IsEquivalentTo(["Skip Elect"]).Because("nothing was registered or voted for");
  }

  /// <summary>
  /// An instance the registry refuses (its row cannot be written) is never elected and never fails the start: it is
  /// unstaged, so it migrates under the schema lock, and says why.
  /// </summary>
  [Test]
  public async Task AnInstanceTheRegistryRefuses_IsUnstaged_NotFailedAsync() {
    var stages = new RecordedStages();
    await using var host = _host(new Identity(serviceName: null!), stages);
    await using var duty = MigratorElection.CreateDuty(host, 600);

    var staging = await MigratorElection.StageAsync(duty, _request(await _closureAsync()), host, logger: null, CancellationToken.None);

    await Assert.That(staging.Stage).IsEqualTo(SchemaStage.Unstaged);
    await Assert.That(staging.Detail).IsEqualTo("this instance could not join the registry");
    await Assert.That(stages.Calls).IsEquivalentTo(["Enter Preflight", "Skip Elect"]);
  }

  /// <summary>
  /// Anything the election did not foresee (here, the stage reporter failing as the vote begins) is kept out of the
  /// startup path: the instance is unstaged and holds no duty.
  /// </summary>
  [Test]
  public async Task AnUnforeseenFailureInTheElection_LeavesTheInstanceUnstaged_HoldingNothingAsync() {
    var stages = new RecordedStages { FailOnVote = true };
    await using var host = _host(new Identity("svc"), stages);
    await using var duty = MigratorElection.CreateDuty(host, 600);

    var staging = await MigratorElection.StageAsync(duty, _request(await _closureAsync()), host, logger: null, CancellationToken.None);

    await Assert.That(staging.Stage).IsEqualTo(SchemaStage.Unstaged);
    await Assert.That(duty.Grant).IsNull();
    await Assert.That(stages.Calls).IsEquivalentTo(["Enter Preflight", "Enter Elect", "Skip Elect"]);
  }

  private sealed class Identity(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    public string ServiceName => serviceName;
    public string HostName => "election-host";
    public int ProcessId => 7;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  /// <summary>Records each stage by kind and name; optionally fails as the vote begins.</summary>
  private sealed class RecordedStages : IStartupStageReporter {
    private readonly ConcurrentQueue<string> _calls = new();

    public bool FailOnVote { get; init; }

    public List<string> Calls => [.. _calls];

    public StartupStageSnapshot? Current => null;

    public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) {
      _calls.Enqueue($"Enter {stage}");
      if (FailOnVote && stage == StartupStage.Elect) {
        throw new InvalidOperationException("the stage reporter failed as the vote began");
      }
      return ValueTask.CompletedTask;
    }

    public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) {
      _calls.Enqueue($"Skip {stage}");
      return ValueTask.CompletedTask;
    }

    public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  /// <summary>
  /// Watches one instance's schema initialization: each committed step, and the wait for another instance. Holds the
  /// pass at the first step at or past <c>holdFrom</c> until released, standing in for a migrator mid-migration.
  /// </summary>
  private sealed class StepObserver(string? holdFrom) : ISchemaInitializationObserver {
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _contended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<string> Committed { get; } = new();

    public Task Held => _held.Task;

    public Task Contended => _contended.Task;

    public void Release() => _release.TrySetResult();

    public ValueTask OnSchemaLockContendedAsync(string schema, CancellationToken cancellationToken) {
      _contended.TrySetResult();
      return ValueTask.CompletedTask;
    }

    public async ValueTask OnSchemaStepCommittedAsync(string schema, string stepName, CancellationToken cancellationToken) {
      Committed.Enqueue(stepName);
      if (holdFrom is not null && string.CompareOrdinal(stepName, holdFrom) >= 0 && _held.TrySetResult()) {
        await _release.Task.WaitAsync(cancellationToken);
      }
    }
  }
}
