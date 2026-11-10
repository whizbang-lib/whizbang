// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The drain decides whether a <c>__collective__</c> row it could not read is one no handler here applies by asking the
/// consumer registry (#2145), the same answer the startup association writer gates the store on, so the two cannot
/// disagree. A registry that cannot answer leaves #2142's test in place: a type this service does not know.
/// </summary>
public partial class PerspectiveWorkerDeepPathDrainTests {

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_CollectiveRowOfAKnownTypeTheRegistrySaysNoHandlerApplies_IsCompleted_NotFailedAsync(
      CancellationToken cancellationToken) {
    // The type is one this service can read (another perspective folds it), but no [CollectiveApplyFor] handler applies
    // it. A row the read skipped was failed toward the dead-letter bound, though nothing here would ever apply it.
    var streamId = Guid.CreateVersion7();
    var sinkWork = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, Guid.CreateVersion7(), sinkWork, perspectiveName: CollectiveRouting.SINK_PERSPECTIVE_NAME)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([]);

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(new DrainRunner()), consumerRegistry: new CollectiveConsumerRegistry([]));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await harness.CompletionCapture.WaitForEventWorkIdsAsync(TimeSpan.FromSeconds(25), sinkWork);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(harness.CompletionCapture.EventWorkIds).IsEquivalentTo([sinkWork]);
    await Assert.That(harness.FailureCapture.Items).IsEmpty();
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_CollectiveRowOfAHandledType_ThatCannotBeRead_IsFailedAsync(CancellationToken cancellationToken) {
    // A handler here applies the type, so a row that cannot be read is a real failure, retried and dead-lettered.
    var streamId = Guid.CreateVersion7();
    var sinkWork = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, Guid.CreateVersion7(), sinkWork, perspectiveName: CollectiveRouting.SINK_PERSPECTIVE_NAME)]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([]);
    var handled = TypeNameFormatter.Format(typeof(DrainDeepEvent)) + ", Version=1.2.3.4, Culture=neutral, PublicKeyToken=null";

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(new DrainRunner()), consumerRegistry: new CollectiveConsumerRegistry([handled]));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await harness.FailureCapture.WaitForCountAsync(1, cancellationToken);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    var (_, failure) = harness.FailureCapture.Items.Single();
    await Assert.That(failure.MessageId).IsEqualTo(sinkWork)
      .Because("the registry's name carries version details the stored type does not; they are ignored");
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(sinkWork);
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_CollectiveRowOfAHandledTypeThisInstanceCannotResolve_IsLeftAloneAsync(CancellationToken cancellationToken) {
    // The registry lists the type, yet this instance cannot resolve it: completing the row would drop work a handler
    // applies, and failing it would count it toward the bound. It is left for an instance that can read it.
    const string newerType = "Contracts.CollectiveFromANewerBuild, Contracts";
    var streamId = Guid.CreateVersion7();
    var goodEvent = Guid.CreateVersion7();
    var sinkWork = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, goodEvent, Guid.CreateVersion7()),
      _raw(streamId, Guid.CreateVersion7(), sinkWork, perspectiveName: CollectiveRouting.SINK_PERSPECTIVE_NAME)
        with { EventType = newerType }]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(goodEvent, new DrainDeepEvent("good"))]);
    var runner = new DrainRunner();

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(runner), consumerRegistry: new CollectiveConsumerRegistry([newerType]));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    // Unread rows are decided before any event is applied, so the good event's completion means the decision is made.
    await coordinator.FirstCompletion.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(sinkWork);
    await Assert.That(harness.FailureCapture.Items).IsEmpty();
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_RegistryThatCannotAnswer_CompletesACollectiveRowOfATypeThisServiceDoesNotKnowAsync(
      CancellationToken cancellationToken) {
    // #2142's test stands when the registry cannot say: a type this service does not know is one nothing here applies.
    var streamId = Guid.CreateVersion7();
    var sinkWork = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, Guid.CreateVersion7(), sinkWork, perspectiveName: CollectiveRouting.SINK_PERSPECTIVE_NAME)
        with { EventType = "Contracts.ArchitectureAppliedCollectiveEvent, Contracts" }]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([]);

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, _registry(new DrainRunner()), consumerRegistry: new CollectiveConsumerRegistry(null));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await harness.CompletionCapture.WaitForEventWorkIdsAsync(TimeSpan.FromSeconds(25), sinkWork);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(harness.CompletionCapture.EventWorkIds).IsEquivalentTo([sinkWork]);
    await Assert.That(harness.FailureCapture.Items).IsEmpty();
  }

  /// <summary>A consumer registry with a fixed collective answer; null is a registry that cannot say.</summary>
  private sealed class CollectiveConsumerRegistry(IReadOnlyList<string>? handled) : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => true;
    public IReadOnlyList<string>? GetHandledCollectiveEventTypes() => handled;
  }
}
