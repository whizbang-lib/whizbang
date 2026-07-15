using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for PerspectiveWorker's Phase C channel-consumer path. When the new channel surfaces
/// (IPerspectiveChannelWriter, IPerspectiveCompletionChannel, IFailureChannel) are wired into
/// the constructor, ExecuteAsync uses ProcessChannelBatchAsync instead of the legacy poll path.
/// </summary>
[NotInParallel("PerspectiveChannelModeTests")]
public class PerspectiveWorkerChannelModeTests {

  [Test]
  public async Task ProcessChannelBatchAsync_RoutesCompletionsToChannelsAsync() {
    // Arrange — channels wired via constructor; we'll call ProcessChannelBatchAsync directly
    // (unit test of the new entry point, not a full ExecuteAsync orchestration test).
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var channelWriter = new PerspectiveChannelWriter();
    var completionCapture = new CapturingPerspectiveCompletionChannel();
    var failureCapture = new CapturingFailureChannel();

    var coordinator = new FakeWorkCoordinatorReturningCursor();
    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent("channel-mode"));
    var eventTypeProvider = new FakeEventTypeProvider([typeof(TestEvent)]);
    var instanceProvider = new FakeServiceInstanceProvider();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new FakePerspectiveRunnerRegistry());
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IEventTypeProvider>(eventTypeProvider);
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    // BatchedCompletionStrategy buffers completions in memory; the flush in ProcessChannelBatchAsync
    // picks them up and routes through IPerspectiveCompletionChannel (the new path).
    var strategy = new BatchedCompletionStrategy();
    await strategy.ReportCompletionAsync(new PerspectiveCursorCompletion {
      StreamId = streamId,
      PerspectiveName = "Test.FakePerspective",
      LastEventId = eventId,
      Status = PerspectiveProcessingStatus.Completed,
      EventsProcessed = 1,
    }, coordinator, default);

    var worker = new PerspectiveWorker(
      instanceProvider,
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 1_000_000,
        MaxStreamsPerBatch = 10
      }),
      tracingOptions: null,
      completionStrategy: strategy,
      eventTypeProvider: eventTypeProvider,
      perspectiveChannelWriter: channelWriter,
      perspectiveCompletionChannel: completionCapture,
      failureChannel: failureCapture);

    // Act — call the new entry point directly with an empty batch. The flush of pending
    // completions should run regardless of batch contents.
    await worker.ProcessChannelBatchAsync([], CancellationToken.None);

    // Assert — the pending completion was routed through IPerspectiveCompletionChannel,
    // not through the legacy claim-based polling.
    await Assert.That(completionCapture.Cursors).IsNotEmpty()
      .Because("ProcessChannelBatchAsync must route pending completions through the channel");
    await Assert.That(coordinator.ClaimWorkAsyncCallCount).IsEqualTo(0)
      .Because("Channel-mode must not call the legacy claim path");
  }

  [Test]
  public async Task ProcessChannelBatchAsync_DrainStreamIds_AcceptedAndDoNotFallToLegacyAsync() {
    // Arrange — empty per-event work + one drain stream ID. Verifies the channel-consumer
    // overload accepts drain stream IDs and routes them through the existing drain path
    // (_processDrainModeStreamsAsync). Full end-to-end drain processing requires the
    // perspective registry to have been initialized via StartAsync, which is exercised by
    // the integration tests in the EFCore Postgres test project — this unit test guards
    // the API surface and the no-fall-back-to-legacy invariant.
    var streamId = Guid.NewGuid();
    var channelWriter = new PerspectiveChannelWriter();
    var drainChannel = new PerspectiveDrainChannel();
    var completionCapture = new CapturingPerspectiveCompletionChannel();
    var failureCapture = new CapturingFailureChannel();

    var coordinator = new FakeWorkCoordinatorReturningCursor();
    var instanceProvider = new FakeServiceInstanceProvider();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new FakePerspectiveRunnerRegistry());
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(new FakeEventStore());
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { MaxStreamsPerBatch = 10 }),
      tracingOptions: null,
      completionStrategy: new BatchedCompletionStrategy(),
      eventTypeProvider: new FakeEventTypeProvider([typeof(TestEvent)]),
      perspectiveChannelWriter: channelWriter,
      perspectiveCompletionChannel: completionCapture,
      failureChannel: failureCapture,
      perspectiveDrainChannel: drainChannel);

    // Act — call the drain-aware overload with NO per-event work but ONE drain stream ID.
    // This must not throw and must not touch the legacy poll path.
    await worker.ProcessChannelBatchAsync([], [streamId], CancellationToken.None);

    await Assert.That(coordinator.ClaimWorkAsyncCallCount).IsEqualTo(0)
      .Because("Channel-mode must never call legacy claim-based polling, even with drain stream IDs");
  }

  // ---------- Test fakes ----------

  private sealed record TestEvent(string Data) : IEvent;

  private sealed class FakeWorkCoordinatorReturningCursor : IWorkCoordinator {
    public int ClaimWorkAsyncCallCount { get; private set; }
    public int CommitHandlerResultCallCount { get; private set; }
    public int GetStreamEventsCallCount { get; private set; }
    public Guid? LastStreamEventsRequestedFor { get; private set; }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      ClaimWorkAsyncCallCount++;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
      });
    }

    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) {
      GetStreamEventsCallCount++;
      LastStreamEventsRequestedFor = streamIds.Length > 0 ? streamIds[0] : null;
      return Task.FromResult(new List<StreamEventData>());
    }
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default)
      => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
  private sealed class FakeEventTypeProvider(IReadOnlyList<Type> eventTypes) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }

  private sealed class FakeEventStore : IEventStore {
    private readonly Dictionary<Guid, List<MessageEnvelope<IEvent>>> _events = [];
    public void AddEvent(Guid streamId, Guid eventId, IEvent payload) {
      if (!_events.TryGetValue(streamId, out var list)) {
        list = [];
        _events[streamId] = list;
      }
      list.Add(new MessageEnvelope<IEvent> {
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
      });
    }
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId,
        IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default)
      => Task.FromResult(_events.TryGetValue(streamId, out var list) ? list : []);

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }

  private sealed class FakePerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider services) =>
      perspectiveName == "Test.FakePerspective" ? new FakePerspectiveRunner() : null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo("Test.FakePerspective", "global::Test.FakePerspective", "global::Test.FakeModel", ["global::Test.FakeEvent"])];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class FakePerspectiveRunner : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);
    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) =>
      Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.NewGuid(),
        Status = PerspectiveProcessingStatus.Completed,
        EventsProcessed = 1,
      });
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }
}
