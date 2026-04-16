using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for PerspectiveWorker drain mode: per-perspective event filtering,
/// full lifecycle chain (PrePerspective → PostPerspective → PostAllPerspectives → PostLifecycle),
/// and performance optimizations.
/// <tests>src/Whizbang.Core/Workers/PerspectiveWorker.cs:_processDrainModeStreamsAsync</tests>
/// </summary>
public class PerspectiveWorkerDrainModeLifecycleTests {

  // ========================================
  // TEST EVENTS (two types to test filtering)
  // ========================================

  private sealed record AlphaEvent(string Data) : IEvent;
  private sealed record BetaEvent(string Data) : IEvent;

  // ========================================
  // FAKES
  // ========================================

  /// <summary>
  /// Captures lifecycle stage invocations for assertion.
  /// Thread-safe — used across parallel perspective processing.
  /// </summary>
  private sealed class CapturingReceptorInvoker : IReceptorInvoker {
    private readonly ConcurrentBag<(Guid EventId, LifecycleStage Stage)> _invocations = [];

    public IReadOnlyList<(Guid EventId, LifecycleStage Stage)> Invocations =>
      _invocations.ToArray().OrderBy(i => i.EventId).ThenBy(i => i.Stage).ToList();

    public int InvocationCount => _invocations.Count;

    public bool HasStage(LifecycleStage stage) =>
      _invocations.Any(i => i.Stage == stage);

    public int CountForStage(LifecycleStage stage) =>
      _invocations.Count(i => i.Stage == stage);

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      _invocations.Add((envelope.MessageId.Value, stage));
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Work coordinator that supports drain mode with configurable stream events.
  /// Signals batch completion on the SECOND ProcessWorkBatchAsync call (meaning the first
  /// batch including Phase 5 PostLifecycle has fully completed).
  /// </summary>
  private sealed class DrainWorkCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _batchCycleComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _batchCount;

    public List<Guid> StreamIdsToReturn { get; set; } = [];
    public List<StreamEventData> StreamEventsToReturn { get; set; } = [];
    public int GetStreamEventsCallCount { get; private set; }

    public async Task WaitForCompletionReportedAsync(TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      await _batchCycleComplete.Task.WaitAsync(cts.Token);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      var batch = Interlocked.Increment(ref _batchCount);
      if (batch >= 2) {
        // Second call means first batch (including Phase 5 PostLifecycle) is complete
        _batchCycleComplete.TrySetResult();
      }
      // Return stream IDs on first batch, empty on subsequent
      var streamIds = batch == 1 ? new List<Guid>(StreamIdsToReturn) : [];
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        PerspectiveStreamIds = streamIds
      });
    }

    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) {
      GetStreamEventsCallCount++;
      return Task.FromResult(new List<StreamEventData>(StreamEventsToReturn));
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      // Do NOT signal here — this fires during perspective processing (before Phase 5).
      // Signal is in ProcessWorkBatchAsync 2nd call, which happens AFTER Phase 5 completes.
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// Event store that returns pre-configured deserialized events.
  /// </summary>
  private sealed class FakeEventStore : IEventStore {
    public List<MessageEnvelope<IEvent>> DeserializedEventsToReturn { get; set; } = [];
    public int DeserializeCallCount { get; private set; }

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
        IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
      DeserializeCallCount++;
      return new List<MessageEnvelope<IEvent>>(DeserializedEventsToReturn);
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
  }

  /// <summary>
  /// Perspective runner registry that supports multiple perspectives with different event types.
  /// Captures which events each perspective received via RunWithEventsAsync.
  /// </summary>
  private sealed class FilteringPerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
    private readonly ConcurrentDictionary<string, List<Guid>> _eventsPerPerspective = new();
    private readonly List<PerspectiveRegistrationInfo> _registrations;
    private int _runWithEventsCount;

    public FilteringPerspectiveRunnerRegistry(List<PerspectiveRegistrationInfo> registrations) {
      _registrations = registrations;
    }

    public int RunWithEventsCallCount => Volatile.Read(ref _runWithEventsCount);

    public List<Guid> GetEventsForPerspective(string perspectiveName) =>
      _eventsPerPerspective.GetValueOrDefault(perspectiveName, []);

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      new CapturingPerspectiveRunner(this);

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => _registrations;

    public IReadOnlyList<Type> GetEventTypes() => [typeof(AlphaEvent), typeof(BetaEvent)];

    private sealed class CapturingPerspectiveRunner(FilteringPerspectiveRunnerRegistry registry) : IPerspectiveRunner {
      public Type PerspectiveType => typeof(object);

      public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string name, Guid? lastProcessedEventId, CancellationToken cancellationToken) =>
        Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = name, LastEventId = Guid.NewGuid(), Status = PerspectiveProcessingStatus.Completed });

      public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string name, Guid? lastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
        Interlocked.Increment(ref registry._runWithEventsCount);
        var eventIds = events.Select(e => e.MessageId.Value).ToList();
        registry._eventsPerPerspective.AddOrUpdate(name, eventIds, (_, existing) => { existing.AddRange(eventIds); return existing; });
        return Task.FromResult(new PerspectiveCursorCompletion {
          StreamId = streamId,
          PerspectiveName = name,
          LastEventId = events.Count > 0 ? events[^1].MessageId.Value : Guid.NewGuid(),
          Status = PerspectiveProcessingStatus.Completed,
          PerspectiveType = typeof(object)
        });
      }

      public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string name, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        RunAsync(streamId, name, null, cancellationToken);

      public Task BootstrapSnapshotAsync(Guid streamId, string name, Guid lastProcessedEventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

  private sealed class FakeEventTypeProvider(IReadOnlyList<Type> eventTypes) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }

  // ========================================
  // HELPERS
  // ========================================

  private static MessageEnvelope<IEvent> _createEnvelope(Guid eventId, IEvent payload) => new() {
    MessageId = new MessageId(eventId),
    Payload = payload,
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.NewGuid(), ServiceName = "Test", HostName = "test", ProcessId = 1 }
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private static StreamEventData _createRawEvent(Guid streamId, Guid eventId, Type eventType, string jsonData) => new() {
    StreamId = streamId,
    EventId = eventId,
    EventType = TypeNameFormatter.Format(eventType),
    EventData = jsonData,
    Metadata = null,
    Scope = null,
    EventWorkId = Guid.NewGuid()
  };

  private (PerspectiveWorker Worker, DrainWorkCoordinator Coordinator, FilteringPerspectiveRunnerRegistry Registry, FakeEventStore EventStore, CapturingReceptorInvoker Invoker, ILifecycleCoordinator LifecycleCoordinator) _createWorkerWithLifecycle(
      List<PerspectiveRegistrationInfo> perspectiveRegistrations,
      List<StreamEventData> rawEvents,
      List<MessageEnvelope<IEvent>> typedEvents,
      List<Guid> streamIds) {

    var coordinator = new DrainWorkCoordinator { StreamIdsToReturn = streamIds, StreamEventsToReturn = rawEvents };
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FilteringPerspectiveRunnerRegistry(perspectiveRegistrations);
    var eventStore = new FakeEventStore { DeserializedEventsToReturn = typedEvents };
    var eventTypeProvider = new FakeEventTypeProvider([typeof(AlphaEvent), typeof(BetaEvent)]);
    var invoker = new CapturingReceptorInvoker();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IEventTypeProvider>(eventTypeProvider);
    services.AddSingleton<ILifecycleCoordinator, LifecycleCoordinator>();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider: null
    );

    var lifecycleCoordinator = serviceProvider.GetRequiredService<ILifecycleCoordinator>();
    return (worker, coordinator, registry, eventStore, invoker, lifecycleCoordinator);
  }

  private async Task _runWorkerOneBatchAsync(PerspectiveWorker worker, DrainWorkCoordinator coordinator) {
    // Use a semaphore to wait for the FULL batch cycle (including Phase 5 PostLifecycle).
    // The 2nd ProcessWorkBatchAsync call means the 1st batch is fully complete.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(10));
    // Small additional wait for any fire-and-forget tasks to complete
    await Task.Yield();
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }
  }

  // ========================================
  // EVENT FILTERING TESTS
  // ========================================

  [Test]
  public async Task DrainMode_FiltersEventsPerPerspective_OnlyMatchingEventsPassedAsync() {
    // Arrange — two event types, two perspectives, each handles only one type
    var streamId = Guid.NewGuid();
    var alphaEventId = Guid.NewGuid();
    var betaEventId = Guid.NewGuid();

    var registrations = new List<PerspectiveRegistrationInfo> {
      new("AlphaPerspective", "global::Test.AlphaPerspective", "global::Test.AlphaModel",
        [TypeNameFormatter.Format(typeof(AlphaEvent))]),
      new("BetaPerspective", "global::Test.BetaPerspective", "global::Test.BetaModel",
        [TypeNameFormatter.Format(typeof(BetaEvent))])
    };

    var rawEvents = new List<StreamEventData> {
      _createRawEvent(streamId, alphaEventId, typeof(AlphaEvent), """{"Data":"alpha"}"""),
      _createRawEvent(streamId, betaEventId, typeof(BetaEvent), """{"Data":"beta"}""")
    };

    var typedEvents = new List<MessageEnvelope<IEvent>> {
      _createEnvelope(alphaEventId, new AlphaEvent("alpha")),
      _createEnvelope(betaEventId, new BetaEvent("beta"))
    };

    var (worker, coordinator, registry, _, _, _) = _createWorkerWithLifecycle(
      registrations, rawEvents, typedEvents, [streamId]);

    // Act
    await _runWorkerOneBatchAsync(worker, coordinator);

    // Assert — each perspective should only receive its matching event
    var alphaEvents = registry.GetEventsForPerspective("AlphaPerspective");
    var betaEvents = registry.GetEventsForPerspective("BetaPerspective");

    await Assert.That(alphaEvents).Count().IsEqualTo(1);
    await Assert.That(alphaEvents[0]).IsEqualTo(alphaEventId);
    await Assert.That(betaEvents).Count().IsEqualTo(1);
    await Assert.That(betaEvents[0]).IsEqualTo(betaEventId);
  }

  [Test]
  public async Task DrainMode_PerspectiveWithNoMatchingEvents_SkippedAsync() {
    // Arrange — only AlphaEvent on the stream, BetaPerspective should not run
    var streamId = Guid.NewGuid();
    var alphaEventId = Guid.NewGuid();

    var registrations = new List<PerspectiveRegistrationInfo> {
      new("AlphaPerspective", "global::Test.AlphaPerspective", "global::Test.AlphaModel",
        [TypeNameFormatter.Format(typeof(AlphaEvent))]),
      new("BetaPerspective", "global::Test.BetaPerspective", "global::Test.BetaModel",
        [TypeNameFormatter.Format(typeof(BetaEvent))])
    };

    var rawEvents = new List<StreamEventData> {
      _createRawEvent(streamId, alphaEventId, typeof(AlphaEvent), """{"Data":"alpha"}""")
    };

    var typedEvents = new List<MessageEnvelope<IEvent>> {
      _createEnvelope(alphaEventId, new AlphaEvent("alpha"))
    };

    var (worker, coordinator, registry, _, _, _) = _createWorkerWithLifecycle(
      registrations, rawEvents, typedEvents, [streamId]);

    // Act
    await _runWorkerOneBatchAsync(worker, coordinator);

    // Assert — only AlphaPerspective ran, BetaPerspective was skipped
    var alphaEvents = registry.GetEventsForPerspective("AlphaPerspective");
    var betaEvents = registry.GetEventsForPerspective("BetaPerspective");

    await Assert.That(alphaEvents).Count().IsEqualTo(1);
    await Assert.That(betaEvents).Count().IsEqualTo(0);
  }

  [Test]
  public async Task DrainMode_MultipleEventTypes_SharedPerspectiveGetsAllMatchingAsync() {
    // Arrange — one perspective handles both AlphaEvent and BetaEvent
    var streamId = Guid.NewGuid();
    var alphaEventId = Guid.NewGuid();
    var betaEventId = Guid.NewGuid();

    var registrations = new List<PerspectiveRegistrationInfo> {
      new("CombinedPerspective", "global::Test.CombinedPerspective", "global::Test.CombinedModel",
        [TypeNameFormatter.Format(typeof(AlphaEvent)), TypeNameFormatter.Format(typeof(BetaEvent))])
    };

    var rawEvents = new List<StreamEventData> {
      _createRawEvent(streamId, alphaEventId, typeof(AlphaEvent), """{"Data":"alpha"}"""),
      _createRawEvent(streamId, betaEventId, typeof(BetaEvent), """{"Data":"beta"}""")
    };

    var typedEvents = new List<MessageEnvelope<IEvent>> {
      _createEnvelope(alphaEventId, new AlphaEvent("alpha")),
      _createEnvelope(betaEventId, new BetaEvent("beta"))
    };

    var (worker, coordinator, registry, _, _, _) = _createWorkerWithLifecycle(
      registrations, rawEvents, typedEvents, [streamId]);

    // Act
    await _runWorkerOneBatchAsync(worker, coordinator);

    // Assert — CombinedPerspective gets both events
    var events = registry.GetEventsForPerspective("CombinedPerspective");
    await Assert.That(events).Count().IsEqualTo(2);
  }

  // ========================================
  // LIFECYCLE CHAIN TESTS
  // ========================================

  [Test]
  public async Task DrainMode_PostPerspectiveInline_FiresPerPerspectivePerEventAsync() {
    // Arrange — single event, single perspective
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    var registrations = new List<PerspectiveRegistrationInfo> {
      new("TestPerspective", "global::Test.TestPerspective", "global::Test.TestModel",
        [TypeNameFormatter.Format(typeof(AlphaEvent))])
    };

    var rawEvents = new List<StreamEventData> {
      _createRawEvent(streamId, eventId, typeof(AlphaEvent), """{"Data":"test"}""")
    };

    var typedEvents = new List<MessageEnvelope<IEvent>> {
      _createEnvelope(eventId, new AlphaEvent("test"))
    };

    var (worker, coordinator, _, _, invoker, lc) = _createWorkerWithLifecycle(
      registrations, rawEvents, typedEvents, [streamId]);

    // Act
    await _runWorkerOneBatchAsync(worker, coordinator);

    // Assert — PostPerspectiveInline should have fired
    await Assert.That(invoker.HasStage(LifecycleStage.PostPerspectiveInline)).IsTrue();
  }

  [Test]
  public async Task DrainMode_PostAllPerspectives_FiresAfterAllPerspectivesCompleteAsync() {
    // Arrange — one event handled by two perspectives
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    var registrations = new List<PerspectiveRegistrationInfo> {
      new("Perspective1", "global::Test.Perspective1", "global::Test.Model1",
        [TypeNameFormatter.Format(typeof(AlphaEvent))]),
      new("Perspective2", "global::Test.Perspective2", "global::Test.Model2",
        [TypeNameFormatter.Format(typeof(AlphaEvent))])
    };

    var rawEvents = new List<StreamEventData> {
      _createRawEvent(streamId, eventId, typeof(AlphaEvent), """{"Data":"test"}""")
    };

    var typedEvents = new List<MessageEnvelope<IEvent>> {
      _createEnvelope(eventId, new AlphaEvent("test"))
    };

    var (worker, coordinator, registry, _, invoker, lc) = _createWorkerWithLifecycle(
      registrations, rawEvents, typedEvents, [streamId]);

    // Act
    await _runWorkerOneBatchAsync(worker, coordinator);

    // Assert — check coordinator state
    var allComplete = lc.AreAllPerspectivesComplete(eventId);
    var hasTracking = lc.GetTracking(eventId) is not null;
    var allStages = invoker.Invocations.Select(i => i.Stage.ToString()).ToList();
    var runCount = registry.RunWithEventsCallCount;

    await Assert.That(runCount).IsGreaterThanOrEqualTo(2)
      .Because("Both perspectives should have run via RunWithEventsAsync");
    await Assert.That(allComplete).IsTrue()
      .Because("Both perspectives signaled complete — WhenAll should resolve");
    await Assert.That(hasTracking).IsTrue()
      .Because("BeginTracking was called in PrePerspective");
    await Assert.That(invoker.HasStage(LifecycleStage.PostAllPerspectivesInline)).IsTrue()
      .Because($"Expected PostAllPerspectivesInline (runCount={runCount}) but only saw: [{string.Join(", ", allStages)}]");
  }

  [Test]
  public async Task DrainMode_PostLifecycle_FiresAfterPostAllPerspectivesAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    var registrations = new List<PerspectiveRegistrationInfo> {
      new("TestPerspective", "global::Test.TestPerspective", "global::Test.TestModel",
        [TypeNameFormatter.Format(typeof(AlphaEvent))])
    };

    var rawEvents = new List<StreamEventData> {
      _createRawEvent(streamId, eventId, typeof(AlphaEvent), """{"Data":"test"}""")
    };

    var typedEvents = new List<MessageEnvelope<IEvent>> {
      _createEnvelope(eventId, new AlphaEvent("test"))
    };

    var (worker, coordinator, _, _, invoker, lc) = _createWorkerWithLifecycle(
      registrations, rawEvents, typedEvents, [streamId]);

    // Act
    await _runWorkerOneBatchAsync(worker, coordinator);

    // Assert — PostLifecycle should fire (tagged notifications)
    await Assert.That(invoker.HasStage(LifecycleStage.PostLifecycleDetached)).IsTrue();
    await Assert.That(invoker.HasStage(LifecycleStage.PostLifecycleInline)).IsTrue();
  }

  [Test]
  public async Task DrainMode_FullLifecycleChain_AllStagesFireInOrderAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    var registrations = new List<PerspectiveRegistrationInfo> {
      new("TestPerspective", "global::Test.TestPerspective", "global::Test.TestModel",
        [TypeNameFormatter.Format(typeof(AlphaEvent))])
    };

    var rawEvents = new List<StreamEventData> {
      _createRawEvent(streamId, eventId, typeof(AlphaEvent), """{"Data":"test"}""")
    };

    var typedEvents = new List<MessageEnvelope<IEvent>> {
      _createEnvelope(eventId, new AlphaEvent("test"))
    };

    var (worker, coordinator, _, _, invoker, lc) = _createWorkerWithLifecycle(
      registrations, rawEvents, typedEvents, [streamId]);

    // Act
    await _runWorkerOneBatchAsync(worker, coordinator);

    // Assert — full lifecycle chain fires for the event
    // PrePerspective fires via coordinator (AdvanceToAsync), not via invoker directly in drain mode
    // PostPerspectiveInline fires via invoker
    await Assert.That(invoker.HasStage(LifecycleStage.PostPerspectiveInline)).IsTrue();
    await Assert.That(invoker.HasStage(LifecycleStage.PostAllPerspectivesDetached)).IsTrue();
    await Assert.That(invoker.HasStage(LifecycleStage.PostAllPerspectivesInline)).IsTrue();
    await Assert.That(invoker.HasStage(LifecycleStage.PostLifecycleDetached)).IsTrue();
    await Assert.That(invoker.HasStage(LifecycleStage.PostLifecycleInline)).IsTrue();
  }

  [Test]
  public async Task DrainMode_TwoEvents_BothGetFullLifecycleAsync() {
    // Arrange — two events, one perspective
    var streamId = Guid.NewGuid();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    var registrations = new List<PerspectiveRegistrationInfo> {
      new("TestPerspective", "global::Test.TestPerspective", "global::Test.TestModel",
        [TypeNameFormatter.Format(typeof(AlphaEvent))])
    };

    var rawEvents = new List<StreamEventData> {
      _createRawEvent(streamId, eventId1, typeof(AlphaEvent), """{"Data":"one"}"""),
      _createRawEvent(streamId, eventId2, typeof(AlphaEvent), """{"Data":"two"}""")
    };

    var typedEvents = new List<MessageEnvelope<IEvent>> {
      _createEnvelope(eventId1, new AlphaEvent("one")),
      _createEnvelope(eventId2, new AlphaEvent("two"))
    };

    var (worker, coordinator, _, _, invoker, lc) = _createWorkerWithLifecycle(
      registrations, rawEvents, typedEvents, [streamId]);

    // Act
    await _runWorkerOneBatchAsync(worker, coordinator);

    // Assert — PostPerspectiveInline fires for EACH event (2 events × 1 perspective = 2)
    await Assert.That(invoker.CountForStage(LifecycleStage.PostPerspectiveInline)).IsEqualTo(2);
    // PostLifecycle fires for EACH event (2)
    await Assert.That(invoker.CountForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(2);
  }

  // ========================================
  // COORDINATOR BEHAVIOR TESTS
  // ========================================

  [Test]
  public async Task DrainMode_ExpectationsRegisteredBeforeSignals_WhenAllResolvesAsync() {
    // Arrange — two perspectives for same event; WhenAll must resolve
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    var registrations = new List<PerspectiveRegistrationInfo> {
      new("P1", "global::Test.P1", "global::Test.M1", [TypeNameFormatter.Format(typeof(AlphaEvent))]),
      new("P2", "global::Test.P2", "global::Test.M2", [TypeNameFormatter.Format(typeof(AlphaEvent))])
    };

    var rawEvents = new List<StreamEventData> {
      _createRawEvent(streamId, eventId, typeof(AlphaEvent), """{"Data":"test"}""")
    };

    var typedEvents = new List<MessageEnvelope<IEvent>> {
      _createEnvelope(eventId, new AlphaEvent("test"))
    };

    var (worker, coordinator, _, _, invoker, lc) = _createWorkerWithLifecycle(
      registrations, rawEvents, typedEvents, [streamId]);

    // Act
    await _runWorkerOneBatchAsync(worker, coordinator);

    // Assert — PostAllPerspectives fires (proves WhenAll resolved after both P1 and P2 signaled)
    await Assert.That(invoker.HasStage(LifecycleStage.PostAllPerspectivesInline)).IsTrue();
  }

  [Test]
  public async Task DrainMode_DuplicateRawEventIds_HandledGracefullyAsync() {
    // Arrange — same event appears twice in raw events (multiple perspective_events rows)
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    var registrations = new List<PerspectiveRegistrationInfo> {
      new("TestPerspective", "global::Test.TestPerspective", "global::Test.TestModel",
        [TypeNameFormatter.Format(typeof(AlphaEvent))])
    };

    // Same eventId appears twice (simulates multiple perspective rows for same event)
    var rawEvents = new List<StreamEventData> {
      _createRawEvent(streamId, eventId, typeof(AlphaEvent), """{"Data":"test"}"""),
      _createRawEvent(streamId, eventId, typeof(AlphaEvent), """{"Data":"test"}""")
    };

    var typedEvents = new List<MessageEnvelope<IEvent>> {
      _createEnvelope(eventId, new AlphaEvent("test"))
    };

    var (worker, coordinator, _, _, invoker, lc) = _createWorkerWithLifecycle(
      registrations, rawEvents, typedEvents, [streamId]);

    // Act — should NOT throw duplicate key exception
    await _runWorkerOneBatchAsync(worker, coordinator);

    // Assert — processed successfully
    await Assert.That(invoker.HasStage(LifecycleStage.PostLifecycleInline)).IsTrue();
  }
}

// ========================================
// MINIMAL ISOLATION TEST — bypasses PerspectiveWorker
// ========================================

public class LifecycleCoordinatorPostAllPerspectivesIsolationTests {
  [Test]
  public async Task Coordinator_AfterAllPerspectivesSignaled_AdvanceToPostAllPerspectivesFires_Async() {
    // Arrange — use REAL coordinator, real tracking
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var invoker = new CapturingInvoker();

    var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    var envelope = new MessageEnvelope<IEvent> {
      MessageId = new MessageId(eventId),
      Payload = new TestIsolationEvent("test"),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.NewGuid(), ServiceName = "Test", HostName = "test", ProcessId = 1 }
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act — simulate the full lifecycle: BeginTracking → Expect → Signal → AdvanceTo
    var tracking = coordinator.BeginTracking(eventId, envelope, LifecycleStage.PrePerspectiveDetached, MessageSource.Local, Guid.NewGuid());

    // Register expectations (2 perspectives)
    coordinator.ExpectPerspectiveCompletions(eventId, ["P1", "P2"]);

    // Signal both perspectives complete
    coordinator.SignalPerspectiveComplete(eventId, "P1");
    coordinator.SignalPerspectiveComplete(eventId, "P2");

    // Verify WhenAll resolved
    var allComplete = coordinator.AreAllPerspectivesComplete(eventId);
    await Assert.That(allComplete).IsTrue();

    // Advance to PostAllPerspectives stages
    await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesDetached, sp, default);
    await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesInline, sp, default);
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, sp, default);
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, sp, default);

    // Allow detached tasks to complete
    await Task.Delay(50);

    // Assert — invoker should have captured inline stages
    await Assert.That(invoker.HasStage(LifecycleStage.PostAllPerspectivesInline)).IsTrue();
    await Assert.That(invoker.HasStage(LifecycleStage.PostLifecycleInline)).IsTrue();
  }

  private sealed record TestIsolationEvent(string Data) : IEvent;

  private sealed class CapturingInvoker : IReceptorInvoker {
    private readonly System.Collections.Concurrent.ConcurrentBag<LifecycleStage> _stages = [];
    public bool HasStage(LifecycleStage stage) => _stages.Contains(stage);

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      _stages.Add(stage);
      return ValueTask.CompletedTask;
    }
  }
}
