using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Slice 17 of plans/pump-then-process.md — PerspectiveWorker parallel consumer loops.
/// Pre-slice-17 ExecuteAsync ran a single channel-consumer loop; while one batch was
/// processing, new perspective work piled up in the channel until the batch completed.
/// In production this capped drain throughput well below the saga fan-out arrival
/// rate, leaving the read models stale and the UI unable to show fresh data.
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
      .Because("Slice 17 invariant: parallel perspective consumer loops are on by default. UI freshness for consumers depends on this.");
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

  [Test]
  public async Task ClampWidthToGate_LeavesHalfTheGateForEverythingElseAsync() {
    // 4 consumers x 30 wide = 120 bodies against a 50-slot gate: the drain held every slot while the
    // completion flusher and lease renewal queued behind it. The drain may use at most half the gate,
    // split across its consumers.
    await Assert.That(PerspectiveWorker.ClampWidthToGate(consumers: 4, width: 30, gateMaxConcurrent: 50)).IsEqualTo(6);
    await Assert.That(PerspectiveWorker.ClampWidthToGate(consumers: 1, width: 30, gateMaxConcurrent: 50)).IsEqualTo(25);
    await Assert.That(PerspectiveWorker.ClampWidthToGate(consumers: 2, width: 3, gateMaxConcurrent: 50)).IsEqualTo(3)
      .Because("a width already under the cap is untouched");
  }

  [Test]
  public async Task ClampWidthToGate_NeverBelowOne_AndIgnoresADisabledGateAsync() {
    await Assert.That(PerspectiveWorker.ClampWidthToGate(consumers: 8, width: 30, gateMaxConcurrent: 4)).IsEqualTo(1)
      .Because("a serialized drain is the floor; the clamp must not stop it");
    await Assert.That(PerspectiveWorker.ClampWidthToGate(consumers: 4, width: 30, gateMaxConcurrent: 0)).IsEqualTo(30)
      .Because("MaxConcurrent <= 0 disables the gate, so there is nothing to protect");
  }
}
