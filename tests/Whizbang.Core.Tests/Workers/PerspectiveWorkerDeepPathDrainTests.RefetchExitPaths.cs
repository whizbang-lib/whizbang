using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Two ways the drain loop's refetch ends an in-progress drain without costing it the work it has
/// already applied: a refetch that is canceled while the worker itself keeps running, and a
/// refetch that brings back rows which carry no readable event.
/// </summary>
/// <remarks>
/// The cancellation case is separated from the error case on purpose. Both exit the loop, but a
/// refetch canceled while the worker runs is the lease handle standing down — ordinary — and a
/// warning for every one of those buries the real failures.
/// <para>
/// The second case is also what keeps the refetch's own "no typed events" check from ever firing:
/// the fetch helper answers with nothing at all rather than with an empty list, so the refetch
/// leaves by the nothing-came-back exit. That is asserted here so the day the helper starts
/// answering differently, this test is the one that says so.
/// </para>
/// </remarks>
public partial class PerspectiveWorkerDeepPathDrainTests {

  [Test]
  public async Task DrainMode_RefetchCanceledWhileWorkerRuns_ExitsLoopQuietlyAndKeepsCompletedWorkAsync() {
    // Arrange — first fetch succeeds (2 events, at the refetch min-batch), the refetch is canceled
    // while the worker itself is not shutting down. The sibling case (a refetch that throws a real
    // error) is DrainMode_RefetchThrows_ExitsLoopWithoutFailingCompletedWorkAsync; the difference
    // this test pins is that the canceled one is not reported.
    var streamId = Guid.CreateVersion7();
    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, eventId1, Guid.CreateVersion7()),
      _raw(streamId, eventId2, Guid.CreateVersion7())
    ]);
    coordinator.EnqueueStreamEventsError(new OperationCanceledException("refetch lease stood down"));
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([
      _envelope(eventId1, new DrainDeepEvent("one")),
      _envelope(eventId2, new DrainDeepEvent("two"))
    ]);
    var runner = new DrainRunner();
    var registry = _registry(runner);
    var logger = new EventIdSignalingLogger<PerspectiveWorker>();

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry, logger: logger);
    var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => cycleComplete.TrySetResult();

    // Act — the worker's own token stays uncanceled for the whole drain, so the canceled refetch
    // is the only cancellation in play.
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(20));
    await cycleComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    // Assert — the refetch was attempted and the loop stopped there
    await Assert.That(coordinator.GetStreamEventsCallCount).IsEqualTo(2);
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1)
      .Because("A canceled refetch ends the drain loop rather than starting another iteration");

    // Assert — the work the first iteration finished is untouched
    await Assert.That(coordinator.Completions.Count).IsGreaterThanOrEqualTo(1)
      .Because("A canceled refetch must not undo the already-completed first iteration");
    await Assert.That(coordinator.Failures.Count).IsEqualTo(0);

    // Assert — and nothing was reported: the warning belongs to the error arm, not this one
    await Assert.That(logger.Lines.Any(l => l.Message.Contains("refetch threw", StringComparison.Ordinal))).IsFalse()
      .Because("A refetch canceled while the worker is running is ordinary and must not be reported as a failure");
  }

  [Test]
  public async Task DrainMode_RefetchRowsCarryNoReadableEvent_ExitsLoopAndKeepsCompletedWorkAsync() {
    // Arrange — the first fetch applies 2 events (at the refetch min-batch), so a refetch fires.
    // The refetch brings back a row, but deserializing it yields no event: nothing to apply, and
    // the fetch helper answers "nothing came back" rather than an empty batch.
    var streamId = Guid.CreateVersion7();
    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, eventId1, Guid.CreateVersion7()),
      _raw(streamId, eventId2, Guid.CreateVersion7())
    ]);
    coordinator.EnqueueStreamEvents([_raw(streamId, Guid.CreateVersion7(), Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    // Only the first deserialization is scripted; the refetch's returns nothing.
    eventStore.EnqueueDeserialized([
      _envelope(eventId1, new DrainDeepEvent("one")),
      _envelope(eventId2, new DrainDeepEvent("two"))
    ]);
    var runner = new DrainRunner();
    var registry = _registry(runner);

    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry);
    var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => cycleComplete.TrySetResult();

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(20));
    await cycleComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    // Assert — the refetch happened, its rows were read, and the loop stopped there
    await Assert.That(coordinator.GetStreamEventsCallCount).IsEqualTo(2);
    await Assert.That(eventStore.DeserializeCallCount).IsEqualTo(2)
      .Because("The refetched rows are read before the loop decides there is nothing to apply");
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1)
      .Because("Rows that carry no readable event start no second drain iteration");

    // Assert — the first iteration's work stands, and nothing was reported as failed
    await Assert.That(coordinator.Completions.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(coordinator.Failures.Count).IsEqualTo(0);
  }
}
