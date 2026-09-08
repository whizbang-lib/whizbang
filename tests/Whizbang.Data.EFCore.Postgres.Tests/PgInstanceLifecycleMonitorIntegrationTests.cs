using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the InstanceDied publish contract: when the monitor scans <c>wh_service_instances</c>
/// and finds a pod whose <c>last_heartbeat_at</c> is older than the stale threshold (30s), it
/// publishes <see cref="InstanceDiedSignal"/> on the bus exactly once per newly-detected death.
/// This is the failover trigger for orphan takeover, so both the "no false positive when everyone
/// is healthy" and "one publish per death, not one per tick" invariants matter.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[Category("Shard3")]
public class PgInstanceLifecycleMonitorIntegrationTests : EFCoreTestBase {
  private sealed class CountingBus : ISignalBus {
    public List<Type> Published { get; } = [];
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Published.Add(typeof(TSignal));
      return ValueTask.CompletedTask;
    }
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal
      => new NoopSub();
    private sealed class NoopSub : ISignalSubscription { public void Dispose() { } }
  }

  /// <summary>Publishes by throwing — the announce path's failure and shutdown arms.</summary>
  private sealed class ThrowingBus(Exception toThrow) : ISignalBus {
    public int Attempts { get; private set; }
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Attempts++;
      return ValueTask.FromException(toThrow);
    }
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal
      => new NoopSub();
    private sealed class NoopSub : ISignalSubscription { public void Dispose() { } }
  }

  /// <summary>
  /// Announces deaths normally and fails the FIRST retraction (InstanceJoinedSignal) with the given
  /// exception, then succeeds: the retract path's failure and shutdown arms.
  /// </summary>
  private sealed class RetractFailingOnceBus(Exception toThrow) : ISignalBus {
    private bool _failed;
    public List<Type> Published { get; } = [];
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      if (typeof(TSignal) == typeof(InstanceJoinedSignal) && !_failed) {
        _failed = true;
        return ValueTask.FromException(toThrow);
      }
      Published.Add(typeof(TSignal));
      return ValueTask.CompletedTask;
    }
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal
      => new NoopSub();
    private sealed class NoopSub : ISignalSubscription { public void Dispose() { } }
  }

  private async Task _insertHeartbeatAsync(Guid instanceId, DateTimeOffset lastHeartbeatAt) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_service_instances (
        instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@id, 'utest-svc', 'utest-host', 1, @started, @last)
      ON CONFLICT (instance_id) DO UPDATE
        SET last_heartbeat_at = EXCLUDED.last_heartbeat_at;", conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("started", lastHeartbeatAt);
    cmd.Parameters.AddWithValue("last", lastHeartbeatAt);
    await cmd.ExecuteNonQueryAsync();
  }

  private PgInstanceLifecycleMonitor _createMonitor(ISignalBus bus) {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    return new PgInstanceLifecycleMonitor(
      Options.Create(opts), cfg, bus,
      NullLogger<PgInstanceLifecycleMonitor>.Instance);
  }

  [Test]
  public async Task Tick_StaleInstance_PublishesInstanceDiedSignalAsync() {
    // Insert a heartbeat that is well past the 30s stale threshold.
    var deadId = Guid.NewGuid();
    await _insertHeartbeatAsync(deadId, DateTimeOffset.UtcNow.AddMinutes(-5));

    var bus = new CountingBus();
    var monitor = _createMonitor(bus);

    await monitor.TickForTestsAsync(CancellationToken.None);

    await Assert.That(bus.Published).Contains(typeof(InstanceDiedSignal))
      .Because("a stale heartbeat older than the 30s threshold must trigger InstanceDiedSignal");
  }

  [Test]
  public async Task Tick_FreshInstance_DoesNotPublishAsync() {
    // Heartbeat well inside the stale window.
    var liveId = Guid.NewGuid();
    await _insertHeartbeatAsync(liveId, DateTimeOffset.UtcNow.AddSeconds(-1));

    var bus = new CountingBus();
    var monitor = _createMonitor(bus);

    await monitor.TickForTestsAsync(CancellationToken.None);

    await Assert.That(bus.Published.Contains(typeof(InstanceDiedSignal))).IsFalse()
      .Because("a healthy heartbeat must not raise a false-positive death signal");
  }

  [Test]
  public async Task Tick_SameDeath_PublishesOnlyOnceAsync() {
    // Same stale row, ticked twice. The second tick must NOT republish — the monitor tracks
    // announced deaths in-process to keep observability + wh_signals growth clean.
    var deadId = Guid.NewGuid();
    await _insertHeartbeatAsync(deadId, DateTimeOffset.UtcNow.AddMinutes(-5));

    var bus = new CountingBus();
    var monitor = _createMonitor(bus);

    await monitor.TickForTestsAsync(CancellationToken.None);
    await monitor.TickForTestsAsync(CancellationToken.None);

    var deathCount = bus.Published.Count(t => t == typeof(InstanceDiedSignal));
    await Assert.That(deathCount).IsEqualTo(1)
      .Because("one publish per newly-detected death, not one per tick — subscribers get a clean event stream");
  }

  [Test]
  [Timeout(60000)]
  public async Task Tick_PublishFails_RetriesTheAnnouncementOnTheNextTickAsync(
      CancellationToken cancellationToken) {
    // The death is marked announced BEFORE the publish, so a failed publish has to un-mark it or
    // the pod's death is never broadcast and its owned streams are never taken over. Two ticks,
    // two attempts.
    var bus = new ThrowingBus(new InvalidOperationException("signal bus unavailable"));
    await _insertHeartbeatAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-30));
    var monitor = _createMonitor(bus);

    await monitor.TickForTestsAsync(cancellationToken);
    await monitor.TickForTestsAsync(cancellationToken);

    await Assert.That(bus.Attempts).IsGreaterThanOrEqualTo(2)
      .Because("an announcement that failed must be retried — otherwise the dead pod's streams "
             + "wait for a takeover signal that was already marked sent");
  }

  [Test]
  [Timeout(60000)]
  public async Task Tick_PublishCanceledByShutdown_SurfacesRatherThanRetryingAsync(
      CancellationToken cancellationToken) {
    // The failure arm un-marks the death so the next tick retries. Shutdown does not need that:
    // the loop breaks, the monitor ends, and the marked-but-unannounced death goes with the
    // object — the next process starts with an empty set and re-detects the same stale row.
    // What must not happen is the shutdown being absorbed as a publish failure, which would keep
    // the tick loop running against a bus that is going away.
    var bus = new ThrowingBus(new OperationCanceledException());
    await _insertHeartbeatAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-30));
    var monitor = _createMonitor(bus);

    await Assert.That(async () => await monitor.TickForTestsAsync(cancellationToken))
      .Throws<OperationCanceledException>()
      .Because("the announcement is not retryable work when the host is on its way down");
    await Assert.That(bus.Attempts).IsEqualTo(1)
      .Because("the tick stops at the first canceled publish rather than walking the rest of "
             + "the dead list on a stopping host");
  }

  [Test]
  [Timeout(60000)]
  public async Task ExecuteAsync_PublishCanceledByShutdown_EndsTheTickLoopAsync(
      CancellationToken cancellationToken) {
    // Same cancellation as the tick-level test above, but observed through the hosted loop, which
    // is where it decides the monitor's fate. Note what is NOT done here: the monitor's own
    // stopping token is never cancelled. The loop condition therefore stays true forever, and the
    // five-second inter-tick delay never faults — so the ONLY way this loop can end is the tick's
    // cancellation being read as "the host is stopping" rather than as "this tick failed, try
    // again". Absorb it as a tick failure and the monitor scans on a dying bus indefinitely; the
    // task below would simply never complete.
    var bus = new ThrowingBus(new OperationCanceledException());
    await _insertHeartbeatAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-30));
    var monitor = _createMonitor(bus);

    await monitor.StartAsync(cancellationToken);
    try {
      var loop = monitor.ExecuteTask;
      await Assert.That(loop is null).IsFalse()
        .Because("BackgroundService publishes its loop task from StartAsync — without it there is nothing to observe");

      await loop!.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

      await Assert.That(loop.IsCompleted).IsTrue()
        .Because("a canceled publish must end the monitor loop; with the stopping token still uncanceled, nothing else could have ended it");
      await Assert.That(loop.IsFaulted).IsFalse()
        .Because("shutdown is an orderly exit, not a crash — the cancellation must not escape the loop as a fault the host reports");
      await Assert.That(bus.Attempts).IsEqualTo(1)
        .Because("the loop stops at the first canceled publish instead of coming back around for another scan");
    } finally {
      // Bounded so a regression that leaves the loop running fails on the assertions above
      // rather than hanging the suite in StopAsync's wait on the loop task.
      using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      await monitor.StopAsync(stopCts.Token);
    }
  }

  // ============================================================
  // Two-signal liveness (heartbeat OR alive-lock), derived threshold, reversible announcements
  // ============================================================

  /// <summary>Holds the instance's session alive-lock on a connection the caller keeps open.</summary>
  private async Task<NpgsqlConnection> _holdAliveLockAsync(Guid instanceId, CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = new NpgsqlCommand("SELECT claim_instance_alive_lock(@id)", conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    var held = (bool)(await cmd.ExecuteScalarAsync(ct))!;
    if (!held) {
      await conn.DisposeAsync();
      throw new InvalidOperationException("precondition: the alive-lock could not be taken");
    }
    return conn;
  }

  [Test]
  [Timeout(60000)]
  public async Task Tick_StaleHeartbeatButAliveLockHeld_DoesNotAnnounceDeathAsync(CancellationToken cancellationToken) {
    // The failure mode this closes: an instance whose heartbeat write is stuck behind a commit
    // stall is still holding its session lock, which is the primary liveness signal. Judging by
    // the timestamp alone announced it dead while it was running, and its work was handed away.
    var stuckId = Guid.CreateVersion7();
    await _insertHeartbeatAsync(stuckId, DateTimeOffset.UtcNow.AddMinutes(-10));
    await using var lockHolder = await _holdAliveLockAsync(stuckId, cancellationToken);
    var bus = new CountingBus();
    var monitor = _createMonitor(bus);

    await monitor.TickForTestsAsync(cancellationToken);

    await Assert.That(bus.Published.Contains(typeof(InstanceDiedSignal))).IsFalse()
      .Because("a held session lock means the process is alive whatever its last timestamp says; the monitor must ask is_instance_alive, not compare timestamps");
    await Assert.That(monitor.AnnouncedDeaths.Contains(stuckId)).IsFalse();
  }

  [Test]
  [Timeout(60000)]
  public async Task Tick_HeartbeatFortySecondsOldWithoutLock_IsNotDeadUnderTheDerivedThresholdAsync(CancellationToken cancellationToken) {
    // Under the old 30 s constant this row was a corpse; a writer on the 60 s slow cadence produced
    // one every minute. The threshold is now derived from the cadence (150 s with the defaults).
    var lateId = Guid.CreateVersion7();
    await _insertHeartbeatAsync(lateId, DateTimeOffset.UtcNow.AddSeconds(-40));
    var bus = new CountingBus();
    var monitor = _createMonitor(bus);

    await monitor.TickForTestsAsync(cancellationToken);

    await Assert.That(bus.Published.Contains(typeof(InstanceDiedSignal))).IsFalse()
      .Because("forty seconds is inside two slow intervals plus a fast one; the beat is late, not missing");
    await Assert.That(monitor.StaleThreshold).IsEqualTo(TimeSpan.FromSeconds(150));
  }

  [Test]
  [Timeout(60000)]
  public async Task Tick_AnnouncedInstanceBeatsAgain_RetractsWithInstanceJoinedAsync(CancellationToken cancellationToken) {
    // A false announcement must be reversible. When a heartbeat lands after the death was
    // published, the monitor publishes InstanceJoined so subscribers that reassigned the
    // instance's work can let it back in, and forgets the death so a later real one is announced.
    var id = Guid.CreateVersion7();
    await _insertHeartbeatAsync(id, DateTimeOffset.UtcNow.AddMinutes(-10));
    var bus = new CountingBus();
    var monitor = _createMonitor(bus);
    await monitor.TickForTestsAsync(cancellationToken);
    await Assert.That(monitor.AnnouncedDeaths.Contains(id)).IsTrue().Because("precondition: the stale row was announced");

    await _insertHeartbeatAsync(id, DateTimeOffset.UtcNow);
    await monitor.TickForTestsAsync(cancellationToken);

    await Assert.That(bus.Published).Contains(typeof(InstanceJoinedSignal))
      .Because("the retraction is the same signal a fresh instance raises, so subscribers need no new handler");
    await Assert.That(monitor.AnnouncedDeaths.Contains(id)).IsFalse()
      .Because("once retracted, the same instance dying for real later must be announced again");
  }

  [Test]
  [Timeout(60000)]
  public async Task Tick_RetractionPublishFails_ReArmsTheRetractionForTheNextTickAsync(CancellationToken cancellationToken) {
    // A retraction that cannot be published must not be forgotten: the death stays announced so the
    // next tick tries again, otherwise a subscriber that reassigned the instance's work never lets it back in.
    var id = Guid.CreateVersion7();
    await _insertHeartbeatAsync(id, DateTimeOffset.UtcNow.AddMinutes(-10));
    var bus = new RetractFailingOnceBus(new InvalidOperationException("signal transport down"));
    var monitor = _createMonitor(bus);
    await monitor.TickForTestsAsync(cancellationToken);
    await Assert.That(monitor.AnnouncedDeaths.Contains(id)).IsTrue().Because("precondition: the stale row was announced");

    await _insertHeartbeatAsync(id, DateTimeOffset.UtcNow);
    await monitor.TickForTestsAsync(cancellationToken);
    await Assert.That(monitor.AnnouncedDeaths.Contains(id)).IsTrue()
      .Because("the failed retraction re-arms the announcement so the next tick retries it");
    await Assert.That(bus.Published.Contains(typeof(InstanceJoinedSignal))).IsFalse();

    await monitor.TickForTestsAsync(cancellationToken);

    await Assert.That(bus.Published).Contains(typeof(InstanceJoinedSignal))
      .Because("the retry on the next tick publishes the retraction");
    await Assert.That(monitor.AnnouncedDeaths.Contains(id)).IsFalse();
  }

  [Test]
  [Timeout(60000)]
  public async Task Tick_RetractionCanceledByShutdown_SurfacesRatherThanRetryingAsync(CancellationToken cancellationToken) {
    var id = Guid.CreateVersion7();
    await _insertHeartbeatAsync(id, DateTimeOffset.UtcNow.AddMinutes(-10));
    var bus = new RetractFailingOnceBus(new OperationCanceledException("shutting down"));
    var monitor = _createMonitor(bus);
    await monitor.TickForTestsAsync(cancellationToken);
    await _insertHeartbeatAsync(id, DateTimeOffset.UtcNow);

    await Assert.That(async () => await monitor.TickForTestsAsync(cancellationToken))
      .Throws<OperationCanceledException>()
      .Because("shutdown is not a transport failure to retry; it ends the tick loop");
  }

  [Test]
  [Timeout(60000)]
  public async Task Tick_RetractedThenDeadAgain_AnnouncesASecondTimeAsync(CancellationToken cancellationToken) {
    var id = Guid.CreateVersion7();
    await _insertHeartbeatAsync(id, DateTimeOffset.UtcNow.AddMinutes(-10));
    var bus = new CountingBus();
    var monitor = _createMonitor(bus);
    await monitor.TickForTestsAsync(cancellationToken);
    await _insertHeartbeatAsync(id, DateTimeOffset.UtcNow);
    await monitor.TickForTestsAsync(cancellationToken);

    await _insertHeartbeatAsync(id, DateTimeOffset.UtcNow.AddMinutes(-10));
    await monitor.TickForTestsAsync(cancellationToken);

    var deaths = bus.Published.Count(t => t == typeof(InstanceDiedSignal));
    await Assert.That(deaths).IsEqualTo(2)
      .Because("dead, back, dead again is two deaths; the retraction cleared the first");
  }
}
