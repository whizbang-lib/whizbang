// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// The lifecycle monitor's remaining decisions against a real registry: the oldest heartbeat across
/// several rows (which sets the next tick's cadence), and the liveness and probe metrics recorded
/// when they are registered.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgInstanceLifecycleMonitor.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class PgInstanceLifecycleMonitorBranchTests : EFCoreTestBase {

  // The cadence follows the OLDEST heartbeat: one instance past half the threshold is enough to
  // tick fast, wherever it sits in the scan. Rows are seeded young, old, middle so the running
  // maximum has to both rise and hold; tracking the last row instead (50 s, under half of the
  // 150 s threshold) would relax the cadence while an instance is drifting toward death.
  [Test]
  [Timeout(60000)]
  public async Task Tick_SeveralLiveInstances_TicksFastWhenTheOldestIsPastHalfTheThresholdAsync(CancellationToken cancellationToken) {
    await _insertHeartbeatAsync(Guid.NewGuid(), ageSeconds: 10, cancellationToken);
    await _insertHeartbeatAsync(Guid.NewGuid(), ageSeconds: 100, cancellationToken);
    await _insertHeartbeatAsync(Guid.NewGuid(), ageSeconds: 50, cancellationToken);
    var whizbangMetrics = _whizbangMetrics();
    var probeMetrics = new ProbeCadenceMetrics(whizbangMetrics);
    var bus = new CountingBus();
    var monitor = _monitor(bus, whizbangMetrics, probeMetrics);

    var next = await monitor.TickForTestsAsync(cancellationToken);

    await Assert.That(monitor.StaleThreshold).IsEqualTo(TimeSpan.FromSeconds(150))
      .Because("the precondition: default heartbeat options give a 150 s threshold");
    await Assert.That(next).IsEqualTo(TimeSpan.FromSeconds(5))
      .Because("the 100 s heartbeat is past half the threshold, so the next look comes fast");
    await Assert.That(bus.Published.Count).IsEqualTo(0)
      .Because("every heartbeat is inside the threshold; nobody is dead");
    await Assert.That(_sum(probeMetrics.ProbeTicks.Instrument, ProbeCadenceMetrics.OUTCOME_IDLE)).IsEqualTo(1L)
      .Because("a tick that found no death is recorded as an idle probe tick");
  }

  [Test]
  [Timeout(60000)]
  public async Task Tick_DeathThenRevival_CountsTheAnnouncementAndTheRetractionAsync(CancellationToken cancellationToken) {
    var instanceId = Guid.NewGuid();
    await _insertHeartbeatAsync(instanceId, ageSeconds: 600, cancellationToken);
    var whizbangMetrics = _whizbangMetrics();
    var livenessMetrics = new InstanceLivenessMetrics(whizbangMetrics);
    var probeMetrics = new ProbeCadenceMetrics(whizbangMetrics);
    var bus = new CountingBus();
    var monitor = _monitor(bus, whizbangMetrics, probeMetrics, livenessMetrics);

    _ = await monitor.TickForTestsAsync(cancellationToken);
    await Assert.That(_sum(livenessMetrics.DeathsAnnounced.Instrument, outcome: null)).IsEqualTo(1L);
    await Assert.That(_sum(probeMetrics.ProbeTicks.Instrument, ProbeCadenceMetrics.OUTCOME_WORK)).IsEqualTo(1L)
      .Because("a tick that found a death is recorded as a probe tick that found work");

    await _insertHeartbeatAsync(instanceId, ageSeconds: 0, cancellationToken);
    _ = await monitor.TickForTestsAsync(cancellationToken);

    await Assert.That(_sum(livenessMetrics.DeathsRetracted.Instrument, outcome: null)).IsEqualTo(1L)
      .Because("the instance heartbeat again, so its announced death is retracted and counted");
    List<Type> expected = [typeof(InstanceDiedSignal), typeof(InstanceJoinedSignal)];
    await Assert.That(bus.Published).IsEquivalentTo(expected);
  }

  private static WhizbangMetrics _whizbangMetrics() =>
    new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());

  private PgInstanceLifecycleMonitor _monitor(
      ISignalBus bus,
      WhizbangMetrics whizbangMetrics,
      ProbeCadenceMetrics probeMetrics,
      InstanceLivenessMetrics? livenessMetrics = null) => new(
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    bus,
    NullLogger<PgInstanceLifecycleMonitor>.Instance,
    metrics: livenessMetrics ?? new InstanceLivenessMetrics(whizbangMetrics),
    probeMetrics: probeMetrics);

  // Seeded on the database clock, which is the clock the scan measures ages against.
  private async Task _insertHeartbeatAsync(Guid instanceId, int ageSeconds, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_service_instances (
        instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@id, 'branch-svc', 'branch-host', 1, NOW() - make_interval(secs => @age), NOW() - make_interval(secs => @age))
      ON CONFLICT (instance_id) DO UPDATE
        SET last_heartbeat_at = EXCLUDED.last_heartbeat_at;", conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("age", (double)ageSeconds);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <summary>Reads a passive counter's current total, optionally only the series with the given outcome tag.</summary>
  private static long _sum(Instrument instrument, string? outcome) {
    long total = 0;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (published, l) => {
      if (ReferenceEquals(published, instrument)) {
        l.EnableMeasurementEvents(published);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      if (outcome is null || _hasTag(tags, ProbeCadenceMetrics.OUTCOME_TAG, outcome)) {
        total += value;
      }
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return total;
  }

  private static bool _hasTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key, string value) {
    foreach (var tag in tags) {
      if (tag.Key == key && Equals(tag.Value, value)) {
        return true;
      }
    }
    return false;
  }

  private sealed class CountingBus : ISignalBus {
    public List<Type> Published { get; } = [];
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
        where TSignal : ISignal {
      Published.Add(typeof(TSignal));
      return ValueTask.CompletedTask;
    }
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal =>
      throw new NotSupportedException("the monitor only publishes");
  }
}
