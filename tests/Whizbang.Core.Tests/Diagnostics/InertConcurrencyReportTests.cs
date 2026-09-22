using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// A concurrency setting that cannot take effect must say so.
/// </summary>
/// <remarks>
/// <para>
/// <c>ParallelizeStreams</c> defaults to <c>false</c> on both
/// <see cref="WorkCoordinatorOptions"/> and <see cref="OrderedStreamProcessorOptions"/>, while
/// <c>MaxConcurrentStreams</c> defaults to 16 and <c>MaxConcurrentDispatch</c> to 8. So the
/// out-of-box configuration advertises concurrency that the runtime will not use. This is not a
/// misconfiguration an operator introduced — it is the shipped default.
/// </para>
/// <para>
/// Measured consequence on a real pipeline: raising <c>MaxConcurrentStreams</c> from 16 to 128 with
/// parallelism disabled moved drain from 26 to 352 rows/min. The same width with parallelism
/// enabled reached 2,664 rows/min. The knob appeared to do something — enough to look like the
/// answer — while the actual constraint sat elsewhere, unnamed.
/// </para>
/// <para>
/// Worse, the two flags have the SAME NAME in different option types. An operator who finds one,
/// sets it, and measures a genuine improvement will reasonably stop looking. That is exactly the
/// sequence that occurred, in an investigation specifically hunting this defect.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Diagnostics/InertConcurrencyReport.cs</code-under-test>
[Category("Diagnostics")]
public class InertConcurrencyReportTests {

  private static (WorkCoordinatorOptions Coordinator, OrderedStreamProcessorOptions Ordered,
                  OutboxDrainWorkerOptions Drain, InboxDispatchWorkerOptions Dispatch) _defaults()
    => (new WorkCoordinatorOptions(), new OrderedStreamProcessorOptions(),
        new OutboxDrainWorkerOptions(), new InboxDispatchWorkerOptions());

  [Test]
  public async Task TheSHIPPEDDefaultsAreThemselvesInertAndMustBeReportedAsync() {
    var (c, o, d, i) = _defaults();

    var findings = InertConcurrencyReport.Analyze(c, o, d, i);

    await Assert.That(findings.Count).IsGreaterThan(0)
      .Because("out of the box ParallelizeStreams is false while MaxConcurrentStreams is 16 and "
             + "MaxConcurrentDispatch is 8 — the framework advertises concurrency it will not use, "
             + "and every deployment inherits that silently");
  }

  [Test]
  public async Task NamesBOTHFlagsSoFixingOneDoesNotLookLikeFixingAllAsync() {
    var (c, o, d, i) = _defaults();
    c.ParallelizeStreams = true;   // operator found and fixed ONE of the two

    var findings = InertConcurrencyReport.Analyze(c, o, d, i);
    var text = string.Join(" | ", findings);

    await Assert.That(findings.Count).IsGreaterThan(0)
      .Because("the second flag still serializes its stage; reporting nothing here is precisely "
             + "how an operator concludes the problem is solved while half the pipeline is serial");
    await Assert.That(text).Contains(nameof(OrderedStreamProcessorOptions), StringComparison.Ordinal)
      .Because("the finding has to name WHICH option type is still disabled — two flags share the "
             + "name ParallelizeStreams, so the name alone does not identify the one to change");
  }

  [Test]
  public async Task SaysNothingWhenParallelismIsFullyEnabledAsync() {
    var (c, o, d, i) = _defaults();
    c.ParallelizeStreams = true;
    o.ParallelizeStreams = true;

    var findings = InertConcurrencyReport.Analyze(c, o, d, i);

    await Assert.That(findings.Count).IsEqualTo(0)
      .Because("a correctly configured deployment must stay quiet, or the warning becomes noise "
             + "everyone filters out — which is the same as not having it");
  }

  [Test]
  public async Task SaysNothingWhenConcurrencyIsDeliberatelySerialAsync() {
    var (c, o, d, i) = _defaults();
    d.MaxConcurrentStreams = 1;
    i.MaxConcurrentDispatch = 1;

    var findings = InertConcurrencyReport.Analyze(c, o, d, i);

    await Assert.That(findings.Count).IsEqualTo(0)
      .Because("width 1 with parallelism off is coherent — the operator asked for serial and got "
             + "serial. Warning here would punish the one configuration that is not contradictory");
  }

  [Test]
  public async Task ReportsTheConfiguredWidthSoTheCostIsLegibleAsync() {
    var (c, o, d, i) = _defaults();
    d.MaxConcurrentStreams = 128;

    var findings = InertConcurrencyReport.Analyze(c, o, d, i);
    var text = string.Join(" | ", findings);

    await Assert.That(text).Contains("128", StringComparison.Ordinal)
      .Because("'concurrency is disabled' is a shrug; 'you configured 128 and are getting 1' is "
             + "a number someone acts on");
  }

  [Test]
  public async Task NullOptionsAreToleratedRatherThanCrashingStartupAsync() {
    var findings = InertConcurrencyReport.Analyze(null, null, null, null);

    await Assert.That(findings.Count).IsEqualTo(0)
      .Because("a diagnostic that can abort startup is a worse defect than the one it reports");
  }
}
