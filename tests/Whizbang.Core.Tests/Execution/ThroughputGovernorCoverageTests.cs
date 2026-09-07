using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;

namespace Whizbang.Core.Tests.Execution;

/// <summary>
/// Covers <see cref="ThroughputGovernor.Observe"/>'s zero-elapsed guard — a cycle can report
/// completed work with zero measured wall time (a clock resolution artifact, or a caller wiring
/// a fake/frozen <c>TimeProvider</c> incorrectly), and the governor must not turn that into a
/// division-by-zero "infinite" rate that drives width growth on noise.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Execution/ThroughputGovernor.cs</code-under-test>
[Category("Execution")]
public class ThroughputGovernorCoverageTests {

  [Test]
  public async Task Observe_ZeroElapsedTime_DoesNotWidenAsync() {
    // Establish a real baseline first, so the zero-elapsed cycle isn't just the (harmless)
    // first-ever measurement. If the elapsed<=0 guard were missing, completedItems/0 evaluates to
    // positive infinity in floating point, which is ">= bestRate * 1.05" and would widen the
    // governor on a measurement that carries no real throughput information at all.
    var g = new ThroughputGovernor(floor: 4, ceiling: 64);
    g.Observe(new GovernorSignal(QueuedItems: 500, Contended: false, Elapsed: TimeSpan.FromMilliseconds(100), CompletedItems: 100));
    var widthAfterBaseline = g.CurrentWidth;

    g.Observe(new GovernorSignal(QueuedItems: 500, Contended: false, Elapsed: TimeSpan.Zero, CompletedItems: 1000));

    await Assert.That(g.CurrentWidth).IsEqualTo(widthAfterBaseline)
      .Because("a cycle with zero elapsed time carries no measurable rate and must be ignored, not read as an infinite (and therefore always-improving) throughput spike.");
  }
}
