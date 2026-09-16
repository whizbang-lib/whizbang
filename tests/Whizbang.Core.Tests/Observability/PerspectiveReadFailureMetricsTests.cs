using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// That a row a perspective could not read is counted, by perspective and by reason.
/// </summary>
/// <remarks>
/// A stored-form failure used to leave no trace but a log line per drain cycle. The counter is what
/// a dashboard alerts on and what says, after a rewrite, that the failures stopped.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/PerspectiveMetrics.cs</code-under-test>
[Category("Core")]
[Category("Observability")]
public class PerspectiveReadFailureMetricsTests {
  /// <summary>The instrument exists under the perspective meter, with the documented name.</summary>
  [Test]
  public async Task ReadFailures_IsCreatedUnderThePerspectiveMeterAsync() {
    var metrics = new PerspectiveMetrics(new WhizbangMetrics());

    await Assert.That(metrics.ReadFailures).IsNotNull();
    await Assert.That(metrics.ReadFailures.Name).IsEqualTo("whizbang.perspective.read_failures");
  }

  /// <summary>A failure is recorded with the perspective and the reason as tags.</summary>
  [Test]
  public async Task ReadFailures_RecordsWithPerspectiveAndReasonAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    metrics.ReadFailures.Add(1,
      new KeyValuePair<string, object?>("perspective_name", "OrderPerspective"),
      new KeyValuePair<string, object?>("reason", StoredFormUnreadable.REASON));

    var measurements = helper.GetByName("whizbang.perspective.read_failures")
      .Where(m => m.Tags.GetValueOrDefault("perspective_name") == "OrderPerspective")
      .ToList();
    await Assert.That(measurements).Count().IsEqualTo(1);
    await Assert.That(measurements[0].Value).IsEqualTo(1);
    await Assert.That(measurements[0].Tags["reason"]).IsEqualTo("stored_form_unreadable");
  }
}
