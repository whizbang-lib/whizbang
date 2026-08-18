namespace Whizbang.Core.Signals;

/// <summary>
/// Tuning for the hosted signal bus's wire-route self-test and doorbell-liveness monitor.
/// Defaults are production-safe; tests shrink the timeout for deterministic failure paths.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public sealed class SignalBusOptions {
  /// <summary>
  /// How long a single transport's loopback probe may take before the wire route is marked failed.
  /// </summary>
  public int ProbeTimeoutMilliseconds { get; set; } = 5_000;

  /// <summary>
  /// Cadence of the runtime re-probe: the same loopback self-test re-runs on this interval so a
  /// listener that dies mid-run is caught even when the service is idle.
  /// </summary>
  public int ReProbeIntervalMilliseconds { get; set; } = 300_000;

  /// <summary>
  /// How many consecutive work batches may be discovered by poll (with no preceding doorbell, on
  /// the empty-to-non-empty edge where the store guarantees one rings) before the signal bus
  /// reports itself <see cref="Health.ComponentState.Degraded"/>.
  /// </summary>
  public int MissedDoorbellThreshold { get; set; } = 3;
}
