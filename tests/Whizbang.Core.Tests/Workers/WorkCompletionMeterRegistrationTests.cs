using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The outstanding budget only engages when it can measure drain, and it measures drain through a
/// shared <see cref="WorkCompletionMeter"/>. That makes registration a correctness concern rather
/// than a wiring detail.
/// </summary>
/// <remarks>
/// <para>
/// Two failure modes are silent, which is why they are pinned here rather than left to inspection:
/// </para>
/// <para>
/// <b>Missing.</b> With no meter, the claim loop declines to bound outstanding work at all — the
/// behaviour the budget exists to replace. The fix would appear to ship and simply not apply, which
/// is the exact failure this whole line of work already produced once.
/// </para>
/// <para>
/// <b>Not shared.</b> A per-consumer instance compiles, runs, and measures nothing: the dispatch
/// side records completions into one meter while the claim side reads another, so the drain rate
/// reads zero forever and the budget sits at its floor. Nothing errors; throughput just quietly
/// collapses.
/// </para>
/// </remarks>
[Category("Workers")]
public class WorkCompletionMeterRegistrationTests {

  [Test]
  public async Task CoreRegistration_ProvidesTheMeterAsync() {
    var services = new ServiceCollection();
    Whizbang.Core.ServiceCollectionExtensions.AddWhizbang(services);

    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetService<WorkCompletionMeter>()).IsNotNull()
      .Because("without a meter the budget never engages, so the over-claim bound silently does not "
             + "apply — a fix that appears to ship and does nothing");
  }

  [Test]
  public async Task Meter_IsSharedBetweenTheClaimAndDispatchSidesAsync() {
    var services = new ServiceCollection();
    Whizbang.Core.ServiceCollectionExtensions.AddWhizbang(services);

    await using var provider = services.BuildServiceProvider();
    var first = provider.GetService<WorkCompletionMeter>();
    var second = provider.GetService<WorkCompletionMeter>();

    // Singleton, not transient. The claim loop reads what the dispatch workers record; two
    // instances would leave the reader permanently at zero while every completion lands in the
    // other object. Nothing throws — the budget just never grows past its floor.
    await Assert.That(first).IsSameReferenceAs(second)
      .Because("the reader and the writers must observe the same counter, or drain measures zero "
             + "forever and throughput collapses without an error anywhere");
  }
}
