namespace Whizbang.Core.Signals;

/// <summary>
/// Tuning for the hosted signal bus's wire-route self-test and doorbell-liveness monitor.
/// Defaults are production-safe; tests shrink the timeout for deterministic failure paths.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <tests>tests/Whizbang.Core.Tests/Signals/SignalBusProbeBackoffTests.cs</tests>
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

  /// <summary>
  /// How long the <em>first</em> probe after startup may take. <see langword="null"/> (the default)
  /// uses <see cref="ProbeTimeoutMilliseconds"/>. Raise it to give the startup probe a grace period
  /// when many services start at once against one database and the first round trip is slow.
  /// </summary>
  /// <docs>fundamentals/signal-bus/signal-bus#probe-backoff-and-recovery</docs>
  public int? FirstProbeTimeoutMilliseconds { get; set; }

  /// <summary>
  /// Delays before each retry after a failed probe, in order, before the loop returns to
  /// <see cref="ReProbeIntervalMilliseconds"/>. <see langword="null"/> (the default) uses
  /// <see cref="DefaultFailedProbeRetryDelaysMilliseconds"/>; an empty array disables the backoff.
  /// </summary>
  /// <remarks>
  /// A transient failure (a busy database on a cold start) then clears within seconds instead of
  /// holding the <c>signal-bus</c> component Degraded for the whole re-probe interval, while a route
  /// that is really down is still reported: every failed probe is logged, as a warning while retries
  /// remain and as an error once they are exhausted. Null rather than a pre-filled array, because
  /// configuration binding appends to an array that already has elements.
  /// </remarks>
  /// <docs>fundamentals/signal-bus/signal-bus#probe-backoff-and-recovery</docs>
  public int[]? FailedProbeRetryDelaysMilliseconds { get; set; }

  /// <summary>The retry delays used when <see cref="FailedProbeRetryDelaysMilliseconds"/> is not set: 5 s, 15 s, 30 s, 60 s.</summary>
  public static IReadOnlyList<int> DefaultFailedProbeRetryDelaysMilliseconds { get; } = [5_000, 15_000, 30_000, 60_000];
}
