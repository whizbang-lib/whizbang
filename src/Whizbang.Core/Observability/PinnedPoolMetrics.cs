using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for the pinned worker connection pool: borrow timing, borrow
/// timeouts, and connection recycle events. Tagged by worker name where
/// applicable so operators can see which workers are saturating the pool.
/// Meter name: Whizbang.Workers.PinnedPool
/// </summary>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PinnedPoolMetricsTests.cs</tests>
public sealed class PinnedPoolMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Workers.PinnedPool";
#pragma warning restore CA1707

  /// <summary>Time from borrow request to connection handed back. Tag: <c>worker</c>.</summary>
  public Histogram<double> BorrowDuration { get; }

  /// <summary>Count of borrow attempts that timed out waiting for an available connection. Tag: <c>worker</c>.</summary>
  public Counter<long> BorrowTimeouts { get; }

  /// <summary>Count of pinned-connection recycles (driven by <c>ConnectionLifetimeSeconds</c>).</summary>
  public Counter<long> ConnectionRecycles { get; }

  /// <summary>Constructs the metrics group via the host's <see cref="WhizbangMetrics"/> meter factory (matches DispatcherMetrics pattern).</summary>
  public PinnedPoolMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);
    BorrowDuration = meter.CreateHistogram<double>(
      "whizbang.workers.pinned_pool.borrow.duration",
      "ms",
      "Time from borrow request to connection handed back by the pinned pool.");
    BorrowTimeouts = meter.CreateCounter<long>(
      "whizbang.workers.pinned_pool.borrow.timeouts",
      description: "Count of borrow attempts that timed out waiting for an available connection.");
    ConnectionRecycles = meter.CreateCounter<long>(
      "whizbang.workers.pinned_pool.connection_recycles",
      description: "Count of pinned-connection recycles (Npgsql ConnectionLifetime).");
  }
}
