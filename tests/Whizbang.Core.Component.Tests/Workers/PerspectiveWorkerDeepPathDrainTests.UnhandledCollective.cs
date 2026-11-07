// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A collective sink row whose event type this service does not register is completed by the drain, because no
/// collective handler here can ever apply it.
/// </summary>
/// <remarks>
/// A service registers a collective event type only when it has a collective handler for it, but it can still
/// store a collective it receives and route it to the sink. Before, the drain could not read such a row, did not
/// fail it (its type does not resolve), and returned without completing it: the row stayed leased, was offered
/// again on every claim, and held its place at the head of the queue in front of the work behind it, forever.
/// </remarks>
public partial class PerspectiveWorkerDeepPathDrainTests {
  [Test]
  [Timeout(30000)]
  public async Task DrainMode_CollectiveSinkRowOfATypeThisServiceDoesNotKnow_IsCompletedAsync(CancellationToken cancellationToken) {
    var sinkStream = Guid.CreateVersion7();
    var sinkWork = Guid.CreateVersion7();
    var otherStream = Guid.CreateVersion7();
    var otherWork = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(sinkStream, Guid.CreateVersion7(), sinkWork, perspectiveName: CollectiveRouting.SINK_PERSPECTIVE_NAME)
        with { EventType = "Contracts.CollectiveNoHandlerHereApplies, Contracts" },
      _raw(otherStream, Guid.CreateVersion7(), otherWork)
        with { EventType = "Contracts.EventFromANewerVersion, Contracts" }]);
    var eventStore = new DrainEventStore();
    var runner = new DrainRunner();

    var (worker, harness, _) = _createWorker(coordinator, eventStore, _registry(runner));
    var cycles = 0;
    var firstCycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondCycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => {
      var completed = Interlocked.Increment(ref cycles);
      if (completed == 1) {
        firstCycle.TrySetResult();
      } else if (completed >= 2) {
        secondCycle.TrySetResult();
      }
    };

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(sinkStream, cts.Token);
    await firstCycle.Task.WaitAsync(cancellationToken);
    // Completions recorded in one cycle are sent at the start of the next. The worker is idle once the first
    // cycle has completed, so this signal starts exactly one more.
    await harness.EnqueueDrainStreamAsync(sinkStream, cts.Token);
    await secondCycle.Task.WaitAsync(cancellationToken);
    await harness.CompletionCapture.WaitForAnyEventWorkIdAsync(cancellationToken);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(sinkWork)
      .Because("no collective handler in this service can apply it, so leaving it leased only blocks the queue");
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(otherWork)
      .Because("a perspective row of an unknown type is still left for an instance that can read it");
    await Assert.That(harness.FailureCapture.Items).IsEmpty();
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0);
  }
}
