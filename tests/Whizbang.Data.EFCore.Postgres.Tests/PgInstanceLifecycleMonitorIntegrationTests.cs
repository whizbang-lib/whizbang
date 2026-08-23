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
}
