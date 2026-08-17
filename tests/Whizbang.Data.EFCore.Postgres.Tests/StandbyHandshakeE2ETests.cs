using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The full standby handshake, multi-instance and end to end (increment 9's testing bar): a newer
/// instance requests standby; live older peers drain, post STANDING BY through the REAL lifecycle
/// broadcast and instance-state recording; the migrator's wait completes; then every path out —
/// migration committed (peers shut down), rolled back (peers revive by re-entering the pipeline at
/// Assess), a dead migrator (nobody is stranded), and a non-responsive peer (evicted, the
/// handshake completes without it). Each simulated instance is its own coordinator, assessor,
/// lifecycle machine and watcher over its own connections, exactly as separate pods would be.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StandbyWatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StandbyHandshake.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/110_StandbyHandshake.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class StandbyHandshakeE2ETests : EFCoreTestBase {

  private const string OLD_VERSION = "999.0.0";
  private const string NEW_VERSION = "999.1.0";

  private sealed class _pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "handshake-svc";
    public string HostName => "handshake-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class _stubHostLifetime : IHostApplicationLifetime {
    public bool StopRequested { get; private set; }
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => StopRequested = true;
  }

  private sealed class _countingStep : IStartupStep {
    private int _executions;
    public int Executions => Volatile.Read(ref _executions);
    public StartupStepDescriptor Descriptor { get; } = new() { Name = "ReviveProbe" };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      Interlocked.Increment(ref _executions);
      return new(new StartupStepReport(StartupStepOutcome.Completed));
    }
  }

  private sealed record _podRig(
    _pod Pod, StandbyWatcher Watcher, _stubHostLifetime HostLifetime,
    IWhizbangLifecycleState Lifecycle, _countingStep ReviveProbe, IServiceProvider Services);

  private IServiceProvider _servicesForPod() {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), JsonContextRegistry.CreateCombinedOptions()));
    return services.BuildServiceProvider();
  }

  private static StandbyWatcherOptions _fastOptions() => new() {
    PollInterval = TimeSpan.FromMilliseconds(50),
    ObsolescenceInterval = TimeSpan.FromDays(1),   // handshake tests drive obsolescence explicitly
    RequesterLivenessWindow = TimeSpan.FromSeconds(30),
  };

  /// <summary>A complete simulated instance: real coordinator, assessor, lifecycle machine (with
  /// the REAL instance-state participant, so StandingBy lands on its row), and watcher.</summary>
  private async Task<_podRig> _bootPodAsync(string version, CancellationToken ct) {
    var pod = new _pod();
    var services = _servicesForPod();
    var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();

    using (var scope = scopeFactory.CreateScope()) {
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
      await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
      await coordinator.RecordInstanceStateAsync(pod.InstanceId, nameof(LifecyclePhase.Running), version, ct);
    }

    var versionProvider = new LibraryVersionProvider(version);
    var lifecycleOptions = new WhizbangLifecycleOptions();
    var lifecycle = new WhizbangLifecycleState(
      new WhizbangLifecycleCoordinator(
        [new InstanceStateRunControl(scopeFactory, pod, versionProvider)], lifecycleOptions),
      lifecycleOptions);
    var assessor = new EFCorePostgresStartupAssessor(scopeFactory, typeof(WorkCoordinationDbContext), versionProvider);
    var reviveProbe = new _countingStep();
    var hostLifetime = new _stubHostLifetime();
    var watcher = new StandbyWatcher(
      scopeFactory, lifecycle, hostLifetime, pod, versionProvider, assessor,
      new StartupPipelineRunner([reviveProbe]), schemaReadyGate: null, _fastOptions());
    return new _podRig(pod, watcher, hostLifetime, lifecycle, reviveProbe, services);
  }

  private StandbyHandshake _handshakeFor(_podRig rig) => new(
    rig.Services.GetRequiredService<IServiceScopeFactory>(),
    new EFCorePostgresStartupFleetStatusSource(
      rig.Services.GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext)),
    rig.Pod,
    _fastOptions());

  private async Task<List<(int Id, string Version)>> _captureLedgerVersionsAsync(CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, library_version FROM wh_schema_versions";
    var rows = new List<(int, string)>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      rows.Add((reader.GetInt32(0), reader.GetString(1)));
    }
    return rows;
  }

  private async Task _setAllLedgerVersionsAsync(string version, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_schema_versions SET library_version = @v";
    cmd.Parameters.AddWithValue("v", version);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private async Task _restoreLedgerVersionsAsync(List<(int Id, string Version)> rows, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    foreach (var (id, version) in rows) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "UPDATE wh_schema_versions SET library_version = @v WHERE id = @id";
      cmd.Parameters.AddWithValue("v", version);
      cmd.Parameters.AddWithValue("id", id);
      await cmd.ExecuteNonQueryAsync(ct);
    }
  }

  private async Task _clearStandbyAsync(Guid requester, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT clear_standby(@id)";
    cmd.Parameters.AddWithValue("id", requester);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  [Test]
  [Timeout(120000)]
  public async Task Handshake_CommitPath_PeersDrainAcknowledgeAndShutDownAsync(CancellationToken cancellationToken) {
    var oldRig = await _bootPodAsync(OLD_VERSION, cancellationToken);
    var newRig = await _bootPodAsync(NEW_VERSION, cancellationToken);
    var handshake = _handshakeFor(newRig);
    var originalVersions = await _captureLedgerVersionsAsync(cancellationToken);
    try {
      // The newer instance announces the planned outage.
      await Assert.That(await handshake.RequestAsync(NEW_VERSION, cancellationToken)).IsTrue();

      // The older peer's watcher sees a binding request: drains, holds, posts STANDING BY.
      await oldRig.Watcher.TickForTestsAsync(cancellationToken);
      await Assert.That(oldRig.Lifecycle.Phase).IsEqualTo(LifecyclePhase.StandingBy);

      // The migrator's wait completes because the ack is OBSERVABLE — recorded state, not hope.
      var acknowledged = await handshake.AwaitPeersStandingByAsync(NEW_VERSION, cancellationToken)
        .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
      await Assert.That(acknowledged).Contains(oldRig.Pod.InstanceId);

      // The migration commits: the ledger now records the newer version. The request is withdrawn.
      await _setAllLedgerVersionsAsync(NEW_VERSION, cancellationToken);
      await handshake.ClearAsync(cancellationToken);

      // The standing-by peer re-assesses: the schema moved beneath it — it shuts down, as the
      // handshake promises.
      await oldRig.Watcher.TickForTestsAsync(cancellationToken);
      await Assert.That(oldRig.HostLifetime.StopRequested).IsTrue()
        .Because("committed means the older binary is now obsolete — success ends in shutdown, "
               + "and the orchestrator replaces it");
    } finally {
      await _restoreLedgerVersionsAsync(originalVersions, cancellationToken);
      await _clearStandbyAsync(newRig.Pod.InstanceId, cancellationToken);
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task Handshake_RollbackPath_PeersReviveByReEnteringThePipelineAsync(CancellationToken cancellationToken) {
    var oldRig = await _bootPodAsync(OLD_VERSION, cancellationToken);
    var newRig = await _bootPodAsync(NEW_VERSION, cancellationToken);
    var handshake = _handshakeFor(newRig);
    try {
      await Assert.That(await handshake.RequestAsync(NEW_VERSION, cancellationToken)).IsTrue();
      await oldRig.Watcher.TickForTestsAsync(cancellationToken);
      await Assert.That(oldRig.Lifecycle.Phase).IsEqualTo(LifecyclePhase.StandingBy);

      // The migration fails and rolls back: the ledger is exactly as the peer last read it.
      // The requester withdraws.
      await handshake.ClearAsync(cancellationToken);

      await oldRig.Watcher.TickForTestsAsync(cancellationToken);
      await Assert.That(oldRig.ReviveProbe.Executions).IsEqualTo(1)
        .Because("revival is not a second pipeline — it is re-entering the re-entrant runner at "
               + "Assess, which finds the schema unchanged and comes back up");
      await Assert.That(oldRig.Lifecycle.Phase).IsEqualTo(LifecyclePhase.Running);
      await Assert.That(oldRig.HostLifetime.StopRequested).IsFalse();
    } finally {
      await _clearStandbyAsync(newRig.Pod.InstanceId, cancellationToken);
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task Handshake_DeadMigrator_StrandsNobodyAsync(CancellationToken cancellationToken) {
    var oldRig = await _bootPodAsync(OLD_VERSION, cancellationToken);
    var newRig = await _bootPodAsync(NEW_VERSION, cancellationToken);
    var handshake = _handshakeFor(newRig);
    try {
      await Assert.That(await handshake.RequestAsync(NEW_VERSION, cancellationToken)).IsTrue();
      await oldRig.Watcher.TickForTestsAsync(cancellationToken);
      await Assert.That(oldRig.Lifecycle.Phase).IsEqualTo(LifecyclePhase.StandingBy);

      // The migrator dies without failing cleanly — its heartbeat lapses, its request is void.
      await using (var conn = new NpgsqlConnection(ConnectionString)) {
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '10 minutes' WHERE instance_id = @id";
        cmd.Parameters.AddWithValue("id", newRig.Pod.InstanceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
      }

      await oldRig.Watcher.TickForTestsAsync(cancellationToken);
      await Assert.That(oldRig.ReviveProbe.Executions).IsEqualTo(1)
        .Because("when the migrator's own record goes stale the wait ends and revival begins — "
               + "every path out of standby is bounded: success, clean failure, or a dead migrator");
      await Assert.That(oldRig.Lifecycle.Phase).IsEqualTo(LifecyclePhase.Running);
    } finally {
      await _clearStandbyAsync(newRig.Pod.InstanceId, cancellationToken);
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task Handshake_UnresponsivePeer_IsEvicted_AndTheHandshakeCompletesWithoutItAsync(
      CancellationToken cancellationToken) {
    var stuckRig = await _bootPodAsync(OLD_VERSION, cancellationToken);   // never ticks — wedged
    var newRig = await _bootPodAsync(NEW_VERSION, cancellationToken);
    var handshake = _handshakeFor(newRig);
    try {
      await Assert.That(await handshake.RequestAsync(NEW_VERSION, cancellationToken)).IsTrue();

      // The wedged peer never acknowledges; the wait would block on it forever.
      using (var shortWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
        shortWait.CancelAfter(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
          await handshake.AwaitPeersStandingByAsync(NEW_VERSION, shortWait.Token));
      }

      // Eviction is deliberate — taken by the migrator during a breaking handshake — and it is
      // what turns the decision into a guarantee: the fenced peer no longer counts.
      await handshake.EvictUnresponsivePeerAsync(stuckRig.Pod.InstanceId,
        "standby handshake: no acknowledgment within the wait bound", cancellationToken);

      var acknowledged = await handshake.AwaitPeersStandingByAsync(NEW_VERSION, cancellationToken)
        .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
      await Assert.That(acknowledged).DoesNotContain(stuckRig.Pod.InstanceId)
        .Because("the handshake completes without the evicted peer — and the fence guarantees "
               + "the zombie cannot heartbeat, win capabilities, or claim work behind it");
    } finally {
      await _clearStandbyAsync(newRig.Pod.InstanceId, cancellationToken);
    }
  }
}
