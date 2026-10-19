// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.DestructionStages;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1381, against the REAL generated receptor registry and invoker: the destruction lifecycle stages run around
/// the ephemeral reap. Before the fix a receptor declared at <see cref="LifecycleStage.PreDestructionInline"/> and the
/// other three destruction stages compiled and registered, and nothing ever ran it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lifecycle/DestructionStageReceptors.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
/// <docs>fundamentals/lifecycle/lifecycle-stages#destruction</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class DestructionStageReceptorComponentTests {
  [Test]
  public async Task TheReap_RunsThePreStagesBeforeTheDeleteAndThePostStagesAfterItAsync() {
    var id = Guid.CreateVersion7();
    var (worker, coordinator, _) = await _buildAsync(id, typeof(DsEphemeralEvent));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await DestructionProbe.RanAsync(id, LifecycleStage.PreDestructionDetached).WaitAsync(TimeSpan.FromSeconds(30));
    await DestructionProbe.RanAsync(id, LifecycleStage.PostDestructionDetached).WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(coordinator.StagesSeenAtTheReap).Contains(LifecycleStage.PreDestructionInline)
      .Because("Pre Inline runs, awaited, before the data is deleted");
    await Assert.That(coordinator.StagesSeenAtTheReap).DoesNotContain(LifecycleStage.PostDestructionInline)
      .Because("Post runs only after the delete");
    var stages = DestructionProbe.Stages(id);
    await Assert.That(stages.Count(s => s == LifecycleStage.PreDestructionInline)).IsEqualTo(1);
    await Assert.That(stages.Count(s => s == LifecycleStage.PostDestructionInline)).IsEqualTo(1);
    await Assert.That(stages.Count(s => s == LifecycleStage.PreDestructionDetached)).IsEqualTo(1);
    await Assert.That(stages.Count(s => s == LifecycleStage.PostDestructionDetached)).IsEqualTo(1);
  }

  [Test]
  public async Task AFailingPreInlineReceptor_HoldsTheBatchAndNoPostStageRunsAsync() {
    var id = Guid.CreateVersion7();
    DestructionProbe.FailPreInlineFor(id);
    var (worker, coordinator, _) = await _buildAsync(id, typeof(DsEphemeralEvent));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Failures).Count().IsEqualTo(1)
      .Because("a throwing Pre Inline receptor fails the destruction as a throwing hook does: the batch is held for retry");
    await Assert.That(DestructionProbe.Stages(id)).DoesNotContain(LifecycleStage.PostDestructionInline);
  }

  [Test]
  public async Task AnEventWithNoDestructionReceptor_IsNotLoadedOrHandedToAnyStageAsync() {
    var id = Guid.CreateVersion7();
    var (worker, _, store) = await _buildAsync(id, typeof(DsUnwatchedEvent));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(DestructionProbe.Stages(id)).IsEmpty();
    await Assert.That(store.Reads).IsEqualTo(0).Because("only events a destruction receptor handles are read before the delete");
  }

  private static async Task<(MaintenanceWorker Worker, ReapCoordinator Coordinator, CountingEventStore Store)> _buildAsync(Guid id, Type eventType) {
    var store = new CountingEventStore();
    object payload = eventType == typeof(DsEphemeralEvent) ? new DsEphemeralEvent(id) : new DsUnwatchedEvent(id);
    await store.AppendAsync(id, new MessageEnvelope<IEvent> {
      MessageId = new MessageId(id),
      Payload = (IEvent)payload,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Local, Source = MessageSource.Local },
    });
    var coordinator = new ReapCoordinator(id) {
      Targets = [new EphemeralDestructionTarget(id, id, $"{eventType.FullName}, {eventType.Assembly.GetName().Name}")],
    };
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IEventStore>(store);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    // The generated message-type catalog, which AddWhizbang() registers through this callback
    ServiceRegistrationCallbacks.MessageTypeCatalog?.Invoke(services);
    var provider = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new MaintenanceWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      NullLogger<MaintenanceWorker>.Instance);
    return (worker, coordinator, store);
  }

  /// <summary>An in-memory event store that counts the reads the destruction stages make.</summary>
  private sealed class CountingEventStore : IEventStore {
    private readonly InMemoryEventStore _inner = new();
    public int Reads { get; private set; }

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId,
        IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      Reads++;
      return _inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken);
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
      _inner.AppendAsync(streamId, envelope, cancellationToken);
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
      _inner.AppendAsync(streamId, message, cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      _inner.ReadAsync<TMessage>(streamId, fromSequence, cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      _inner.ReadAsync<TMessage>(streamId, fromEventId, cancellationToken);
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      _inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, cancellationToken);
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      _inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, cancellationToken);
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      _inner.GetLastSequenceAsync(streamId, cancellationToken);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      _inner.DeserializeStreamEvents(streamEvents, eventTypes);
  }

  private sealed class ReapCoordinator(Guid watched) : IWorkCoordinator {
    public List<EphemeralDestructionTarget> Targets { get; init; } = [];
    public List<LifecycleStage> StagesSeenAtTheReap { get; } = [];
    public List<IReadOnlyList<Guid>> Failures { get; } = [];
    public Task<IReadOnlyList<EphemeralDestructionTarget>> GetEphemeralBodiesAboutToReapAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<EphemeralDestructionTarget>>(Targets);
    public Task<int> RecordDestructionFailureAsync(IReadOnlyList<Guid> eventIds, DateTimeOffset retryHoldUntil, int maxRetries,
        Whizbang.Core.Lifecycle.OnDestroyFailure onFailure = Whizbang.Core.Lifecycle.OnDestroyFailure.RetryThenForcedDelete, CancellationToken cancellationToken = default) {
      Failures.Add(eventIds);
      return Task.FromResult(1);
    }
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
      StagesSeenAtTheReap.AddRange(DestructionProbe.Stages(watched));
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
