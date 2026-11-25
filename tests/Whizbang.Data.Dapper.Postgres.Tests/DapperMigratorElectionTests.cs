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
  private ServiceProvider _instance(string pod, StepObserver observer, StageLog? stages = null, HoldingElector? elector = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    if (stages is not null) {
      services.AddSingleton<IStartupStageReporter>(stages);
    }
    services.AddSingleton<TimeProvider>(new FakeTimeProvider());
    services.AddSingleton<ISchemaInitializationObserver>(observer);
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    services.Configure<RoleAssignmentOptions>(o => o.RoleLeases[StartupDuties.MIGRATOR] = TimeSpan.FromHours(1));
    services.AddWhizbangPostgres(
      _connectionFor(pod), JsonContextRegistry.CreateCombinedOptions(), initializeSchema: true, _entries, managedObjects: null);
    if (elector is not null) {
      services.AddSingleton<IDutyElector>(sp => elector.Over(sp.GetRequiredService<Whizbang.Data.Postgres.Notifications.PgRoleElector>()));
    }
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
  /// loses, and waits on the migrator's duty with its gate shut. The migrator is then held again as it gives its duty
  /// back, its schema work done, so the waiter's next look can only find the schema current while the duty is still held:
  /// it takes the migrated-elsewhere start, applies no step, and serves. Once the migrator lets go, nobody holds the duty.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task TwoInstancesStarting_OneMigrates_TheOtherWaitsForIt_ThenServesAsync(CancellationToken cancellationToken) {
    var migratorSteps = new StepObserver(holdFrom: HOLD_FROM_STEP);
    var waiterSteps = new StepObserver(holdFrom: null);
    var migratorDuty = new HoldingElector();
    var waiterStages = new StageLog();
    await using var migrator = _instance("pod-migrator", migratorSteps, elector: migratorDuty);
    await using var waiter = _instance("pod-waiter", waiterSteps, waiterStages);

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
    await migratorDuty.Releasing.WaitAsync(cancellationToken);
    await waiting;
    var waiterServedWhileTheDutyWasHeld = waiter.GetRequiredService<ISchemaReadyGate>().IsReady;
    migratorDuty.LetGo();
    await migrating;

    await Assert.That(waiterStages.Messages.Any(m => m.Contains("another instance migrated the schema", StringComparison.Ordinal))).IsTrue()
      .Because("the migrator still held its duty with the schema current, so the waiter took the migrated-elsewhere start; "
        + "the waiter's stages: " + string.Join(" | ", waiterStages.Messages));
    await Assert.That(waiterServedWhileTheDutyWasHeld).IsTrue();
    await Assert.That(migrator.GetRequiredService<ISchemaReadyGate>().IsReady).IsTrue();
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
    var waiterStages = new StageLog();
    await using var migrator = _instance("pod-dying", migratorSteps);
    await using var waiter = _instance("pod-survivor", waiterSteps, waiterStages);
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
    await Assert.That(waiterStages.Messages.Any(m => m.Contains("another instance migrated the schema", StringComparison.Ordinal))).IsFalse()
      .Because("the survivor found the migrator gone before the schema was current, so it voted and ran the pass itself");
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

  /// <summary>
  /// A waiter whose migrator is gone (no duty is held, the schema is still behind) votes before it works, pauses before
  /// each vote after the first so a vote that keeps losing does not spin, and works once a vote leaves it free to.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task AWaiterWhoseMigratorIsGone_VotesAgainUntilItMayWork_PausingBetweenVotesAsync(CancellationToken cancellationToken) {
    var elector = new ScriptedElector(
      DutyAttempt.Lost(DutyRefusal.Contended, "another instance won"),
      DutyAttempt.Lost(DutyRefusal.Contended, "another instance won again"));
    var services = new ServiceCollection();
    services.AddSingleton<IDutyElector>(elector);
    await using var host = services.BuildServiceProvider();
    await using var duty = MigratorElection.CreateDuty(host, 600);
    await duty.HoldAsync(new SchemaStaging(SchemaStage.Waiter, null, "another instance holds the migrator duty"));
    var worked = 0;

    var migratedElsewhere = await MigratorElection.RunPreLockAsync(
      duty, _request([]), _ => { worked++; return Task.CompletedTask; }, host, logger: null, cancellationToken);

    await Assert.That(elector.Votes).IsEqualTo(3)
      .Because("two votes lost to another instance, and the third could not be decided, which leaves it unstaged");
    await Assert.That(worked).IsEqualTo(1).Because("an unstaged instance does the work, once");
    await Assert.That(migratedElsewhere).IsFalse().Because("this instance did the work, so its pass follows");
  }

  /// <summary>
  /// An instance that could not be staged waits while another session holds the schema lock (a sibling's bootstrap or
  /// DDL), says so, and works once the lock is released.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task AnUnstagedInstance_WaitsWhileTheSchemaLockIsHeld_ThenWorksAsync(CancellationToken cancellationToken) {
    var observer = new StepObserver(holdFrom: null);
    var services = new ServiceCollection();
    services.AddSingleton<ISchemaInitializationObserver>(observer);
    await using var host = services.BuildServiceProvider();
    await using var duty = MigratorElection.CreateDuty(host, 600);
    await using var holder = new NpgsqlConnection(_database.ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using var held = await holder.BeginTransactionAsync(cancellationToken);
    await holder.ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", new { key = SchemaInitializationLockKey.Compute("public") }, held);
    var worked = false;

    var running = MigratorElection.RunPreLockAsync(
      duty, _request([]), _ => { worked = true; return Task.CompletedTask; }, host, logger: null, cancellationToken);
    await observer.Contended.WaitAsync(cancellationToken);
    await Assert.That(worked).IsFalse().Because("the holder of the schema lock is doing the schema work right now");

    await held.CommitAsync(cancellationToken);
    var migratedElsewhere = await running;

    await Assert.That(worked).IsTrue().Because("the wait ends when the lock is released, and the work follows");
    await Assert.That(migratedElsewhere).IsFalse();
  }

  private static readonly DateTime _untouched = new(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>This build's registry for the probe perspective: one model, folded by one named perspective.</summary>
  private static readonly Whizbang.Core.Perspectives.PerspectiveRegistryPayload _registry = new("election-svc", """
    [{"ClrTypeName":"Tests.ElectionProbe","TableName":"wh_per_election_probe","SchemaJson":{"columns":["id","data"]},"SchemaHash":"probe-hash","ServiceName":"election-svc","PerspectiveName":"ElectionProbePerspective"}]
    """);

  /// <summary>A schema a migrator of this build finished, registry included, with the registry row's stamp set aside.</summary>
  private async Task _migratedByThisBuildAsync() {
    await new PostgresSchemaInitializer(_database.ConnectionString, _entries) { PerspectiveRegistry = [_registry] }
      .InitializeSchemaAsync(CancellationToken.None);
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.ExecuteAsync("UPDATE wh_perspective_registry SET updated_at = @at", new { at = _untouched });
  }

  private async Task<(DateTime UpdatedAt, string[] Names)> _registryRowAsync() {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    return await db.QuerySingleAsync<(DateTime, string[])>(
      "SELECT updated_at, perspective_names FROM wh_perspective_registry WHERE clr_type_name = 'Tests.ElectionProbe'");
  }

  /// <summary>The check every start's converge step runs, over this test's database.</summary>
  private Task<bool> _checkRegistryAsync(
      IReadOnlyList<Whizbang.Core.Perspectives.PerspectiveRegistryPayload> registry, IServiceProvider? host = null,
      CancellationToken cancellationToken = default) =>
    PerspectiveRegistryCurrency.ReconcileIfStaleAsync(
      () => new NpgsqlConnection(_database.ConnectionString), "public", registry, 600, host, logger: null, cancellationToken);

  /// <summary>
  /// A waiter whose migrator was this build finds the stored registry already matching what it declares, and leaves it
  /// alone: the reconcile stamps every row it touches, and the stamp is the one set aside.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task AWaiterBehindASameBuildMigrator_DoesNoRegistryReconcileAsync(CancellationToken cancellationToken) {
    await _migratedByThisBuildAsync();

    var reconciled = await _checkRegistryAsync([_registry], cancellationToken: cancellationToken);

    await Assert.That(reconciled).IsFalse();
    await Assert.That((await _registryRowAsync()).UpdatedAt).IsEqualTo(_untouched)
      .Because("the stored registry already matches this build, so nothing was reconciled");
  }

  /// <summary>
  /// A waiter whose migrator left a registry that differs from this build in a way no hash covers (here, the
  /// perspective that folds the model was renamed) reconciles it, so the registry says what this build declares.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task AWaiterBehindAMigratorWithAnotherRegistry_ReconcilesItAsync(CancellationToken cancellationToken) {
    await _migratedByThisBuildAsync();
    await using (var db = new NpgsqlConnection(_database.ConnectionString)) {
      await db.ExecuteAsync("UPDATE wh_perspective_registry SET perspective_names = ARRAY['FormerProbePerspective']");
    }

    var reconciled = await _checkRegistryAsync([_registry], cancellationToken: cancellationToken);

    var (updatedAt, names) = await _registryRowAsync();
    await Assert.That(reconciled).IsTrue();
    await Assert.That(names).IsEquivalentTo(["ElectionProbePerspective"]).Because("the waiter reconciled to its own build");
    await Assert.That(updatedAt).IsNotEqualTo(_untouched);
  }

  /// <summary>
  /// Two waiters do not both reconcile: the reconcile runs under the schema lock and checks again once it holds it, so a
  /// waiter that waited for another's reconcile finds the registry current and does nothing.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task AWaiterThatWaitsForAnothersReconcile_ChecksAgainUnderTheLock_AndDoesNothingAsync(CancellationToken cancellationToken) {
    await _migratedByThisBuildAsync();
    await using (var db = new NpgsqlConnection(_database.ConnectionString)) {
      await db.ExecuteAsync("UPDATE wh_perspective_registry SET perspective_names = ARRAY['FormerProbePerspective']");
    }
    var contention = new ContentionCounter(waitFor: 1);
    var services = new ServiceCollection();
    services.AddSingleton<ISchemaInitializationObserver>(contention);
    await using var host = services.BuildServiceProvider();

    // The other waiter: it holds the schema lock and reconciles in its transaction.
    await using var other = new NpgsqlConnection(_database.ConnectionString);
    await other.OpenAsync(cancellationToken);
    await using var reconciling = await other.BeginTransactionAsync(cancellationToken);
    await other.ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", new { key = SchemaInitializationLockKey.Compute("public") }, reconciling);
    var waiting = _checkRegistryAsync([_registry], host, cancellationToken);
    await Assert.That(await Task.WhenAny(contention.Reached, waiting)).IsSameReferenceAs(contention.Reached)
      .Because("a stale registry is reconciled under the schema lock, so this waiter waits for the holder of it");
    var otherStamp = await other.ExecuteScalarAsync<DateTime>("SELECT now()", transaction: reconciling);
    await other.ExecuteAsync(
      "SELECT count(*) FROM reconcile_perspective_registry(@p::jsonb, @s)", new { p = _registry.Json, s = _registry.ServiceName },
      reconciling);
    await reconciling.CommitAsync(cancellationToken);

    await Assert.That(await waiting).IsFalse();
    await Assert.That((await _registryRowAsync()).UpdatedAt).IsEqualTo(otherStamp)
      .Because("the other waiter's reconcile is the only one: this one found the registry current under the lock");
  }

  /// <summary>A registry that cannot be read or reconciled is said, and never fails the start: it is informational.</summary>
  [Test]
  [Timeout(120_000)]
  public async Task ARegistryThatCannotBeReconciled_IsNotFatalAsync(CancellationToken cancellationToken) {
    await _migratedByThisBuildAsync();

    var reconciled = await _checkRegistryAsync([new("election-svc", "not json")], cancellationToken: cancellationToken);

    await Assert.That(reconciled).IsFalse();
    await Assert.That((await _registryRowAsync()).UpdatedAt).IsEqualTo(_untouched);
  }

  /// <summary>
  /// The same check on a connection the caller owns (#2578), as an EF Core host with no separate connection runs it: a
  /// failure is said and is not fatal, and the connection it opened is closed again, not disposed.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task ABorrowedConnection_WhoseRegistryCannotBeReconciled_IsNotFatal_AndIsClosedAgainAsync(
      CancellationToken cancellationToken) {
    await _migratedByThisBuildAsync();
    await using var borrowed = new NpgsqlConnection(_database.ConnectionString);

    var reconciled = await PerspectiveRegistryCurrency.ReconcileIfStaleAsync(
      borrowed, "public", [new("election-svc", "not json")], 600, services: null, logger: null, cancellationToken);

    await Assert.That(reconciled).IsFalse();
    await Assert.That((await _registryRowAsync()).UpdatedAt).IsEqualTo(_untouched);
    await Assert.That(borrowed.State).IsEqualTo(System.Data.ConnectionState.Closed)
      .Because("the connection it opened is closed again, as it was found");
    await borrowed.OpenAsync(cancellationToken);
    await Assert.That(borrowed.State).IsEqualTo(System.Data.ConnectionState.Open)
      .Because("closed, not disposed: the caller still owns it");
  }

  private PostgresSchemaInitializer _thisBuild(bool applicationHeld = false) =>
    new(_database.ConnectionString, _entries) { PerspectiveRegistry = [_registry], ApplicationHeld = applicationHeld };

  private async Task _registryFromAnotherBuildAsync() {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.ExecuteAsync("UPDATE wh_perspective_registry SET schema_hash = 'from-another-build'");
  }

  private async Task<string> _storedSchemaHashAsync() {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    return await db.ExecuteScalarAsync<string>(
      "SELECT schema_hash FROM wh_perspective_registry WHERE clr_type_name = 'Tests.ElectionProbe'") ?? "";
  }

  /// <summary>
  /// A start on a schema Assess found current runs no pass, so it checks the registry: one that matches this build is
  /// left alone (it used to be reconciled, and every row stamped, on every start).
  /// </summary>
  [Test]
  public async Task ACurrentStart_AgainstAMatchingRegistry_TouchesNothingAsync() {
    await _migratedByThisBuildAsync();

    await _thisBuild().ConvergeCurrentAsync(CancellationToken.None);

    await Assert.That((await _registryRowAsync()).UpdatedAt).IsEqualTo(_untouched);
  }

  /// <summary>
  /// A start on a schema Assess found current, over a registry another build wrote (a restored database: every hash
  /// matches, and no hash covers the registry), reconciles it to this build.
  /// </summary>
  [Test]
  public async Task ACurrentStart_AgainstARegistryNoHashCovers_ReconcilesItAsync() {
    await _migratedByThisBuildAsync();
    await _registryFromAnotherBuildAsync();

    await _thisBuild().ConvergeCurrentAsync(CancellationToken.None);

    await Assert.That(await _storedSchemaHashAsync()).IsEqualTo("probe-hash");
  }

  /// <summary>A waiter whose migrator finished converges, and reconciles a registry another build's migrator left.</summary>
  [Test]
  public async Task AWaitersConverge_AgainstARegistryNoHashCovers_ReconcilesItAsync() {
    await _migratedByThisBuildAsync();
    await _registryFromAnotherBuildAsync();

    await _thisBuild().ConvergeMigratedAsync(CancellationToken.None);

    await Assert.That(await _storedSchemaHashAsync()).IsEqualTo("probe-hash");
  }

  /// <summary>A start that holds the application's schema (#2553) leaves its registry as it is, differing or not.</summary>
  [Test]
  public async Task AStartHoldingTheApplication_LeavesTheRegistryAsItIsAsync() {
    await _migratedByThisBuildAsync();
    await _registryFromAnotherBuildAsync();

    await _thisBuild(applicationHeld: true).ConvergeMigratedAsync(CancellationToken.None);

    await Assert.That(await _storedSchemaHashAsync()).IsEqualTo("from-another-build");
  }

  /// <summary>Completes once the schema lock has been reported contended the given number of times.</summary>
  private sealed class ContentionCounter(int waitFor) : ISchemaInitializationObserver {
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _seen;

    public Task Reached => _reached.Task;

    public ValueTask OnSchemaLockContendedAsync(string schema, CancellationToken cancellationToken) {
      if (Interlocked.Increment(ref _seen) >= waitFor) {
        _reached.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Answers each vote from a script; once the script runs out, it cannot be asked.</summary>
  private sealed class ScriptedElector(params DutyAttempt[] answers) : IDutyElector {
    private int _votes;

    public int Votes => _votes;

    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      var vote = Interlocked.Increment(ref _votes);
      return vote <= answers.Length
        ? Task.FromResult(answers[vote - 1])
        : throw new InvalidOperationException("the elector has no connection of its own");
    }
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
  /// <summary>Records each stage an instance enters or skips, with its message, as the host's stage reporter.</summary>
  private sealed class StageLog : IStartupStageReporter {
    private readonly ConcurrentQueue<string> _messages = new();

    public List<string> Messages => [.. _messages];

    public StartupStageSnapshot? Current { get; private set; }

    public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) {
      Current = new StartupStageSnapshot(stage, message, false, DateTimeOffset.UnixEpoch);
      _messages.Enqueue($"{stage}: {message}");
      return ValueTask.CompletedTask;
    }

    public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) {
      _messages.Enqueue($"{stage}: {reason}");
      return ValueTask.CompletedTask;
    }

    public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  /// <summary>
  /// The host's elector, with the migrator duty's grant held as it is given back until the test lets go: the instance's
  /// schema work is done and it still holds the duty.
  /// </summary>
  private sealed class HoldingElector {
    private readonly TaskCompletionSource _releasing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _letGo = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Releasing => _releasing.Task;

    public void LetGo() => _letGo.TrySetResult();

    public IDutyElector Over(IDutyElector inner) => new Elector(inner, this);

    private sealed class Elector(IDutyElector inner, HoldingElector hold) : IDutyElector {
      public async Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
        var attempt = await inner.TryAcquireAsync(duty, cancellationToken);
        return attempt.Grant is { } grant && duty == StartupDuties.MIGRATOR ? DutyAttempt.Granted(new Grant(grant, hold)) : attempt;
      }
    }

    private sealed class Grant(IDutyGrant inner, HoldingElector hold) : IDutyGrant {
      public string Duty => inner.Duty;
      public DateTimeOffset AcquiredAt => inner.AcquiredAt;
      public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => inner.VerifyStillHeldAsync(cancellationToken);

      public async ValueTask DisposeAsync() {
        hold._releasing.TrySetResult();
        await hold._letGo.Task;
        await inner.DisposeAsync();
      }
    }
  }

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
