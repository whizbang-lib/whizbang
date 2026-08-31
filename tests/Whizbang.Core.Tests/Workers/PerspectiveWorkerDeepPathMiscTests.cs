using System.Collections.Concurrent;
using System.Threading.Channels;
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
/// Deep-path coverage for PerspectiveWorker infrastructure seams:
/// - Activity-triggered idle sweep of the stream-affinity gate dictionary (incl. gate disposal)
/// - Cursor-cache eviction hook cascading into the affinity gates
/// - Idle/active work-state transitions
/// - Rewind stream-lock keepalive renewal loop
/// - Faulted background PostLifecycle observed by the next batch and by shutdown
/// - Drain-channel sliding-window accumulation (cancellation + second-arrival coalescing)
/// - Collective sink with no work-row ids (empty completion set early-return)
/// </summary>
public class PerspectiveWorkerDeepPathMiscTests {

  private const string PERSPECTIVE = "Misc.DeepPerspective";

  [Test]
  public async Task Worker_ZeroIdleWindow_SweepsAffinityGatesBetweenBatchesAsync() {
    // Arrange — zero idle window + zero sweep interval: every gate release sweeps and
    // evicts (disposes) the just-used gate. A subsequent batch for the same stream must
    // get a fresh gate and process normally.
    var streamId = Guid.CreateVersion7();
    var coordinator = new MiscWorkCoordinator();
    var runner = new MiscRunner();
    var registry = new MiscRegistry(PERSPECTIVE, runner, [typeof(MiscDeepEvent)]);
    var (worker, harness, _) = _createWorker(
      coordinator, new MiscEventStore(), registry,
      affinityOptions: new PerspectiveStreamAffinityOptions {
        IdleEvictionWindow = TimeSpan.Zero,
        SweepInterval = TimeSpan.Zero
      });

    // Act — two sequential batches for the same (stream, perspective)
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueWorkAsync(_work(streamId), cts.Token);
    await coordinator.WaitForCompletionsAsync(1, TimeSpan.FromSeconds(10));
    await harness.EnqueueWorkAsync(_work(streamId), cts.Token);
    await coordinator.WaitForCompletionsAsync(2, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — both batches completed even though the gate was evicted + disposed between them
    await Assert.That(coordinator.Completions.Count).IsEqualTo(2)
      .Because("Gate eviction between batches must be transparent — a fresh gate is created on demand");
    await Assert.That(runner.RunCallCount).IsEqualTo(2);
  }

  [Test]
  public async Task Worker_CursorCacheEviction_DropsAffinityGatesAndProcessingContinuesAsync() {
    // Arrange — zero-window affinity options shared by the cursor cache and gate dict.
    // Channel work creates the gate + wires the eviction subscription; a drain of the same
    // stream touches the cursor cache, whose sweep evicts the stream and cascades into the
    // gate dictionary. A final channel batch proves the worker keeps functioning.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new MiscWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new MiscEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new MiscDeepEvent("evict"))]);
    var runner = new MiscRunner();
    var registry = new MiscRegistry(PERSPECTIVE, runner, [typeof(MiscDeepEvent)]);
    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, registry,
      affinityOptions: new PerspectiveStreamAffinityOptions {
        IdleEvictionWindow = TimeSpan.Zero,
        SweepInterval = TimeSpan.Zero
      });

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await harness.EnqueueWorkAsync(_work(streamId), cts.Token);
    await coordinator.WaitForCompletionsAsync(1, TimeSpan.FromSeconds(10));

    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.WaitForCompletionsAsync(2, TimeSpan.FromSeconds(10));

    await harness.EnqueueWorkAsync(_work(streamId), cts.Token);
    await coordinator.WaitForCompletionsAsync(3, TimeSpan.FromSeconds(10));

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — all three passes completed; the eviction cascade did not strand the stream
    await Assert.That(coordinator.Completions.Count).IsEqualTo(3);
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1)
      .Because("The drain pass applied the stream's pending event exactly once");
  }

  [Test]
  public async Task Worker_WorkThenSilence_TransitionsActiveThenIdleAsync() {
    // Arrange
    var streamId = Guid.CreateVersion7();
    var coordinator = new MiscWorkCoordinator();
    var runner = new MiscRunner();
    var registry = new MiscRegistry(PERSPECTIVE, runner, [typeof(MiscDeepEvent)]);
    var (worker, harness, _) = _createWorker(
      coordinator, new MiscEventStore(), registry,
      configure: opts => {
        opts.PollingIntervalMilliseconds = 25;
        opts.IdleThresholdPolls = 1;
      });

    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var idled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingStarted += () => started.TrySetResult();
    worker.OnWorkProcessingIdle += () => idled.TrySetResult();

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueWorkAsync(_work(streamId), cts.Token);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await idled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(started.Task.IsCompleted).IsTrue();
    await Assert.That(idled.Task.IsCompleted).IsTrue();
    await Assert.That(worker.IsIdle).IsTrue();
    await Assert.That(worker.ConsecutiveEmptyPolls).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task Worker_RewindHoldingLock_RenewsLockViaKeepaliveAsync() {
    // Arrange — rewind path with a locker: the keepalive loop must renew the lock while the
    // rewind is in flight (the runner completes only after observing the first renewal).
    var streamId = Guid.CreateVersion7();
    var triggerEventId = Guid.CreateVersion7();
    var locker = new RenewSignalingLocker();
    var runner = new MiscRunner { RewindDelayUntil = locker.FirstRenew };
    var registry = new MiscRegistry(PERSPECTIVE, runner, [typeof(MiscDeepEvent)]);
    var coordinator = new MiscWorkCoordinator();
    coordinator.CursorOverrides[(PERSPECTIVE, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = PERSPECTIVE,
      LastEventId = Guid.CreateVersion7(),
      Status = PerspectiveProcessingStatus.RewindRequired,
      RewindTriggerEventId = triggerEventId
    };
    var (worker, harness, _) = _createWorker(
      coordinator, new MiscEventStore(), registry,
      streamLocker: locker,
      streamLockOptions: new PerspectiveStreamLockOptions {
        KeepAliveInterval = TimeSpan.FromMilliseconds(1)
      });

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueWorkAsync(_work(streamId, PerspectiveProcessingStatus.RewindRequired), cts.Token);
    await coordinator.WaitForCompletionsAsync(1, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — the keepalive renewed at least once while the rewind ran, then released
    await Assert.That(locker.RenewCallCount).IsGreaterThanOrEqualTo(1)
      .Because("The keepalive loop must renew the stream lock during a long rewind");
    await Assert.That(locker.AcquireCallCount).IsEqualTo(1);
    await Assert.That(locker.LastReason).IsEqualTo("rewind");
    await Assert.That(locker.ReleaseCallCount).IsEqualTo(1);
    await Assert.That(runner.RewindCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task Worker_BackgroundPostLifecycleFaults_NextBatchAndShutdownObserveTheFaultAsync() {
    // Arrange — no lifecycle coordinator → fallback PostLifecycle in the background task.
    // The receptor throws at PostLifecycleInline, faulting the background task. The next
    // batch must observe (and swallow) the prior fault, and shutdown must drain the last one.
    var stream1 = Guid.CreateVersion7();
    var stream2 = Guid.CreateVersion7();
    var event1 = Guid.CreateVersion7();
    var event2 = Guid.CreateVersion7();
    var coordinator = new MiscWorkCoordinator();
    var eventStore = new MiscEventStore();
    eventStore.StreamEnvelopes[stream1] = [_envelope(event1, new MiscDeepEvent("one"))];
    eventStore.StreamEnvelopes[stream2] = [_envelope(event2, new MiscDeepEvent("two"))];
    var runner = new MiscRunner();
    var registry = new MiscRegistry(PERSPECTIVE, runner, [typeof(MiscDeepEvent)]);
    var invoker = new ThrowAtPostLifecycleInvoker();

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, registry,
      receptorInvoker: invoker);

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await harness.EnqueueWorkAsync(_work(stream1), cts.Token);
    await coordinator.WaitForCompletionsAsync(1, TimeSpan.FromSeconds(10));
    await invoker.WaitForThrowsAsync(1, TimeSpan.FromSeconds(10));

    await harness.EnqueueWorkAsync(_work(stream2), cts.Token);
    await coordinator.WaitForCompletionsAsync(2, TimeSpan.FromSeconds(10));
    await invoker.WaitForThrowsAsync(2, TimeSpan.FromSeconds(10));

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    var executeTask = worker.ExecuteTask ?? Task.CompletedTask;

    // Assert — both batches completed and the worker shut down cleanly despite two faulted
    // background PostLifecycle tasks (one observed by batch 2, one drained at shutdown).
    await Assert.That(invoker.ThrowCount).IsEqualTo(2);
    await Assert.That(coordinator.Completions.Count).IsEqualTo(2);
    await Assert.That(executeTask.IsCompleted).IsTrue();
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("Faulted PostLifecycle background tasks are logged and swallowed, never propagated");
  }

  [Test]
  public async Task Worker_CancelledDuringSlidingWindowAccumulation_ShutsDownPromptlyAsync() {
    // Arrange — a long sliding window (30 s). After the first drain signal is consumed the
    // worker parks inside the accumulation window; cancelling must exit promptly instead of
    // waiting out the window.
    var streamId = Guid.CreateVersion7();
    var coordinator = new MiscWorkCoordinator();
    var runner = new MiscRunner();
    var registry = new MiscRegistry(PERSPECTIVE, runner, [typeof(MiscDeepEvent)]);
    var drainChannel = new SignalingDrainChannel();
    var (worker, _, _) = _createWorker(
      coordinator, new MiscEventStore(), registry,
      configure: opts => {
        opts.MaxConcurrentDrainConsumers = 1;
        opts.DrainBatcher = new SlidingWindowBatcherOptions {
          SlidingWindow = TimeSpan.FromSeconds(30),
          MaxWait = TimeSpan.FromSeconds(30),
          MaxSize = 10
        };
      },
      drainChannelOverride: drainChannel);

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId, cts.Token);
    await drainChannel.ReaderImpl.WindowWaitEntered.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();

    var executeTask = worker.ExecuteTask ?? Task.CompletedTask;
    try {
      await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
    } catch (OperationCanceledException) {
      // Cancellation surfacing through the execute task is acceptable shutdown behavior.
    }
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — the worker exited well before the 30 s window elapsed
    await Assert.That(executeTask.IsCompleted).IsTrue()
      .Because("Cancellation inside the sliding-window accumulator must exit promptly");
  }

  [Test]
  public async Task Worker_SecondDrainSignalWithinWindow_CoalescesIntoSingleFetchAsync() {
    // Arrange — a short sliding window: stream B's signal arriving inside stream A's window
    // must coalesce into ONE batched fetch covering both streams.
    var streamA = Guid.CreateVersion7();
    var streamB = Guid.CreateVersion7();
    var eventA = Guid.CreateVersion7();
    var eventB = Guid.CreateVersion7();
    var coordinator = new MiscWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamA, eventA, Guid.CreateVersion7()),
      _raw(streamB, eventB, Guid.CreateVersion7())
    ]);
    var eventStore = new MiscEventStore();
    eventStore.EnqueueDeserialized([
      _envelope(eventA, new MiscDeepEvent("a")),
      _envelope(eventB, new MiscDeepEvent("b"))
    ]);
    var runner = new MiscRunner();
    var registry = new MiscRegistry(PERSPECTIVE, runner, [typeof(MiscDeepEvent)]);
    var drainChannel = new SignalingDrainChannel();
    var (worker, _, _) = _createWorker(
      coordinator, eventStore, registry,
      configure: opts => {
        opts.MaxConcurrentDrainConsumers = 1;
        opts.DrainBatcher = new SlidingWindowBatcherOptions {
          SlidingWindow = TimeSpan.FromMilliseconds(150),
          MaxWait = TimeSpan.FromSeconds(2),
          MaxSize = 10
        };
      },
      drainChannelOverride: drainChannel);

    // Act — write A, wait until the worker is inside the accumulation window, then write B
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamA, cts.Token);
    await drainChannel.ReaderImpl.WindowWaitEntered.WaitAsync(TimeSpan.FromSeconds(10));
    await drainChannel.WriteAsync(streamB, cts.Token);
    await coordinator.WaitForCompletionsAsync(2, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — one fetch covered both streams (coalesced batch), then both applied
    await Assert.That(coordinator.GetStreamEventsCallCount).IsEqualTo(1)
      .Because("The sliding window must coalesce close-in-time drain signals into one fetch");
    coordinator.StreamEventsRequests.TryPeek(out var requestedStreams);
    var requested = requestedStreams ?? [];
    await Assert.That(requested).Contains(streamA);
    await Assert.That(requested).Contains(streamB);
    await Assert.That(coordinator.Completions.Count).IsEqualTo(2);
  }

  [Test]
  public async Task CollectiveSink_EmptyWorkIdsAndNoEvents_CompletesNothingWithoutDispatchAsync() {
    // Arrange — a sink work item whose WorkId is Guid.Empty and a stream with no collective
    // events: the sink must neither dispatch nor enqueue completions (empty-set early return).
    var streamId = Guid.CreateVersion7();
    var coordinator = new MiscWorkCoordinator();
    var eventStore = new MiscEventStore(); // no events on the stream
    var runner = new MiscRunner();
    var registry = new MiscRegistry(PERSPECTIVE, runner, [typeof(MiscDeepEvent)]);
    var dispatcher = new RecordingCollectiveDispatcher();

    var (worker, harness, _) = _createWorker(
      coordinator, eventStore, registry,
      collectiveDispatcher: dispatcher);

    var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBatchCycleComplete += () => cycleComplete.TrySetResult();

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueWorkAsync(new PerspectiveWork {
      WorkId = Guid.Empty,
      StreamId = streamId,
      PerspectiveName = CollectiveRouting.SINK_PERSPECTIVE_NAME,
      LastProcessedEventId = null,
      PartitionNumber = 1
    }, cts.Token);
    await cycleComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — the sink consulted the cursor, dispatched nothing, and enqueued no work-row deletions
    await Assert.That(coordinator.GetPerspectiveCursorCallCount).IsGreaterThanOrEqualTo(1)
      .Because("The sink path runs (infra registered) before discovering there is nothing to complete");
    await Assert.That(dispatcher.CallCount).IsEqualTo(0);
    await Assert.That(harness.CompletionCapture.EventWorkIds.Count).IsEqualTo(0)
      .Because("Guid.Empty work ids are filtered — nothing must reach the completion channel");
  }

  #region Test event + helpers

  private sealed record MiscDeepEvent(string Data) : IEvent;

  private static PerspectiveWork _work(Guid streamId, PerspectiveProcessingStatus status = PerspectiveProcessingStatus.None) => new() {
    WorkId = Guid.CreateVersion7(),
    StreamId = streamId,
    PerspectiveName = PERSPECTIVE,
    LastProcessedEventId = null,
    PartitionNumber = 1,
    Status = status
  };

  private static StreamEventData _raw(Guid streamId, Guid eventId, Guid workId) => new() {
    StreamId = streamId,
    EventId = eventId,
    EventType = TypeNameFormatter.Format(typeof(MiscDeepEvent)),
    EventData = "{}",
    Metadata = null,
    Scope = null,
    EventWorkId = workId,
    PerspectiveName = PERSPECTIVE
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

  private static (PerspectiveWorker Worker, PerspectiveWorkerTestHarness Harness, ServiceProvider Provider) _createWorker(
      MiscWorkCoordinator coordinator,
      MiscEventStore eventStore,
      MiscRegistry registry,
      Action<PerspectiveWorkerOptions>? configure = null,
      PerspectiveStreamAffinityOptions? affinityOptions = null,
      IPerspectiveStreamLocker? streamLocker = null,
      PerspectiveStreamLockOptions? streamLockOptions = null,
      IReceptorInvoker? receptorInvoker = null,
      ICollectiveDispatcher? collectiveDispatcher = null,
      IPerspectiveDrainChannel? drainChannelOverride = null) {
    var instanceProvider = new FakeInstanceProvider();
    var harness = new PerspectiveWorkerTestHarness();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    if (receptorInvoker is not null) {
      services.AddSingleton(receptorInvoker);
    }
    if (collectiveDispatcher is not null) {
      services.AddSingleton(collectiveDispatcher);
      services.AddSingleton<ICollectiveSessionAccessor>(new StubSessionAccessor());
    }
    services.AddLogging();
    var provider = services.BuildServiceProvider();

    var options = new PerspectiveWorkerOptions {
      PollingIntervalMilliseconds = 50,
      DrainBatcher = new SlidingWindowBatcherOptions {
        SlidingWindow = TimeSpan.Zero,
        MaxWait = TimeSpan.Zero,
        MaxSize = 1000
      }
    };
    configure?.Invoke(options);

    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(options),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      eventTypeProvider: registry,
      streamLocker: streamLocker,
      streamLockOptions: streamLockOptions is null ? null : Options.Create(streamLockOptions),
      streamAffinityOptions: affinityOptions is null ? null : Options.Create(affinityOptions),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: drainChannelOverride ?? harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());
    return (worker, harness, provider);
  }

  #endregion

  #region Fakes

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "DeepPathMiscTest";
    public string HostName => "test-host";
    public int ProcessId => 4244;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class MiscWorkCoordinator : IWorkCoordinator {
    private readonly ConcurrentQueue<List<StreamEventData>> _streamEventsResponses = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _completionWaiters = new();
    private int _completionCount;
    private int _streamEventsCallCount;
    private int _cursorCallCount;

    public ConcurrentQueue<PerspectiveCursorCompletion> Completions { get; } = new();
    public ConcurrentQueue<PerspectiveCursorFailure> Failures { get; } = new();
    public ConcurrentQueue<Guid[]> StreamEventsRequests { get; } = new();
    public Dictionary<(string PerspectiveName, Guid StreamId), PerspectiveCursorInfo> CursorOverrides { get; } = [];
    public int GetStreamEventsCallCount => Volatile.Read(ref _streamEventsCallCount);
    public int GetPerspectiveCursorCallCount => Volatile.Read(ref _cursorCallCount);

    public void EnqueueStreamEvents(List<StreamEventData> rows) => _streamEventsResponses.Enqueue(rows);

    public Task WaitForCompletionsAsync(int count, TimeSpan timeout) {
      var tcs = _completionWaiters.GetOrAdd(count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      if (Volatile.Read(ref _completionCount) >= count) {
        tcs.TrySetResult();
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _streamEventsCallCount);
      StreamEventsRequests.Enqueue(streamIds);
      if (_streamEventsResponses.TryDequeue(out var rows)) {
        return Task.FromResult(new List<StreamEventData>(rows));
      }
      return Task.FromResult(new List<StreamEventData>());
    }

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _cursorCallCount);
      if (CursorOverrides.TryGetValue((perspectiveName, streamId), out var cursor)) {
        return Task.FromResult<PerspectiveCursorInfo?>(cursor);
      }
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      Completions.Enqueue(completion);
      var count = Interlocked.Increment(ref _completionCount);
      foreach (var waiter in _completionWaiters) {
        if (count >= waiter.Key) {
          waiter.Value.TrySetResult();
        }
      }
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) {
      Failures.Enqueue(failure);
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class MiscEventStore : IEventStore {
    private readonly ConcurrentQueue<List<MessageEnvelope<IEvent>>> _deserializedResponses = new();

    public ConcurrentDictionary<Guid, List<MessageEnvelope<IEvent>>> StreamEnvelopes { get; } = new();

    public void EnqueueDeserialized(List<MessageEnvelope<IEvent>> envelopes) => _deserializedResponses.Enqueue(envelopes);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
      if (_deserializedResponses.TryDequeue(out var next)) {
        return [.. next];
      }
      return [];
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      if (StreamEnvelopes.TryGetValue(streamId, out var envelopes)) {
        return Task.FromResult(new List<MessageEnvelope<IEvent>>(envelopes));
      }
      return Task.FromResult(new List<MessageEnvelope<IEvent>>());
    }

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

  private sealed class MiscRegistry(string perspectiveName, IPerspectiveRunner runner, IReadOnlyList<Type> eventTypes) : IPerspectiveRunnerRegistry, IEventTypeProvider {
    public IPerspectiveRunner? GetRunner(string name, IServiceProvider serviceProvider) =>
      name == perspectiveName ? runner : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(
        perspectiveName,
        $"global::{perspectiveName}",
        "global::Test.MiscDeepModel",
        [.. eventTypes.Select(TypeNameFormatter.Format)])];

    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class MiscRunner : IPerspectiveRunner {
    private int _runCallCount;
    private int _runWithEventsCallCount;
    private int _rewindCallCount;

    public Task? RewindDelayUntil { get; init; }
    public int RunCallCount => Volatile.Read(ref _runCallCount);
    public int RunWithEventsCallCount => Volatile.Read(ref _runWithEventsCallCount);
    public int RewindCallCount => Volatile.Read(ref _rewindCallCount);
    public Type PerspectiveType => typeof(MiscRunner);

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _runCallCount);
      return Task.FromResult(_completed(streamId, perspectiveName, Guid.CreateVersion7()));
    }

    public Task<PerspectiveCursorCompletion> RunWithEventsAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
        IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _runWithEventsCallCount);
      var lastEventId = events.Count > 0 ? events[^1].MessageId.Value : Guid.Empty;
      return Task.FromResult(_completed(streamId, perspectiveName, lastEventId));
    }

    public async Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _rewindCallCount);
      if (RewindDelayUntil is not null) {
        await RewindDelayUntil.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
      }
      return _completed(streamId, perspectiveName, triggeringEventId);
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

  private sealed class RenewSignalingLocker : IPerspectiveStreamLocker {
    private readonly TaskCompletionSource _firstRenew = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _acquireCallCount;
    private int _renewCallCount;
    private int _releaseCallCount;

    public int AcquireCallCount => Volatile.Read(ref _acquireCallCount);
    public int RenewCallCount => Volatile.Read(ref _renewCallCount);
    public int ReleaseCallCount => Volatile.Read(ref _releaseCallCount);
    public string? LastReason { get; private set; }
    public Task FirstRenew => _firstRenew.Task;

    public Task<bool> TryAcquireLockAsync(Guid streamId, string perspectiveName, Guid instanceId, string reason, CancellationToken ct = default) {
      Interlocked.Increment(ref _acquireCallCount);
      LastReason = reason;
      return Task.FromResult(true);
    }

    public Task RenewLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) {
      Interlocked.Increment(ref _renewCallCount);
      _firstRenew.TrySetResult();
      return Task.CompletedTask;
    }

    public Task ReleaseLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) {
      Interlocked.Increment(ref _releaseCallCount);
      return Task.CompletedTask;
    }
  }

  private sealed class ThrowAtPostLifecycleInvoker : IReceptorInvoker {
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _throwWaiters = new();
    private int _throwCount;

    public int ThrowCount => Volatile.Read(ref _throwCount);

    public Task WaitForThrowsAsync(int count, TimeSpan timeout) {
      var tcs = _throwWaiters.GetOrAdd(count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      if (Volatile.Read(ref _throwCount) >= count) {
        tcs.TrySetResult();
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      if (stage == LifecycleStage.PostLifecycleInline) {
        var count = Interlocked.Increment(ref _throwCount);
        foreach (var waiter in _throwWaiters) {
          if (count >= waiter.Key) {
            waiter.Value.TrySetResult();
          }
        }
        throw new InvalidOperationException("post-lifecycle receptor failed");
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Drain channel whose reader signals when <c>WaitToReadAsync</c> is called AFTER at least
  /// one item has been read — i.e. when the worker has entered the sliding-window
  /// accumulation wait. Enables deterministic sequencing without polling.
  /// </summary>
  private sealed class SignalingDrainChannel : IPerspectiveDrainChannel {
    private readonly Channel<Guid> _inner = Channel.CreateUnbounded<Guid>();

    public SignalingDrainChannel() {
      ReaderImpl = new SignalingReader(_inner.Reader);
    }

    public SignalingReader ReaderImpl { get; }
    public ChannelReader<Guid> Reader => ReaderImpl;

    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      _inner.Writer.WriteAsync(streamId, cancellationToken);

    public bool TryWrite(Guid streamId) => _inner.Writer.TryWrite(streamId);

    internal sealed class SignalingReader(ChannelReader<Guid> inner) : ChannelReader<Guid> {
      private readonly TaskCompletionSource _windowWaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
      private int _readCount;

      public Task WindowWaitEntered => _windowWaitEntered.Task;

      public override bool TryRead(out Guid item) {
        var read = inner.TryRead(out item);
        if (read) {
          Interlocked.Increment(ref _readCount);
        }
        return read;
      }

      public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) {
        if (Volatile.Read(ref _readCount) > 0) {
          _windowWaitEntered.TrySetResult();
        }
        return inner.WaitToReadAsync(cancellationToken);
      }
    }
  }

  private sealed class RecordingCollectiveDispatcher : ICollectiveDispatcher {
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<CollectiveDispatchResult> DispatchAsync(ICollectiveEvent evt, Guid collectiveEventId, object dbContextOrSession, Func<CancellationToken, ValueTask>? onBatchApplied, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _callCount);
      return Task.FromResult(new CollectiveDispatchResult(1, 1));
    }
  }

  private sealed class StubSessionAccessor : ICollectiveSessionAccessor {
    public object GetSession(IServiceProvider scopedServiceProvider) => new object();
  }

  #endregion
}
