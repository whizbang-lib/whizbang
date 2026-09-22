using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The composite and collective meters (#738): composites disappear once expanded, so without these
/// counters nothing shows how many a consumer received, how many times each was expanded, how many child
/// rows the expansions created, or how many children were dropped as unsubscribed. Passive counters, read
/// at collection, like every other framework meter.
/// </summary>
/// <docs>operations/observability/metrics#composites-and-collectives</docs>
[Category("Unit")]
[Category("Observability")]
public class CompositeMetricsTests {

  [Test]
  public async Task Counters_ReportWhatWasAddedWhenPolledAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new CompositeMetrics(new WhizbangMetrics(factory));
    var meter = factory.CreatedMeters[0];

    metrics.Received.Add(1);
    metrics.Expansions.Add(1);
    metrics.Expansions.Add(1);
    metrics.ChildrenCreated.Add(44);
    metrics.ChildrenUnsubscribed.Add(40);
    metrics.ChildrenRefused.Add(2);
    metrics.DeadLettered.Add(1);
    metrics.CommitFailures.Add(1);
    metrics.CollectivesReceived.Add(3);
    metrics.CollectivesApplied.Add(2);
    metrics.CollectivesSkipped.Add(1);

    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.received")).IsEqualTo(1);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.expansions")).IsEqualTo(2)
      .Because("expansions above received is exactly the repeat the meter exists to show");
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.children_created")).IsEqualTo(44);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.children_unsubscribed")).IsEqualTo(40);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.children_refused")).IsEqualTo(2);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.dead_lettered")).IsEqualTo(1);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.commit_failures")).IsEqualTo(1);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.collectives.received")).IsEqualTo(3);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.collectives.applied")).IsEqualTo(2);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.collectives.skipped")).IsEqualTo(1);
  }

  [Test]
  public async Task Meter_IsNamedForTheCompositesDomain_AndIsInTheFrameworkListAsync() {
    using var factory = new TestMeterFactory();
    _ = new CompositeMetrics(new WhizbangMetrics(factory));

    await Assert.That(factory.CreatedMeters[0].Name).IsEqualTo("Whizbang.Composites");
    await Assert.That(WhizbangMeters.All).Contains(CompositeMetrics.METER_NAME)
      .Because("a host subscribes to every framework meter from this list; a meter missing here is invisible");
  }

  [Test]
  public async Task Counters_StartAtZero_WithoutAnyAddAsync() {
    using var factory = new TestMeterFactory();
    _ = new CompositeMetrics(new WhizbangMetrics(factory));
    var meter = factory.CreatedMeters[0];

    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.expansions")).IsEqualTo(0)
      .Because("a consumer that received no composite reports zero, not nothing, so the dashboard can tell idle from unreported");
  }
}
