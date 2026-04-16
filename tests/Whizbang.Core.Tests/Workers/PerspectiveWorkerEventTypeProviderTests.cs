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
/// Tests for PerspectiveWorker lazy-resolving IEventTypeProvider from DI scope
/// when constructor injection provides null (DI registration order issue).
/// </summary>
public class PerspectiveWorkerEventTypeProviderTests {

  [Test]
  public async Task EventTypeProvider_ResolvedFromScope_WhenConstructorInjectionIsNull_Async() {
    // Arrange — DO NOT pass eventTypeProvider to constructor (simulates DI registration order issue).
    // Instead, register IEventTypeProvider in the service collection so it's available from scope.
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FakePerspectiveRunnerRegistry();

    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent("lazy-resolve-test"));
    var eventTypeProvider = new FakeEventTypeProvider([typeof(TestEvent)]);

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IEventTypeProvider>(eventTypeProvider); // Available in DI, NOT in constructor
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    // Constructor gets null for eventTypeProvider — simulates the real DI registration order bug
    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider: null // Explicitly null — the bug scenario
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert — If lazy-resolve works, the worker should have resolved IEventTypeProvider from scope
    // and used it to load events from the event store for trace context extraction.
    await Assert.That(eventStore.GetEventsBetweenPolymorphicCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should lazy-resolve IEventTypeProvider from DI scope and use it to load events");
  }

  [Test]
  public async Task EventTypeProvider_ReturnsNonEmptyTypeList_WhenResolvedFromScope_Async() {
    // Arrange — Same setup: null constructor, provider in DI with real event types
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FakePerspectiveRunnerRegistry();

    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent("type-count-test"));
    var eventTypeProvider = new TrackingEventTypeProvider([typeof(TestEvent)]);

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
      eventTypeProvider: null // Explicitly null
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert — The resolved provider should have been called and returned non-empty types
    await Assert.That(eventTypeProvider.GetEventTypesCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Lazy-resolved IEventTypeProvider should be called to get event types");
    await Assert.That(eventTypeProvider.LastReturnedCount).IsGreaterThan(0)
      .Because("Resolved provider should return non-empty type list");
  }

  #region Test Fakes

  private sealed record TestEvent(string Data) : IEvent;

  private sealed class FakeEventStore : IEventStore {
    private readonly Dictionary<Guid, List<MessageEnvelope<IEvent>>> _events = [];
    public int GetEventsBetweenPolymorphicCallCount { get; private set; }

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
        IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      GetEventsBetweenPolymorphicCallCount++;
      if (_events.TryGetValue(streamId, out var events)) {
        return Task.FromResult(events);
      }
      return Task.FromResult(new List<MessageEnvelope<IEvent>>());
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }

  private sealed class FakeEventTypeProvider(IReadOnlyList<Type> eventTypes) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }

  /// <summary>
  /// EventTypeProvider that tracks calls to GetEventTypes for assertion.
  /// </summary>
  private sealed class TrackingEventTypeProvider(IReadOnlyList<Type> eventTypes) : IEventTypeProvider {
    public int GetEventTypesCallCount { get; private set; }
    public int LastReturnedCount { get; private set; }

    public IReadOnlyList<Type> GetEventTypes() {
      GetEventTypesCallCount++;
      LastReturnedCount = eventTypes.Count;
      return eventTypes;
    }
  }

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _completionReported = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<PerspectiveWork> PerspectiveWorkToReturn { get; set; } = [];
    public int ReportCompletionCallCount { get; private set; }

    public async Task WaitForCompletionReportedAsync(TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      try {
        await _completionReported.Task.WaitAsync(cts.Token);
      } catch (OperationCanceledException) {
        throw new TimeoutException($"Completion was not reported within {timeout}");
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      var work = new List<PerspectiveWork>(PerspectiveWorkToReturn);
      PerspectiveWorkToReturn.Clear();
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = work });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      ReportCompletionCallCount++;
      _completionReported.TrySetResult();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
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

  private sealed class FakePerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => new FakePerspectiveRunner();
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [new PerspectiveRegistrationInfo("Test.FakePerspective", "global::Test.FakePerspective", "global::Test.FakeModel", ["global::Test.FakeEvent"])];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class FakePerspectiveRunner : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);
    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) =>
      Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.NewGuid(), Status = PerspectiveProcessingStatus.Completed });
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  #endregion
}
