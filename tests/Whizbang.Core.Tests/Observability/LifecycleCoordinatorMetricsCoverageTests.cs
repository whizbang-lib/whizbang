using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage round 23 tail: <see cref="LifecycleCoordinatorMetrics.PostLifecycleErrors"/>. Every
/// other counter/histogram on this class has an increment-and-record test in
/// LifecycleCoordinatorMetricsTests.cs; this one alone is never read or recorded to.
/// </summary>
[Category("Core")]
[Category("Observability")]
public class LifecycleCoordinatorMetricsCoverageTests {

  /// <summary>
  /// Operator impact: PostLifecycle stage errors are isolated per-event (one bad event must not
  /// take down the whole batch) — this counter is the ONLY way an operator sees that isolation is
  /// silently absorbing failures. A gap here means those errors happen and nothing counts them.
  /// </summary>
  [Test]
  public async Task PostLifecycleErrors_Incremented_RecordedAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var metrics = new LifecycleCoordinatorMetrics(whizbangMetrics);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.PostLifecycleErrors.Add(1);

    var measurements = helper.GetByName("whizbang.lifecycle_coordinator.post_lifecycle_errors");
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(1);
  }
}
