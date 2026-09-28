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
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #938, second defect: when an inbox row's payload could not be deserialized at dispatch, the
/// worker logged "(continuing): the payload could not be deserialized, which a retry will not change"
/// and then finished the row as processed. A composite in that state was never recognized as a
/// composite, so none of its inner events were fanned out, nothing was dead-lettered, and the row was
/// deleted: silent loss. A payload the serializer refuses is now dead-lettered with its body, through
/// the same inbox dead-letter path the attempts bound uses, and the row is never completed.
/// </summary>
/// <docs>messaging/transports/transport-consumer#unreadable-messages</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerUndeserializablePayloadTests {

  /// <summary>A composite the consumer registers; the stored payload below is not a valid one.</summary>
  public sealed class UndeserializableProbeComposite : CompositeEventBase;

  [Test]
  public async Task Dispatch_PayloadRefusedBySerializer_DeadLettersWithBody_AndNeverCompletesAsync() {
    var h = new Harness();
    var work = _work("""{"Inner":5,"StreamId":"not-a-guid"}""", _compositeTypeName());

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    var (sourceTable, sourceId, reason, errorText) = h.DeadLetters.Moves.Single();
    await Assert.That(sourceId).IsEqualTo(work.MessageId);
    await Assert.That(sourceTable).IsEqualTo(DeadLetterSourceTable.INBOX)
      .Because("the inbox dead-letter move snapshots the row's own body into the dead-letter store, which is what makes it replayable");
    await Assert.That(reason).IsEqualTo(MessageFailureReason.SerializationError);
    await Assert.That(errorText).Contains("could not be deserialized");
    await Assert.That(h.Commits.All).IsEmpty()
      .Because("a row whose payload failed to deserialize must never be completed: completion deletes it without a trace");
    await Assert.That(h.Inbox.Released).Contains(work.MessageId)
      .Because("dead-lettering is terminal and bypasses the commit channel, so the in-flight entry is released here");
    await Assert.That(h.Logs.Any(l => l.Level == LogLevel.Error && l.Message.Contains("dead-letter", StringComparison.OrdinalIgnoreCase))).IsTrue();
    await Assert.That(h.Logs.Any(l => l.Message.Contains("(continuing)", StringComparison.Ordinal))).IsFalse()
      .Because("the old log line promised the dispatch would continue, which is exactly what must not happen");
  }

  [Test]
  public async Task Dispatch_PayloadMissingMetadata_NotSupported_DeadLettersAndNeverCompletesAsync() {
    // System.Text.Json reports metadata it cannot find as NotSupportedException, which the lifecycle
    // deserializer does not wrap. It is as terminal on this build as malformed JSON.
    var h = new Harness(new ThrowingDeserializer(new NotSupportedException("JsonTypeInfo metadata for type 'X' was not provided")));
    var work = _work("{}", _compositeTypeName());

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(h.DeadLetters.Moves.Single().Reason).IsEqualTo(MessageFailureReason.SerializationError);
    await Assert.That(h.Commits.All).IsEmpty();
  }

  [Test]
  public async Task Dispatch_PayloadRefused_NoDeadLetterStore_RoutesFailure_AndNeverCompletesAsync() {
    var h = new Harness(deadLetters: NullDeadLetterStore.Instance);
    var work = _work("""{"Inner":5}""", _compositeTypeName());

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(h.Commits.All).IsEmpty()
      .Because("without a dead-letter store the row still must not be completed; it is failed, so the attempts bound governs it");
    var failure = h.Failures.All.Single();
    await Assert.That(failure.MessageId).IsEqualTo(work.MessageId);
    await Assert.That(failure.Reason).IsEqualTo(MessageFailureReason.SerializationError);
    await Assert.That(failure.Error).Contains("could not be deserialized");
  }

  [Test]
  public async Task Dispatch_PayloadRefused_DeadLetterMoveThrows_RoutesFailure_AndNeverCompletesAsync() {
    var h = new Harness(deadLetters: new ThrowingDeadLetterStore());
    var work = _work("""{"Inner":5}""", _compositeTypeName());

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(h.Commits.All).IsEmpty()
      .Because("a failed dead-letter move must not fall back to completing the row, as the attempts-bound path does; the row is failed instead");
    await Assert.That(h.Failures.All.Single().Reason).IsEqualTo(MessageFailureReason.SerializationError);
  }

  [Test]
  public async Task Dispatch_PayloadRefused_DeadLetterRowAlreadyGone_TerminalWithoutCompletionAsync() {
    var h = new Harness(deadLetters: new AlreadyGoneDeadLetterStore());
    var work = _work("""{"Inner":5}""", _compositeTypeName());

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(h.Commits.All).IsEmpty();
    await Assert.That(h.Failures.All).IsEmpty()
      .Because("a row someone else already dead-lettered is terminal; failing it again would only record a phantom error");
    await Assert.That(h.Inbox.Released).Contains(work.MessageId);
  }

  [Test]
  public async Task LifecycleStages_PayloadRefused_NoStageRuns_AndNoCompletionIsEnqueuedAsync() {
    var invoker = new CountingInvoker();
    var h = new Harness(invoker: invoker);
    var work = _work("""{"Inner":5}""", _compositeTypeName());

    await h.Worker.ProcessOneInnerAsync(work, CancellationToken.None);

    await Assert.That(invoker.Calls).IsEqualTo(0)
      .Because("no lifecycle stage has a message to run on");
    await Assert.That(h.Commits.All.Any(c => (c.InboxCompletion.Status & (int)MessageProcessingStatus.EventStored) != 0)).IsFalse()
      .Because("the lifecycle stage never marks a row processed after its payload failed to deserialize");
  }

  [Test]
  public async Task IsPayloadRefusal_ClassifiesTheSerializersRefusalsOnlyAsync() {
    await Assert.That(InboxDispatchWorker.IsPayloadRefusal(new InvalidOperationException("wrapped", new JsonException("bad")))).IsTrue()
      .Because("the lifecycle deserializer wraps a JsonException; the refusal is found through the chain");
    await Assert.That(InboxDispatchWorker.IsPayloadRefusal(new NotSupportedException("no metadata"))).IsTrue();
    await Assert.That(InboxDispatchWorker.IsPayloadRefusal(
      new InvalidOperationException("outer", new AggregateException(new TimeoutException(), new JsonException("inner"))))).IsTrue()
      .Because("a refusal inside an aggregate is still the payload's");
    await Assert.That(InboxDispatchWorker.IsPayloadRefusal(new AggregateException(new TimeoutException()))).IsFalse();
    await Assert.That(InboxDispatchWorker.IsPayloadRefusal(new InvalidOperationException("Failed to resolve message type"))).IsFalse()
      .Because("a type name this build does not register is not the payload refusing; it keeps the existing continue-and-log behavior");
  }

  // ------------------------------------------------------------------------------------------

  private static string _compositeTypeName() =>
    TypeNameFormatter.Format(typeof(UndeserializableProbeComposite));

  private static InboxWork _work(string payloadJson, string messageType) {
    var messageId = (Guid)TrackedGuid.New();
    return new InboxWork {
      MessageId = messageId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonDocument.Parse(payloadJson).RootElement.Clone(),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      },
      MessageType = messageType,
      StreamId = (Guid)TrackedGuid.New(),
      PartitionNumber = 1,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private sealed class Harness {
    public CapturingCommitChannel Commits { get; } = new();
    public CapturingFailureChannel Failures { get; } = new();
    public CapturingDeadLetterStore DeadLetters { get; } = new();
    public ReleasingInboxChannelWriter Inbox { get; } = new();
    public ConcurrentQueue<(LogLevel Level, string Message)> Logs { get; } = new();
    public InboxDispatchWorker Worker { get; }

    public Harness(ILifecycleMessageDeserializer? deserializer = null, IDeadLetterStore? deadLetters = null, IReceptorInvoker? invoker = null) {
      var services = new ServiceCollection();
      if (invoker is not null) {
        services.AddSingleton(invoker);
      }
      var sp = services.BuildServiceProvider();
      var gate = new SchemaReadyGate();
      gate.MarkReady();
      Worker = new InboxDispatchWorker(
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        instanceProvider: new FakeInstanceProvider(),
        inboxChannelWriter: Inbox,
        handlerCommitChannel: Commits,
        failureChannel: Failures,
        schemaReadyGate: gate,
        options: Options.Create(new InboxDispatchWorkerOptions()),
        coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
        logger: new RecordingLogger(Logs),
        integrityOptions: Options.Create(new StreamIntegrityOptions()),
        lifecycleMessageDeserializer: deserializer ?? new JsonLifecycleMessageDeserializer(),
        leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
        receptorRegistry: new PermissiveReceptorRegistryQuery(),
        discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance, new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
        runtimeReceptorRegistry: NullReceptorRegistry.Instance,
        deadLetterStore: deadLetters ?? DeadLetters,
        generationProvider: new DefaultGenerationProvider());
    }
  }

  private sealed class ThrowingDeserializer(Exception toThrow) : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => throw toThrow;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => throw toThrow;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => throw toThrow;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => throw toThrow;
  }

  private sealed class CountingInvoker : IReceptorInvoker {
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _calls);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "consumer-svc";
    public string HostName => "consumer-host";
    public int ProcessId => 12;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class ReleasingInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ConcurrentQueue<Guid> Released { get; } = new();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) => Released.Enqueue(messageId);
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class CapturingCommitChannel : IInboxHandlerCommitChannel {
    public ConcurrentQueue<HandlerCommitRequest> All { get; } = new();
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      All.Enqueue(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class CapturingFailureChannel : IFailureChannel {
    public ConcurrentQueue<MessageFailure> All { get; } = new();
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Enqueue(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class CapturingDeadLetterStore : IDeadLetterStore {
    public ConcurrentQueue<(string SourceTable, Guid SourceId, MessageFailureReason Reason, string? ErrorText)> Moves { get; } = new();
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      Moves.Enqueue((sourceTable, sourceId, failureReason, errorText));
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class ThrowingDeadLetterStore : IDeadLetterStore {
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default)
      => Task.FromException<Guid?>(new InvalidOperationException("simulated dead-letter store outage"));
  }

  private sealed class AlreadyGoneDeadLetterStore : IDeadLetterStore {
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default)
      => Task.FromResult<Guid?>(null);
  }

  private sealed class RecordingLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger<InboxDispatchWorker> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => entries.Enqueue((logLevel, formatter(state, exception)));
  }
}
