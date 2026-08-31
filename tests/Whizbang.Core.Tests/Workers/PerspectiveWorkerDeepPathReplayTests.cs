using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Deep-path coverage for PerspectiveWorker's rewind replay-source selection
/// (channel path, ProcessingMode.Replay):
/// - When IPerspectiveReplayReader is registered, pending (IsNew) replay events are pulled
///   from it, deduped against the range-loaded set, and prepended to processedEvents.
/// - When it is NOT registered, the narrow trigger-only fallback loads the rewind trigger
///   envelope from the event store and prepends it.
/// </summary>
public class PerspectiveWorkerDeepPathReplayTests {

  private const string PERSPECTIVE = "Replay.DeepPerspective";

  [Test]
  public async Task Worker_RewindWithReplayReader_PrependsPendingReplayEventsOnceAsync() {
    // Arrange — rewind-required cursor; the replay reader yields:
    //   1. a pending (IsNew) event below the cursor  → must be prepended
    //   2. the range-loaded event again (IsNew)      → deduped by the seen-set
    //   3. an already-completed event (IsNew=false)  → skipped
    var streamId = Guid.CreateVersion7();
    var pendingEventId = Guid.CreateVersion7();
    var completedEventId = Guid.CreateVersion7();
    var rangeLoadedEventId = Guid.CreateVersion7();
    var triggerEventId = pendingEventId;

    var coordinator = new ReplayWorkCoordinator();
    coordinator.CursorOverrides[(PERSPECTIVE, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = PERSPECTIVE,
      LastEventId = rangeLoadedEventId,
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = triggerEventId
    };

    var rangeLoadedEnvelope = _envelope(rangeLoadedEventId, new ReplayDeepEvent("range"));
    var eventStore = new ReplayEventStore();
    eventStore.EnqueueResponse([rangeLoadedEnvelope]); // upcoming-events load
    eventStore.EnqueueResponse([rangeLoadedEnvelope]); // processed-events load

    var replayReader = new FakeReplayReader([
      new ReplayEventEnvelope(_envelope(pendingEventId, new ReplayDeepEvent("pending")), IsNew: true),
      new ReplayEventEnvelope(rangeLoadedEnvelope, IsNew: true),
      new ReplayEventEnvelope(_envelope(completedEventId, new ReplayDeepEvent("completed")), IsNew: false)
    ]);

    var runner = new ReplayRunner();
    var registry = new ReplayRegistry(PERSPECTIVE, runner, [typeof(ReplayDeepEvent)]);
    var invoker = new EventIdRecordingInvoker();

    var (worker, harness) = _createWorker(coordinator, eventStore, registry, invoker, replayReader);

    PerspectiveEventProcessedEvent? processedEvent = null;
    var processedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnPerspectiveEventProcessed += e => {
      processedEvent = e;
      processedSignal.TrySetResult();
    };

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueWorkAsync(_rewindWork(streamId), cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    await processedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — the rewind ran, and processedEvents = pending (prepended) + range-loaded
    await Assert.That(runner.RewindCallCount).IsEqualTo(1);
    await Assert.That(processedEvent?.EventCount).IsEqualTo(2)
      .Because("The replay reader's IsNew pending event is prepended; duplicates and completed events are filtered");
    var postPerspectiveIds = invoker.EventIdsAtStage(LifecycleStage.PostPerspectiveInline);
    await Assert.That(postPerspectiveIds).Contains(pendingEventId)
      .Because("The pending replay event must flow through PostPerspective lifecycle");
    await Assert.That(postPerspectiveIds).Contains(rangeLoadedEventId);
    await Assert.That(postPerspectiveIds.Contains(completedEventId)).IsFalse()
      .Because("IsNew=false replay events were already handled in a prior pass");
  }

  [Test]
  public async Task Worker_RewindWithoutReplayReader_FallsBackToTriggerEnvelopeLookupAsync() {
    // Arrange — no IPerspectiveReplayReader registered: the fallback loads events up to the
    // rewind trigger and prepends the trigger envelope when the range load missed it.
    var streamId = Guid.CreateVersion7();
    var triggerEventId = Guid.CreateVersion7();
    var rangeLoadedEventId = Guid.CreateVersion7();

    var coordinator = new ReplayWorkCoordinator();
    coordinator.CursorOverrides[(PERSPECTIVE, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = PERSPECTIVE,
      LastEventId = rangeLoadedEventId,
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = triggerEventId
    };

    var rangeLoadedEnvelope = _envelope(rangeLoadedEventId, new ReplayDeepEvent("range"));
    var triggerEnvelope = _envelope(triggerEventId, new ReplayDeepEvent("trigger"));
    var eventStore = new ReplayEventStore();
    eventStore.EnqueueResponse([rangeLoadedEnvelope]);                  // upcoming-events load
    eventStore.EnqueueResponse([rangeLoadedEnvelope]);                  // processed-events load
    eventStore.EnqueueResponse([triggerEnvelope, rangeLoadedEnvelope]); // fallback up-to-trigger load

    var runner = new ReplayRunner();
    var registry = new ReplayRegistry(PERSPECTIVE, runner, [typeof(ReplayDeepEvent)]);
    var invoker = new EventIdRecordingInvoker();

    var (worker, harness) = _createWorker(coordinator, eventStore, registry, invoker, replayReader: null);

    PerspectiveEventProcessedEvent? processedEvent = null;
    var processedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnPerspectiveEventProcessed += e => {
      processedEvent = e;
      processedSignal.TrySetResult();
    };

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueWorkAsync(_rewindWork(streamId), cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    await processedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — trigger envelope was found in the up-to-trigger range and prepended
    await Assert.That(runner.RewindCallCount).IsEqualTo(1);
    await Assert.That(eventStore.GetEventsBetweenCallCount).IsEqualTo(3)
      .Because("The fallback issues a third range query up to the rewind trigger event");
    await Assert.That(processedEvent?.EventCount).IsEqualTo(2);
    var postPerspectiveIds = invoker.EventIdsAtStage(LifecycleStage.PostPerspectiveInline);
    await Assert.That(postPerspectiveIds).Contains(triggerEventId)
      .Because("The rewind trigger must fire handlers even though the post-cursor range load missed it");
    await Assert.That(postPerspectiveIds).Contains(rangeLoadedEventId);
  }

  #region Test event + helpers

  private sealed record ReplayDeepEvent(string Data) : IEvent;

  private static PerspectiveWork _rewindWork(Guid streamId) => new() {
    WorkId = Guid.CreateVersion7(),
    StreamId = streamId,
    PerspectiveName = PERSPECTIVE,
    LastProcessedEventId = null,
    PartitionNumber = 1,
    Status = PerspectiveProcessingStatus.RewindRequired
  };

  private static MessageEnvelope<IEvent> _envelope(Guid eventId, IEvent payload) => new() {
    MessageId = new MessageId(eventId),
    Payload = payload,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = CorrelationId.New(),
        CausationId = MessageId.New(),
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.NewGuid(),
          ServiceName = "TestService",
          HostName = "test-host",
          ProcessId = 1234
        }
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private static (PerspectiveWorker Worker, PerspectiveWorkerTestHarness Harness) _createWorker(
      ReplayWorkCoordinator coordinator,
      ReplayEventStore eventStore,
      ReplayRegistry registry,
      IReceptorInvoker invoker,
      IPerspectiveReplayReader? replayReader) {
    var instanceProvider = new FakeInstanceProvider();
    var harness = new PerspectiveWorkerTestHarness();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton(invoker);
    if (replayReader is not null) {
      services.AddSingleton(replayReader);
    }
    services.AddLogging();
    var provider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      eventTypeProvider: registry,
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());
    return (worker, harness);
  }

  #endregion

  #region Fakes

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "DeepPathReplayTest";
    public string HostName => "test-host";
    public int ProcessId => 4245;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class ReplayWorkCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _firstCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Dictionary<(string PerspectiveName, Guid StreamId), PerspectiveCursorInfo> CursorOverrides { get; } = [];
    public Task FirstCompletion => _firstCompletion.Task;

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      _firstCompletion.TrySetResult();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      if (CursorOverrides.TryGetValue((perspectiveName, streamId), out var cursor)) {
        return Task.FromResult<PerspectiveCursorInfo?>(cursor);
      }
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }

  private sealed class ReplayEventStore : IEventStore {
    private readonly ConcurrentQueue<List<MessageEnvelope<IEvent>>> _responses = new();
    private int _getEventsBetweenCallCount;

    public int GetEventsBetweenCallCount => Volatile.Read(ref _getEventsBetweenCallCount);

    public void EnqueueResponse(List<MessageEnvelope<IEvent>> envelopes) => _responses.Enqueue(envelopes);

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _getEventsBetweenCallCount);
      if (_responses.TryDequeue(out var next)) {
        return Task.FromResult(new List<MessageEnvelope<IEvent>>(next));
      }
      return Task.FromResult(new List<MessageEnvelope<IEvent>>());
    }

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
  }

  private sealed class ReplayRegistry(string perspectiveName, IPerspectiveRunner runner, IReadOnlyList<Type> eventTypes) : IPerspectiveRunnerRegistry, IEventTypeProvider {
    public IPerspectiveRunner? GetRunner(string name, IServiceProvider serviceProvider) =>
      name == perspectiveName ? runner : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(
        perspectiveName,
        $"global::{perspectiveName}",
        "global::Test.ReplayDeepModel",
        [.. eventTypes.Select(TypeNameFormatter.Format)])];

    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class ReplayRunner : IPerspectiveRunner {
    private int _rewindCallCount;

    public int RewindCallCount => Volatile.Read(ref _rewindCallCount);
    public Type PerspectiveType => typeof(ReplayRunner);

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) =>
      Task.FromResult(_completed(streamId, perspectiveName, lastProcessedEventId ?? Guid.Empty));

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _rewindCallCount);
      return Task.FromResult(_completed(streamId, perspectiveName, triggeringEventId));
    }

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    private static PerspectiveCursorCompletion _completed(Guid streamId, string perspectiveName, Guid lastEventId) => new() {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = lastEventId,
      Status = PerspectiveProcessingStatus.Completed
    };
  }

  private sealed class FakeReplayReader(IReadOnlyList<ReplayEventEnvelope> items) : IPerspectiveReplayReader {
    public async IAsyncEnumerable<ReplayEventEnvelope> ReadReplayEventsAsync(
        Guid streamId,
        string perspectiveName,
        int fromVersionExclusive,
        IReadOnlyCollection<Type> eventTypes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) {
      await Task.CompletedTask;
      foreach (var item in items) {
        yield return item;
      }
    }
  }

  private sealed class EventIdRecordingInvoker : IReceptorInvoker {
    private readonly Lock _lock = new();
    private readonly List<(LifecycleStage Stage, Guid EventId)> _invocations = [];

    public List<Guid> EventIdsAtStage(LifecycleStage stage) {
      lock (_lock) {
        return [.. _invocations.Where(i => i.Stage == stage).Select(i => i.EventId)];
      }
    }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _invocations.Add((stage, envelope.MessageId.Value));
      }
      return ValueTask.CompletedTask;
    }
  }

  #endregion
}
