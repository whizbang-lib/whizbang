using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Shared parent metrics class that owns the IMeterFactory reference.
/// Other Whizbang metrics classes inject this to access the factory.
/// </summary>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/WorkCoordinatorMetricsTests.cs</tests>
public sealed class WhizbangMetrics {
  /// <summary>The meter factory for creating meters.</summary>
  public IMeterFactory? MeterFactory { get; }

  /// <summary>
  /// Initializes WhizbangMetrics with an optional meter factory.
  /// </summary>
  public WhizbangMetrics(IMeterFactory? meterFactory = null) => MeterFactory = meterFactory;
}
