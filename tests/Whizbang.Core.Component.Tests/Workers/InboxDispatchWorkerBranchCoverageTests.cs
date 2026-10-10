// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branch-coverage tests for <see cref="InboxDispatchWorker"/>: the optional collaborators
/// (completion meter, dead-letter metrics, composite metrics, stream-integrity metrics in the
/// dispatch scope) each wired on the paths where the existing suites only ran them unwired,
/// the composite-commit failure with no composite meter, and the lifecycle gate fed an envelope
/// whose payload is null. Each test asserts the observable effect of the branch, not just that
/// the line ran.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerBranchCoverageTests {

  // ============================================================
  // Fakes (copied from the existing InboxDispatchWorker suites)
  // ============================================================

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
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
    public ConcurrentBag<Guid> RemovedInFlight { get; } = [];
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) => RemovedInFlight.Add(messageId);
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class FakeHandlerCommitChannel : IInboxHandlerCommitChannel {
    public ConcurrentBag<HandlerCommitRequest> All { get; } = [];
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      All.Add(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<(WorkCategory Category, MessageFailure Failure)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Add((category, failure));
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeGenerationProvider : IGenerationProvider {
    public string GetGeneration() => "test-generation";
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

  /// <summary>Deserializer whose every call is the serializer refusing the payload (a JsonException).</summary>
  private sealed class RefusingDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName)
      => throw new JsonException("simulated refusal: discriminator out of place");
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope)
      => throw new JsonException("simulated refusal: discriminator out of place");
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName)
      => throw new JsonException("simulated refusal: discriminator out of place");
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName)
      => throw new JsonException("simulated refusal: discriminator out of place");
  }

  // Returns a fixed composite so _resolveTypedEnvelope yields a typed ICompositeEvent payload.
  private sealed class FakeCompositeDeserializer(ICompositeEvent composite) : ILifecycleMessageDeserializer {
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => composite;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => composite;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => composite;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => composite;
  }

  // Minimal serializer for composite fan-out plumbing (mirrors InboxDispatchWorkerGapTests).
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

  /// <summary>Coordinator whose composite commit always throws; counts the attempts.</summary>
  private sealed class ThrowingCoordinator : IWorkCoordinator {
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public Task CommitHandlerResultAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _calls);
      throw new InvalidOperationException("store unavailable");
    }
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>Records every lifecycle stage invocation; never throws.</summary>
  private sealed class CapturingReceptorInvoker : IReceptorInvoker {
    public ConcurrentBag<LifecycleStage> Invocations { get; } = [];
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      Invocations.Add(stage);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Compile-time registry that reports no receptor for any stage.</summary>
  private sealed class EmptyReceptorRegistryQuery : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType) => false;
  }

  /// <summary>Runtime registry that answers one receptor for ANY resolved type at the given stage.</summary>
  private sealed class AnyTypeRuntimeReceptorRegistry(LifecycleStage answeredStage) : IReceptorRegistry {
    private static readonly IReadOnlyList<ReceptorInfo> _empty = [];
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      stage == answeredStage
        ? [new ReceptorInfo(
            MessageType: messageType,
            ReceptorId: "test-runtime-receptor",
            InvokeAsync: (_, _, _, _, _) => ValueTask.FromResult<object?>(null))]
        : _empty;
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage
      => throw new NotSupportedException("Test double: answers through GetReceptorsFor only.");
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage
      => throw new NotSupportedException("Test double: answers through GetReceptorsFor only.");
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private sealed record InnerImportEvent(string Id) : IEvent;

  /// <summary>A composite whose own cap is generous, so only the consumer budget can refuse it.</summary>
  private sealed class WideComposite(int count) : ICompositeEvent {
    public int MaxInnerEventsAllowed => 1_000_000;
    public IEnumerable<IMessage> InnerEvents => Enumerable.Range(0, count).Select(i => (IMessage)new InnerImportEvent($"W-{i}"));
  }

  private sealed class OverCapComposite(int count) : ICompositeEvent {
    public int MaxInnerEventsAllowed => 1;
    public IEnumerable<IMessage> InnerEvents => Enumerable.Range(0, count).Select(i => (IMessage)new InnerImportEvent($"J-{i}"));
  }

  private static InboxWork _makeWork(int attempts = 0, string messageType = "System.Text.Json.JsonElement, System.Text.Json") {
    var msgId = (Guid)TrackedGuid.New();
    return new InboxWork {
      MessageId = msgId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = messageType,
      StreamId = (Guid)TrackedGuid.New(),
      PartitionNumber = 1,
      Attempts = attempts,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private sealed class WorkerParts {
    private static MessageDiscardPolicy _permissiveDiscardPolicy() =>
      new(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance, new Meter("test"),
        Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance));

    public FakeInboxChannelWriter Inbox { get; } = new();
    public FakeHandlerCommitChannel HandlerCommit { get; } = new();
    public FakeFailureChannel Failure { get; } = new();
    public InboxDispatchWorkerOptions WorkerOptions { get; init; } = new();
    public StreamIntegrityOptions IntegrityOptions { get; init; } = new();
    public ILifecycleMessageDeserializer Deserializer { get; init; } = new JsonLifecycleMessageDeserializer();
    public IReceptorRegistryQuery ReceptorRegistry { get; init; } = new PermissiveReceptorRegistryQuery();
    public IReceptorRegistry RuntimeRegistry { get; init; } = NullReceptorRegistry.Instance;
    public IDeadLetterStore DeadLetterStore { get; init; } = NullDeadLetterStore.Instance;
    public DeadLetterMetrics? DlqMetrics { get; init; }
    public InboxMetrics? InboxMetrics { get; init; }
    public WorkCompletionMeter? CompletionMeter { get; init; }
    public CompositeMetrics? CompositeMetrics { get; init; }

    public InboxDispatchWorker Build(IServiceProvider sp) {
      var gate = new SchemaReadyGate();
      gate.MarkReady();
      return new InboxDispatchWorker(
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        instanceProvider: new FakeInstanceProvider(),
        inboxChannelWriter: Inbox,
        handlerCommitChannel: HandlerCommit,
        failureChannel: Failure,
        schemaReadyGate: gate,
        options: Options.Create(WorkerOptions),
        coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
        logger: NullLogger<InboxDispatchWorker>.Instance,
        integrityOptions: Options.Create(IntegrityOptions),
        lifecycleMessageDeserializer: Deserializer,
        leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
        receptorRegistry: ReceptorRegistry,
        discardPolicy: _permissiveDiscardPolicy(),
        runtimeReceptorRegistry: RuntimeRegistry,
        deadLetterStore: DeadLetterStore,
        generationProvider: new FakeGenerationProvider(),
        dlqMetrics: DlqMetrics,
        inboxMetrics: InboxMetrics,
        completionMeter: CompletionMeter,
        compositeMetrics: CompositeMetrics);
    }
  }

  private static List<(long Value, Dictionary<string, object?> Tags)> _nonZeroSeries(TestMeterFactory factory, string meterName, string instrument) =>
    [.. ProbeMeterReader.ReadSeries(factory.CreatedMeters.Single(m => m.Name == meterName), instrument).Where(s => s.Value != 0)];

  private static long _total(TestMeterFactory factory, string meterName, string instrument) =>
    ProbeMeterReader.ReadTotal(factory.CreatedMeters.Single(m => m.Name == meterName), instrument);

  private static string? _tag(Dictionary<string, object?> tags, string key) =>
    tags.TryGetValue(key, out var value) ? value?.ToString() : null;

  // ============================================================
  // _processOneAsync: completion meter wired (line 283, non-null side)
  // ============================================================

  /// <summary>
  /// With a completion meter wired, each dispatched row records exactly one completion in the
  /// dispatch's finally. The claim loop budgets against this drain rate, so a missed record reads
  /// as a service that cannot keep up and throttles it.
  /// </summary>
  [Test]
  public async Task ProcessedRow_WithCompletionMeterWired_RecordsExactlyOneCompletionAsync() {
    using var factory = new TestMeterFactory();
    var inboxMetrics = new InboxMetrics(new WhizbangMetrics(factory));
    var meter = new WorkCompletionMeter();
    var parts = new WorkerParts { InboxMetrics = inboxMetrics, CompletionMeter = meter };
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = parts.Build(sp);

    // The dispatch-duration histogram is recorded on the statement after the completion meter in
    // the same finally, so its measurement is a signal the worker body emits once the meter ran.
    var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument, inboxMetrics.DispatchDuration)) {
          l.EnableMeasurementEvents(instrument);
        }
      }
    };
    listener.SetMeasurementEventCallback<double>((_, _, _, _) => dispatched.TrySetResult());
    listener.Start();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await parts.Inbox.WriteAsync(_makeWork(), cts.Token);
    await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(meter.ReadAndReset()).IsEqualTo(1L)
      .Because("one row finished, so the drain meter the claim loop budgets against must read exactly one");
    await Assert.That(parts.HandlerCommit.All).Count().IsEqualTo(1);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Control-plane drop at max attempts (lines 368, 371)
  // ============================================================

  /// <summary>
  /// A control-plane message past its attempts is dropped, never stored, and with DLQ metrics
  /// wired the drop is still counted under its own reason and as an arrival, so a burst of
  /// failing control traffic stays visible on the dead-letter dashboard.
  /// </summary>
  [Test]
  public async Task ControlPlaneOverMaxAttempts_WithDlqMetrics_CountsDropWithoutStoringAsync() {
    using var factory = new TestMeterFactory();
    var store = new CapturingDeadLetterStore();
    var parts = new WorkerParts {
      WorkerOptions = new InboxDispatchWorkerOptions { MaxInboxAttempts = 3 },
      DeadLetterStore = store,
      DlqMetrics = new DeadLetterMetrics(new WhizbangMetrics(factory)),
    };
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = parts.Build(sp);

    var work = _makeWork(attempts: 4, messageType: typeof(RequestRedeliveryCommand).AssemblyQualifiedName!);
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(store.Moves).IsEmpty()
      .Because("a stored control-plane row is re-emitted on the next boot; it must be dropped");
    var routed = parts.HandlerCommit.All.Single();
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)(work.Status | MessageProcessingStatus.Published));

    var added = _nonZeroSeries(factory, DeadLetterMetrics.METER_NAME, "whizbang.dead_letters.added");
    await Assert.That(added).Count().IsEqualTo(1);
    await Assert.That(added[0].Value).IsEqualTo(1L);
    await Assert.That(_tag(added[0].Tags, DeadLetterMetrics.SOURCE_TABLE_TAG)).IsEqualTo(DeadLetterSourceTable.INBOX);
    await Assert.That(_tag(added[0].Tags, DeadLetterMetrics.REASON_TAG)).IsEqualTo("ControlPlaneDropped");

    var arrivals = _nonZeroSeries(factory, DeadLetterMetrics.METER_NAME, "whizbang.dead_letters.arrivals_by_stack");
    await Assert.That(arrivals).Count().IsEqualTo(1);
    await Assert.That(_tag(arrivals[0].Tags, DeadLetterMetrics.REASON_TAG))
      .IsEqualTo(((int)MessageFailureReason.PoisonRedeliveryLoop).ToString(CultureInfo.InvariantCulture));
    await Assert.That(_tag(arrivals[0].Tags, "stack_id")).IsEqualTo("none")
      .Because("a drop carries no error text, so it has no stack identity");
  }

  /// <summary>
  /// The same drop with no DLQ metrics wired still terminates the row and stores nothing.
  /// </summary>
  [Test]
  public async Task ControlPlaneOverMaxAttempts_WithoutDlqMetrics_StillTerminatesWithoutStoringAsync() {
    var store = new CapturingDeadLetterStore();
    var parts = new WorkerParts {
      WorkerOptions = new InboxDispatchWorkerOptions { MaxInboxAttempts = 3 },
      DeadLetterStore = store,
    };
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = parts.Build(sp);

    var work = _makeWork(attempts: 4, messageType: typeof(RequestRedeliveryCommand).AssemblyQualifiedName!);
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(store.Moves).IsEmpty();
    var routed = parts.HandlerCommit.All.Single();
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)(work.Status | MessageProcessingStatus.Published))
      .Because("an unmetered drop still has to end the row, or it re-claims forever");
  }

  // ============================================================
  // Undeserializable payload dead-lettered with metrics (lines 615, 618)
  // ============================================================

  /// <summary>
  /// A payload the serializer refuses is dead-lettered with its body; with DLQ metrics wired the
  /// move is counted as a SerializationError arrival from the inbox.
  /// </summary>
  [Test]
  public async Task UndeserializablePayload_StoreMoves_WithDlqMetrics_CountsSerializationErrorAsync() {
    using var factory = new TestMeterFactory();
    var store = new CapturingDeadLetterStore();
    var parts = new WorkerParts {
      Deserializer = new RefusingDeserializer(),
      DeadLetterStore = store,
      DlqMetrics = new DeadLetterMetrics(new WhizbangMetrics(factory)),
    };
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = parts.Build(sp);

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var (_, _, reason) = store.Moves.Single();
    await Assert.That(reason).IsEqualTo(MessageFailureReason.SerializationError);
    await Assert.That(parts.HandlerCommit.All).IsEmpty()
      .Because("a refused payload is never completed; the move deleted the row");
    await Assert.That(parts.Failure.All).IsEmpty();
    await Assert.That(parts.Inbox.RemovedInFlight.Contains(work.MessageId)).IsTrue();

    var added = _nonZeroSeries(factory, DeadLetterMetrics.METER_NAME, "whizbang.dead_letters.added");
    await Assert.That(added).Count().IsEqualTo(1);
    await Assert.That(added[0].Value).IsEqualTo(1L);
    await Assert.That(_tag(added[0].Tags, DeadLetterMetrics.REASON_TAG)).IsEqualTo(nameof(MessageFailureReason.SerializationError));

    var arrivals = _nonZeroSeries(factory, DeadLetterMetrics.METER_NAME, "whizbang.dead_letters.arrivals_by_stack");
    await Assert.That(arrivals).Count().IsEqualTo(1);
    await Assert.That(_tag(arrivals[0].Tags, DeadLetterMetrics.SOURCE_TABLE_TAG)).IsEqualTo(DeadLetterSourceTable.INBOX);
    await Assert.That(_tag(arrivals[0].Tags, DeadLetterMetrics.REASON_TAG))
      .IsEqualTo(((int)MessageFailureReason.SerializationError).ToString(CultureInfo.InvariantCulture));
  }

  // ============================================================
  // Repair bundle discarded with integrity metrics in scope (line 647)
  // ============================================================

  /// <summary>
  /// Under report-only, a re-delivery bundle is discarded; when the dispatch scope carries the
  /// stream-integrity metrics, the discard is counted under the consumer_bundle role.
  /// </summary>
  [Test]
  public async Task RepairBundleDiscarded_WithIntegrityMetricsInScope_CountsConsumerBundleAsync() {
    using var factory = new TestMeterFactory();
    var integrityMetrics = new StreamIntegrityMetrics(new WhizbangMetrics(factory));
    var bundle = new RedeliveryComposite { OriginServiceId = (Guid)TrackedGuid.New() };
    for (var i = 0; i < 2; i++) {
      bundle.InnerPayloads.Add(JsonDocument.Parse("{}").RootElement);
      bundle.InnerTypeNames.Add(typeof(InnerImportEvent).AssemblyQualifiedName!);
      bundle.InnerEventIds.Add((Guid)TrackedGuid.New());
    }
    var parts = new WorkerParts {
      Deserializer = new FakeCompositeDeserializer(bundle),
      IntegrityOptions = new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly },
    };
    await using var sp = new ServiceCollection()
      .AddSingleton(integrityMetrics)
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();
    var worker = parts.Build(sp);

    await worker.ProcessOneInnerAsync(_makeWork(), CancellationToken.None);

    var routed = parts.HandlerCommit.All.Single();
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)MessageProcessingStatus.EventStored);
    await Assert.That(routed.NewInboxMessages is null || routed.NewInboxMessages.Count == 0).IsTrue();

    var discarded = _nonZeroSeries(factory, StreamIntegrityMetrics.METER_NAME, "whizbang.stream_integrity.repair_traffic_discarded");
    await Assert.That(discarded).Count().IsEqualTo(1);
    await Assert.That(discarded[0].Value).IsEqualTo(1L);
    await Assert.That(_tag(discarded[0].Tags, "role")).IsEqualTo("consumer_bundle")
      .Because("the consumer side of report-only must be distinguishable from the origin's refusals");
  }

  // ============================================================
  // Composite metrics on the refusal and failure paths (lines 747, 770, 863)
  // ============================================================

  /// <summary>
  /// An enforced budget refusal counts every refused child and one dead-lettered composite.
  /// </summary>
  [Test]
  public async Task CompositeOverConsumerBudget_WithCompositeMetrics_CountsRefusedChildrenAndDeadLetterAsync() {
    using var factory = new TestMeterFactory();
    var parts = new WorkerParts {
      Deserializer = new FakeCompositeDeserializer(new WideComposite(12)),
      WorkerOptions = new InboxDispatchWorkerOptions { MaxCompositeChildrenPerExpansion = 3, EnforceCompositeExpansionBudget = true },
      CompositeMetrics = new CompositeMetrics(new WhizbangMetrics(factory)),
    };
    await using var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();
    var worker = parts.Build(sp);

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var routed = parts.HandlerCommit.All.Single();
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)(work.Status | MessageProcessingStatus.Published));
    await Assert.That(routed.NewInboxMessages is null || routed.NewInboxMessages.Count == 0).IsTrue();
    await Assert.That(_total(factory, CompositeMetrics.METER_NAME, "whizbang.composites.children_refused")).IsEqualTo(12L)
      .Because("the meter carries the size of what was refused so an operator can size the budget");
    await Assert.That(_total(factory, CompositeMetrics.METER_NAME, "whizbang.composites.dead_lettered")).IsEqualTo(1L);
  }

  /// <summary>
  /// A composite past its own cap is dead-lettered; with composite metrics wired it counts as
  /// received and dead-lettered, never as an expansion.
  /// </summary>
  [Test]
  public async Task CompositeOverOwnCap_WithCompositeMetrics_CountsDeadLetterNotExpansionAsync() {
    using var factory = new TestMeterFactory();
    var parts = new WorkerParts {
      Deserializer = new FakeCompositeDeserializer(new OverCapComposite(5)),
      CompositeMetrics = new CompositeMetrics(new WhizbangMetrics(factory)),
    };
    await using var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();
    var worker = parts.Build(sp);

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var routed = parts.HandlerCommit.All.Single();
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)(work.Status | MessageProcessingStatus.Published));
    await Assert.That(_total(factory, CompositeMetrics.METER_NAME, "whizbang.composites.received")).IsEqualTo(1L);
    await Assert.That(_total(factory, CompositeMetrics.METER_NAME, "whizbang.composites.dead_lettered")).IsEqualTo(1L);
    await Assert.That(_total(factory, CompositeMetrics.METER_NAME, "whizbang.composites.expansions")).IsEqualTo(0L)
      .Because("a cap breach is not an expansion; counting it would hide the failure inside the success rate");
  }

  // ============================================================
  // Composite commit failure without composite metrics (line 823, null side)
  // ============================================================

  /// <summary>
  /// A failed composite commit with no composite meter wired is still swallowed, and the row is
  /// still released for the claim loop to re-offer.
  /// </summary>
  [Test]
  public async Task CompositeCommitFails_WithoutCompositeMetrics_StillReleasesRowForRetryAsync() {
    var coordinator = new ThrowingCoordinator();
    var parts = new WorkerParts {
      Deserializer = new FakeCompositeDeserializer(new WideComposite(2)),
    };
    await using var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .AddScoped<IWorkCoordinator>(_ => coordinator)
      .BuildServiceProvider();
    var worker = parts.Build(sp);

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(coordinator.Calls).IsEqualTo(1);
    await Assert.That(parts.Inbox.RemovedInFlight.Contains(work.MessageId)).IsTrue()
      .Because("a failed commit leaves the row unprocessed; its in-flight entry must not block the re-offer");
    await Assert.That(parts.HandlerCommit.All).IsEmpty()
      .Because("the expansion is committed through the coordinator only, never re-routed to the batched channel");
    await Assert.That(parts.Failure.All).IsEmpty();
  }

  // ============================================================
  // Lifecycle gate with a null payload (line 1013)
  // ============================================================

  /// <summary>
  /// An envelope whose payload is null resolves no runtime type, so the runtime registry (which
  /// would answer for any real type) is not consulted and the gated stage is skipped.
  /// </summary>
  [Test]
  public async Task LifecycleStage_NullPayload_NoRuntimeTypeToConsult_SkipsGatedStageAsync() {
    var parts = new WorkerParts {
      ReceptorRegistry = new EmptyReceptorRegistryQuery(),
      RuntimeRegistry = new AnyTypeRuntimeReceptorRegistry(LifecycleStage.PreInboxInline),
    };
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = parts.Build(sp);
    var work = _makeWork();
    var nullPayloadEnvelope = new MessageEnvelope<InnerImportEvent> {
      MessageId = MessageId.From(work.MessageId),
      Payload = null!,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
    };
    var invoker = new CapturingReceptorInvoker();

    await worker.InvokeInboxLifecycleStageAsync(
      work, nullPayloadEnvelope, invoker,
      LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
      "PreInbox", CancellationToken.None);

    await Assert.That(invoker.Invocations).IsEmpty()
      .Because("with no payload there is no type to ask the runtime registry about, and the compile-time registry has nothing");
    await Assert.That(parts.Failure.All).IsEmpty();
  }

  /// <summary>
  /// The control for the null-payload case: the same registries with a real payload fire the
  /// inline stage, proving the runtime registry is what the null payload bypassed.
  /// </summary>
  [Test]
  public async Task LifecycleStage_RealPayload_RuntimeRegistryAnswers_FiresInlineStageAsync() {
    var parts = new WorkerParts {
      ReceptorRegistry = new EmptyReceptorRegistryQuery(),
      RuntimeRegistry = new AnyTypeRuntimeReceptorRegistry(LifecycleStage.PreInboxInline),
    };
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = parts.Build(sp);
    var work = _makeWork();
    var invoker = new CapturingReceptorInvoker();

    await worker.InvokeInboxLifecycleStageAsync(
      work, work.Envelope, invoker,
      LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
      "PreInbox", CancellationToken.None);

    await Assert.That(invoker.Invocations).Contains(LifecycleStage.PreInboxInline);
    await Assert.That(invoker.Invocations).DoesNotContain(LifecycleStage.PreInboxDetached)
      .Because("the runtime registry answers only for the inline stage");
  }

  // ============================================================
  // Dead-letter moves that suspend (lines 378, 602, 772, 864)
  // ============================================================
  // Each IsConfigured check is followed by a try whose MoveAsync a real store completes
  // asynchronously. Every other suite's store completes synchronously, so the state machine's
  // resume path into that try had never run; these tests make the move suspend once and assert
  // the terminal behavior still happens after resumption.

  /// <summary>DLQ store whose move suspends before it records and succeeds, as a database write does.</summary>
  private sealed class YieldingDeadLetterStore : IDeadLetterStore {
    public ConcurrentBag<(string SourceTable, Guid SourceId, MessageFailureReason Reason)> Moves { get; } = [];
    public async Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      await Task.Yield();
      Moves.Add((sourceTable, sourceId, failureReason));
      return deadLetterId;
    }
  }

  [Test]
  public async Task MaxAttemptsExceeded_DeadLetterMoveSuspends_StillReleasesTheRowWithoutCompletingAsync() {
    var store = new YieldingDeadLetterStore();
    var parts = new WorkerParts {
      WorkerOptions = new InboxDispatchWorkerOptions { MaxInboxAttempts = 3 },
      DeadLetterStore = store,
    };
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = parts.Build(sp);

    var work = _makeWork(attempts: 4);
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var (sourceTable, sourceId, reason) = store.Moves.Single();
    await Assert.That(sourceTable).IsEqualTo(DeadLetterSourceTable.INBOX);
    await Assert.That(sourceId).IsEqualTo(work.MessageId);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.MaxAttemptsExceeded);
    await Assert.That(parts.HandlerCommit.All).IsEmpty()
      .Because("after the suspended move resumes, the row is gone and the legacy terminal commit must be skipped");
    await Assert.That(parts.Inbox.RemovedInFlight.Contains(work.MessageId)).IsTrue();
  }

  [Test]
  public async Task UndeserializablePayload_DeadLetterMoveSuspends_StillReleasesTheRowWithoutFailureAsync() {
    var store = new YieldingDeadLetterStore();
    var parts = new WorkerParts {
      Deserializer = new RefusingDeserializer(),
      DeadLetterStore = store,
    };
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = parts.Build(sp);

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var (_, sourceId, reason) = store.Moves.Single();
    await Assert.That(sourceId).IsEqualTo(work.MessageId);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.SerializationError);
    await Assert.That(parts.Failure.All).IsEmpty()
      .Because("a move that resumed successfully is terminal; routing a failure as well would re-feed the row");
    await Assert.That(parts.HandlerCommit.All).IsEmpty();
    await Assert.That(parts.Inbox.RemovedInFlight.Contains(work.MessageId)).IsTrue();
  }

  [Test]
  public async Task CompositeOverOwnCap_DeadLetterMoveSuspends_StillReleasesTheRowWithoutCompletingAsync() {
    var store = new YieldingDeadLetterStore();
    var parts = new WorkerParts {
      Deserializer = new FakeCompositeDeserializer(new OverCapComposite(5)),
      DeadLetterStore = store,
    };
    await using var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();
    var worker = parts.Build(sp);

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var (_, sourceId, reason) = store.Moves.Single();
    await Assert.That(sourceId).IsEqualTo(work.MessageId);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.CompositeInnerEventLimitExceeded);
    await Assert.That(parts.HandlerCommit.All).IsEmpty()
      .Because("the resumed move deleted the composite row, so neither children nor a terminal commit may follow");
    await Assert.That(parts.Inbox.RemovedInFlight.Contains(work.MessageId)).IsTrue();
  }

  [Test]
  public async Task CompositeOverConsumerBudget_DeadLetterMoveSuspends_StillReleasesTheRowWithoutCompletingAsync() {
    var store = new YieldingDeadLetterStore();
    var parts = new WorkerParts {
      Deserializer = new FakeCompositeDeserializer(new WideComposite(12)),
      WorkerOptions = new InboxDispatchWorkerOptions { MaxCompositeChildrenPerExpansion = 3, EnforceCompositeExpansionBudget = true },
      DeadLetterStore = store,
    };
    await using var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();
    var worker = parts.Build(sp);

    var work = _makeWork();
    await worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var (_, sourceId, reason) = store.Moves.Single();
    await Assert.That(sourceId).IsEqualTo(work.MessageId);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.CompositeInnerEventLimitExceeded);
    await Assert.That(parts.HandlerCommit.All).IsEmpty()
      .Because("a refused expansion is dead-lettered, and after the move resumes nothing is committed for it");
    await Assert.That(parts.Inbox.RemovedInFlight.Contains(work.MessageId)).IsTrue();
  }
}
