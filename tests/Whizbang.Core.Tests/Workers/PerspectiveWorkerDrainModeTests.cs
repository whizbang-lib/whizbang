using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
/// Tests for PerspectiveWorker drain mode processing.
/// Verifies that when WorkBatch.PerspectiveStreamIds is populated,
/// the worker batch-fetches events and uses RunWithEventsAsync.
/// </summary>
public class PerspectiveWorkerDrainModeTests {

  [Test]
  public async Task DrainMode_LegacyPathSkipped_WhenPerspectiveStreamIdsPopulated_Async() {
    // Arrange — coordinator returns PerspectiveStreamIds (drain mode) with no PerspectiveWork (legacy)
    var coordinator = new DrainModeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new DrainModePerspectiveRunnerRegistry();

    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    // Configure drain mode: return stream IDs, not perspective work
    coordinator.StreamIdsToReturn = [streamId];
    coordinator.StreamEventsToReturn = [
      new StreamEventData {
        StreamId = streamId,
        EventId = eventId,
        EventType = TypeNameFormatter.Format(typeof(DrainModeTestEvent)),
        EventData = JsonSerializer.Serialize(new DrainModeTestEvent("drain-test")),
        Metadata = null,
        Scope = null,
        EventWorkId = Guid.NewGuid()
      }
    ];

    var eventStore = new DrainModeEventStore();
    eventStore.DeserializedEventsToReturn = [
      new MessageEnvelope<IEvent> {
        MessageId = new MessageId(eventId),
        Payload = new DrainModeTestEvent("drain-test"),
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
      }
    ];

    var eventTypeProvider = new FakeEventTypeProvider([typeof(DrainModeTestEvent)]);

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IEventTypeProvider>(eventTypeProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider: null // null — lazy-resolved from DI
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert — drain mode should have been used
    await Assert.That(coordinator.GetStreamEventsCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Drain mode should call GetStreamEventsAsync for batch-fetching");
    await Assert.That(eventStore.DeserializeStreamEventsCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Drain mode should call DeserializeStreamEvents for type resolution");
    await Assert.That(registry.RunWithEventsCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Drain mode should use RunWithEventsAsync, not RunAsync");
  }

  #region Test Event

  private sealed record DrainModeTestEvent(string Data) : IEvent;

  #endregion

  #region Test Fakes

  private sealed class DrainModeWorkCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _completionReported = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<Guid> StreamIdsToReturn { get; set; } = [];
    public List<StreamEventData> StreamEventsToReturn { get; set; } = [];
    public int GetStreamEventsCallCount { get; private set; }

    public async Task WaitForCompletionReportedAsync(TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      try {
        await _completionReported.Task.WaitAsync(cts.Token);
      } catch (OperationCanceledException) {
        throw new TimeoutException($"Completion was not reported within {timeout}");
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      var streamIds = new List<Guid>(StreamIdsToReturn);
      StreamIdsToReturn.Clear();

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [], // NO legacy work — drain mode only
        PerspectiveStreamIds = streamIds
      });
    }

    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) {
      GetStreamEventsCallCount++;
      return Task.FromResult(new List<StreamEventData>(StreamEventsToReturn));
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      _completionReported.TrySetResult();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class DrainModeEventStore : IEventStore {
    public List<MessageEnvelope<IEvent>> DeserializedEventsToReturn { get; set; } = [];
    public int DeserializeStreamEventsCallCount { get; private set; }

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
        IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
      DeserializeStreamEventsCallCount++;
      return new List<MessageEnvelope<IEvent>>(DeserializedEventsToReturn);
    }

    // IEventStore stubs — not used in drain mode
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
  }

  private sealed class FakeEventTypeProvider(IReadOnlyList<Type> eventTypes) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }

  private sealed class DrainModePerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
    private int _runWithEventsCount;
    public int RunWithEventsCallCount => Volatile.Read(ref _runWithEventsCount);

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      new DrainModePerspectiveRunner(this);

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(
        "Test.DrainPerspective",
        "global::Test.DrainPerspective",
        "global::Test.DrainModel",
        [TypeNameFormatter.Format(typeof(DrainModeTestEvent))])];

    public IReadOnlyList<Type> GetEventTypes() => [typeof(DrainModeTestEvent)];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();

    private sealed class DrainModePerspectiveRunner(DrainModePerspectiveRunnerRegistry registry) : IPerspectiveRunner {
      public Type PerspectiveType => typeof(object);

      public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) =>
        Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.NewGuid(), Status = PerspectiveProcessingStatus.Completed });

      public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
        Interlocked.Increment(ref registry._runWithEventsCount);
        return Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.NewGuid(), Status = PerspectiveProcessingStatus.Completed });
      }

      public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        RunAsync(streamId, perspectiveName, null, cancellationToken);

      public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;
    public ServiceInstanceInfo ToInfo() => new() { ServiceName = ServiceName, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class FakeDatabaseReadinessCheck : IDatabaseReadinessCheck {
    public bool IsReady { get; set; } = true;
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsReady);
  }

  #endregion
}
