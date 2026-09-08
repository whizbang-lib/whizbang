using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Passive meters for instance liveness: the heartbeat writer's out-of-cadence beats and slow
/// beats, and the lifecycle monitor's death announcements and retractions. Every series exists at
/// zero from construction, so a fleet that never had a false death shows the retraction counter at
/// zero rather than as a missing meter.
/// Meter name: <c>Whizbang.Liveness</c>.
/// </summary>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/InstanceLivenessMetricsTests.cs</tests>
public sealed class InstanceLivenessMetrics {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Liveness";
#pragma warning restore CA1707

  /// <summary>Initializes the liveness meters on the shared Whizbang meter factory.</summary>
  /// <param name="whizbangMetrics">The shared meter factory holder.</param>
  /// <exception cref="ArgumentNullException">Thrown when the holder is null.</exception>
  public InstanceLivenessMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    WatchdogBeats = meter.CreatePassiveCounter<long>("whizbang.liveness.watchdog_beats",
      description: "Heartbeats forced ahead of cadence because the regular beat ran late enough to approach the stale threshold");
    SlowBeats = meter.CreatePassiveCounter<long>("whizbang.liveness.slow_beats",
      description: "Heartbeats whose round trip took at least one fast interval");
    DeathsAnnounced = meter.CreatePassiveCounter<long>("whizbang.liveness.deaths_announced",
      description: "InstanceDied signals published by the lifecycle monitor");
    DeathsRetracted = meter.CreatePassiveCounter<long>("whizbang.liveness.deaths_retracted",
      description: "Announced deaths retracted because the instance was alive again on a later tick");
  }

  /// <summary>Heartbeats the writer forced ahead of cadence.</summary>
  public PassiveCounter<long> WatchdogBeats { get; }

  /// <summary>Heartbeats whose round trip took at least one fast interval.</summary>
  public PassiveCounter<long> SlowBeats { get; }

  /// <summary>Death announcements published.</summary>
  public PassiveCounter<long> DeathsAnnounced { get; }

  /// <summary>Death announcements retracted.</summary>
  public PassiveCounter<long> DeathsRetracted { get; }
}
