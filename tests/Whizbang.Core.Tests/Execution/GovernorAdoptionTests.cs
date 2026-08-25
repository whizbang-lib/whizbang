using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Execution;

/// <summary>
/// Every worker that adopts the seam must default to its previously-configured width.
/// </summary>
/// <remarks>
/// <para>
/// This is the property that makes the adoption safe to land: a host supplying no governor runs
/// at exactly the number its options already specified. If any of these drift, the refactor has
/// smuggled a scheduling change into a commit that claims to change nothing — which on a live
/// system means a concurrency change nobody reviewed.
/// </para>
/// <para>
/// Each case reads the option through the SAME expression the worker uses to build its fallback,
/// so a future edit that changes which option feeds a worker breaks here rather than silently
/// re-tuning production.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
[Category("Execution")]
public class GovernorAdoptionTests {

  [Test]
  public async Task OutboxDrain_DefaultsToItsConfiguredStreamWidthAsync() {
    var options = new OutboxDrainWorkerOptions { MaxConcurrentStreams = 23 };
    var fallback = new FixedWidthGovernor(options.MaxConcurrentStreams);

    await Assert.That(fallback.CurrentWidth).IsEqualTo(23);
    await Assert.That(fallback.Floor).IsEqualTo(23);
    await Assert.That(fallback.Ceiling).IsEqualTo(23)
      .Because("a fixed governor must pin floor and ceiling to the same value, or a later "
             + "adaptive swap could drift within a band the operator never configured");
  }

  [Test]
  public async Task PerspectiveWorker_DefaultsToItsConfiguredPerspectiveWidthAsync() {
    var options = new PerspectiveWorkerOptions { MaxConcurrentPerspectives = 30 };
    var fallback = new FixedWidthGovernor(options.MaxConcurrentPerspectives);

    await Assert.That(fallback.CurrentWidth).IsEqualTo(30)
      .Because("30 is the shipped default; adopting the seam must not quietly change how many "
             + "perspectives run at once");
  }

  [Test]
  public async Task FixedGovernor_ClampsNonsenseWidthsToSomethingRunnableAsync() {
    // Options are host-supplied and can be wrong. A width of zero would stall the worker
    // completely, which is a worse failure than ignoring the bad value.
    await Assert.That(new FixedWidthGovernor(0).CurrentWidth).IsEqualTo(1);
    await Assert.That(new FixedWidthGovernor(-5).CurrentWidth).IsEqualTo(1)
      .Because("a governor that returns zero width converts a misconfiguration into a silent "
             + "total stall — the worker would run forever and process nothing");
  }

  [Test]
  public async Task AdaptiveGovernor_StaysWithinItsBandAcrossAMixedRunAsync() {
    // A mixed sequence, because the real risk is drift over many cycles rather than any single
    // transition: alternating pressure and pushback must not walk the width outside its band.
    var g = new AdaptiveConcurrencyGovernor(floor: 4, ceiling: 32);

    for (var i = 0; i < 500; i++) {
      var contended = i % 3 == 0;
      g.Observe(new GovernorSignal(QueuedItems: 100, Contended: contended, Elapsed: TimeSpan.FromMilliseconds(10)));
      if (g.CurrentWidth < 4 || g.CurrentWidth > 32) {
        break;
      }
    }

    await Assert.That(g.CurrentWidth).IsGreaterThanOrEqualTo(4);
    await Assert.That(g.CurrentWidth).IsLessThanOrEqualTo(32)
      .Because("the band is the safety contract — an adaptive width that escapes it is exactly "
             + "the connection-exhaustion failure the ceiling exists to prevent");
  }
}
