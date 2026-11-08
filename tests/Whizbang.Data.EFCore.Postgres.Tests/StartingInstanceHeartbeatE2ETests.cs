// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Signals;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A starting instance beats from its registration (#2126), end to end on a real database. Before this, the heartbeat
/// started at schema-ready and the registration recorded no liveness window, so under the one liveness rule (316) a
/// live peer's reap evicted an instance still in Migrate once 30 seconds had passed.
/// </summary>
/// <remarks>
/// <para>
/// The starting instance is composed the way a host composes it: the real bootstrap registration, the real stage
/// reporter (the one writer of the row's stage and phase) feeding the real stage gate, and the real heartbeat worker
/// hosted at the start point the core registration gives it (<see cref="HeartbeatWorker.StartsAt"/>), with its
/// schema-ready gate held closed so the instance stays in Migrate. The live peer is a ready instance whose own beat runs
/// the reap.
/// </para>
/// <para>
/// Time is the database's: a heartbeat is aged with <c>NOW() - age</c> on the database clock. The heartbeat worker's
/// clock is a <see cref="FakeTimeProvider"/> that is never advanced, so the only beats are the one the worker owes when
/// it starts and the one it owes when the schema-ready gate opens; each is waited for on its own signal.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/353_TheHeartbeatStartsAtRegistration.sql</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/HeartbeatWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaBootstrapPhase.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class StartingInstanceHeartbeatE2ETests : EFCoreTestBase {
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(60);
  private static readonly int[] _allPartitions = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

  [Test]
  [Timeout(120000)]
  public async Task AStartingInstanceHeldInMigrate_PastThirtySeconds_IsNotEvictedByALivePeersReapAsync(
      CancellationToken cancellationToken) {
    var (peerId, startingId) = _pair();
    await using var peer = await _readyPeerAsync(peerId, cancellationToken);
    await using var starting = new StartingPod(startingId, CreateDbContext);
    await starting.RegisterAndHoldInMigrateAsync(ConnectionString, cancellationToken);

    // Registered and held in Migrate for 45 seconds, before its heartbeat has started.
    await _ageHeartbeatAsync(startingId, TimeSpan.FromSeconds(45), cancellationToken);
    await peer.BeatAsync(cancellationToken);
    var registeredAtFortyFive = await _isRegisteredAsync(startingId, cancellationToken);
    var evictedAtFortyFive = await _isEvictedAsync(startingId, cancellationToken);

    // Held well past its own window: only a beat it really sends keeps it.
    await _ageHeartbeatAsync(startingId, TimeSpan.FromSeconds(200), cancellationToken);
    await starting.StartHeartbeatAsync(cancellationToken);
    await peer.BeatAsync(cancellationToken);

    await Assert.That(registeredAtFortyFive).IsTrue()
      .Because("the registration records the instance's own window, so 45 seconds is not a death");
    await Assert.That(evictedAtFortyFive).IsFalse();
    await Assert.That(starting.StartupGate.IsReady).IsFalse().Because("the beat was sent while the instance was in Migrate");
    await Assert.That(await _isRegisteredAsync(startingId, cancellationToken)).IsTrue()
      .Because("the heartbeat starts at registration, so a peer's reap finds a fresh beat");
    await Assert.That(await _isEvictedAsync(startingId, cancellationToken)).IsFalse();
  }

  [Test]
  [Timeout(120000)]
  public async Task AStartingInstanceThatBeats_KeepsItsStageAndPhase_AndIsNotEligibleForWork_UntilItRecordsAcceptingCommandsAsync(
      CancellationToken cancellationToken) {
    var (peerId, startingId) = _pair();
    await using var peer = await _readyPeerAsync(peerId, cancellationToken);
    await using var starting = new StartingPod(startingId, CreateDbContext);
    await starting.RegisterAndHoldInMigrateAsync(ConnectionString, cancellationToken);
    var beforeTheBeat = await _stageAndPhaseAsync(startingId, cancellationToken);
    await starting.StartHeartbeatAsync(cancellationToken);
    await _seedOutboxAsync("while-starting", cancellationToken);

    var whileStarting = await _stageAndPhaseAsync(startingId, cancellationToken);
    var candidatesWhileStarting = await _candidatesAsync(cancellationToken);
    await starting.ClaimAsync(cancellationToken);
    await peer.ClaimAsync(cancellationToken);

    await Assert.That(beforeTheBeat).IsEqualTo("30 Migrating");
    await Assert.That(whileStarting).IsEqualTo(beforeTheBeat)
      .Because("the beat announced Running and wrote nothing of it: the stage record is the one writer of the phase");
    await Assert.That(candidatesWhileStarting).Contains(peerId);
    await Assert.That(candidatesWhileStarting).DoesNotContain(startingId)
      .Because("alive is not ready: the partition assigner leaves a migrating instance out");
    await Assert.That(await _leasedPartitionsAsync(startingId, "while-starting", cancellationToken)).IsEmpty()
      .Because("a claim from an instance whose row records Migrate is refused");
    await Assert.That(await _leasedPartitionsAsync(peerId, "while-starting", cancellationToken)).IsEquivalentTo(_allPartitions)
      .Because("the ready peer does not rank the starting instance, so it covers every partition");

    // The migration pass ends: the schema-ready gate opens and the instance records Accept commands.
    await starting.FinishStartupAsync(cancellationToken);

    await Assert.That(await _stageAndPhaseAsync(startingId, cancellationToken)).IsEqualTo("60 AcceptingCommands");
    await Assert.That(await _candidatesAsync(cancellationToken)).Contains(startingId)
      .Because("eligibility begins when the stage record says Accept commands, not when the heartbeat starts");
  }

  [Test]
  [Timeout(120000)]
  public async Task TheFleetReadModel_ShowsAStartingInstanceAsMigrating_LiveAndNotEvictedAsync(CancellationToken cancellationToken) {
    var (peerId, startingId) = _pair();
    await using var peer = await _readyPeerAsync(peerId, cancellationToken);
    await using var starting = new StartingPod(startingId, CreateDbContext);
    await starting.RegisterAndHoldInMigrateAsync(ConnectionString, cancellationToken);
    await _ageHeartbeatAsync(startingId, TimeSpan.FromSeconds(200), cancellationToken);
    await starting.StartHeartbeatAsync(cancellationToken);
    await peer.BeatAsync(cancellationToken);

    var fleet = await new EFCorePostgresStartupFleetStatusSource(
      starting.Services.GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext)).GetFleetAsync(cancellationToken);

    var row = fleet.Single(i => i.InstanceId == startingId);
    await Assert.That(row.LifecyclePhase).IsEqualTo(nameof(LifecyclePhase.Migrating))
      .Because("a peer and an operator read the instance as still migrating, not ready");
    await Assert.That(row.IsLive).IsTrue().Because("its beats during Migrate keep it live by the one rule");
    await Assert.That(row.Evicted).IsFalse();
    await Assert.That(fleet.Single(i => i.InstanceId == peerId).LifecyclePhase).IsEqualTo(nameof(LifecyclePhase.Running));
  }

  /// <summary>
  /// #2126: an instance the fleet evicted while it is still starting (a peer's reap judged it dead in Migrate) learns it
  /// from its refused startup beat and exits once through the one exit path, code 3, so its host restarts it under a new
  /// instance id. The exit is recorded with the eviction as the peer recorded it.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task AnInstanceEvictedWhileStarting_ExitsOnce_WithCodeThree_AndRecordsWhoEvictedItAsync(
      CancellationToken cancellationToken) {
    var (peerId, startingId) = _pair();
    await using var peer = await _readyPeerAsync(peerId, cancellationToken);
    await using var starting = new StartingPod(startingId, CreateDbContext);
    await starting.RegisterAndHoldInMigrateAsync(ConnectionString, cancellationToken);
    var window = await _scalarAsync<int>(
      $"SELECT liveness_window_seconds FROM wh_service_instances WHERE instance_id = '{startingId}'", cancellationToken);
    await _ageHeartbeatAsync(startingId, TimeSpan.FromSeconds(window + 60), cancellationToken);
    await peer.BeatAsync(cancellationToken);

    await starting.StartHeartbeatExpectingRefusalAsync(cancellationToken);
    await starting.Exit.Completion.WaitAsync(_signalDeadline, cancellationToken);

    await _assertExitedOnceAsEvictedAsync(starting, startingId, peerId, window, "Migrate", cancellationToken);
  }

  /// <summary>#2126: the same for an instance evicted while it runs: its full heartbeat is refused, and it exits once.</summary>
  [Test]
  [Timeout(120000)]
  public async Task AnInstanceEvictedWhileRunning_ExitsOnce_WithCodeThree_AndRecordsWhoEvictedItAsync(
      CancellationToken cancellationToken) {
    var (peerId, startingId) = _pair();
    await using var peer = await _readyPeerAsync(peerId, cancellationToken);
    await using var starting = new StartingPod(startingId, CreateDbContext);
    await starting.RegisterAndHoldInMigrateAsync(ConnectionString, cancellationToken);
    await starting.FinishStartupAsync(cancellationToken);
    var window = await _scalarAsync<int>(
      $"SELECT liveness_window_seconds FROM wh_service_instances WHERE instance_id = '{startingId}'", cancellationToken);
    await _ageHeartbeatAsync(startingId, TimeSpan.FromSeconds(window + 60), cancellationToken);
    await peer.BeatAsync(cancellationToken);

    await starting.StartHeartbeatExpectingRefusalAsync(cancellationToken);
    await starting.Exit.Completion.WaitAsync(_signalDeadline, cancellationToken);

    await _assertExitedOnceAsEvictedAsync(starting, startingId, peerId, window, "Accept commands", cancellationToken);
  }

  private async Task _assertExitedOnceAsEvictedAsync(
      StartingPod starting, Guid startingId, Guid peerId, int window, string stage, CancellationToken cancellationToken) {
    await Assert.That(starting.ExitCodes.Codes).IsEquivalentTo([3]).Because("evicted: the host restarts it");
    await Assert.That(starting.Lifetime.StopCalls).IsEqualTo(1);
    var report = starting.Exit.Report!;
    await Assert.That(report.Record.Cause).IsEqualTo(ExitCause.ClusterEviction);
    await Assert.That(report.Record.StartupStage).IsEqualTo(stage);
    await Assert.That(report.Recording.Recorded).IsTrue();
    await Assert.That(report.Recording.Eviction!.EvictedBy).IsEqualTo(peerId).Because("the peer whose reap evicted it");
    await Assert.That(report.Recording.Eviction.LivenessWindowSeconds).IsEqualTo(window);
    await Assert.That(report.Recording.Eviction.LastHeartbeatAt.HasValue).IsTrue();
    await Assert.That(await _scalarAsync<long>(
        $"SELECT count(*) FROM wh_instance_exits WHERE instance_id = '{startingId}'", cancellationToken))
      .IsEqualTo(1L).Because("one exit, one row");
    await Assert.That(await _scalarAsync<string>(
        $"SELECT exit_cause || ' ' || exit_code || ' ' || evicted_by FROM wh_instance_exits WHERE instance_id = '{startingId}'",
        cancellationToken))
      .IsEqualTo($"ClusterEviction 3 {peerId}");
  }

  [Test]
  public async Task AStartupBeat_OfAnEvictedInstance_IsRefused_AndOfAnUnregisteredOne_WritesNothingAsync(
      CancellationToken cancellationToken) {
    var evicted = (Guid)TrackedGuid.New();
    var unregistered = (Guid)TrackedGuid.New();
    await _executeAsync(
      $"INSERT INTO wh_instance_evictions (instance_id, evicted_at, reason) VALUES ('{evicted}', NOW(), 'test')",
      cancellationToken);
    await using var context = CreateDbContext();
    IWorkCoordinator coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      context, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var refused = await coordinator.RecordStartupHeartbeatAsync(_beat(evicted), cancellationToken);
    var accepted = await coordinator.RecordStartupHeartbeatAsync(_beat(unregistered), cancellationToken);

    await Assert.That(refused).IsFalse().Because("an evicted instance learns it from its startup beat, as from any beat");
    await Assert.That(accepted).IsTrue();
    await Assert.That(await _isRegisteredAsync(unregistered, cancellationToken)).IsFalse()
      .Because("a startup beat never inserts a row: a row without a phase would read as an older build, eligible for work");
  }

  // ------------------------------------------------------------------

  private static HeartbeatRequest _beat(Guid id) =>
    new(id, "starting-svc", "starting-host", 1, LifecyclePhase: nameof(LifecyclePhase.Running), StaleThresholdSeconds: 150,
      ConnectionMode: InstanceConnectionMode.Pooled);

  /// <summary>A starting instance as a host composes it, held in Migrate by its closed schema-ready gate.</summary>
  private sealed class StartingPod : IAsyncDisposable {
    private readonly ServiceProvider _services;
    private readonly StartupStageGatedService<HeartbeatWorker> _hosted;
    private readonly HeartbeatWorker _heartbeat;
    private readonly TaskCompletionSource _firstBeat = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;

    public StartingPod(Guid id, Func<WorkCoordinationDbContext> createDbContext) {
      Instance = new ServiceInstanceProvider(id, "starting-svc", "starting-host", processId: 1);
      var stageGate = new StartupStageGate(StartupGate);
      var services = new ServiceCollection();
      services.AddSingleton<IServiceInstanceProvider>(Instance);
      services.AddScoped(_ => createDbContext());
      services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        sp.GetRequiredService<WorkCoordinationDbContext>(), Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
      services.AddSingleton<IStartupStageReporter>(sp => new StartupStageReporter(
        sp.GetRequiredService<IServiceScopeFactory>(), Instance, [stageGate], NullLogger<StartupStageReporter>.Instance,
        new LibraryVersionProvider("999.9.9")));
      // #2126: the one exit path, read from the pod's own services when it exits; the test keeps its own exit code.
      services.AddSingleton<ILastAcceptedHeartbeat>(_ => _heartbeat!);
      services.AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(Lifetime);
      _services = services.BuildServiceProvider();
      var lifecycleOptions = new WhizbangLifecycleOptions();
      Exit = new WhizbangInstanceExit(_services,
        new WhizbangRunController(new WhizbangRunPermit(), new AnnouncesRunning(), [], [], lifecycleOptions, null,
          NullLogger<WhizbangRunController>.Instance),
        ExitCodes, null, NullLogger<WhizbangInstanceExit>.Instance, new NoCrashEvents());
      Stages = _services.GetRequiredService<IStartupStageReporter>();
      _heartbeat = new HeartbeatWorker(
        _services.GetRequiredService<IServiceScopeFactory>(), Instance, Options.Create(new HeartbeatWorkerOptions()),
        NullLogger<HeartbeatWorker>.Instance, new AnnouncesRunning(), new LibraryVersionProvider("999.9.9"),
        new Whizbang.Core.Versioning.ApplicationVersionProvider(Whizbang.Core.Versioning.ApplicationVersion.Unknown),
        NoOpPinnedConnectionPool.Instance, NullInstanceAliveLockSource.Instance, NullSignalBus.Instance, StartupGate, Exit,
        timeProvider: new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
      _heartbeat.OnHeartbeatRecorded += () => _firstBeat.TrySetResult();
      _hosted = new StartupStageGatedService<HeartbeatWorker>(_heartbeat, HeartbeatWorker.StartsAt, stageGate, StartupGate);
    }

    public ServiceInstanceProvider Instance { get; }
    public IServiceProvider Services => _services;
    public WhizbangInstanceExit Exit { get; }
    public RecordingExitCodes ExitCodes { get; } = new();
    public StoppingLifetime Lifetime { get; } = new();

    /// <summary>Starts the hosted heartbeat as the host starts it, without waiting for an accepted beat.</summary>
    public async Task StartHeartbeatExpectingRefusalAsync(CancellationToken cancellationToken) {
      _started = true;
      await _hosted.StartAsync(cancellationToken);
    }
    public IStartupStageReporter Stages { get; }
    public SchemaReadyGate StartupGate { get; } = new();

    /// <summary>Preflight's registration through the real bootstrap registration, then Elect and Migrate.</summary>
    public async Task RegisterAndHoldInMigrateAsync(string connectionString, CancellationToken cancellationToken) {
      await Stages.EnterAsync(StartupStage.Preflight, "registering", cancellationToken);
      await SchemaBootstrapPhase.RegisterInstanceAsync(() => new NpgsqlConnection(connectionString), "public", Instance,
        liveness: InstanceRegistrationLiveness.Of(_services), cancellationToken: cancellationToken);
      await Stages.InstanceRegisteredAsync(cancellationToken);
      await Stages.EnterAsync(StartupStage.Elect, "another instance migrates the schema", cancellationToken);
      await Stages.EnterAsync(StartupStage.Migrate, "applying migrations", cancellationToken);
    }

    /// <summary>Starts the hosted heartbeat as the host starts it, and waits for its first recorded beat.</summary>
    public async Task StartHeartbeatAsync(CancellationToken cancellationToken) {
      _started = true;
      await _hosted.StartAsync(cancellationToken);
      await _firstBeat.Task.WaitAsync(_signalDeadline, cancellationToken);
    }

    /// <summary>The pass ends: Converge, Coordinate, the schema-ready gate, and Accept commands.</summary>
    public async Task FinishStartupAsync(CancellationToken cancellationToken) {
      await Stages.EnterAsync(StartupStage.Converge, "converging", cancellationToken);
      await Stages.EnterAsync(StartupStage.Coordinate, "coordinating", cancellationToken);
      StartupGate.MarkReady();
      await Stages.EnterAsync(StartupStage.AcceptingCommands, "accepting", cancellationToken);
    }

    public async Task ClaimAsync(CancellationToken cancellationToken) {
      await using var scope = _services.CreateAsyncScope();
      _ = await scope.ServiceProvider.GetRequiredService<IWorkCoordinator>().ClaimWorkAsync(new ClaimWorkRequest(
        Instance.InstanceId, Instance.ServiceName, Instance.HostName, Instance.ProcessId, MaxStreams: 100), cancellationToken);
    }

    public async ValueTask DisposeAsync() {
      if (_started) {
        await _hosted.StopAsync(CancellationToken.None);
      }
      _hosted.Dispose();
      _heartbeat.Dispose();
      await _services.DisposeAsync();
    }
  }

  /// <summary>The test process keeps its own exit code; the exit's codes are recorded instead.</summary>
  private sealed class RecordingExitCodes : IProcessExitCode {
    public List<int> Codes { get; } = [];
    public void SetExitCode(int exitCode) => Codes.Add(exitCode);
  }

  /// <summary>A host lifetime that records each stop.</summary>
  private sealed class StoppingLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime, IDisposable {
    private readonly CancellationTokenSource _stopping = new();
    public void Dispose() => _stopping.Dispose();
    public int StopCalls { get; private set; }
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() {
      StopCalls++;
      _stopping.Cancel();
    }
  }

  /// <summary>A lifecycle that announces Running, ahead of the row, to show a startup beat never writes the phase.</summary>
  private sealed class AnnouncesRunning : IWhizbangLifecycleState {
    public LifecyclePhase Phase => LifecyclePhase.Running;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  /// <summary>A ready instance: registered, recorded Running, and beating through the full heartbeat, which reaps.</summary>
  private sealed class ReadyPeer(Guid id, WorkCoordinationDbContext context) : IAsyncDisposable {
    private readonly IWorkCoordinator _coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      context, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    public async Task StartAsync(string connectionString, CancellationToken cancellationToken) {
      await SchemaBootstrapPhase.RegisterInstanceAsync(() => new NpgsqlConnection(connectionString), "public",
        new ServiceInstanceProvider(id, "ready-svc", "ready-host", processId: 2),
        liveness: new InstanceRegistrationLiveness(InstanceConnectionMode.Pooled, 150), cancellationToken: cancellationToken);
      _ = await _coordinator.RecordInstanceTransitionAsync(
        new InstanceTransition(id, StartupStage.Running, "serving", LifecyclePhase.Running, null), cancellationToken);
    }

    public async Task BeatAsync(CancellationToken cancellationToken) {
      var accepted = await _coordinator.RecordHeartbeatAsync(new HeartbeatRequest(
        id, "ready-svc", "ready-host", 2, StaleThresholdSeconds: 150, ConnectionMode: InstanceConnectionMode.Pooled), cancellationToken);
      if (!accepted) {
        throw new InvalidOperationException($"the ready peer {id} was refused");
      }
    }

    public Task ClaimAsync(CancellationToken cancellationToken) =>
      _coordinator.ClaimWorkAsync(new ClaimWorkRequest(id, "ready-svc", "ready-host", 2, MaxStreams: 100,
        LifecyclePhase: nameof(LifecyclePhase.Running)), cancellationToken);

    public ValueTask DisposeAsync() => context.DisposeAsync();
  }

  private async Task<ReadyPeer> _readyPeerAsync(Guid id, CancellationToken cancellationToken) {
    var peer = new ReadyPeer(id, CreateDbContext());
    await peer.StartAsync(ConnectionString, cancellationToken);
    await peer.BeatAsync(cancellationToken);
    return peer;
  }

  /// <summary>Two fresh instance ids, the first sorting after the second.</summary>
  private static (Guid First, Guid Second) _pair() {
    var suffix = Guid.NewGuid().ToString("N")[..12];
    return (new Guid($"ffffffff-0000-0000-0000-{suffix}"), new Guid($"00000000-0000-0000-0000-{suffix}"));
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken cancellationToken) {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync(cancellationToken);
    return connection;
  }

  private async Task _executeAsync(string sql, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<T> _scalarAsync<T>(string sql, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)Convert.ChangeType(await cmd.ExecuteScalarAsync(cancellationToken), typeof(T), CultureInfo.InvariantCulture)!;
  }

  /// <summary>Ages an instance's heartbeat on the database's clock.</summary>
  private async Task _ageHeartbeatAsync(Guid id, TimeSpan age, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - @age WHERE instance_id = @id";
    cmd.Parameters.AddWithValue(nameof(age), age);
    cmd.Parameters.AddWithValue(nameof(id), id);
    if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1) {
      throw new InvalidOperationException($"no row for {id} to age");
    }
  }

  private Task<bool> _isRegisteredAsync(Guid id, CancellationToken cancellationToken) =>
    _scalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM wh_service_instances WHERE instance_id = '{id}')", cancellationToken);

  private Task<bool> _isEvictedAsync(Guid id, CancellationToken cancellationToken) =>
    _scalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM wh_instance_evictions WHERE instance_id = '{id}')", cancellationToken);

  private Task<string> _stageAndPhaseAsync(Guid id, CancellationToken cancellationToken) =>
    _scalarAsync<string>(
      $"SELECT startup_stage || ' ' || lifecycle_phase FROM wh_service_instances WHERE instance_id = '{id}'", cancellationToken);

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
    cmd.CommandText =
      "SELECT partition_number FROM wh_outbox WHERE instance_id = @id AND destination = @destination ORDER BY partition_number";
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue(nameof(destination), destination);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      partitions.Add(reader.GetInt32(0));
    }
    return partitions;
  }

  /// <summary>Process failure events nothing raises: a pod here never crashes for real.</summary>
  private sealed class NoCrashEvents : IProcessCrashEvents {
    public event UnhandledExceptionEventHandler UnhandledException {
      add => _ = value;
      remove => _ = value;
    }

    public event EventHandler<UnobservedTaskExceptionEventArgs> UnobservedTaskException {
      add => _ = value;
      remove => _ = value;
    }
  }
}
