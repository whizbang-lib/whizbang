// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1676: each post-perspective lifecycle stage fires exactly once per collective event in a service, however
/// many collective handlers apply the event, however many batches the run spans, and whichever path received it.
/// </summary>
public partial class PerspectiveWorkerCollectiveSinkTests {

  private static readonly LifecycleStage[] _postPerspectiveStagesOnce = [
    LifecycleStage.PostAllPerspectivesDetached, LifecycleStage.PostAllPerspectivesInline,
    LifecycleStage.PostLifecycleDetached, LifecycleStage.PostLifecycleInline,
  ];

  /// <summary>
  /// The shape the issue reports: a collective event received from another service, in a service that runs the
  /// collective sink. The inbox received it and fired PostAllPerspectives and PostLifecycle at once, since no
  /// per-stream perspective lists a collective event type; the sink fired them again when its apply finished, so
  /// every hook ran twice for one event.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task CollectiveEvent_ReceivedThenApplied_FiresEachPostPerspectiveStageOnceInTheServiceAsync(CancellationToken cancellationToken) {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var invoker = new StageCountingInvoker();

    // Receive: the inbox dispatches the row in a service whose collective sink is registered.
    var inbox = _inboxWorker(invoker, collectiveEvent);
    await inbox.ProcessOneInnerAsync(_inboxRow(eventId, TypeNameFormatter.Format(typeof(TestCollectiveEvent))), CancellationToken.None);

    // Apply: the sink applies the stored event and runs it through the post-apply stages.
    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: new RecordingDispatcher(),
      receptorInvoker: invoker);
    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await invoker.SinkFinished(eventId).WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    foreach (var stage in _postPerspectiveStagesOnce) {
      await Assert.That(invoker.Count(eventId, stage)).IsEqualTo(1)
        .Because($"{stage} fires once per event in a service: the sink fires it, so the inbox must not fire it as well");
    }
  }

  /// <summary>
  /// The issue's first hypothesis, ruled out and locked: two collective handlers apply the same event to different
  /// models, and the hook still fires once, because the sink dispatches the event once and the dispatcher fans it out.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task CollectiveEvent_AppliedByTwoCollectiveHandlers_FiresEachPostPerspectiveStageOnceAsync(CancellationToken cancellationToken) {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var collectiveEvent = new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") };
    var invoker = new StageCountingInvoker();
    var firstModel = new CountingExecutor(typeof(FirstCollectiveModel));
    var secondModel = new CountingExecutor(typeof(SecondCollectiveModel));
    var handlers = new ServiceCollection()
      .AddSingleton(new FirstCollectiveHandler())
      .AddSingleton(new SecondCollectiveHandler())
      .BuildServiceProvider();
    var dispatcher = new CollectiveDispatcher(
      handlers,
      [
        _applyEntry(typeof(FirstCollectiveModel), typeof(FirstCollectiveHandler)),
        _applyEntry(typeof(SecondCollectiveModel), typeof(SecondCollectiveHandler)),
      ],
      [new TenantCollectiveScopeResolver()],
      [firstModel, secondModel]);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, collectiveEvent)] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      receptorInvoker: invoker);
    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await invoker.SinkFinished(eventId).WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(firstModel.Applied).IsEqualTo(1);
    await Assert.That(secondModel.Applied).IsEqualTo(1)
      .Because("both handlers apply the event, each to its own model");
    foreach (var stage in _postPerspectiveStagesOnce) {
      await Assert.That(invoker.Count(eventId, stage)).IsEqualTo(1)
        .Because($"{stage} fires once per event, not once per collective handler");
    }
  }

  /// <summary>
  /// The issue's second hypothesis, ruled out and locked: two batches of one run's collectives, the second claimed
  /// while the first batch's rows still sit in the queue awaiting their completion flush. The second run does not
  /// re-apply the first batch, so it does not fire the first batch's stages again.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task CollectiveEvents_InTwoBatchesOfOneRun_FireEachPostPerspectiveStageOncePerEventAsync(CancellationToken cancellationToken) {
    var streamId = TrackedGuid.New().Value;
    var firstBatch = new[] { _sinkWork(streamId), _sinkWork(streamId) };
    var secondBatch = new[] { _sinkWork(streamId), _sinkWork(streamId) };
    var eventIds = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
    var works = firstBatch.Concat(secondBatch).ToArray();
    var invoker = new StageCountingInvoker();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [.. firstBatch],
      eventStore: new EventStore {
        Deserialized = [.. eventIds.Select(id => _envelope(id, new TestCollectiveEvent { Scope = new TenantCollectiveScope("t-1") }))],
      },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: new RecordingDispatcher(),
      receptorInvoker: invoker);
    // Every row of the run is queued; the first batch's rows stay there until their completion flushes.
    coordinator.SinkQueue = [.. works.Select((w, i) => new CollectiveSinkQueueEntry(w.WorkId, eventIds[i], CommitSequence: i + 1))];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await Task.WhenAll(invoker.SinkFinished(eventIds[0]), invoker.SinkFinished(eventIds[1])).WaitAsync(cancellationToken);
    coordinator.OfferWork([.. secondBatch]);
    await Task.WhenAll(invoker.SinkFinished(eventIds[2]), invoker.SinkFinished(eventIds[3])).WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    foreach (var eventId in eventIds) {
      foreach (var stage in _postPerspectiveStagesOnce) {
        await Assert.That(invoker.Count(eventId, stage)).IsEqualTo(1)
          .Because($"{stage} fires once per event: the second batch must not fire it again for the first batch's events");
      }
    }
  }

  // ------------------------------------------------------------------------------------------

  private static CollectiveApplyEntry _applyEntry(Type modelType, Type handlerType) => new(
    ModelType: modelType,
    EventType: typeof(TestCollectiveEvent),
    HandlerType: handlerType,
    MethodName: "Apply",
    ScopeHandling: CollectiveScopeHandling.Framework,
    SpecKind: CollectiveSpecKind.Linq,
    Invoker: static (handler, _, _) => handler);

  private static InboxWork _inboxRow(Guid messageId, string messageType) => new() {
    MessageId = messageId,
    Envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonElement.Parse("{}"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
    },
    MessageType = messageType,
    StreamId = TrackedGuid.New().Value,
    PartitionNumber = 1,
    Attempts = 1,
    Status = MessageProcessingStatus.Stored,
    Flags = WorkBatchOptions.None,
  };

  /// <summary>The inbox of a service that runs the collective sink and folds the collective in no per-stream perspective.</summary>
  private static InboxDispatchWorker _inboxWorker(IReceptorInvoker invoker, object payload) {
    var services = new ServiceCollection();
    services.AddSingleton(invoker);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new SinkOnlyRegistry());
    services.AddSingleton<ICollectiveDispatcher>(new RecordingDispatcher());
    var sp = services.BuildServiceProvider();
    return new InboxDispatchWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new InstanceProvider(),
      inboxChannelWriter: new IdleInboxChannelWriter(),
      handlerCommitChannel: new DiscardingCommitChannel(),
      failureChannel: new DiscardingFailureChannel(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      options: Options.Create(new InboxDispatchWorkerOptions()),
      coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
      logger: NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: new FixedPayloadDeserializer(payload),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      receptorRegistry: new NoInboxStageReceptors(),
      discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance,
        new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider());
  }

  /// <summary>
  /// Counts each post-perspective stage per event. The inline stages are awaited by the path that fires them, and the
  /// sink fires every stage in line, so a count read after the paths finish is exact. Completes an event's signal
  /// when the sink fires its last stage.
  /// </summary>
  private sealed class StageCountingInvoker : IReceptorInvoker {
    private readonly ConcurrentQueue<(Guid EventId, LifecycleStage Stage)> _fired = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _sinkFinished = new();

    public int Count(Guid eventId, LifecycleStage stage) => _fired.Count(f => f.EventId == eventId && f.Stage == stage);

    public Task SinkFinished(Guid eventId) => _signal(eventId).Task;

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      var eventId = envelope.MessageId.Value;
      if (Array.IndexOf(_postPerspectiveStagesOnce, stage) >= 0) {
        _fired.Enqueue((eventId, stage));
      }
      if (stage == LifecycleStage.PostLifecycleInline && context is LifecycleExecutionContext { MessageSource: MessageSource.Local }) {
        _signal(eventId).TrySetResult();
      }
      return ValueTask.CompletedTask;
    }

    private TaskCompletionSource _signal(Guid eventId) =>
      _sinkFinished.GetOrAdd(eventId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
  }

  private sealed class FirstCollectiveModel;
  private sealed class SecondCollectiveModel;
  private sealed class FirstCollectiveHandler;
  private sealed class SecondCollectiveHandler;

  /// <summary>Stands in for a model's set-based apply; counts how often its model was applied.</summary>
  private sealed class CountingExecutor(Type modelType) : ICollectiveEventExecutor {
    private int _applied;
    public Type ModelType { get; } = modelType;
    public int Applied => Volatile.Read(ref _applied);
    public Task<int> ApplyAsync(
        CollectiveApplyEntry entry, object handlerInstance, ICollectiveEvent evt, ICollectiveScopeResolver resolver,
        object dbContextOrSession, Guid collectiveEventId, Func<CancellationToken, ValueTask>? onBatchApplied = null,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _applied);
      return Task.FromResult(1);
    }
  }

  /// <summary>A generated registry's view: per-stream perspectives fold other events; the collective is only in the sink's types.</summary>
  private sealed class SinkOnlyRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [
      new PerspectiveRegistrationInfo("Test.OtherPerspective", "global::Test.OtherPerspective", "global::Test.Model", ["Test.OtherEvent, Test"])
    ];
    public IReadOnlyList<Type> GetEventTypes() => [typeof(TestCollectiveEvent)];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class FixedPayloadDeserializer(object payload) : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => payload;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => payload;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => payload;
  }

  private sealed class NoInboxStageReceptors : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType) => true;
  }

  private sealed class IdleInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class DiscardingCommitChannel : IInboxHandlerCommitChannel {
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class DiscardingFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }
}
