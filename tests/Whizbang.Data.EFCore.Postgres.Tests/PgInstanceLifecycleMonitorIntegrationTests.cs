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
}
