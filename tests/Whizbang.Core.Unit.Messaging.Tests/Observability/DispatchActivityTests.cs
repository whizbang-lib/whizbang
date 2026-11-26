// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Every dispatch and perspective sync wait starts its activity through one helper, named <c>{Operation} {Type}</c>
/// (#2548). The name reads the
/// message type's name, which the runtime caches behind a weak handle, and an activity name is built whether or not
/// anything listens. So with nothing listening the helper builds no name and allocates nothing, even right after a
/// collection; with a listener the activity carries the name the dispatcher always gave it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/WhizbangActivitySource.cs</code-under-test>
[NotInParallel("AllocationMeasurement")]
public class DispatchActivityTests {
  private const int ITERATIONS = 1_000;

  private sealed record DispatchActivityProbe;

  [Test]
  public async Task StartForType_WithNothingListening_BuildsNoNameAndAllocatesNothingAsync() {
    using var source = new ActivitySource("Whizbang.Tests.DispatchActivity.Unheard." + Guid.CreateVersion7().ToString("N"));
    await Assert.That(source.HasListeners()).IsFalse().Because("the measurement needs a source nothing listens to");
    _ = WhizbangActivitySource.StartForType(source, "Dispatch", typeof(DispatchActivityProbe));

    var before = GC.GetAllocatedBytesForCurrentThread();
    GC.Collect();
    Activity? last = null;
    for (var i = 0; i < ITERATIONS; i++) {
      last = WhizbangActivitySource.StartForType(source, "Dispatch", typeof(DispatchActivityProbe), "(Outbox)");
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    await Assert.That(last).IsNull();
    await Assert.That(allocated).IsEqualTo(0L);
  }

  [Test]
  [Arguments(null, "Dispatch DispatchActivityProbe")]
  [Arguments("(Outbox)", "Dispatch DispatchActivityProbe (Outbox)")]
  public async Task StartForType_WithAListener_NamesTheActivityByTheTypeAsync(string? suffix, string expected) {
    var name = "Whizbang.Tests.DispatchActivity.Heard." + Guid.CreateVersion7().ToString("N");
    using var source = new ActivitySource(name);
    using var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == name,
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    };
    ActivitySource.AddActivityListener(listener);

    using var activity = WhizbangActivitySource.StartForType(source, "Dispatch", typeof(DispatchActivityProbe), suffix, ActivityKind.Producer);

    await Assert.That(activity).IsNotNull();
    await Assert.That(activity!.OperationName).IsEqualTo(expected);
    await Assert.That(activity.Kind).IsEqualTo(ActivityKind.Producer);
  }
}
