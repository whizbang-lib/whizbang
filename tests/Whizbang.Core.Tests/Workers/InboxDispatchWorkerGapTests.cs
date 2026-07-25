using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;
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
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage-gap tests for <see cref="InboxDispatchWorker"/> — locks the branches the
/// existing functional suites (InboxDispatchWorkerTests / ...LifecycleGatingTests /
/// ...ParallelismTests / ...SchedulingTests / InboxPrePublishGateForensicPreservationTests /
/// SecurityContextTimeoutTests / LifecycleExceptionInvariantTests) do not reach:
/// constructor null guards, schema-gate shutdown cancellation, the gate-derived
/// partition-count clamp log, DLQ-store failure fallbacks (max-attempts + composite),
/// the composite legacy terminal path without a store, DLQ metrics tag emission,
/// deserialize-failure continuation, the in-worker discard-policy short-circuit,
/// detached-lifecycle exception routing, the detached-scope missing-invoker early
/// return, and InboxMetrics dispatch-duration recording. The Debug-gated PERF log
/// branch (&gt;100 ms wall clock via raw Stopwatch.GetTimestamp) is intentionally NOT
/// tested: it cannot be crossed deterministically without a real delay, which the
/// no-timing-tests rule forbids.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerGapTests {

  // ============================================================
  // Test fakes
  // ============================================================

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 7;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class FakeInboxChannelWriter : IInboxChannelWriter {
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

  private sealed class FakeHandlerCommitChannel : IInboxHandlerCommitChannel {
    public TaskCompletionSource<HandlerCommitRequest> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<HandlerCommitRequest> All { get; } = [];
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) {
      All.Add(request);
      First.TrySetResult(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public TaskCompletionSource<MessageFailure> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<(WorkCategory Category, MessageFailure Failure)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add((category, failure));
      First.TrySetResult(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeGenerationProvider : IGenerationProvider {
    public string GetGeneration() => "test-generation";
  }

  /// <summary>DLQ store whose MoveAsync always throws — drives the best-effort fallback branches.</summary>
  private sealed class ThrowingDeadLetterStore : IDeadLetterStore {
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default)
      => Task.FromException<Guid?>(new InvalidOperationException("simulated wh_dead_letters outage"));
  }

  /// <summary>DLQ store that succeeds and records the call.</summary>
  private sealed class CapturingDeadLetterStore : IDeadLetterStore {
    public ConcurrentBag<(string SourceTable, Guid SourceId, MessageFailureReason Reason)> Moves { get; } = [];
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      Moves.Add((sourceTable, sourceId, failureReason));
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  /// <summary>Deserializer that always throws — drives _resolveTypedEnvelope's catch branch.</summary>
  private sealed class ThrowingDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName)
      => throw new InvalidOperationException("simulated deserialize fault");
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope)
      => throw new InvalidOperationException("simulated deserialize fault");
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName)
      => throw new InvalidOperationException("simulated deserialize fault");
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName)
      => throw new InvalidOperationException("simulated deserialize fault");
  }

  // Returns a fixed composite so _resolveTypedEnvelope yields a typed ICompositeEvent payload.
  private sealed class FakeCompositeDeserializer(ICompositeEvent composite) : ILifecycleMessageDeserializer {
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => composite;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => composite;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => composite;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => composite;
  }

  // Minimal serializer for composite fan-out plumbing (mirrors InboxDispatchWorkerTests).
  private sealed class FakeEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var aqn = envelope.Payload!.GetType().AssemblyQualifiedName!;
      var jsonEnv = new MessageEnvelope<JsonElement> {
        DispatchContext = envelope.DispatchContext,
        MessageId = envelope.MessageId,
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = envelope.Hops?.ToList() ?? [],
      };
      return new SerializedEnvelope(jsonEnv, $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core", aqn);
    }
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed record InnerImportEvent(string Id) : IEvent;

  private sealed class OverCapComposite(int count) : ICompositeEvent {
    public int MaxInnerEventsAllowed => 1;
    public IEnumerable<IMessage> InnerEvents => Enumerable.Range(0, count).Select(i => (IMessage)new InnerImportEvent($"J-{i}"));
  }

  /// <summary>
  /// Discard policy that always says "skip" for the inbox gate and records the
  /// RecordDiscard call so tests can assert the telemetry contract.
  /// </summary>
  private sealed class AlwaysDiscardInboxPolicy : IMessageDiscardPolicy {
    public ConcurrentBag<(MessageDiscardGate Gate, MessageDiscardDecision Decision, string PayloadClrType, IReadOnlyDictionary<string, object?>? Tags)> Recorded { get; } = [];
    public MessageDiscardDecision EvaluateReceive(string payloadClrType, string topic, string subscription)
      => new(ShouldDiscard: false, MessageDiscardReason.None);
    public MessageDiscardDecision EvaluateInbox(string payloadClrType)
      => new(ShouldDiscard: true, MessageDiscardReason.RegistryChanged, "no consumer registered anymore");
    public MessageDiscardDecision EvaluateOutbox(string payloadClrType)
      => new(ShouldDiscard: false, MessageDiscardReason.None);
    public void RecordDiscard(MessageDiscardGate gate, MessageDiscardDecision decision,
        string payloadClrType, IReadOnlyDictionary<string, object?>? additionalTags = null) {
      Recorded.Add((gate, decision, payloadClrType, additionalTags));
    }
  }

  /// <summary>Records every lifecycle stage invocation; never throws.</summary>
  private sealed class CapturingReceptorInvoker : IReceptorInvoker {
    public ConcurrentBag<LifecycleStage> Invocations { get; } = [];
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      Invocations.Add(stage);
      return ValueTask.CompletedTask;
    }
    public bool HasStage(LifecycleStage stage) => Invocations.Any(s => s == stage);
  }

  /// <summary>Throws only for the configured stage; no-ops otherwise.</summary>
  private sealed class StageThrowingInvoker(LifecycleStage throwingStage, Exception toThrow) : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      if (stage == throwingStage) {
        throw toThrow;
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Thread-safe recording logger with IsEnabled=true for all levels (so Debug-gated
  /// branches execute).
  /// </summary>
  private sealed class RecordingLogger<T> : ILogger<T> {
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Enqueue((logLevel, formatter(state, exception)));
    }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  /// <summary>
  /// Scope factory wrapper that signals when the FIRST scope it created gets disposed.
  /// Used to deterministically observe the detached lifecycle body running to completion
  /// (its <c>await using</c> scope disposal is the last statement of its early-return path).
  /// </summary>
  private sealed class SignalingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory {
    public TaskCompletionSource FirstScopeDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IServiceScope CreateScope() => new SignalingScope(inner.CreateScope(), FirstScopeDisposed);

    private sealed class SignalingScope(IServiceScope inner, TaskCompletionSource disposed) : IServiceScope, IAsyncDisposable {
      public IServiceProvider ServiceProvider => inner.ServiceProvider;
      public void Dispose() {
        inner.Dispose();
        disposed.TrySetResult();
      }
      public async ValueTask DisposeAsync() {
        if (inner is IAsyncDisposable asyncDisposable) {
          await asyncDisposable.DisposeAsync();
        } else {
          inner.Dispose();
        }
        disposed.TrySetResult();
      }
    }
  }

  private static InboxWork _makeWork(int attempts = 0, MessageProcessingStatus status = MessageProcessingStatus.Stored) {
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    return new InboxWork {
      MessageId = msgId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
      PartitionNumber = 1,
      Attempts = attempts,
      Status = status,
      Flags = WorkBatchOptions.None,
    };
  }

  // ============================================================
  // Constructor null guards
  // ============================================================

  /// <summary>
  /// Mutable bag of valid constructor dependencies; tests null exactly one member and
  /// assert the guard fires with the matching parameter name.
  /// </summary>
  private sealed class CtorDeps {
    public IServiceScopeFactory? ScopeFactory { get; set; } =
      new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    public IServiceInstanceProvider? InstanceProvider { get; set; } = new FakeInstanceProvider();
    public IInboxChannelWriter? InboxChannelWriter { get; set; } = new FakeInboxChannelWriter();
    public IInboxHandlerCommitChannel? HandlerCommitChannel { get; set; } = new FakeHandlerCommitChannel();
    public IFailureChannel? FailureChannel { get; set; } = new FakeFailureChannel();
    public ISchemaReadyGate? SchemaGate { get; set; } = new SchemaReadyGate();
    public IOptions<InboxDispatchWorkerOptions>? WorkerOptions { get; set; } = Options.Create(new InboxDispatchWorkerOptions());
    public IOptions<WorkCoordinatorOptions>? CoordinatorOptions { get; set; } = Options.Create(new WorkCoordinatorOptions());
    public ILogger<InboxDispatchWorker>? Logger { get; set; } = NullLogger<InboxDispatchWorker>.Instance;

    public InboxDispatchWorker Build() => new(
      ScopeFactory!,
      InstanceProvider!,
      InboxChannelWriter!,
      HandlerCommitChannel!,
      FailureChannel!,
      SchemaGate!,
      WorkerOptions!,
      CoordinatorOptions!,
      Logger!);
  }

  private static async Task _assertCtorGuardAsync(CtorDeps deps, string expectedParamName) {
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = deps.Build();
      await Task.CompletedTask;
    });
    await Assert.That(ex?.ParamName).IsEqualTo(expectedParamName)
      .Because("each guard must name the offending parameter so DI misconfiguration errors are actionable");
  }

  /// <summary>Constructor guard: null IServiceScopeFactory throws with the right ParamName.</summary>
  [Test]
  public async Task Constructor_NullScopeFactory_ThrowsArgumentNullExceptionAsync() {
    await _assertCtorGuardAsync(new CtorDeps { ScopeFactory = null }, "scopeFactory");
  }

  /// <summary>Constructor guard: null IServiceInstanceProvider throws with the right ParamName.</summary>
  [Test]
  public async Task Constructor_NullInstanceProvider_ThrowsArgumentNullExceptionAsync() {
    await _assertCtorGuardAsync(new CtorDeps { InstanceProvider = null }, "instanceProvider");
  }

  /// <summary>Constructor guard: null IInboxChannelWriter throws with the right ParamName.</summary>
  [Test]
  public async Task Constructor_NullInboxChannelWriter_ThrowsArgumentNullExceptionAsync() {
    await _assertCtorGuardAsync(new CtorDeps { InboxChannelWriter = null }, "inboxChannelWriter");
  }

  /// <summary>Constructor guard: null IInboxHandlerCommitChannel throws with the right ParamName.</summary>
  [Test]
  public async Task Constructor_NullHandlerCommitChannel_ThrowsArgumentNullExceptionAsync() {
    await _assertCtorGuardAsync(new CtorDeps { HandlerCommitChannel = null }, "handlerCommitChannel");
  }

  /// <summary>Constructor guard: null IFailureChannel throws with the right ParamName.</summary>
  [Test]
  public async Task Constructor_NullFailureChannel_ThrowsArgumentNullExceptionAsync() {
    await _assertCtorGuardAsync(new CtorDeps { FailureChannel = null }, "failureChannel");
  }

  /// <summary>Constructor guard: null ISchemaReadyGate throws with the right ParamName.</summary>
  [Test]
  public async Task Constructor_NullSchemaReadyGate_ThrowsArgumentNullExceptionAsync() {
    await _assertCtorGuardAsync(new CtorDeps { SchemaGate = null }, "schemaReadyGate");
  }

  /// <summary>Constructor guard: null IOptions&lt;InboxDispatchWorkerOptions&gt; throws with the right ParamName.</summary>
  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await _assertCtorGuardAsync(new CtorDeps { WorkerOptions = null }, "options");
  }

  /// <summary>Constructor guard: null IOptions&lt;WorkCoordinatorOptions&gt; throws with the right ParamName.</summary>
  [Test]
  public async Task Constructor_NullCoordinatorOptions_ThrowsArgumentNullExceptionAsync() {
    await _assertCtorGuardAsync(new CtorDeps { CoordinatorOptions = null }, "coordinatorOptions");
  }

  /// <summary>Constructor guard: null ILogger throws with the right ParamName.</summary>
  [Test]
  public async Task Constructor_NullLogger_ThrowsArgumentNullExceptionAsync() {
    await _assertCtorGuardAsync(new CtorDeps { Logger = null }, "logger");
  }

  // ============================================================
  // ExecuteAsync — schema-gate cancellation + clamp log
  // ============================================================

  /// <summary>
  /// Covers the OperationCanceledException catch around
  /// <c>_schemaReadyGate.WaitForReadyAsync</c>: when shutdown fires while the gate is
  /// still closed, ExecuteAsync must return cleanly (no fault, no partition spin-up,
  /// no dispatched work).
  /// </summary>
  [Test]
  public async Task SchemaGateWaitCancelledByShutdown_ExitsCleanlyWithoutDispatchingAsync() {
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();  // never marked ready
    await using var sp = new ServiceCollection().BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    await worker.StartAsync(CancellationToken.None);
    var executeTask = worker.ExecuteTask;
    await inbox.WriteAsync(_makeWork());

    // StopAsync cancels the stoppingToken; WaitForReadyAsync throws OCE; the catch returns.
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask is not null).IsTrue();
    // Graceful shutdown = completed without faulting. Depending on where the OCE surfaces
    // relative to the catch, the task may end RanToCompletion or Canceled — both are clean.
    // IsCompletedSuccessfully excludes Canceled and so flakes under CI timing.
    await Assert.That(executeTask!.IsCompleted).IsTrue()
      .Because("cancellation while awaiting the schema gate must complete ExecuteAsync");
    await Assert.That(executeTask!.IsFaulted).IsFalse()
      .Because("the schema-gate shutdown path must return, not fault");
    await Assert.That(handlerCommit.All).IsEmpty()
      .Because("the dispatch loop never started, so the queued work must not have been consumed");
  }

  /// <summary>
  /// Covers the partition-count clamp log branch: when the WorkCoordinatorGate's
  /// MaxConcurrent is smaller than MaxConcurrentDispatch, the worker logs the clamp
  /// warning (EventId 25) and still dispatches through the reduced fan-out.
  /// </summary>
  [Test]
  public async Task GateSmallerThanConfiguredDispatch_LogsClampWarning_AndStillDispatchesAsync() {
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new RecordingLogger<InboxDispatchWorker>();
    using var coordinatorGate = new WorkCoordinatorGate(maxConcurrent: 2);
    await using var sp = new ServiceCollection().BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { MaxConcurrentDispatch = 8 }),
      Options.Create(new WorkCoordinatorOptions()),
      logger,
      gate: coordinatorGate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await inbox.WriteAsync(work, cts.Token);

    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(routed.InboxCompletion.MessageId).IsEqualTo(work.MessageId)
      .Because("clamping the fan-out must not break dispatch itself");
    await Assert.That(logger.Entries.Any(e =>
        e.Level == LogLevel.Warning
        && e.Message.Contains("MaxConcurrentDispatch=8", StringComparison.Ordinal)
        && e.Message.Contains("clamped to 2", StringComparison.Ordinal))).IsTrue()
      .Because("operators need the clamp surfaced so they can either raise the gate or lower MaxConcurrentDispatch");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Max-attempts dead-letter branch — store failure fallback + metrics
  // ============================================================

  /// <summary>
  /// Covers the max-attempts DLQ-store failure fallback: when
  /// <c>IDeadLetterStore.MoveAsync</c> throws, the worker logs the failure and falls back
  /// to the legacy mark-Published terminal completion so the row never re-claims.
  /// </summary>
  [Test]
  public async Task MaxAttemptsExceeded_DeadLetterStoreThrows_FallsBackToTerminalCommitAsync() {
    var handlerCommit = new FakeHandlerCommitChannel();
    var logger = new RecordingLogger<InboxDispatchWorker>();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    await using var sp = new ServiceCollection().BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), new FakeInboxChannelWriter(), handlerCommit, new FakeFailureChannel(), gate,
      Options.Create(new InboxDispatchWorkerOptions { MaxInboxAttempts = 3 }),
      Options.Create(new WorkCoordinatorOptions()),
      logger,
      deadLetterStore: new ThrowingDeadLetterStore(),
      generationProvider: new FakeGenerationProvider());

    var work = _makeWork(attempts: 4);
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var routed = handlerCommit.All.Single();
    var expectedStatus = (int)(work.Status | MessageProcessingStatus.Published);
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo(expectedStatus)
      .Because("the retry budget is exhausted — even when the DLQ move fails, the row must terminate via the legacy mark-Published path");
    await Assert.That(routed.InboxCompletion.MessageId).IsEqualTo(work.MessageId);
    await Assert.That(logger.Entries.Any(e =>
        e.Level == LogLevel.Warning
        && e.Message.Contains("falling back to legacy mark-Published", StringComparison.Ordinal))).IsTrue()
      .Because("the store failure must be operator-visible; silent fallback would hide a broken wh_dead_letters pipeline");
  }

  /// <summary>
  /// Covers the DLQ metrics emission on a successful max-attempts promotion: the Added
  /// counter increments once with source_table=wh_inbox and reason=MaxAttemptsExceeded,
  /// and the handler-commit path is bypassed (the SQL move deleted the row atomically).
  /// </summary>
  [Test]
  public async Task MaxAttemptsExceeded_StoreSucceeds_IncrementsDlqAddedCounterAndSkipsCommitAsync() {
    var handlerCommit = new FakeHandlerCommitChannel();
    var store = new CapturingDeadLetterStore();
    var metrics = new DeadLetterMetrics(new WhizbangMetrics());
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    await using var sp = new ServiceCollection().BuildServiceProvider();

    var added = new List<(long Value, string? SourceTable, string? Reason)>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument, metrics.Added)) {
          l.EnableMeasurementEvents(instrument);
        }
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? sourceTable = null;
      string? reason = null;
      foreach (var tag in tags) {
        if (tag.Key == "source_table") {
          sourceTable = tag.Value?.ToString();
        } else if (tag.Key == "reason") {
          reason = tag.Value?.ToString();
        }
      }
      added.Add((value, sourceTable, reason));
    });
    listener.Start();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), new FakeInboxChannelWriter(), handlerCommit, new FakeFailureChannel(), gate,
      Options.Create(new InboxDispatchWorkerOptions { MaxInboxAttempts = 3 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      deadLetterStore: store,
      generationProvider: new FakeGenerationProvider(),
      dlqMetrics: metrics);

    var work = _makeWork(attempts: 4);
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(store.Moves).Count().IsEqualTo(1);
    await Assert.That(added).Count().IsEqualTo(1)
      .Because("each successful DLQ promotion must increment the Added counter exactly once");
    await Assert.That(added[0].Value).IsEqualTo(1L);
    await Assert.That(added[0].SourceTable).IsEqualTo(DeadLetterSourceTable.INBOX);
    await Assert.That(added[0].Reason).IsEqualTo("MaxAttemptsExceeded");
    await Assert.That(handlerCommit.All).IsEmpty()
      .Because("the SQL move deletes the wh_inbox row in the same transaction — the legacy commit path must be bypassed");
  }

  // ============================================================
  // Composite fan-out failure branches
  // ============================================================

  private static InboxDispatchWorker _buildCompositeWorker(
      ServiceProvider sp,
      FakeHandlerCommitChannel handlerCommit,
      ILogger<InboxDispatchWorker> logger,
      ICompositeEvent composite,
      IDeadLetterStore? deadLetterStore,
      IGenerationProvider? generationProvider,
      DeadLetterMetrics? dlqMetrics = null) {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), new FakeInboxChannelWriter(), handlerCommit, new FakeFailureChannel(), gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      logger,
      lifecycleMessageDeserializer: new FakeCompositeDeserializer(composite),
      deadLetterStore: deadLetterStore,
      generationProvider: generationProvider,
      dlqMetrics: dlqMetrics);
  }

  /// <summary>
  /// Covers the composite-failure DLQ-store fallback: an over-cap composite dead-letters,
  /// but when MoveAsync throws, the worker logs the store failure and terminates the row
  /// via the legacy mark-Published completion instead.
  /// </summary>
  [Test]
  public async Task CompositeOverCap_DeadLetterStoreThrows_FallsBackToTerminalCommitAsync() {
    var handlerCommit = new FakeHandlerCommitChannel();
    var logger = new RecordingLogger<InboxDispatchWorker>();
    await using var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();
    var worker = _buildCompositeWorker(
      sp, handlerCommit, logger, new OverCapComposite(5),
      deadLetterStore: new ThrowingDeadLetterStore(),
      generationProvider: new FakeGenerationProvider());

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var routed = handlerCommit.All.Single();
    var expectedStatus = (int)(work.Status | MessageProcessingStatus.Published);
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo(expectedStatus)
      .Because("a composite that can't dead-letter must still terminate — otherwise it re-claims and re-fails forever");
    await Assert.That(routed.NewInboxMessages is null || routed.NewInboxMessages.Count == 0).IsTrue()
      .Because("cap breach is all-or-nothing: no children may be committed");
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("composite fan-out failed", StringComparison.Ordinal))).IsTrue();
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("falling back to legacy mark-Published", StringComparison.Ordinal))).IsTrue()
      .Because("the store failure must be operator-visible on the composite path too");
  }

  /// <summary>
  /// Covers the composite-failure branch with NO dead-letter store wired: the worker
  /// goes straight to the legacy terminal completion (status |= Published).
  /// </summary>
  [Test]
  public async Task CompositeOverCap_NoDeadLetterStore_TerminatesViaLegacyCommitAsync() {
    var handlerCommit = new FakeHandlerCommitChannel();
    var logger = new RecordingLogger<InboxDispatchWorker>();
    await using var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();
    var worker = _buildCompositeWorker(
      sp, handlerCommit, logger, new OverCapComposite(5),
      deadLetterStore: null,
      generationProvider: null);

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var routed = handlerCommit.All.Single();
    var expectedStatus = (int)(work.Status | MessageProcessingStatus.Published);
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo(expectedStatus)
      .Because("without a store, the legacy mark-Published completion is the only terminal path");
    await Assert.That(logger.Entries.Any(e =>
        e.Level == LogLevel.Warning
        && e.Message.Contains("CompositeInnerEventLimitExceeded", StringComparison.Ordinal))).IsTrue()
      .Because("the fan-out failure reason must reach the log even when no DLQ store is available");
  }

  /// <summary>
  /// Covers the DLQ metrics emission on the composite-failure path: the Added counter
  /// increments with reason=CompositeInnerEventLimitExceeded and the commit is bypassed.
  /// </summary>
  [Test]
  public async Task CompositeOverCap_StoreSucceeds_IncrementsDlqAddedCounterAndSkipsCommitAsync() {
    var handlerCommit = new FakeHandlerCommitChannel();
    var store = new CapturingDeadLetterStore();
    var metrics = new DeadLetterMetrics(new WhizbangMetrics());
    await using var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();

    var added = new List<(long Value, string? SourceTable, string? Reason)>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument, metrics.Added)) {
          l.EnableMeasurementEvents(instrument);
        }
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? sourceTable = null;
      string? reason = null;
      foreach (var tag in tags) {
        if (tag.Key == "source_table") {
          sourceTable = tag.Value?.ToString();
        } else if (tag.Key == "reason") {
          reason = tag.Value?.ToString();
        }
      }
      added.Add((value, sourceTable, reason));
    });
    listener.Start();

    var worker = _buildCompositeWorker(
      sp, handlerCommit, NullLogger<InboxDispatchWorker>.Instance, new OverCapComposite(5),
      deadLetterStore: store,
      generationProvider: new FakeGenerationProvider(),
      dlqMetrics: metrics);

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(store.Moves).Count().IsEqualTo(1);
    await Assert.That(store.Moves.Single().Reason).IsEqualTo(MessageFailureReason.CompositeInnerEventLimitExceeded);
    await Assert.That(added).Count().IsEqualTo(1);
    await Assert.That(added[0].SourceTable).IsEqualTo(DeadLetterSourceTable.INBOX);
    await Assert.That(added[0].Reason).IsEqualTo("CompositeInnerEventLimitExceeded");
    await Assert.That(handlerCommit.All).IsEmpty()
      .Because("a successful composite dead-letter deletes the row atomically — no handler commit may follow");
  }

  // ============================================================
  // Deserialize failure + discard-policy short-circuit
  // ============================================================

  /// <summary>
  /// Covers _resolveTypedEnvelope's catch branch: a throwing deserializer must be
  /// surfaced once via the lifecycle-error log and dispatch must continue — the row
  /// still commits EventStored (lifecycle stages simply no-op on the null envelope).
  /// </summary>
  [Test]
  public async Task DeserializerThrows_LogsDeserializeError_AndStillCommitsEventStoredAsync() {
    var handlerCommit = new FakeHandlerCommitChannel();
    var logger = new RecordingLogger<InboxDispatchWorker>();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    await using var sp = new ServiceCollection().BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), new FakeInboxChannelWriter(), handlerCommit, new FakeFailureChannel(), gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      logger,
      lifecycleMessageDeserializer: new ThrowingDeserializer());

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var routed = handlerCommit.All.Single();
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)MessageProcessingStatus.EventStored)
      .Because("deserialize is best-effort for lifecycle stages only — a failure must not block the inbox completion");
    await Assert.That(logger.Entries.Any(e =>
        e.Level == LogLevel.Warning
        && e.Message.Contains("'Deserialize' failed", StringComparison.Ordinal))).IsTrue()
      .Because("a deserialize fault silently skips ALL lifecycle stages — it must be surfaced once per dispatch");
  }

  /// <summary>
  /// Covers the in-worker discard-policy short-circuit (the branch after ShouldSkipInbox
  /// returns true): the row commits terminal (status |= Published), RecordDiscard fires
  /// exactly once with the Inbox gate and the message_id tag, and no lease/dispatch
  /// machinery runs.
  /// </summary>
  [Test]
  public async Task DiscardPolicySaysDiscard_ShortCircuitsRowWithTerminalCommitAsync() {
    var handlerCommit = new FakeHandlerCommitChannel();
    var policy = new AlwaysDiscardInboxPolicy();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    await using var sp = new ServiceCollection().BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), new FakeInboxChannelWriter(), handlerCommit, new FakeFailureChannel(), gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      discardPolicy: policy);

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var routed = handlerCommit.All.Single();
    var expectedStatus = (int)(work.Status | MessageProcessingStatus.Published);
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo(expectedStatus)
      .Because("a discarded row must terminate with the Published bit so it never re-claims");
    await Assert.That(policy.Recorded).Count().IsEqualTo(1)
      .Because("RecordDiscard is invoked exactly once per positive decision — that is the policy contract");
    var recorded = policy.Recorded.Single();
    await Assert.That(recorded.Gate).IsEqualTo(MessageDiscardGate.Inbox);
    await Assert.That(recorded.Tags).IsNotNull();
    await Assert.That(recorded.Tags!["message_id"]).IsEqualTo((object)work.MessageId)
      .Because("the skip telemetry must carry the message id so operators can trace individual discarded rows");
  }

  // ============================================================
  // Detached lifecycle stage — exception routing + missing invoker
  // ============================================================

  /// <summary>
  /// Covers the detached lifecycle body's exception catch: a receptor that throws inside
  /// the fire-and-forget detached stage must route the exception through IFailureChannel
  /// (so process_inbox_failures populates wh_inbox.error) rather than swallowing it.
  /// </summary>
  [Test]
  public async Task DetachedLifecycleReceptorThrows_RoutesFailureToFailureChannelAsync() {
    var failure = new FakeFailureChannel();
    var handlerCommit = new FakeHandlerCommitChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ =>
      new StageThrowingInvoker(LifecycleStage.PreInboxDetached, new InvalidOperationException("simulated detached receptor fault")));
    await using var sp = services.BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), new FakeInboxChannelWriter(), handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    var work = _makeWork();
    await using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();

    await worker.InvokeInboxLifecycleStageAsync(
      work, work.Envelope, scope, new CapturingReceptorInvoker(),
      LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
      "PreInbox", CancellationToken.None);

    var captured = await failure.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(captured.MessageId).IsEqualTo(work.MessageId);
    await Assert.That(captured.Error).Contains("PreInboxDetached failed")
      .Because("the stage name must reach wh_inbox.error so operators can distinguish detached from inline faults");
    await Assert.That(captured.Error).Contains("simulated detached receptor fault")
      .Because("the real exception text is what fingerprinting clusters on — losing it recreates a production-class silent retry loop");
    await Assert.That(failure.All.Single().Category).IsEqualTo(WorkCategory.Inbox)
      .Because("inbox-side detached faults must target wh_inbox, not wh_outbox");
  }

  /// <summary>
  /// Covers the detached body's early return when the fresh DI scope resolves no
  /// IReceptorInvoker: the detached task creates + disposes its scope without invoking
  /// anything and without reporting a failure.
  /// </summary>
  [Test]
  public async Task DetachedScope_NoReceptorInvokerRegistered_SkipsDetachedInvocationAsync() {
    var failure = new FakeFailureChannel();
    var handlerCommit = new FakeHandlerCommitChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    // No IReceptorInvoker registered — the detached scope's GetService returns null.
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var signalingFactory = new SignalingScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());

    var worker = new InboxDispatchWorker(
      signalingFactory,
      new FakeInstanceProvider(), new FakeInboxChannelWriter(), handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    var work = _makeWork();
    var inlineInvoker = new CapturingReceptorInvoker();
    // Outer scope comes from the RAW provider so only the detached body's scope flows
    // through the signaling factory — its disposal marks the detached path complete.
    await using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();

    await worker.InvokeInboxLifecycleStageAsync(
      work, work.Envelope, scope, inlineInvoker,
      LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
      "PreInbox", CancellationToken.None);

    await signalingFactory.FirstScopeDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(inlineInvoker.HasStage(LifecycleStage.PreInboxInline)).IsTrue()
      .Because("the inline half still fires via the caller-supplied invoker");
    await Assert.That(inlineInvoker.HasStage(LifecycleStage.PreInboxDetached)).IsFalse()
      .Because("the detached body resolves its invoker from its OWN scope — with none registered it must invoke nothing");
    await Assert.That(failure.All).IsEmpty()
      .Because("a missing detached invoker is a benign no-op, not a failure");
  }

  // ============================================================
  // Observability — InboxMetrics histogram + Debug PERF log
  // ============================================================

  /// <summary>
  /// Covers the InboxMetrics recording in _processOneAsync's finally: every dispatched
  /// message records one dispatch-duration observation tagged with the SHORT message
  /// type name.
  /// </summary>
  [Test]
  public async Task InboxMetricsWired_RecordsDispatchDurationTaggedWithShortMessageTypeAsync() {
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var metrics = new InboxMetrics(new WhizbangMetrics());
    await using var sp = new ServiceCollection().BuildServiceProvider();

    var measured = new TaskCompletionSource<(double Value, string? MessageType)>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument, metrics.DispatchDuration)) {
          l.EnableMeasurementEvents(instrument);
        }
      }
    };
    listener.SetMeasurementEventCallback<double>((_, value, tags, _) => {
      string? messageType = null;
      foreach (var tag in tags) {
        if (tag.Key == "message_type") {
          messageType = tag.Value?.ToString();
        }
      }
      measured.TrySetResult((value, messageType));
    });
    listener.Start();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      inboxMetrics: metrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await inbox.WriteAsync(_makeWork(), cts.Token);

    var (value, messageType) = await measured.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(value).IsGreaterThanOrEqualTo(0d)
      .Because("wall time can be sub-millisecond but never negative");
    await Assert.That(messageType).IsEqualTo("JsonElement")
      .Because("the AQN 'System.Text.Json.JsonElement, System.Text.Json' must shorten to its class name for dashboard cardinality");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
