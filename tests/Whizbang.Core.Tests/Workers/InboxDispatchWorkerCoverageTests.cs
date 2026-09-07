using System.Collections.Concurrent;
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
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage-round-23 tests for <see cref="InboxDispatchWorker"/>. Targets: the schema-gate
/// cancellation return in <c>ExecuteAsync</c>; the Debug-gated PERF diagnostic in
/// <c>_processOneAsync</c>'s <c>finally</c> (crossed deterministically via an injected
/// <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/>, not a real delay); the
/// "row already gone" idempotent no-op in both composite dead-letter call sites
/// (<c>_fanoutCompositeAsync</c> and <c>_deadLetterCompositeAsync</c>); the pre-fanout hook's
/// no-receptor early return and its PreInboxInline invocation branch in
/// <c>_invokePreFanoutHookAsync</c>; and the "perspective registry is non-empty but nothing
/// matches this event" fallthrough in <c>_hasNoPerspectives</c>.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerCoverageTests {

  // ============================================================
  // Test fakes
  // ============================================================

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 42;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ConcurrentBag<Guid> RemovedInFlight { get; } = [];
    // Fires with the FIRST message id ever released via RemoveInFlight — a deterministic signal
    // for tests asserting a terminal no-op path released its in-flight guard.
    public TaskCompletionSource<Guid> RemovedInFlightFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) {
      RemovedInFlight.Add(messageId);
      RemovedInFlightFirst.TrySetResult(messageId);
    }
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

  /// <summary>Handler-commit channel that advances an injected <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/>
  /// by a fixed, comfortably-over-threshold amount on every enqueue — simulates real dispatch cost
  /// deterministically, without a real delay, so the &gt;100ms PERF log gate can be crossed.</summary>
  private sealed class TimeAdvancingHandlerCommitChannel(Microsoft.Extensions.Time.Testing.FakeTimeProvider timeProvider) : IInboxHandlerCommitChannel {
    public TaskCompletionSource<HandlerCommitRequest> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<HandlerCommitRequest> All { get; } = [];
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) {
      All.Add(request);
      timeProvider.Advance(TimeSpan.FromMilliseconds(150));
      First.TrySetResult(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public TaskCompletionSource<MessageFailure> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<(WorkCategory cat, MessageFailure f)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add((category, failure));
      First.TrySetResult(failure);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Thread-safe recording logger, IsEnabled=true for all levels so Debug-gated
  /// branches execute (mirrors InboxDispatchWorkerGapTests' RecordingLogger).</summary>
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

  // Returns a fixed composite from DeserializeFromJsonElement so _resolveTypedEnvelope yields a
  // typed ICompositeEvent payload at the dispatch seam (mirrors InboxDispatchWorkerTests).
  private sealed class FakeCompositeDeserializer(ICompositeEvent composite) : ILifecycleMessageDeserializer {
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => composite;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => composite;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => composite;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => composite;
  }

  /// <summary>Returns the given composite ONLY for the one message type that opts in — every
  /// other message type gets its raw JsonElement payload back unchanged and takes the ordinary
  /// (non-composite) dispatch path. Lets a single worker/partition carry both a
  /// swallowed-already-gone composite row AND a normal row, proving the swallow doesn't stall
  /// the partition consumer's loop for the row queued behind it.</summary>
  private sealed class SelectiveDeserializer(string compositeMessageType, ICompositeEvent composite) : ILifecycleMessageDeserializer {
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) =>
      messageTypeName == compositeMessageType ? composite : jsonElement;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) =>
      DeserializeFromJsonElement(envelope.Payload, envelopeTypeName);
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => JsonDocument.Parse(jsonBytes).RootElement;
  }

  /// <summary>Pass-through deserializer for the (non-composite) perspective-gating test: the
  /// lifecycle path only needs a non-null typed envelope, not a specific payload type.</summary>
  private sealed class PassThroughLifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] payload, string messageType) => JsonDocument.Parse(payload).RootElement;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => jsonElement;
  }

  private sealed record _innerImportEvent(string Id) : IEvent;

  private sealed class _bulkComposite(params _innerImportEvent[] inner) : ICompositeEvent {
    public int MaxInnerEventsAllowed => 10_000;
    public IEnumerable<IMessage> InnerEvents => inner;
  }

  private sealed class _overCapComposite(int count) : ICompositeEvent {
    public int MaxInnerEventsAllowed => 1;
    public IEnumerable<IMessage> InnerEvents => Enumerable.Range(0, count).Select(i => (IMessage)new _innerImportEvent($"J-{i}"));
  }

  /// <summary>DLQ store whose MoveAsync reports the source row already gone (the documented
  /// null no-op) — drives the #571 already-terminal branch at both composite call sites.</summary>
  private sealed class NullMoveDeadLetterStore : IDeadLetterStore {
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default)
      => Task.FromResult<Guid?>(null);
  }

  private sealed class FakeGenerationProvider : IGenerationProvider {
    public string GetGeneration() => "test-generation";
  }

  /// <summary>Records every (envelope, stage) invocation; never throws. All call sites in this
  /// file invoke it via a directly-awaited <c>ProcessOneInnerAsync</c> call, so by the time that
  /// await returns every synchronous-inline invocation has already been recorded — no separate
  /// completion signal is needed.</summary>
  private sealed class RecordingInvoker : IReceptorInvoker {
    public ConcurrentBag<LifecycleStage> Invoked { get; } = [];
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      Invoked.Add(stage);
      return ValueTask.CompletedTask;
    }
  }

  // Registry that reports a receptor for PreInboxInline only — drives the pre-fanout hook's
  // "hasPre" branch in _invokePreFanoutHookAsync without opening the PostInboxInline branch
  // (already covered elsewhere).
  private sealed class PreInboxInlineOnlyRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => stage == LifecycleStage.PreInboxInline;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  /// <summary>Registry that reports a controllable list of perspectives (mirrors
  /// InboxDispatchWorkerLifecycleIntegrationTests' StubPerspectiveRunnerRegistry).</summary>
  private sealed class StubPerspectiveRunnerRegistry(
      IReadOnlyList<PerspectiveRegistrationInfo> perspectives) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private static InboxWork _makeWork(int attempts = 0, MessageProcessingStatus status = MessageProcessingStatus.Stored, Guid? id = null, string? messageType = null) {
    var msgId = id ?? (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    return new InboxWork {
      MessageId = msgId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = messageType ?? "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
      PartitionNumber = 1,
      Attempts = attempts,
      Status = status,
      Flags = WorkBatchOptions.None,
    };
  }

  // ============================================================
  // Tests
  // ============================================================

  [Test]
  public async Task SchemaGateCanceledBeforeReady_ReturnsCleanlyWithoutDispatchingAsync() {
    // If shutdown races schema readiness and this catch stopped returning cleanly, ExecuteAsync
    // would throw an OperationCanceledException out of the BackgroundService's execute task —
    // surfaced by the host as a faulted hosted service (and, unobserved, a process-ending
    // exception) instead of the worker just quietly never having started dispatching.
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate(); // never marked ready

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // The gate is never marked ready, so ExecuteAsync is parked on WaitForReadyAsync. Canceling
    // the stopping token now must resolve that await via OperationCanceledException, caught at
    // the ExecuteAsync call site, which returns rather than propagating.
    await cts.CancelAsync();

    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(worker.ExecuteTask!.IsCompletedSuccessfully).IsTrue()
      .Because("canceling the stopping token while parked on the schema gate must return cleanly, not fault ExecuteAsync");
    await Assert.That(handlerCommit.All).IsEmpty()
      .Because("no dispatch work should ever run when shutdown wins the race against schema readiness");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SlowDispatch_DebugLoggingEnabled_EmitsPerfDiagnosticLineAsync() {
    // This is the only per-message dispatch-latency signal broken down by message type available
    // to an operator without standing up metrics infrastructure. If this branch regressed to
    // never firing (or fired unconditionally, burying operators in noise on every fast message),
    // the "why is inbox slow" investigation loses its one per-event diagnostic trail.
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var handlerCommit = new TimeAdvancingHandlerCommitChannel(fakeTime);
    var logger = new RecordingLogger<InboxDispatchWorker>();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      logger,
      integrityOptions: Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      timeProvider: fakeTime);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await inbox.WriteAsync(work, cts.Token);

    // The clock advance happens synchronously inside EnqueueAsync (mid-dispatch); the finally
    // block's totalMs computation runs shortly after dispatch returns. Wait for the commit first,
    // then a short bounded poll for the log line to land (matches this file family's established
    // Stopwatch+Task.Yield idiom for post-completion continuations).
    await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));

    (LogLevel Level, string Message)? perf = null;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(5)) {
      var match = logger.Entries.FirstOrDefault(e => e.Message.StartsWith("PERF InboxDispatch", StringComparison.Ordinal));
      if (match.Message is not null) {
        perf = match;
        break;
      }
      await Task.Yield();
    }

    await Assert.That(perf).IsNotNull()
      .Because("crossing the >100ms threshold with Debug enabled must emit the PERF diagnostic line");
    await Assert.That(perf!.Value.Message).Contains(work.MessageId.ToString())
      .Because("the diagnostic must identify WHICH message was slow");
    await Assert.That(perf!.Value.Message).Contains(work.MessageType)
      .Because("per-type breakdown is the entire point of this line over the aggregate histogram");
    await Assert.That(handlerCommit.All.Any(r => r.InboxCompletion.MessageId == work.MessageId)).IsTrue()
      .Because("the perf diagnostic must not block or replace the actual dispatch commit");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task CompositeOverCap_DeadLetterRowAlreadyGone_ReleasesInFlightAndPartitionContinuesAsync() {
    // If the DLQ move's idempotent "already gone" result stopped being treated as terminal here,
    // a composite row deleted by a concurrent recovery pass would wedge in the in-flight guard
    // forever — and because this branch lives INSIDE the per-partition consumer's foreach loop,
    // every row queued behind it on that same partition would never get dispatched either.
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var store = new NullMoveDeadLetterStore();
    var gate = SchemaReadyGate.AlreadyReady();

    const string compositeType = "Test.CompositeOverCap, Test";
    var composite = new _overCapComposite(5); // cap = 1 -> CapExceeded outcome
    var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { MaxConcurrentDispatch = 1 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      lifecycleMessageDeserializer: new SelectiveDeserializer(compositeType, composite),
      deadLetterStore: store,
      generationProvider: new FakeGenerationProvider());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var overCapWork = _makeWork(messageType: compositeType);
    await inbox.WriteAsync(overCapWork, cts.Token);

    var removedId = await inbox.RemovedInFlightFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(removedId).IsEqualTo(overCapWork.MessageId)
      .Because("already-gone is a terminal no-op — the in-flight guard must release even though no commit followed");
    await Assert.That(handlerCommit.All.Any(r => r.InboxCompletion.MessageId == overCapWork.MessageId)).IsFalse()
      .Because("already-gone bypasses the legacy mark-Published fallback entirely — it is neither a fresh dead-letter nor a normal completion");

    // The stream-affinity partition consumer's foreach loop must move on: a distinct, ordinary
    // row dispatched right after must still complete normally.
    var plainWork = _makeWork();
    await inbox.WriteAsync(plainWork, cts.Token);
    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(routed.InboxCompletion.MessageId).IsEqualTo(plainWork.MessageId)
      .Because("the swallow of an already-gone composite must not stall this partition's consumer loop for later work");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task CompositeOverConsumerBudget_DeadLetterRowAlreadyGone_ReleasesInFlightAndPartitionContinuesAsync() {
    // Same idempotent-no-op contract as the composite-cap path, but through the consumer-budget
    // refusal's OWN dead-letter call site (_deadLetterCompositeAsync): if this branch stopped
    // releasing the in-flight guard, a composite whose row a concurrent recovery pass already
    // deleted would wedge, stalling the rest of this partition exactly as the cap-path regression
    // would.
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var store = new NullMoveDeadLetterStore();
    var gate = SchemaReadyGate.AlreadyReady();

    const string compositeType = "Test.CompositeOverBudget, Test";
    var composite = new _bulkComposite(
      new _innerImportEvent("J-1"), new _innerImportEvent("J-2"), new _innerImportEvent("J-3"),
      new _innerImportEvent("J-4"), new _innerImportEvent("J-5")); // 5 children, own cap 10_000 -> Expanded
    var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions {
        MaxConcurrentDispatch = 1,
        MaxCompositeChildrenPerExpansion = 2, // 5 children > 2 -> over budget
        EnforceCompositeExpansionBudget = true, // over budget + enforce -> refuse -> _deadLetterCompositeAsync
      }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      lifecycleMessageDeserializer: new SelectiveDeserializer(compositeType, composite),
      deadLetterStore: store,
      generationProvider: new FakeGenerationProvider());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var overBudgetWork = _makeWork(messageType: compositeType);
    await inbox.WriteAsync(overBudgetWork, cts.Token);

    var removedId = await inbox.RemovedInFlightFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(removedId).IsEqualTo(overBudgetWork.MessageId)
      .Because("already-gone is terminal here too — the in-flight guard must release without a legacy fallback commit");
    await Assert.That(handlerCommit.All.Any(r => r.InboxCompletion.MessageId == overBudgetWork.MessageId)).IsFalse()
      .Because("no children and no legacy mark-Published — the row is already gone");

    var plainWork = _makeWork();
    await inbox.WriteAsync(plainWork, cts.Token);
    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(routed.InboxCompletion.MessageId).IsEqualTo(plainWork.MessageId)
      .Because("the swallow must not stall this partition's consumer loop for later work");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task CompositePreFanoutHook_NoReceptorRegisteredForEitherStage_SkipsHookAndStillFansOutAsync() {
    // If this early return regressed to always resolving/invoking the receptor for pre-fanout,
    // every composite on a service with no pre-fanout receptor would pay for opening the ambient
    // outbox collector and fan-out control for nothing — and a bug in that unconditional path
    // could smuggle a stray emitted event into commits that no receptor asked to produce.
    var invoker = new RecordingInvoker();
    var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .AddSingleton<IReceptorInvoker>(invoker)
      .BuildServiceProvider();

    var composite = new _bulkComposite(new _innerImportEvent("J-1"), new _innerImportEvent("J-2"));
    var handlerCommit = new FakeHandlerCommitChannel();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), new FakeInboxChannelWriter(), handlerCommit, new FakeFailureChannel(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      lifecycleMessageDeserializer: new FakeCompositeDeserializer(composite));
    // No receptorRegistry / runtimeReceptorRegistry supplied -> HasReceptors is never true for
    // either Pre/PostInboxInline, so the gate must close before touching the invoker at all.

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(invoker.Invoked.IsEmpty).IsTrue()
      .Because("with no receptor registered for Pre/PostInboxInline, the pre-fanout hook must never call the invoker");
    var routed = handlerCommit.All.Single();
    await Assert.That(routed.NewInboxMessages!.Count).IsEqualTo(2)
      .Because("skipping the hook must not skip the fan-out itself — children still commit");
    await Assert.That(routed.NewOutboxMessages is null || routed.NewOutboxMessages.Count == 0).IsTrue()
      .Because("no pre-fanout receptor ran, so nothing should ride along as an emitted outbox message");
  }

  [Test]
  public async Task CompositePreFanoutHook_PreInboxInlineReceptorRegistered_InvokesBeforeFanoutAsync() {
    // The pre-fanout hook exists so a receptor can validate/stamp a batch (or emit a durable
    // event) BEFORE any child row exists. If this invocation stopped firing when only
    // PreInboxInline (not PostInboxInline) is registered, a receptor written to run at
    // PreInboxInline would silently never see composite batches at all.
    var invoker = new RecordingInvoker();
    var registry = new PreInboxInlineOnlyRegistry();
    var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .AddSingleton<IReceptorInvoker>(invoker)
      .BuildServiceProvider();

    var composite = new _bulkComposite(new _innerImportEvent("J-1"), new _innerImportEvent("J-2"), new _innerImportEvent("J-3"));
    var handlerCommit = new FakeHandlerCommitChannel();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), new FakeInboxChannelWriter(), handlerCommit, new FakeFailureChannel(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      lifecycleMessageDeserializer: new FakeCompositeDeserializer(composite),
      receptorRegistry: registry);

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(invoker.Invoked.Count(s => s == LifecycleStage.PreInboxInline)).IsEqualTo(1)
      .Because("PreInboxInline must fire exactly once when a receptor is registered for it");
    await Assert.That(invoker.Invoked.Contains(LifecycleStage.PostInboxInline)).IsFalse()
      .Because("only PreInboxInline is registered here — PostInboxInline must not fire");
    var routed = handlerCommit.All.Single();
    await Assert.That(routed.NewInboxMessages!.Count).IsEqualTo(3)
      .Because("firing the pre-fanout hook must not short-circuit the fan-out that follows it");
  }

  [Test]
  public async Task NoMatchingLocalPerspective_StillFiresPostAllPerspectivesInlineAsync() {
    // A perspective registered for OTHER events must not suppress tag-notification stages for
    // events it doesn't own. If the "no match found after scanning every registered perspective"
    // fallthrough regressed to answering false (treating "some perspective exists somewhere" as
    // "this event has one locally"), PostAllPerspectives would silently stop firing for every
    // cross-service event on any host that also happens to run one unrelated local perspective —
    // exactly the tag-hook stall this worker exists to prevent.
    var invoker = new RecordingInvoker();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorInvoker>(invoker);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new StubPerspectiveRunnerRegistry([
      new PerspectiveRegistrationInfo(
        ClrTypeName: "Test.UnrelatedPerspective",
        FullyQualifiedName: "global::Test.UnrelatedPerspective",
        ModelType: "global::Test.UnrelatedModel",
        EventTypes: ["Some.Unrelated.Event, Test"])
    ]));
    var sp = services.BuildServiceProvider();

    var handlerCommit = new FakeHandlerCommitChannel();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), new FakeInboxChannelWriter(), handlerCommit, new FakeFailureChannel(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      lifecycleMessageDeserializer: new PassThroughLifecycleDeserializer());

    var work = _makeWork(messageType: "Some.Different.Event, Test");
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(invoker.Invoked.Contains(LifecycleStage.PostAllPerspectivesInline)).IsTrue()
      .Because("the registered perspective's event types don't include this message's type, so this event has no LOCAL perspective and must still get PostAllPerspectives");
    await Assert.That(invoker.Invoked.Contains(LifecycleStage.PostLifecycleInline)).IsTrue()
      .Because("PostLifecycle rides the same no-local-perspective gate as PostAllPerspectives");
  }
}
