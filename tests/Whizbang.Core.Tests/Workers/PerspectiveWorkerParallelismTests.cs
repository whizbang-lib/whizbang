using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Slice 17 of plans/pump-then-process.md — PerspectiveWorker parallel consumer loops.
/// Pre-slice-17 ExecuteAsync ran a single channel-consumer loop; while one batch was
/// processing, new perspective work piled up in the channel until the batch completed.
/// On JDX BFF this capped drain throughput at ~38/sec while saga fan-out arrived at
/// ~180/sec, leaving the read models stale and the UI unable to show fresh data.
/// </summary>
public class PerspectiveWorkerParallelismTests {

  [Test]
  public async Task MaxConcurrentDrainConsumers_DefaultIsGreaterThanOneAsync() {
    // Lock the default config: out-of-the-box, MaxConcurrentDrainConsumers must be > 1 so
    // multiple consumer loops race for batches off the channel. With one consumer (the
    // pre-slice-17 default) the loop was serial — every test fixture's saga fan-out load
    // accumulates in the channel during ProcessChannelBatchAsync.
    var defaults = new PerspectiveWorkerOptions();
    await Assert.That(defaults.MaxConcurrentDrainConsumers).IsGreaterThan(1)
      .Because("Slice 17 invariant: parallel perspective consumer loops are on by default. UI freshness on JDX depends on this.");
  }

  [Test]
  public async Task MaxConcurrentDrainConsumers_RespectsExplicitOverrideAsync() {
    // Lock the override path so tests / dev environments can pin to 1 to reproduce the
    // pre-slice-17 single-consumer behavior for regression debugging.
    var opts = new PerspectiveWorkerOptions { MaxConcurrentDrainConsumers = 1 };
    await Assert.That(opts.MaxConcurrentDrainConsumers).IsEqualTo(1);

    opts.MaxConcurrentDrainConsumers = 8;
    await Assert.That(opts.MaxConcurrentDrainConsumers).IsEqualTo(8);
  }
}
