// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Every collective sink test again, against an engine that serves the bounded queue reads (#1524): the head of the
/// queue and the membership of named rows, instead of the whole queue. The sink must behave exactly as it does with
/// the whole queue, and must not read it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
[InheritsTests]
[NotInParallel("CollectiveSinkWorker")]
public class PerspectiveWorkerCollectiveSinkQueueHeadTests : PerspectiveWorkerCollectiveSinkTests {
  /// <inheritdoc />
  protected override bool ServesQueueHead => true;

  /// <summary>
  /// A run that holds the head of a long queue reads its own rows and the one after them, never the whole queue.
  /// </summary>
  [Test]
  public async Task CollectiveSink_QueueHead_NeverReadsTheWholeQueueAsync() {
    var streamId = TrackedGuid.New().Value;
    var held = _sinkWork(streamId);
    var heldId = Guid.CreateVersion7();
    var dispatcher = new FlipDispatcher(expected: 1);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [held],
      eventStore: new EventStore {
        Deserialized = [_envelope(heldId, new FlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), Chosen = "a" })],
      },
      registry: new Registry([typeof(FlipCollectiveEvent)]),
      dispatcher: dispatcher);
    coordinator.SinkQueue = [
      new CollectiveSinkQueueEntry(held.WorkId, heldId, CommitSequence: 1),
      .. Enumerable.Range(0, 50).Select(i => new CollectiveSinkQueueEntry(Guid.CreateVersion7(), Guid.CreateVersion7(), CommitSequence: 2 + i)),
    ];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await harness.CompletionCapture.EventWorkIdsCaptured(held.WorkId).WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Applied).IsEquivalentTo(["a"]);
    await Assert.That(coordinator.WholeQueueReads).IsEqualTo(0)
      .Because("The head and the held rows' membership answer everything the run needs.");
  }
}
