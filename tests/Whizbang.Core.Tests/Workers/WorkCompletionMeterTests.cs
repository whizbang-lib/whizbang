using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Counts rows that finished processing, so the claim loop can size its outstanding budget from a
/// measured drain rate rather than an inferred one.
/// </summary>
/// <remarks>
/// <para>
/// Inferring drain from the fall in outstanding work undercounts: rows arriving in the same interval
/// mask completions, so the measured rate reads low. It also makes any test of the control loop
/// depend on wall-clock timing. Counting real completions removes both problems.
/// </para>
/// <para>
/// This is deliberately ADVISORY. It only sizes the budget; the authoritative outstanding figure
/// comes from the store. If this meter stalls or is never fed, the drain rate reads zero, the budget
/// falls to its floor, and the loop keeps polling — degraded throughput, never a stuck worker. That
/// property is what keeps it clear of the stranded-in-memory-state failure this codebase has already
/// hit once.
/// </para>
/// </remarks>
[Category("Workers")]
public class WorkCompletionMeterTests {

  [Test]
  public async Task ReadAndReset_ReturnsWhatWasRecordedAsync() {
    var meter = new WorkCompletionMeter();

    meter.Record();
    meter.Record();
    meter.Record(5);

    await Assert.That(meter.ReadAndReset()).IsEqualTo(7);
  }

  [Test]
  public async Task ReadAndReset_ClearsSoIntervalsDoNotDoubleCountAsync() {
    var meter = new WorkCompletionMeter();
    meter.Record(3);

    _ = meter.ReadAndReset();

    // Each read covers one sampling interval. Leaving the count in place would carry old
    // completions into the next window and inflate the measured drain rate indefinitely.
    await Assert.That(meter.ReadAndReset()).IsEqualTo(0);
  }

  [Test]
  public async Task ReadAndReset_WithNothingRecorded_IsZeroNotNegativeAsync() {
    var meter = new WorkCompletionMeter();

    await Assert.That(meter.ReadAndReset()).IsEqualTo(0);
  }

  [Test]
  public async Task Record_IsSafeUnderConcurrentCompletionsAsync() {
    var meter = new WorkCompletionMeter();

    // Dispatch runs partitioned consumers in parallel, so completions genuinely race. A lost
    // increment here would silently understate drain and shrink the budget for no reason.
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => {
      for (var i = 0; i < 1_000; i++) {
        meter.Record();
      }
    })));

    await Assert.That(meter.ReadAndReset()).IsEqualTo(8_000);
  }
}
