// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Signals;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Alive versus eligible for work, end to end (#1638): instances composed the way a booting host composes them, on a
/// real database. Each instance has the real lifecycle machine with the real stage participant and stage reporter (the
/// one writer of the row's stage, phase and version since #1636), the real lifecycle driver walking the phases off its own startup gate, the real heartbeat worker, the real bootstrap
/// registration the migrator election makes, and (where the assignment matters) the real elected partition assigner.
/// Claims go through the real coordinator with the arguments the claim loop passes.
/// </summary>
/// <remarks>
/// <para>
/// An instance is "held in startup" by leaving its startup gate closed: its lifecycle stays at Migrating and its row
/// records the Migrate stage, exactly where a long migration pass leaves it, while its heartbeat keeps it alive. That is the state in which the stall happened:
/// alive, registered, and counted for work it could not claim.
/// </para>
/// <para>
/// Every wait is on the signal for the exact transition asserted: the stage participant having recorded a phase
/// (observed by wrapping the real participant, never replacing it), the heartbeat worker's recorded beat, and the
/// assigner's publish. The deadlines only bound those waits.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/229_OnlyReadyInstancesAreEligibleForWork.sql</code-under-test>
/// <code-under-test>src/Whizbang.Core/RunControl/WhizbangLifecycleState.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PartitionAssigner.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaBootstrapPhase.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class AliveVersusEligibleE2ETests : EFCoreTestBase {
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(60);
  private static readonly int[] _oddPartitions = [1, 3, 5, 7, 9];
  private static readonly int[] _allPartitions = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

  // --- the held instance is alive, never eligible, while a ready one takes everything ---------

  [Test]
  [Timeout(120000)]
  public async Task AnInstanceHeldInStartup_IsAliveButNeverACandidateNorRanked_WhileAReadyPeerTakesAllWorkAsync(
      CancellationToken cancellationToken) {
    var (readyId, heldId) = _pair();
    await using var ready = await _startReadyAsync(readyId, cancellationToken);
    await using var held = await _startHeldAsync(heldId, cancellationToken);
    await _seedOutboxAsync("backlog", cancellationToken);

    var candidates = await _candidatesAsync(cancellationToken);
    await ready.ClaimAsync(assignment: null, cancellationToken);

    await Assert.That(await _isAliveAsync(heldId, cancellationToken)).IsTrue()
      .Because("the held instance beats: it is alive, which is what keeps it from being reaped");
    await Assert.That(await _phaseAsync(heldId, cancellationToken)).IsEqualTo(nameof(LifecyclePhase.Migrating));
    await Assert.That(candidates).Contains(readyId);
    await Assert.That(candidates).DoesNotContain(heldId)
      .Because("alive is not ready: a share assigned to an instance still migrating stalls until it is ready");
    await Assert.That(await _leasedPartitionsAsync(readyId, "backlog", cancellationToken)).IsEquivalentTo(_allPartitions)
      .Because("the ready instance does not rank the held one, so it covers every partition");
    await Assert.That(await _leasedPartitionsAsync(heldId, "backlog", cancellationToken)).IsEmpty();
  }

  // --- eligibility begins exactly at AcceptingCommands ------------------------------------------

  [Test]
  [Timeout(120000)]
  public async Task AHeldInstance_BecomesEligibleExactlyWhenItRecordsAcceptingCommandsAsync(CancellationToken cancellationToken) {
    var (readyId, heldId) = _pair();
    await using var ready = await _startReadyAsync(readyId, cancellationToken);
    await using var held = await _startHeldAsync(heldId, cancellationToken);
    var whileHeld = await _candidatesAsync(cancellationToken);

    // The cause: the instance's migration pass ends. The wait: the stage participant recorded the transition.
    held.StartupGate.MarkReady();
    await held.Recorded(LifecyclePhase.AcceptingCommands).WaitAsync(_signalDeadline, cancellationToken);
    var onceAccepting = await _candidatesAsync(cancellationToken);
    await _seedOutboxAsync("after-ready", cancellationToken);
    await ready.ClaimAsync(assignment: null, cancellationToken);

    await Assert.That(whileHeld).DoesNotContain(heldId);
    await Assert.That(onceAccepting).Contains(heldId)
      .Because("the AcceptingCommands transition is the moment its claim loop can take its share");
    await Assert.That(await _phaseAsync(heldId, cancellationToken)).IsEqualTo(nameof(LifecyclePhase.AcceptingCommands));
    await Assert.That(await _leasedPartitionsAsync(readyId, "after-ready", cancellationToken)).IsEquivalentTo(_oddPartitions)
      .Because("the newly ready peer is ranked, so the ready instance leaves it the even partitions");
  }

  // --- draining ends eligibility at once --------------------------------------------------------

  [Test]
  [Timeout(120000)]
  public async Task ADrainingInstance_StopsBeingEligible_AndItsShareGoesToItsPeerAsync(CancellationToken cancellationToken) {
    var (stayingId, drainingId) = _pair();
    await using var staying = await _startReadyAsync(stayingId, cancellationToken);
    await using var draining = await _startReadyAsync(drainingId, cancellationToken);
    await _seedOutboxAsync("before-drain", cancellationToken);
    await staying.ClaimAsync(assignment: null, cancellationToken);
    var shareWhileBothRun = await _leasedPartitionsAsync(stayingId, "before-drain", cancellationToken);

    // The cause: the host begins its graceful stop. The wait: the participant recorded Stopping on the row.
    await draining.Lifecycle.AdvanceToAsync(LifecyclePhase.Stopping, cancellationToken);
    await draining.Recorded(LifecyclePhase.Stopping).WaitAsync(_signalDeadline, cancellationToken);
    var candidates = await _candidatesAsync(cancellationToken);
    await staying.ClaimAsync(assignment: null, cancellationToken);

    await Assert.That(shareWhileBothRun).IsEquivalentTo(_oddPartitions);
    await Assert.That(candidates).DoesNotContain(drainingId)
      .Because("a draining instance is alive but takes no new work");
    await Assert.That(await _isAliveAsync(drainingId, cancellationToken)).IsTrue()
      .Because("its heartbeat is still fresh: nothing waited for it to lapse");
    await Assert.That(await _leasedPartitionsAsync(stayingId, "before-drain", cancellationToken)).IsEquivalentTo(_allPartitions)
      .Because("the even partitions it left to its peer are released to it as soon as the peer drains");
  }

  // --- liveness is a separate test ---------------------------------------------------------------

  [Test]
  [Timeout(120000)]
  public async Task AnAliveButIneligibleInstance_IsNeverReapedNorEvictedAsync(CancellationToken cancellationToken) {
    var (readyId, heldId) = _pair();
    await using var held = await _startHeldAsync(heldId, cancellationToken);
    // A direct instance held in startup whose heartbeat has gone quiet past the reap threshold, alive by its lock.
    var lockedId = Guid.CreateVersion7();
    await _registerDirectMigratingAsync(lockedId, TimeSpan.FromSeconds(200), cancellationToken);
    await using var aliveLock = await _holdAliveLockAsync(lockedId, cancellationToken);

    // The ready instance's beat is the one that reaps stale peers; it runs here, after the rows are in place.
    await using var ready = await _startReadyAsync(readyId, cancellationToken);

    await Assert.That(await _isRegisteredAsync(heldId, cancellationToken)).IsTrue();
    await Assert.That(await _isEvictedAsync(heldId, cancellationToken)).IsFalse();
    await Assert.That(await _isRegisteredAsync(lockedId, cancellationToken)).IsTrue()
      .Because("eligibility never feeds the reap: a direct instance holding its lock is alive whatever its phase");
    await Assert.That(await _isEvictedAsync(lockedId, cancellationToken)).IsFalse();
    var candidates = await _candidatesAsync(cancellationToken);
    await Assert.That(candidates).DoesNotContain(heldId);
    await Assert.That(candidates).DoesNotContain(lockedId);
  }

  // --- the stall, end to end ---------------------------------------------------------------------

  [Test]
  [Timeout(180000)]
  public async Task TheStall_AnAssignerHeldInStartup_StrandsNoWork_AndTheReadyPeerDrainsTheBacklogAsync(
      CancellationToken cancellationToken) {
    var (readyId, heldId) = _pair();
    // The held instance wins the assigner role while it is still migrating: the assigner runs from host start.
    await using var held = await _startHeldAsync(heldId, cancellationToken);
    var assigner = _assignerFor(held.Instance);
    var firstPublish = _published(assigner, _ => true);
    await assigner.StartAsync(cancellationToken);
    try {
      var first = await firstPublish.WaitAsync(_signalDeadline, cancellationToken);
      await Assert.That(first.Members).DoesNotContain(heldId)
        .Because("elected while it is still migrating, the assigner must not give itself a share it cannot claim");

      // A ready peer joins; the assigner publishes it.
      var readyAlone = _published(assigner, members => members.Contains(readyId));
      await using var ready = await _startReadyAsync(readyId, cancellationToken);
      var assignment = await readyAlone.WaitAsync(_signalDeadline, cancellationToken);
      await _seedOutboxAsync("backlog", cancellationToken);
      await ready.ClaimAsync(assignment.Version, cancellationToken);

      await Assert.That(assignment.Members).IsEquivalentTo([readyId])
        .Because("the assigner gives no share to an instance that cannot claim one, itself included");
      await Assert.That(await _leasedPartitionsAsync(readyId, "backlog", cancellationToken)).IsEquivalentTo(_allPartitions)
        .Because("under the published assignment the ready instance owns every partition, so nothing waits on the held one");
      await Assert.That(await _leasedPartitionsAsync(heldId, "backlog", cancellationToken)).IsEmpty();

      // The held instance finishes its migration: once it records AcceptingCommands it is given its share.
      var both = _published(assigner, members => members.Count == 2);
      held.StartupGate.MarkReady();
      await held.Recorded(LifecyclePhase.AcceptingCommands).WaitAsync(_signalDeadline, cancellationToken);
      var shared = await both.WaitAsync(_signalDeadline, cancellationToken);

      await Assert.That(shared.Members.Order()).IsEquivalentTo(new[] { heldId, readyId }.Order());
    } finally {
      await assigner.StopAsync(CancellationToken.None);
    }
  }

  // ------------------------------------------------------------------

  /// <summary>One instance as a host composes it, with the observation points the tests wait on.</summary>
  private sealed class Pod : IAsyncDisposable {
    private readonly ServiceProvider _services;
    private readonly LifecyclePhaseWorker _driver;
    private readonly HeartbeatWorker _heartbeat;
    private readonly RecordedRunControl _instanceState;
    private readonly TaskCompletionSource _firstBeat = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _heartbeatStarted;

    public Pod(Guid id, Func<WorkCoordinationDbContext> createDbContext) {
      Instance = new ServiceInstanceProvider(id, "eligibility-svc", "eligibility-host", processId: 1);
      var services = new ServiceCollection();
      services.AddSingleton<IServiceInstanceProvider>(Instance);
      services.AddScoped(_ => createDbContext());
      services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        sp.GetRequiredService<WorkCoordinationDbContext>(), Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
      // The one writer of the row's stage, phase and version (#1636), as the worker pipeline registers it; the heartbeat
      // tells it the row exists after every accepted beat.
      services.AddSingleton<IStartupStageReporter>(sp => new StartupStageReporter(
        sp.GetRequiredService<IServiceScopeFactory>(), Instance, [], NullLogger<StartupStageReporter>.Instance,
        new LibraryVersionProvider("999.9.9")));
      _services = services.BuildServiceProvider();
      var scopeFactory = _services.GetRequiredService<IServiceScopeFactory>();
      Stages = _services.GetRequiredService<IStartupStageReporter>();
      _instanceState = new RecordedRunControl(new StartupStageRunControl(Stages));
      var lifecycleOptions = new WhizbangLifecycleOptions();
      Lifecycle = new WhizbangLifecycleState(new WhizbangLifecycleCoordinator([_instanceState], lifecycleOptions), lifecycleOptions);
      _driver = new LifecyclePhaseWorker(Lifecycle, StartupGate, ReadGate);
      var alreadyMigrated = new SchemaReadyGate();
      alreadyMigrated.MarkReady();
      _heartbeat = new HeartbeatWorker(
        scopeFactory, Instance, alreadyMigrated, Options.Create(new HeartbeatWorkerOptions()),
        NullLogger<HeartbeatWorker>.Instance, Lifecycle, new LibraryVersionProvider("999.9.9"),
        NoOpPinnedConnectionPool.Instance, NullInstanceAliveLockSource.Instance, NullSignalBus.Instance);
      _heartbeat.OnHeartbeatRecorded += () => _firstBeat.TrySetResult();
    }

    public ServiceInstanceProvider Instance { get; }
    public IWhizbangLifecycleState Lifecycle { get; }
    /// <summary>The instance's stage reporter: the host lifecycle and the schema pass enter stages through it.</summary>
    public IStartupStageReporter Stages { get; }
    /// <summary>Held closed, the instance stays in its migration pass; marked, it moves on to AcceptingCommands.</summary>
    public SchemaReadyGate StartupGate { get; } = new();
    public ReadModelsReadyGate ReadGate { get; } = new();
    public Task Recorded(LifecyclePhase phase) => _instanceState.Recorded(phase);

    public Task StartDriverAsync(CancellationToken cancellationToken) => _driver.StartAsync(cancellationToken);

    public async Task StartHeartbeatAsync(CancellationToken cancellationToken) {
      _heartbeatStarted = true;
      await _heartbeat.StartAsync(cancellationToken);
      await _firstBeat.Task.WaitAsync(_signalDeadline, cancellationToken);
    }

    /// <summary>One claim as the claim loop makes it: the announced phase, and the cached assignment when there is one.</summary>
    public async Task ClaimAsync(PartitionAssignmentVersion? assignment, CancellationToken cancellationToken) {
      await using var scope = _services.CreateAsyncScope();
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
      _ = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
        Instance.InstanceId, Instance.ServiceName, Instance.HostName, Instance.ProcessId, MaxStreams: 100,
        PartitionAssignment: assignment, LifecyclePhase: Lifecycle.AnnouncedPhase.ToString()), cancellationToken);
    }

    public async ValueTask DisposeAsync() {
      if (_heartbeatStarted) {
        await _heartbeat.StopAsync(CancellationToken.None);
      }
      await _driver.StopAsync(CancellationToken.None);
      _heartbeat.Dispose();
      _driver.Dispose();
      await _services.DisposeAsync();
    }
  }

  /// <summary>
  /// The real stage participant, observed: each phase's signal completes once the real participant has recorded it
  /// through the stage reporter. Nothing is replaced; the write is the reporter's own.
  /// </summary>
  private sealed class RecordedRunControl(IWhizbangRunControl inner) : IWhizbangRunControl {
    private readonly ConcurrentDictionary<LifecyclePhase, TaskCompletionSource> _recorded = new();
    public string Component => inner.Component;
    public Task Recorded(LifecyclePhase phase) => _signal(phase).Task;
    public async ValueTask OnPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      await inner.OnPhaseAsync(phase, cancellationToken);
      _signal(phase).TrySetResult();
    }
    private TaskCompletionSource _signal(LifecyclePhase phase) =>
      _recorded.GetOrAdd(phase, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
  }

  /// <summary>
  /// A ready instance: registered by the bootstrap, its startup and read gates open, its lifecycle at Running as the
  /// participant recorded it, and its first heartbeat recorded.
  /// </summary>
  private async Task<Pod> _startReadyAsync(Guid id, CancellationToken cancellationToken) {
    var pod = new Pod(id, CreateDbContext);
    await pod.Stages.EnterAsync(StartupStage.Preflight, "host starting", cancellationToken);
    await SchemaBootstrapPhase.RegisterInstanceAsync(() => new NpgsqlConnection(ConnectionString), "public", pod.Instance,
      cancellationToken: cancellationToken);
    await pod.Stages.InstanceRegisteredAsync(cancellationToken);
    pod.StartupGate.MarkReady();
    pod.ReadGate.MarkReady();
    await pod.StartDriverAsync(cancellationToken);
    await pod.Recorded(LifecyclePhase.Running).WaitAsync(_signalDeadline, cancellationToken);
    await pod.StartHeartbeatAsync(cancellationToken);
    return pod;
  }

  /// <summary>
  /// An instance held in startup: its lifecycle at Migrating as the participant recorded it, registered by the bootstrap
  /// as the migrator election registers it, and alive by its heartbeat.
  /// </summary>
  private async Task<Pod> _startHeldAsync(Guid id, CancellationToken cancellationToken) {
    var pod = new Pod(id, CreateDbContext);
    await pod.Stages.EnterAsync(StartupStage.Preflight, "host starting", cancellationToken);
    await pod.StartDriverAsync(cancellationToken);
    await pod.Recorded(LifecyclePhase.Migrating).WaitAsync(_signalDeadline, cancellationToken);
    await SchemaBootstrapPhase.RegisterInstanceAsync(() => new NpgsqlConnection(ConnectionString), "public", pod.Instance,
      cancellationToken: cancellationToken);
    await pod.Stages.InstanceRegisteredAsync(cancellationToken);
    // The schema pass reports Migrate where the work happens, with the phase it maps to; the pass itself is held.
    await pod.Stages.EnterAsync(StartupStage.Migrate, "applying migrations", cancellationToken);
    await pod.StartHeartbeatAsync(cancellationToken);
    return pod;
  }

  private PartitionAssignerWorker _assignerFor(IServiceInstanceProvider instance) {
    var notification = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString, SignalingMode = WorkSignalingMode.ListenNotify };
    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var roles = new RoleAssignmentOptions { HoldLegacySessionLock = false, RenewInterval = TimeSpan.FromMilliseconds(250) };
    roles.Roles.Add(PartitionAssignerOptions.ROLE);
    var elector = new PgRoleElector(Options.Create(notification), Options.Create(roles), config, instance,
      new PgDutyElector(Options.Create(notification), config, instance, NullLogger<PgDutyElector>.Instance),
      NullLogger<PgRoleElector>.Instance, null);
    return new PartitionAssignerWorker(elector, new PgPartitionAssignmentStore(Options.Create(notification), config),
      NullInstanceConnectionModeSource.Instance, instance, Options.Create(new PartitionAssignerOptions()), Options.Create(roles),
      NullLogger<PartitionAssignerWorker>.Instance);
  }

  /// <summary>Completes with the first assignment the worker publishes whose members satisfy <paramref name="when"/>.</summary>
  private static Task<PartitionAssignment> _published(PartitionAssignerWorker worker, Func<IReadOnlyList<Guid>, bool> when) {
    var signal = new TaskCompletionSource<PartitionAssignment>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnPublished += assignment => {
      if (when(assignment.Members)) {
        signal.TrySetResult(assignment);
      }
    };
    return signal.Task;
  }

  /// <summary>Two fresh instance ids, the first sorting after the second, so among two eligible instances it is rank 1 of 2.</summary>
  private static (Guid First, Guid Second) _pair() {
    var suffix = Guid.NewGuid().ToString("N")[..12];
    return (new Guid($"ffffffff-0000-0000-0000-{suffix}"), new Guid($"00000000-0000-0000-0000-{suffix}"));
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken cancellationToken) {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(cancellationToken);
    return connection;
  }

  private async Task<T> _scalarAsync<T>(string sql, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)Convert.ChangeType(await cmd.ExecuteScalarAsync(cancellationToken), typeof(T), CultureInfo.InvariantCulture)!;
  }

  private async Task<List<Guid>> _candidatesAsync(CancellationToken cancellationToken) {
    var candidates = new List<Guid>();
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_partition_assignment_candidates()";
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      candidates.Add(reader.GetGuid(0));
    }
    return candidates;
  }

  private Task<bool> _isAliveAsync(Guid id, CancellationToken cancellationToken) =>
    _scalarAsync<bool>($"SELECT is_instance_alive('{id}'::uuid, 150)", cancellationToken);

  private Task<bool> _isRegisteredAsync(Guid id, CancellationToken cancellationToken) =>
    _scalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM wh_service_instances WHERE instance_id = '{id}')", cancellationToken);

  private Task<bool> _isEvictedAsync(Guid id, CancellationToken cancellationToken) =>
    _scalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM wh_instance_evictions WHERE instance_id = '{id}')", cancellationToken);

  private async Task<string?> _phaseAsync(Guid id, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT lifecycle_phase FROM wh_service_instances WHERE instance_id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    return await cmd.ExecuteScalarAsync(cancellationToken) as string;
  }

  /// <summary>A direct instance registered while migrating, with a heartbeat of the given age.</summary>
  private async Task _registerDirectMigratingAsync(Guid id, TimeSpan age, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode, lifecycle_phase)
      VALUES (@id, 'eligibility-svc', 'eligibility-host', 1, NOW() - @age, NOW() - INTERVAL '2 hours', 'direct', 'Migrating')";
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue(nameof(age), age);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <summary>A session of its own holding the instance's alive-lock, as its shared notify connection does.</summary>
  private async Task<NpgsqlConnection> _holdAliveLockAsync(Guid id, CancellationToken cancellationToken) {
    var holder = await _openAsync(cancellationToken);
    await using var cmd = holder.CreateCommand();
    cmd.CommandText = "SELECT claim_instance_alive_lock(@id)";
    cmd.Parameters.AddWithValue(nameof(id), id);
    if (await cmd.ExecuteScalarAsync(cancellationToken) is not true) {
      throw new InvalidOperationException($"the alive-lock for {id} was already held");
    }
    return holder;
  }

  /// <summary>One unowned outbox row on each of partitions 0 to 9, each on a stream of its own, tagged by destination.</summary>
  private async Task _seedOutboxAsync(string destination, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id,
         partition_number)
      SELECT gen_random_uuid(), @destination, 'TestEvent', '{}', '{}', 0, 0,
             NOW() - INTERVAL '1 minute' + (p * INTERVAL '1 millisecond'), gen_random_uuid(), p
      FROM generate_series(0, 9) p";
    cmd.Parameters.AddWithValue(nameof(destination), destination);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<List<int>> _leasedPartitionsAsync(Guid id, string destination, CancellationToken cancellationToken) {
    var partitions = new List<int>();
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT partition_number FROM wh_outbox WHERE instance_id = @id AND destination = @destination ORDER BY partition_number";
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue(nameof(destination), destination);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      partitions.Add(reader.GetInt32(0));
    }
    return partitions;
  }
}
