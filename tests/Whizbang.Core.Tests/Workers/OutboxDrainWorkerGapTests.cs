using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage-gap tests for <see cref="OutboxDrainWorker"/> — branches not exercised by
/// <c>OutboxDrainWorkerTests</c>, <c>OutboxDrainWorkerLifecycleFailureTests</c>,
/// <c>DrainWorkerIdleSignalTests</c>, <c>PrePublishGateForensicPreservationTests</c>,
/// <c>PublishTimeoutTests</c>, <c>SecurityContextTimeoutTests</c>, or
/// <c>LifecycleExceptionInvariantTests</c>: the disabled/no-transport startup paths,
/// schema-gate cancellation, local-service-identity resolution + envelope injection,
/// deserialize-failure routing, PublishTimeoutSeconds=0 branches, DLQ move-failure
/// fallthrough + metrics tagging, per-stream drain-error isolation, Debug perf logging,
/// runtime-receptor-registry fallback, detached-lifecycle failure routing, constructor
/// guard clauses, and options defaults.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxDrainWorkerGapTests {

  // --- fakes ---

  private sealed class GapDrainChannel : IOutboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _channel =
      System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public System.Threading.Channels.ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

  private sealed class GapCompletionChannel : IOutboxCompletionChannel {
    public ConcurrentBag<Guid> AllIds { get; } = [];
    public int Target { get; set; } = 1;
    public TaskCompletionSource ReachedTarget { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) {
      AllIds.Add(id);
      if (AllIds.Count >= Target) {
        ReachedTarget.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class GapFailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public int Target { get; set; } = 1;
    public TaskCompletionSource ReachedTarget { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add(failure);
      if (All.Count >= Target) {
        ReachedTarget.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class GapPublishStrategy : IMessagePublishStrategy {
    public ConcurrentQueue<OutboxWork> Published { get; } = new();
    public int TargetCount { get; set; } = 1;
    public TaskCompletionSource ReachedCount { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Published.Enqueue(work);
      if (Published.Count >= TargetCount) {
        ReachedCount.TrySetResult();
      }
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      });
    }
  }

  /// <summary>Bulk-capable strategy that records batch calls; the singular path throws so a
  /// test fails loudly if the worker ever falls back to <c>PublishAsync</c>.</summary>
  private sealed class GapBulkPublishStrategy : IMessagePublishStrategy {
    public List<IReadOnlyList<OutboxWork>> BatchCalls { get; } = [];
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      throw new InvalidOperationException("PublishAsync must not be called on a bulk-capable strategy");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct) {
      lock (BatchCalls) { BatchCalls.Add(works); }
      var results = works.Select(w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      }).ToList();
      return Task.FromResult<IReadOnlyList<MessagePublishResult>>(results);
    }
  }

  /// <summary>Signals when a publish call is in flight, then hangs until the worker's
  /// cancellation token fires — used to drive the graceful-shutdown OCE rethrow path.</summary>
  private sealed class GapCancellableHangingPublishStrategy : IMessagePublishStrategy {
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<MessagePublishResult> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Started.TrySetResult();
      return await _never.Task.WaitAsync(ct);
    }
  }

  private sealed class GapServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "gap-test-svc";
    public string HostName => "gap-test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  /// <summary>
  /// Configurable coordinator: consumes rows on fetch (mimics post-completion DELETE),
  /// resolves a configurable local service id (or throws), and can throw the fetch for
  /// designated stream_ids to exercise the per-stream drain-error isolation branch.
  /// </summary>
  private sealed class GapWorkCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<OutboxBatchRow>> RowsByStream { get; } = [];
    public HashSet<Guid> ThrowOnFetchStreams { get; } = [];
    public Guid LocalServiceId { get; set; }
    public bool ThrowOnLocalServiceIdLookup { get; set; }
    public int FetchCalls;

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      ThrowOnLocalServiceIdLookup
        ? Task.FromException<Guid>(new InvalidOperationException("wh_service_config unavailable"))
        : Task.FromResult(LocalServiceId);

    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref FetchCalls);
      var result = new List<OutboxBatchRow>();
      lock (RowsByStream) {
        foreach (var sid in streamIds) {
          if (ThrowOnFetchStreams.Contains(sid)) {
            throw new InvalidOperationException("fetch_outbox_batch failed for stream");
          }
          if (RowsByStream.TryGetValue(sid, out var rows)) {
            var taken = rows.Take(maxPerStream).ToList();
            result.AddRange(taken);
            rows.RemoveRange(0, taken.Count);
          }
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class GapLifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => jsonBytes;
    public object DeserializeFromJsonElement(JsonElement payload, string messageTypeName) => payload;
  }

  private sealed class GapCapturingReceptorInvoker : IReceptorInvoker {
    private readonly List<LifecycleStage> _stages = [];
    private readonly Lock _lock = new();
    public TaskCompletionSource PostInlineSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<LifecycleStage> Stages {
      get { lock (_lock) { return [.. _stages]; } }
    }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _stages.Add(stage);
      }
      if (stage == LifecycleStage.PostOutboxInline) {
        PostInlineSeen.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class GapThrowingReceptorInvoker(Exception toThrow) : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      throw toThrow;
  }

  private sealed class GapNeverHasReceptorsQuery : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType) => false;
  }

  /// <summary>Runtime registry that reports one receptor for every type/stage — drives the
  /// <c>_runtimeHasReceptors</c> fallback when the compile-time query registry says no.</summary>
  private sealed class GapRuntimeReceptorRegistry : IReceptorRegistry {
    private static readonly ReceptorInfo _info = new(
      typeof(object),
      "gap-runtime-receptor",
      (_, _, _, _, _) => ValueTask.FromResult<object?>(null));
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [_info];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private sealed class GapGenerationProvider : IGenerationProvider {
    public string GetGeneration() => "gap-test-gen";
  }

  private sealed class GapCapturingDeadLetterStore : IDeadLetterStore {
    public ConcurrentBag<Guid> MovedSourceIds { get; } = [];
    public TaskCompletionSource FirstMove { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText,
        Guid instanceId, string generation, CancellationToken ct = default) {
      MovedSourceIds.Add(sourceId);
      FirstMove.TrySetResult();
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class GapThrowingDeadLetterStore : IDeadLetterStore {
    public int Calls;
    public Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText,
        Guid instanceId, string generation, CancellationToken ct = default) {
      Interlocked.Increment(ref Calls);
      return Task.FromException<Guid?>(new InvalidOperationException("wh_dead_letters unavailable"));
    }
  }

  /// <summary>Always-enabled logger that captures formatted messages — used to cover the
  /// <c>IsEnabled(LogLevel.Debug)</c>-guarded perf branch and the no-transport warning.</summary>
  private sealed class GapCapturingLogger : ILogger<OutboxDrainWorker> {
    private readonly ConcurrentQueue<string> _messages = new();
    public IReadOnlyList<string> Messages => [.. _messages];

    /// <summary>Completes once the no-transport warning has been captured, so tests can
    /// await the background ExecuteAsync reaching that branch instead of racing StopAsync.</summary>
    public TaskCompletionSource NoTransportWarningLogged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      _messages.Enqueue(message);
      if (message.Contains("no IMessagePublishStrategy registered", StringComparison.Ordinal)) {
        NoTransportWarningLogged.TrySetResult();
      }
    }
  }

  // --- helpers ---

  private static readonly JsonSerializerOptions _jsonOpts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static OutboxBatchRow _row(
      Guid messageId, Guid streamId, int attempts = 0,
      Guid? originServiceId = null, long? originCommitSequence = null, long? commitSequence = null) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup: no JsonTypeInfo for MessageEnvelope<JsonElement>");
    var envelopeJson = JsonSerializer.Serialize(envelope, typeInfo);
    return new OutboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      Destination = "gap-test-topic",
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      EventData = envelopeJson,
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = attempts,
      PartitionNumber = 0,
      IsEvent = false,
      CommitSequence = commitSequence,
      OriginServiceId = originServiceId,
      OriginCommitSequence = originCommitSequence,
    };
  }

  private static OutboxBatchRow _badRow(Guid messageId, Guid streamId, string eventData) =>
    new() {
      MessageId = messageId,
      StreamId = streamId,
      Destination = "gap-test-topic",
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      EventData = eventData,
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = 0,
      PartitionNumber = 0,
      IsEvent = false,
    };

  private static OutboxDrainWorker _worker(
      IServiceProvider sp,
      GapDrainChannel drainChannel,
      GapCompletionChannel completion,
      GapFailureChannel failure,
      OutboxDrainWorkerOptions options,
      IMessagePublishStrategy? publish,
      ILogger<OutboxDrainWorker>? logger = null,
      ILifecycleMessageDeserializer? deserializer = null,
      IReceptorRegistryQuery? registryQuery = null,
      IReceptorRegistry? runtimeRegistry = null,
      IDeadLetterStore? deadLetterStore = null,
      IGenerationProvider? generationProvider = null,
      DeadLetterMetrics? dlqMetrics = null,
      ISchemaReadyGate? gate = null) {
    if (gate is null) {
      var readyGate = new SchemaReadyGate();
      readyGate.MarkReady();
      gate = readyGate;
    }
    return new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new GapServiceInstanceProvider(),
      drainChannel,
      completion,
      failure,
      gate,
      Options.Create(options),
      _jsonOpts,
      logger ?? NullLogger<OutboxDrainWorker>.Instance,
      publish,
      lifecycleMessageDeserializer: deserializer,
      receptorRegistry: registryQuery,
      runtimeReceptorRegistry: runtimeRegistry,
      deadLetterStore: deadLetterStore,
      generationProvider: generationProvider,
      dlqMetrics: dlqMetrics);
  }

  private static ServiceProvider _sp(GapWorkCoordinator coord, IReceptorInvoker? invoker = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (invoker is not null) {
      services.AddSingleton(invoker);
    }
    return services.BuildServiceProvider();
  }

  // --- tests ---

  /// <summary>
  /// Killswitch branch: Enabled=false with a transport wired. The worker must park on the
  /// infinite idle wait without ever consuming the drain channel or touching the coordinator,
  /// and exit cleanly (RanToCompletion) when stopped.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_Disabled_NeverFetches_StopsCleanlyAsync() {
    var coord = new GapWorkCoordinator();
    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapPublishStrategy();
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = false }, publish);

    await drainChannel.WriteAsync((Guid)TrackedGuid.NewMedo());
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    var execTask = worker.ExecuteTask;
    await Assert.That(execTask is not null).IsTrue();
    await Assert.That(execTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the disabled branch must swallow the shutdown OperationCanceledException and return normally");
    await Assert.That(coord.FetchCalls).IsEqualTo(0)
      .Because("a disabled drainer must never call FetchOutboxBatchAsync even with stream_ids pending");
    await Assert.That(publish.Published).IsEmpty();
  }

  /// <summary>
  /// No-transport branch: when no <see cref="IMessagePublishStrategy"/> is registered the
  /// worker logs the LogNoTransportRegistered warning, degrades to the disabled idle wait,
  /// and never fetches.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_NoPublishStrategy_LogsWarning_NeverFetchesAsync() {
    var coord = new GapWorkCoordinator();
    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var logger = new GapCapturingLogger();
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish: null, logger: logger);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Wait for ExecuteAsync to reach the no-transport branch before shutting down; otherwise
    // cancellation can race the background task before it logs the warning.
    await logger.NoTransportWarningLogged.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(coord.FetchCalls).IsEqualTo(0);
    var sawNoTransportWarning = logger.Messages.Any(m => m.Contains("no IMessagePublishStrategy registered"));
    await Assert.That(sawNoTransportWarning).IsTrue()
      .Because("the null-strategy branch must emit the LogNoTransportRegistered warning so operators can diagnose a silent drainer");
  }

  /// <summary>
  /// Schema-gate cancellation branch: the gate never becomes ready; shutdown fires while
  /// waiting. The OperationCanceledException from WaitForReadyAsync must be caught and the
  /// worker must return before resolving the local service id or fetching anything.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_SchemaGateCancelled_ReturnsWithoutFetchingAsync() {
    var coord = new GapWorkCoordinator();
    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapPublishStrategy();
    var sp = _sp(coord);
    var neverReadyGate = new SchemaReadyGate();  // MarkReady intentionally NOT called

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish, gate: neverReadyGate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    var execTask = worker.ExecuteTask;
    await Assert.That(execTask is not null).IsTrue();
    // Graceful shutdown: the worker either catches the OCE and returns (RanToCompletion) or,
    // when StopAsync's token is already cancelled before ExecuteAsync's body runs, the async
    // state machine surfaces the cancellation (Canceled). Both are clean; only a fault is a bug.
    await Assert.That(execTask!.IsCompleted).IsTrue();
    await Assert.That(execTask.IsFaulted).IsFalse()
      .Because("cancellation during the schema-ready wait must shut down gracefully, not fault the worker");
    await Assert.That(coord.FetchCalls).IsEqualTo(0);
  }

  /// <summary>
  /// Slice 26.6b happy path: the coordinator resolves a local service id at startup, and a
  /// locally-originated row (OriginServiceId null) publishes with the envelope's
  /// SourceServiceId set to the local id and SourceCommitSequence taken from the JOINed
  /// wh_event_store.commit_sequence.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_LocalRow_InjectsLocalServiceIdAndCommitSequenceAsync() {
    var localServiceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator { LocalServiceId = localServiceId };
    coord.RowsByStream[streamId] = [_row(msgId, streamId, commitSequence: 42L)];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapPublishStrategy { TargetCount = 1 };
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await publish.ReachedCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(1);
    _ = publish.Published.TryDequeue(out var work);
    var concrete = work!.Envelope as MessageEnvelope<JsonElement>;
    await Assert.That(concrete).IsNotNull()
      .Because("the publish path deserializes into the concrete MessageEnvelope<JsonElement> and re-stamps identity");
    await Assert.That(concrete!.SourceServiceId).IsEqualTo(localServiceId)
      .Because("locally-originated rows must COALESCE SourceServiceId to the local wh_service_config identity");
    await Assert.That(concrete.SourceCommitSequence).IsEqualTo(42L);
  }

  /// <summary>
  /// Slice 26.6b forwarded path: a 1:1 forwarded row carries origin_service_id +
  /// origin_commit_sequence. The publisher must preserve the ORIGIN identity, not overwrite
  /// it with the local service id.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_ForwardedRow_PreservesOriginIdentityAsync() {
    var localServiceId = (Guid)TrackedGuid.NewMedo();
    var originServiceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator { LocalServiceId = localServiceId };
    coord.RowsByStream[streamId] =
      [_row(msgId, streamId, originServiceId: originServiceId, originCommitSequence: 7L, commitSequence: 99L)];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapPublishStrategy { TargetCount = 1 };
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await publish.ReachedCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    _ = publish.Published.TryDequeue(out var work);
    var concrete = work!.Envelope as MessageEnvelope<JsonElement>;
    await Assert.That(concrete is not null).IsTrue();
    await Assert.That(concrete!.SourceServiceId).IsEqualTo(originServiceId)
      .Because("forwarded rows must keep the origin service identity so downstream cursor comparison stays per-source");
    await Assert.That(concrete.SourceCommitSequence).IsEqualTo(7L)
      .Because("origin_commit_sequence wins over the local commit_sequence for forwarded rows");
  }

  /// <summary>
  /// Startup identity-lookup failure branch: GetLocalServiceIdAsync throwing must NOT kill
  /// the worker — it falls back to Guid.Empty (downstream SQL COALESCEs) and keeps draining.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_LocalServiceIdLookupThrows_FallsBackToGuidEmpty_StillPublishesAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator { ThrowOnLocalServiceIdLookup = true };
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapPublishStrategy { TargetCount = 1 };
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await publish.ReachedCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    _ = publish.Published.TryDequeue(out var work);
    var concrete = work!.Envelope as MessageEnvelope<JsonElement>;
    await Assert.That(concrete is not null).IsTrue();
    await Assert.That(concrete!.SourceServiceId).IsEqualTo(Guid.Empty)
      .Because("identity lookup failure is best-effort — Guid.Empty lets the consumer-side SQL trigger COALESCE to its own local service");
    await Assert.That(concrete.SourceCommitSequence).IsEqualTo(0L)
      .Because("with no commit sequence on the row, the ?? 0L fallback applies");
    await Assert.That(completion.AllIds).Contains(msgId);
  }

  /// <summary>
  /// PublishTimeoutSeconds=0 singular branch: the legacy no-timeout path awaits the publish
  /// task directly (no WaitAsync wrapper). Publish + completion must still work.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_PublishTimeoutZero_SingularPath_PublishesAndCompletesAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapPublishStrategy { TargetCount = 1 };
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true, PublishTimeoutSeconds = 0 }, publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await completion.ReachedTarget.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(completion.AllIds).Contains(msgId)
      .Because("PublishTimeoutSeconds=0 disables the WaitAsync timeout wrapper but must not change the success path");
    await Assert.That(failure.All).IsEmpty();
  }

  /// <summary>
  /// PublishTimeoutSeconds=0 bulk branch: same no-timeout await on the PublishBatchAsync path.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_PublishTimeoutZero_BulkPath_PublishesAndCompletesAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapBulkPublishStrategy();
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true, PublishTimeoutSeconds = 0 }, publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await completion.ReachedTarget.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.BatchCalls.Count).IsEqualTo(1);
    await Assert.That(completion.AllIds).Contains(msgId);
    await Assert.That(failure.All).IsEmpty();
  }

  /// <summary>
  /// Singular-path envelope deserialize failure: rows whose event_data is "null" (valid JSON,
  /// null envelope → the ?? throw branch) or malformed JSON (JsonException branch) must both
  /// route to the failure channel without any publish or completion.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_UndeserializableRows_SingularPath_RouteToFailureChannelAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var nullEnvelopeId = (Guid)TrackedGuid.NewMedo();
    var malformedId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator();
    coord.RowsByStream[streamId] = [
      _badRow(nullEnvelopeId, streamId, eventData: "null"),
      _badRow(malformedId, streamId, eventData: "{{{ not json"),
    ];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel { Target = 2 };
    var publish = new GapPublishStrategy();
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await failure.ReachedTarget.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(failure.All.Count).IsEqualTo(2)
      .Because("both the null-envelope and malformed-JSON deserialize failures must enqueue a MessageFailure");
    var failedIds = failure.All.Select(f => f.MessageId).ToList();
    await Assert.That(failedIds).Contains(nullEnvelopeId);
    await Assert.That(failedIds).Contains(malformedId);
    await Assert.That(publish.Published).IsEmpty()
      .Because("undeserializable rows must never reach the transport");
    await Assert.That(completion.AllIds).IsEmpty();
  }

  /// <summary>
  /// Bulk-path deserialize failure with survivors: the bad row routes to the failure channel
  /// and is EXCLUDED from the batch; the good row still ships in the (now smaller) batch and
  /// completes.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_BulkPath_BadRowExcluded_GoodRowStillPublishesAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var badId = (Guid)TrackedGuid.NewMedo();
    var goodId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator();
    coord.RowsByStream[streamId] = [
      _badRow(badId, streamId, eventData: "null"),
      _row(goodId, streamId),
    ];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapBulkPublishStrategy();
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await completion.ReachedTarget.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.BatchCalls.Count).IsEqualTo(1);
    await Assert.That(publish.BatchCalls[0].Count).IsEqualTo(1)
      .Because("the undeserializable row must be excluded from the PublishBatchAsync payload");
    await Assert.That(publish.BatchCalls[0][0].MessageId).IsEqualTo(goodId);
    await Assert.That(failure.All.Single().MessageId).IsEqualTo(badId);
    await Assert.That(completion.AllIds).Contains(goodId);
  }

  /// <summary>
  /// Bulk-path "no eligible works" branch: when EVERY row fails deserialization the worker
  /// must return before calling PublishBatchAsync at all.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_BulkPath_AllRowsBad_SkipsPublishBatchEntirelyAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var badA = (Guid)TrackedGuid.NewMedo();
    var badB = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator();
    coord.RowsByStream[streamId] = [
      _badRow(badA, streamId, eventData: "null"),
      _badRow(badB, streamId, eventData: "not json at all"),
    ];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel { Target = 2 };
    var publish = new GapBulkPublishStrategy();
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish);
    var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idle.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await failure.ReachedTarget.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.BatchCalls).IsEmpty()
      .Because("with zero surviving works the bulk path must short-circuit before PublishBatchAsync");
    await Assert.That(failure.All.Count).IsEqualTo(2);
    await Assert.That(completion.AllIds).IsEmpty();
  }

  /// <summary>
  /// DLQ gate success with metrics wired: attempts over the cap move the row via
  /// IDeadLetterStore.MoveAsync, tag the DeadLetterMetrics.Added counter (non-null-metrics
  /// branch), and skip publish + completion entirely.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_DlqGateFires_WithMetricsWired_SkipsPublishAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId, attempts: 11)];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapPublishStrategy();
    var dlqStore = new GapCapturingDeadLetterStore();
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true, MaxOutboxAttempts = 10 }, publish,
      deadLetterStore: dlqStore,
      generationProvider: new GapGenerationProvider(),
      dlqMetrics: new DeadLetterMetrics(new WhizbangMetrics()));
    var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idle.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await dlqStore.FirstMove.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(dlqStore.MovedSourceIds).Contains(msgId);
    await Assert.That(dlqStore.MovedSourceIds.Count).IsEqualTo(1);
    await Assert.That(publish.Published).IsEmpty()
      .Because("a dead-lettered row is deleted by the move and must never reach the publish path");
    await Assert.That(completion.AllIds).IsEmpty();
  }

  /// <summary>
  /// DLQ move-failure fallthrough: when MoveAsync throws (transient DB fault) the row must
  /// fall through to a normal publish attempt instead of getting stuck — the next failure
  /// cycle retries the DLQ move.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_DlqMoveThrows_FallsThroughToPublishAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId, attempts: 11)];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapPublishStrategy { TargetCount = 1 };
    var dlqStore = new GapThrowingDeadLetterStore();
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true, MaxOutboxAttempts = 10 }, publish,
      deadLetterStore: dlqStore,
      generationProvider: new GapGenerationProvider());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await publish.ReachedCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await completion.ReachedTarget.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(dlqStore.Calls).IsEqualTo(1)
      .Because("the gate must have attempted the DLQ move before falling through");
    await Assert.That(publish.Published.Count).IsEqualTo(1)
      .Because("a failed DLQ move must NOT strand the row — attempting delivery beats leaving it stuck in claim_orphaned_outbox");
    await Assert.That(completion.AllIds).Contains(msgId);
  }

  /// <summary>
  /// Per-stream drain-error isolation: a coordinator fetch that throws for one stream is
  /// caught + logged inside the Parallel.ForEachAsync body; other streams keep draining and
  /// the worker survives.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_FetchThrowsForOneStream_OtherStreamStillDrainsAsync() {
    var brokenStream = (Guid)TrackedGuid.NewMedo();
    var healthyStream = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator();
    coord.ThrowOnFetchStreams.Add(brokenStream);
    coord.RowsByStream[healthyStream] = [_row(msgId, healthyStream)];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapPublishStrategy { TargetCount = 1 };
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(brokenStream);
    await drainChannel.WriteAsync(healthyStream);
    await publish.ReachedCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(completion.AllIds).Contains(msgId)
      .Because("a drain exception on one stream must be isolated — sibling streams in the batch still publish");
    var execTask = worker.ExecuteTask;
    await Assert.That(execTask is not null).IsTrue();
    await Assert.That(execTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the drain error is logged, not propagated — the worker loop survives");
  }

  /// <summary>
  /// Debug perf-instrumentation branch: with a Debug-enabled logger and a drain that
  /// publishes ≥5 rows, the PERF summary line must be emitted on drain exit.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_DebugLoggerAndBigDrain_EmitsPerfSummaryLineAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgIds = Enumerable.Range(0, 6).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new GapWorkCoordinator();
    coord.RowsByStream[streamId] = [.. msgIds.Select(id => _row(id, streamId))];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel { Target = msgIds.Length };
    var failure = new GapFailureChannel();
    var publish = new GapPublishStrategy { TargetCount = msgIds.Length };
    var logger = new GapCapturingLogger();
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish, logger: logger);
    var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idle.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await completion.ReachedTarget.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    var sawPerfLine = logger.Messages.Any(m => m.Contains("PERF OutboxDrain"));
    await Assert.That(sawPerfLine).IsTrue()
      .Because("with Debug enabled and ≥5 published rows, _logPerfIfInteresting must emit the PERF summary for operators");
  }

  /// <summary>
  /// Runtime-registry fallback: the compile-time query registry reports NO receptors for the
  /// gated Outbox stages, but the runtime IReceptorRegistry has a dynamically-registered one.
  /// The _runtimeHasReceptors fallback must keep the lifecycle stages firing.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_QueryRegistrySaysNo_RuntimeRegistryFallback_FiresLifecycleAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapPublishStrategy { TargetCount = 1 };
    var invoker = new GapCapturingReceptorInvoker();
    var sp = _sp(coord, invoker);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish,
      deserializer: new GapLifecycleDeserializer(),
      registryQuery: new GapNeverHasReceptorsQuery(),
      runtimeRegistry: new GapRuntimeReceptorRegistry());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await invoker.PostInlineSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    var stages = invoker.Stages;
    await Assert.That(stages).Contains(LifecycleStage.PreOutboxInline)
      .Because("runtime-registered receptors must fire PreOutboxInline even when the source-generated query registry has no entry");
    await Assert.That(stages).Contains(LifecycleStage.PostOutboxInline);
    await Assert.That(publish.Published.Count).IsEqualTo(1);
  }

  /// <summary>
  /// Detached-lifecycle failure routing: a receptor that throws on the DETACHED stage (the
  /// fire-and-forget Task.Run branch) must still enqueue a failure record with the full
  /// exception text — the production forensic invariant, detached-side.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_DetachedLifecycleThrows_EnqueuesFailureWithStageNameAsync() {
    var failure = new GapFailureChannel();
    var thrown = new InvalidOperationException("gap-test detached receptor fault");
    var coord = new GapWorkCoordinator();
    // Detached path resolves ITS OWN invoker from a fresh DI scope — register the thrower there.
    var sp = _sp(coord, new GapThrowingReceptorInvoker(thrown));
    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var inlineInvoker = new GapCapturingReceptorInvoker();

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, new GapPublishStrategy());

    var messageId = (Guid)TrackedGuid.NewMedo();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var work = new OutboxWork {
      MessageId = messageId,
      Destination = "gap-test-topic",
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      Envelope = envelope,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
    };

    await worker.InvokeOutboxLifecycleStageAsync(
      work, envelope, inlineInvoker,
      LifecycleStage.PreOutboxDetached, LifecycleStage.PreOutboxInline,
      "PreOutbox", CancellationToken.None);

    // The detached branch runs on a background Task.Run — wait on the failure enqueue signal.
    await failure.ReachedTarget.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var captured = failure.All.Single();
    await Assert.That(captured.MessageId).IsEqualTo(messageId);
    await Assert.That(captured.Error).Contains("PreOutboxDetached")
      .Because("the stage name must surface so operators can distinguish detached from inline faults in wh_outbox.error");
    await Assert.That(captured.Error).Contains("gap-test detached receptor fault")
      .Because("the full exception text must reach the failure record for fingerprinting");
    await Assert.That(inlineInvoker.Stages).Contains(LifecycleStage.PreOutboxInline)
      .Because("the inline stage uses the caller-provided invoker and must still run despite the detached fault");
  }

  /// <summary>
  /// Graceful-shutdown rethrow branch: cancellation firing while a publish is in flight must
  /// propagate as OperationCanceledException (NOT get converted into a failure record) and
  /// end the worker loop cleanly.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_CancelledDuringPublish_NoFailureRecord_StopsCleanlyAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new GapWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var publish = new GapCancellableHangingPublishStrategy();
    var sp = _sp(coord);

    var worker = _worker(sp, drainChannel, completion, failure,
      new OutboxDrainWorkerOptions { Enabled = true }, publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    await publish.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    var execTask = worker.ExecuteTask;
    await Assert.That(execTask is not null).IsTrue();
    await Assert.That(execTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("shutdown cancellation is caught by the outer OperationCanceledException handler, not surfaced as a fault");
    await Assert.That(failure.All).IsEmpty()
      .Because("graceful shutdown must NOT masquerade as a publish failure — the row stays leased for the next claim cycle");
    await Assert.That(completion.AllIds).IsEmpty();
  }

  /// <summary>
  /// Constructor guard clauses: every required dependency null-checks with the right
  /// parameter name.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_Constructor_NullRequiredDependency_ThrowsWithParamNameAsync() {
    var coord = new GapWorkCoordinator();
    var sp = _sp(coord);
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var instance = new GapServiceInstanceProvider();
    var drainChannel = new GapDrainChannel();
    var completion = new GapCompletionChannel();
    var failure = new GapFailureChannel();
    var gate = new SchemaReadyGate();
    var options = Options.Create(new OutboxDrainWorkerOptions());
    var logger = NullLogger<OutboxDrainWorker>.Instance;

    var ex1 = await Assert.That(() =>
      new OutboxDrainWorker(null!, instance, drainChannel, completion, failure, gate, options, _jsonOpts, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(ex1!.ParamName).IsEqualTo("scopeFactory");

    var ex2 = await Assert.That(() =>
      new OutboxDrainWorker(scopeFactory, null!, drainChannel, completion, failure, gate, options, _jsonOpts, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(ex2!.ParamName).IsEqualTo("instanceProvider");

    var ex3 = await Assert.That(() =>
      new OutboxDrainWorker(scopeFactory, instance, null!, completion, failure, gate, options, _jsonOpts, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(ex3!.ParamName).IsEqualTo("drainChannel");

    var ex4 = await Assert.That(() =>
      new OutboxDrainWorker(scopeFactory, instance, drainChannel, null!, failure, gate, options, _jsonOpts, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(ex4!.ParamName).IsEqualTo("completionChannel");

    var ex5 = await Assert.That(() =>
      new OutboxDrainWorker(scopeFactory, instance, drainChannel, completion, null!, gate, options, _jsonOpts, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(ex5!.ParamName).IsEqualTo("failureChannel");

    var ex6 = await Assert.That(() =>
      new OutboxDrainWorker(scopeFactory, instance, drainChannel, completion, failure, null!, options, _jsonOpts, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(ex6!.ParamName).IsEqualTo("schemaReadyGate");

    var ex7 = await Assert.That(() =>
      new OutboxDrainWorker(scopeFactory, instance, drainChannel, completion, failure, gate, null!, _jsonOpts, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(ex7!.ParamName).IsEqualTo("options");

    var ex8 = await Assert.That(() =>
      new OutboxDrainWorker(scopeFactory, instance, drainChannel, completion, failure, gate, options, null!, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(ex8!.ParamName).IsEqualTo("jsonOptions");

    var ex9 = await Assert.That(() =>
      new OutboxDrainWorker(scopeFactory, instance, drainChannel, completion, failure, gate, options, _jsonOpts, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(ex9!.ParamName).IsEqualTo("logger");
  }

  /// <summary>
  /// Options defaults not already locked by V502DefaultsTests (which owns MaxOutboxAttempts):
  /// Enabled, MaxPerStream, MaxConcurrentStreams, SecurityContextTimeoutSeconds,
  /// PublishTimeoutSeconds, and a non-null Batcher policy.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorkerOptions_Defaults_MatchDocumentedValuesAsync() {
    var options = new OutboxDrainWorkerOptions();

    await Assert.That(options.Enabled).IsTrue()
      .Because("Phase H step 4b made the drain worker the active outbox publish path by default");
    await Assert.That(options.MaxPerStream).IsEqualTo(100);
    await Assert.That(options.MaxConcurrentStreams).IsEqualTo(16);
    await Assert.That(options.SecurityContextTimeoutSeconds).IsEqualTo(10);
    await Assert.That(options.PublishTimeoutSeconds).IsEqualTo(60)
      .Because("the transport publish timeout defaults on (60s) so every consumer gets stuck-SDK protection out of the box");
    await Assert.That(options.Batcher).IsNotNull();
  }
}
