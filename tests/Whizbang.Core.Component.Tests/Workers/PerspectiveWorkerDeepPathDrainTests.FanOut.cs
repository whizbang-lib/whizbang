// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2620: the drain tracks the lifecycle of a fanned-out event only when a perspective of this service reads it. One
/// that only the sink applies is like a collective: its own stream is elsewhere, and the receive path already fired its
/// stages, so a tracking entry here would never be completed.
/// </summary>
public partial class PerspectiveWorkerDeepPathDrainTests {
  [Test]
  [Timeout(30000)]
  public async Task DrainMode_AFanOutEventAPerspectiveHereReads_IsTrackedAndAppliedAsync(CancellationToken cancellationToken) {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("renamed"))]);
    var runner = new DrainRunner();
    var lifecycle = new RecordingLifecycleCoordinator();

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner), lifecycleCoordinator: lifecycle,
      fanOutRegistry: new DrainFanOutRegistry([_fanOutOf(typeof(DrainDeepEvent))]));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await runner.FirstRunWithEvents.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(lifecycle.Tracked).Contains(eventId)
      .Because("the foreign stream's own perspective reads it, so its stages are tracked as any event's are");
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_AFanOutEventOnlyTheSinkApplies_IsNotTrackedAsync(CancellationToken cancellationToken) {
    var streamId = Guid.CreateVersion7();
    var fanOutId = Guid.CreateVersion7();
    var ordinaryId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, fanOutId, Guid.CreateVersion7(), perspectiveName: CollectiveRouting.SINK_PERSPECTIVE_NAME),
      _raw(streamId, ordinaryId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([
      _envelope(fanOutId, new DrainDeepEvent("renamed")),
      _envelope(ordinaryId, new OrdinaryDrainEvent("placed"))]);
    var runner = new DrainRunner();
    var registry = new MultiRunnerRegistry([typeof(DrainDeepEvent), typeof(OrdinaryDrainEvent)], readTypes: [typeof(OrdinaryDrainEvent)]);
    registry.Add(PERSPECTIVE, runner);
    var lifecycle = new RecordingLifecycleCoordinator();

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, registry, lifecycleCoordinator: lifecycle,
      fanOutRegistry: new DrainFanOutRegistry([_fanOutOf(typeof(DrainDeepEvent))]));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    // The lap's tracking is registered before its perspectives run, so the ordinary event's run settles both.
    await runner.FirstRunWithEvents.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(lifecycle.Tracked).Contains(ordinaryId).Because("the control: an event a perspective here reads is tracked");
    await Assert.That(lifecycle.Tracked).DoesNotContain(fanOutId);
  }

  private sealed record OrdinaryDrainEvent(string Data) : IEvent;

  private static PerspectiveFanOutEntry _fanOutOf(Type eventType) => new(
    typeof(object), typeof(object), eventType, "Key", "key", static (_, _) => null!,
    (Expression<Func<object, object?>>)(row => row));
}
