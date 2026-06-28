using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Unit tests for the PerspectiveWorker collective-event sink seam (<c>_processCollectiveSinkAsync</c>):
/// a perspective-work item for the <see cref="CollectiveRouting.SINK_PERSPECTIVE_NAME"/> sink is dispatched
/// through <see cref="ICollectiveDispatcher"/> exactly once and bypasses the per-stream runner. Covers the
/// dispatch path, the not-configured short-circuit, and the no-collective-event short-circuit.
/// </summary>
[NotInParallel("CollectiveSinkWorker")]
public class PerspectiveWorkerCollectiveSinkTests {

  [Test]
  public async Task CollectiveSink_DispatchesEventOnceAndSkipsRunner_Async() {
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    var collectiveEvent = new _testCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var dispatcher = new _recordingDispatcher();
    var runner = new _trackingRunner();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new _eventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new _registry(runner, [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.WaitForCyclesAsync(2, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(1)
      .Because("The collective event is dispatched exactly once.");
    await Assert.That(dispatcher.Calls[0].EventId).IsEqualTo(eventId);
    await Assert.That(ReferenceEquals(dispatcher.Calls[0].Event, collectiveEvent)).IsTrue();
    await Assert.That(runner.RunWithEventsCount).IsEqualTo(0)
      .Because("The sink bypasses the per-stream runner.");
  }

  [Test]
  public async Task CollectiveSink_NoDispatcherRegistered_DoesNotThrow_Async() {
    var streamId = TrackedGuid.NewMedo().Value;
    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new _eventStore { Envelopes = { [streamId] = [_envelope(TrackedGuid.NewMedo().Value, new _testCollectiveEvent { Scope = new TenantCollectiveScope("t") })] } },
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: null); // not configured

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.WaitForCyclesAsync(2, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }
    // No assertion needed beyond "did not throw" — the not-configured branch logs and returns.
  }

  [Test]
  public async Task CollectiveSink_NoCollectiveEventOnStream_NoDispatch_Async() {
    var streamId = TrackedGuid.NewMedo().Value;
    var dispatcher = new _recordingDispatcher();
    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new _eventStore(), // empty — no events on the stream
      registry: new _registry(new _trackingRunner(), [typeof(_testCollectiveEvent)]),
      dispatcher: dispatcher);

    var workerTask = worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.WaitForCyclesAsync(2, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    await Assert.That(dispatcher.Calls.Count).IsEqualTo(0)
      .Because("No collective event on the stream → nothing to dispatch.");
  }

  // ── helpers ────────────────────────────────────────────────────────────

  private static PerspectiveWork _sinkWork(Guid streamId) => new() {
    WorkId = Guid.CreateVersion7(),
    StreamId = streamId,
    PerspectiveName = CollectiveRouting.SINK_PERSPECTIVE_NAME,
    LastProcessedEventId = null,
    PartitionNumber = 1
  };

  private static MessageEnvelope<IEvent> _envelope(Guid eventId, IEvent payload) => new() {
    MessageId = new MessageId(eventId),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private static (PerspectiveWorker Worker, Whizbang.Testing.Workers.PerspectiveWorkerTestHarness Harness, _coordinator Coordinator) _createWorker(
      List<PerspectiveWork> work, _eventStore eventStore, _registry registry, _recordingDispatcher? dispatcher) {
    var instanceProvider = new _instanceProvider();
    var strategy = new InstantCompletionStrategy();
    var harness = new Whizbang.Testing.Workers.PerspectiveWorkerTestHarness();
    var coordinator = new _coordinator(work);

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    if (dispatcher is not null) {
      services.AddSingleton<ICollectiveDispatcher>(dispatcher);
      services.AddSingleton<ICollectiveSessionAccessor>(new _stubSessionAccessor());
    }
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      strategy,
      eventTypeProvider: registry,
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel);
    return (worker, harness, coordinator);
  }

  private sealed record _testCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  private sealed class _recordingDispatcher : ICollectiveDispatcher {
    public List<(ICollectiveEvent Event, Guid EventId)> Calls { get; } = [];
    public Task<CollectiveDispatchResult> DispatchAsync(
        ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, CancellationToken cancellationToken) {
      Calls.Add((evt, collectiveEventId));
      return Task.FromResult(new CollectiveDispatchResult(1, 1));
    }
  }

  private sealed class _stubSessionAccessor : ICollectiveSessionAccessor {
    public object GetSession(IServiceProvider scopedServiceProvider) => new object();
  }

  private sealed class _coordinator(List<PerspectiveWork> work) : NoOpWorkCoordinator, IWorkCoordinator {
    private int _cycle;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _waiters = new();
    public Task WaitForCyclesAsync(int minCycles, TimeSpan timeout) =>
      _waiters.GetOrAdd(minCycles, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(timeout);
    public new Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) {
      var c = Interlocked.Increment(ref _cycle);
      foreach (var kv in _waiters) { if (c >= kv.Key) { kv.Value.TrySetResult(); } }
      var pw = c == 1 ? new List<PerspectiveWork>(work) : [];
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = pw, PerspectiveStreamIds = [] });
    }
  }

  private sealed class _eventStore : IEventStore {
    public ConcurrentDictionary<Guid, List<MessageEnvelope<IEvent>>> Envelopes { get; } = new();
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(Envelopes.TryGetValue(streamId, out var e) ? e.ToList() : []);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
    private static async IAsyncEnumerable<MessageEnvelope<T>> _empty<T>([EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
  }

  private sealed class _registry(_trackingRunner runner, IReadOnlyList<Type> eventTypes) : IPerspectiveRunnerRegistry, IEventTypeProvider {
    // Sink perspective has no runner — return null so the worker would fall through (but the sink guard fires first).
    public IPerspectiveRunner? GetRunner(string name, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
    public _trackingRunner Runner => runner;
  }

  private sealed class _trackingRunner : IPerspectiveRunner {
    public int RunWithEventsCount { get; private set; }
    public Type PerspectiveType => typeof(object);
    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) =>
      Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.Empty, Status = PerspectiveProcessingStatus.Completed });
    public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
      RunWithEventsCount++;
      return Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.Empty, Status = PerspectiveProcessingStatus.Completed });
    }
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = Guid.Empty, Status = PerspectiveProcessingStatus.Completed });
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class _instanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "CollectiveSinkTest";
    public string HostName => "test-host";
    public int ProcessId => 1234;
    ServiceInstanceInfo IServiceInstanceProvider.ToInfo() =>
      new() { ServiceName = ServiceName, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }
}
