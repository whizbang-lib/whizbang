// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;

#pragma warning disable CS0618 // The obsolete LocalInvokeAndSync overloads are still shipped and still record their duration.
#pragma warning disable RCS1163 // Unused parameter: fakes match interface and override signatures.

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// What the dispatcher does on its optional seams once they are switched on: a dispatch span carries the
/// message type, id and correlation, and an outbox send under a caller's span names that caller; the
/// envelope registry sees the envelope for exactly the receptor's lifetime; every batch, cascade, publish
/// failure and invoke-and-sync is measured once registered metrics exist; the re-emission diagnostic hears
/// the options publish; a missing outbox strategy is explained in the log before the send fails; a stream id
/// the extractor cannot find counts as empty for generation; an event-store-only deferral carries no
/// destination; a null inner event of an atomic composite is skipped rather than failing the publish.
/// </summary>
/// <remarks>
/// Every message type here is this class's own (prefix "Bcb"), so spans and measurements from other tests
/// can never be mistaken for these, and the cases run one at a time because an ActivityListener is
/// process-wide. Metrics go to a meter factory of the test's own.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
[NotInParallel(nameof(DispatcherBranchCoverageBTests))]
public sealed class DispatcherBranchCoverageBTests {

  // ========================================
  // MESSAGES
  // ========================================

  private sealed record BcbCommand(Guid Id);
  private sealed record BcbResult(Guid Id);
  private sealed record BcbVoidCommand(Guid Id);
  private sealed record BcbOutboxCommand(string Data);
  private sealed record BcbUnhandledCommand(string Data);
  private sealed record BcbCascadeCommand(string Data) : ICommand;
  private sealed record BcbEvent([property: StreamId] Guid Id) : IEvent;
  private sealed record BcbOutboxEvent([property: StreamId] Guid Id) : IEvent;
  private sealed record BcbGeneratedEvent([property: StreamId] Guid Id) : IEvent;
  private sealed record BcbInnerEvent([property: StreamId] Guid Id, string Name) : IEvent;
  private sealed class BcbComposite : CompositeEventBase;
  private sealed class BcbPerspective;

  private const string PARENT_SOURCE_NAME = "Whizbang.Tests.DispatcherBranchCoverageB";
  private static readonly ActivitySource _parentSource = new(PARENT_SOURCE_NAME);

  // ========================================
  // TEST DISPATCHER
  // ========================================

  /// <summary>A dispatcher whose generated lookups answer from properties, so each test chooses its receptors.</summary>
  private sealed class ProbeDispatcher(
    IServiceProvider sp,
    ITraceStore? traceStore = null,
    IEnvelopeRegistry? envelopeRegistry = null,
    IEnvelopeSerializer? envelopeSerializer = null,
    IStreamIdExtractor? streamIdExtractor = null
    ) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      traceStore: traceStore,
      envelopeSerializer: envelopeSerializer,
      envelopeRegistry: envelopeRegistry,
      streamIdExtractor: streamIdExtractor) {
    public Type? HandledType { get; init; }
    public Func<object, object?> Handler { get; init; } = static _ => null;
    public bool PublisherThrows { get; init; }
    public List<IMessage> OutboxCascades { get; } = [];
    public List<IMessage> EventStoreCascades { get; } = [];

    public Task CallPublishToOutboxAsync<TEvent>(TEvent eventData, MessageId messageId, bool eventStoreOnly) =>
      PublishToOutboxAsync(eventData, typeof(TEvent), messageId, sourceEnvelope: null, eventStoreOnly: eventStoreOnly);

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      if (messageType != HandledType) {
        return null;
      }
      return msg => new ValueTask<TResult>((TResult)Handler(msg)!);
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      if (messageType != HandledType) {
        return null;
      }
      return msg => {
        Handler(msg);
        return ValueTask.CompletedTask;
      };
    }

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) {
      if (PublisherThrows) {
        return _ => throw new InvalidOperationException("bcb publisher failed");
      }
      return _ => Task.CompletedTask;
    }

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;

    protected override Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      OutboxCascades.Add(message);
      return Task.CompletedTask;
    }

    protected override Task CascadeToEventStoreOnlyAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      EventStoreCascades.Add(message);
      return Task.CompletedTask;
    }
  }

  // ========================================
  // FAKES
  // ========================================

  private sealed record LogEntry(string Category, LogLevel Level, string Message);

  private sealed class CapturingLoggerProvider(List<LogEntry> sink) : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

    public void Dispose() {
      // The sink is owned by the test.
    }

    private sealed class CapturingLogger(string category, List<LogEntry> sink) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        lock (sink) {
          sink.Add(new LogEntry(category, logLevel, formatter(state, exception)));
        }
      }
    }
  }

  /// <summary>The test's own meter factory and a listener on it.</summary>
  private sealed class Metered : IDisposable {
    private readonly TestMeterFactory _factory = new();

    public Metered() {
      Instruments = new DispatcherMetrics(new WhizbangMetrics(_factory));
      Recorded = new MetricAssertionHelper([.. _factory.CreatedMeters]);
    }

    public DispatcherMetrics Instruments { get; }
    public MetricAssertionHelper Recorded { get; }

    public List<RecordedMeasurement> Of(string instrument) => Recorded.GetByName(instrument);

    public double Sum(string instrument, string tag, string value) =>
      Of(instrument).Where(m => m.Tags.GetValueOrDefault(tag) == value).Sum(m => m.Value);

    public void Dispose() {
      Recorded.Dispose();
      _factory.Dispose();
    }
  }

  /// <summary>Samples the dispatcher's source and this class's parent source; keeps the "Dispatch Bcb..." spans.</summary>
  private sealed class SpanCapture : IDisposable {
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = [];

    public SpanCapture() {
      _listener = new ActivityListener {
        ShouldListenTo = source => source.Name == "Whizbang.Execution" || source.Name == PARENT_SOURCE_NAME,
        Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = activity => {
          if (activity.OperationName.StartsWith("Dispatch Bcb", StringComparison.Ordinal)) {
            lock (_stopped) {
              _stopped.Add(activity);
            }
          }
        }
      };
      ActivitySource.AddActivityListener(_listener);
    }

    public Activity SpanFor(string operation, string messageId) {
      lock (_stopped) {
        return _stopped.Single(a => a.OperationName == operation && Equals(a.GetTagItem("whizbang.message.id"), messageId));
      }
    }

    public void Dispose() => _listener.Dispose();
  }

  private sealed class RecordingTraceStore : ITraceStore {
    public List<IMessageEnvelope> Stored { get; } = [];

    public Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default) {
      Stored.Add(envelope);
      return Task.CompletedTask;
    }

    public Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default) =>
      Task.FromResult(Stored.Find(e => e.MessageId == messageId));

    public Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default) =>
      Task.FromResult(new List<IMessageEnvelope>());

    public Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default) =>
      Task.FromResult(new List<IMessageEnvelope>());

    public Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset toTime, CancellationToken ct = default) =>
      Task.FromResult(new List<IMessageEnvelope>());
  }

  private sealed class RecordingEnvelopeRegistry : IEnvelopeRegistry {
    public List<MessageId> Registered { get; } = [];
    public List<MessageId> Unregistered { get; } = [];

    public void Register<T>(MessageEnvelope<T> envelope) => Registered.Add(envelope.MessageId);
    public MessageEnvelope<T>? TryGetEnvelope<T>(T message) where T : notnull => null;
    public void Unregister<T>(T message) where T : notnull => Unregistered.Add((message as IMessageEnvelope)?.MessageId ?? default);
    public void Unregister<T>(MessageEnvelope<T> envelope) => Unregistered.Add(envelope.MessageId);
  }

  private sealed class RecordingStreamIdExtractor : IStreamIdExtractor {
    private readonly Dictionary<object, Guid> _assigned = new(ReferenceEqualityComparer.Instance);

    public Func<object, Guid?>? OnExtract { get; init; }
    public Func<object, (bool ShouldGenerate, bool OnlyIfEmpty)>? OnPolicy { get; init; }
    public List<(object Message, Guid StreamId)> SetCalls { get; } = [];

    public Guid? ExtractStreamId(object message, Type messageType) =>
      _assigned.TryGetValue(message, out var assigned) ? assigned : OnExtract?.Invoke(message);

    public (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) =>
      OnPolicy?.Invoke(message) ?? (false, false);

    public bool SetStreamId(object message, Guid streamId) {
      SetCalls.Add((message, streamId));
      _assigned[message] = streamId;
      return true;
    }
  }

  private sealed class FakeEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = envelope.Hops?.ToList() ?? [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      var messageType = typeof(TMessage).AssemblyQualifiedName ?? typeof(TMessage).Name;
      return new SerializedEnvelope(jsonEnvelope, $"Envelope[[{messageType}]]", messageType);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => new();
  }

  private sealed class RecordingWorkStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> Queued { get; } = [];

    public void QueueOutboxMessage(OutboxMessage message) => Queued.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  private sealed class RecordingDeferredChannel : IDeferredOutboxChannel {
    public List<OutboxMessage> Queued { get; } = [];

    public ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default) {
      Queued.Add(message);
      return ValueTask.CompletedTask;
    }

    public IReadOnlyList<OutboxMessage> DrainAll() {
      var drained = Queued.ToList();
      Queued.Clear();
      return drained;
    }

    public bool HasPending => Queued.Count > 0;
  }

  private sealed class ConsumesEventRegistry : IReceptorRegistryQuery {
    public bool HasAnyConsumer(string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public IReadOnlyList<HandledMessageInfo> GetHandledMessages() =>
      [new HandledMessageInfo(TypeNameFormatter.Format(typeof(BcbEvent)), "tests", MessageKind.Event)];
  }

  // ========================================
  // HELPERS
  // ========================================

  private static ServiceProvider _provider(Metered? metered = null, List<LogEntry>? logs = null, Action<IServiceCollection>? configure = null) {
    var services = new ServiceCollection();
    if (metered is not null) {
      services.AddSingleton(metered.Instruments);
    }
    if (logs is not null) {
      services.AddLogging(builder => {
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddProvider(new CapturingLoggerProvider(logs));
      });
    }
    configure?.Invoke(services);
    return services.BuildServiceProvider();
  }

  private static List<LogEntry> _snapshot(List<LogEntry> logs) {
    lock (logs) {
      return [.. logs];
    }
  }

  private static Guid _newId() => (Guid)TrackedGuid.New();

  // ========================================
  // VOID LOCAL INVOKE WITH OPTIONS + TRACE STORE: the dispatch span's tags
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptionsAndTraceStore_TagsTheDispatchSpanAsync() {
    using var spans = new SpanCapture();
    var traceStore = new RecordingTraceStore();
    var invoked = 0;
    await using var provider = _provider();
    var dispatcher = new ProbeDispatcher(provider, traceStore: traceStore) {
      HandledType = typeof(BcbVoidCommand),
      Handler = _ => {
        invoked++;
        return null;
      }
    };

    await dispatcher.LocalInvokeAsync((object)new BcbVoidCommand(_newId()), new DispatchOptions());

    await Assert.That(invoked).IsEqualTo(1);
    await Assert.That(traceStore.Stored).Count().IsEqualTo(1)
      .Because("the trace store is what routes a void options invoke through the traced path");
    var envelope = traceStore.Stored[0];
    var span = spans.SpanFor("Dispatch BcbVoidCommand", envelope.MessageId.ToString());
    await Assert.That((string?)span.GetTagItem("whizbang.message.type")).Contains("BcbVoidCommand");
    await Assert.That(span.GetTagItem("whizbang.correlation.id")).IsNotNull();
    await Assert.That(span.GetTagItem("whizbang.correlation.id")).IsEqualTo(envelope.GetCorrelationId()?.ToString());
  }

  // ========================================
  // LOCAL INVOKE WITH RECEIPT + OPTIONS: the envelope registry spans the receptor
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptionsAndEnvelopeRegistry_RegistersForTheReceptorThenUnregistersAsync() {
    var registry = new RecordingEnvelopeRegistry();
    var registeredDuringReceptor = -1;
    var unregisteredDuringReceptor = -1;
    await using var provider = _provider();
    var dispatcher = new ProbeDispatcher(provider, envelopeRegistry: registry) {
      HandledType = typeof(BcbCommand),
      Handler = msg => {
        registeredDuringReceptor = registry.Registered.Count;
        unregisteredDuringReceptor = registry.Unregistered.Count;
        return new BcbResult(((BcbCommand)msg).Id);
      }
    };
    var command = new BcbCommand(_newId());

    var invoked = await dispatcher.LocalInvokeWithReceiptAsync<BcbResult>(command, new DispatchOptions());

    await Assert.That(invoked.Value.Id).IsEqualTo(command.Id);
    await Assert.That(registeredDuringReceptor).IsEqualTo(1)
      .Because("the envelope is registered before the receptor runs, so the receptor can find it");
    await Assert.That(unregisteredDuringReceptor).IsEqualTo(0)
      .Because("the envelope stays registered for the whole receptor call");
    await Assert.That(registry.Registered).IsEquivalentTo([invoked.Receipt.MessageId]);
    await Assert.That(registry.Unregistered).IsEquivalentTo([invoked.Receipt.MessageId])
      .Because("the same envelope is unregistered once the invocation finishes");
  }

  // ========================================
  // CASCADE OF A NULL RESULT: the debug line names the result "null"
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_ReceptorReturnsNull_DebugLogNamesTheResultNullAndSkipsCascadeAsync() {
    var logs = new List<LogEntry>();
    await using var provider = _provider(logs: logs);
    var dispatcher = new ProbeDispatcher(provider) {
      HandledType = typeof(BcbCommand),
      Handler = _ => null
    };

    var invoked = await dispatcher.LocalInvokeWithReceiptAsync<BcbResult?>(new BcbCommand(_newId()), new DispatchOptions());

    await Assert.That(invoked.Value).IsNull();
    var cascadeLogs = _snapshot(logs).Where(l => l.Category == "Whizbang.Core.Dispatcher.Cascade").ToList();
    await Assert.That(cascadeLogs.Any(l => l.Level == LogLevel.Debug
                                        && l.Message.Contains("ResultType=null", StringComparison.Ordinal)
                                        && l.Message.Contains("OriginalMessageType=BcbCommand", StringComparison.Ordinal)))
      .IsTrue()
      .Because("a null result has no type to name, so the debug line says null rather than failing");
    await Assert.That(cascadeLogs.Any(l => l.Level == LogLevel.Warning
                                        && l.Message.Contains("Result is null, skipping cascade", StringComparison.Ordinal)))
      .IsTrue();
  }

  // ========================================
  // CascadeMessageAsync WITH METRICS
  // ========================================

  [Test]
  public async Task CascadeMessageAsync_WithMetrics_RecordsDurationAndCountsTheCascadeByTypeAndDestinationAsync() {
    using var metered = new Metered();
    await using var provider = _provider(metered);
    var dispatcher = new ProbeDispatcher(provider);
    var command = new BcbCascadeCommand("cascade");

    await dispatcher.CascadeMessageAsync(command, sourceEnvelope: null, DispatchModes.Outbox);

    await Assert.That(dispatcher.OutboxCascades).IsEquivalentTo([(IMessage)command]);
    await Assert.That(metered.Of("whizbang.dispatcher.cascade.duration")).Count().IsEqualTo(1);
    await Assert.That(metered.Of("whizbang.dispatcher.events_cascaded")
        .Where(m => m.Tags.GetValueOrDefault("event_type") == nameof(BcbCascadeCommand)
                 && m.Tags.GetValueOrDefault("destination") == nameof(DispatchModes.Outbox))
        .Sum(m => m.Value))
      .IsEqualTo(1);
  }

  // ========================================
  // PublishAsync(event) FAILURE WITH METRICS
  // ========================================

  [Test]
  public async Task PublishAsync_ReceptorThrows_WithMetrics_CountsTheErrorByTypeAndRethrowsAsync() {
    using var metered = new Metered();
    await using var provider = _provider(metered);
    var dispatcher = new ProbeDispatcher(provider) { PublisherThrows = true };

    await Assert.That(async () => await dispatcher.PublishAsync(new BcbEvent(_newId())))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("bcb publisher failed");

    var errors = metered.Of("whizbang.dispatcher.errors")
      .Where(m => m.Tags.GetValueOrDefault("message_type") == nameof(BcbEvent)
               && m.Tags.GetValueOrDefault("error_type") == nameof(InvalidOperationException))
      .Sum(m => m.Value);
    await Assert.That(errors).IsEqualTo(1);
    await Assert.That(metered.Sum("whizbang.dispatcher.messages_dispatched", "pattern", "publish")).IsEqualTo(0)
      .Because("a publish that failed was not dispatched");
    await Assert.That(metered.Of("whizbang.dispatcher.publish.duration")).Count().IsEqualTo(1)
      .Because("the duration is recorded on failure too");
  }

  // ========================================
  // PublishAsync(event, options) FEEDS THE RE-EMISSION DIAGNOSTIC
  // ========================================

  [Test]
  public async Task PublishAsync_WithOptions_OfAConsumedType_FiresTheReEmissionDiagnosticAsync() {
    using var metered = new Metered();
    var registry = new ConsumesEventRegistry();
    await using var provider = _provider(metered, configure: services => {
      services.AddSingleton<IReceptorRegistryQuery>(registry);
      services.AddSingleton(new ReEmissionDiagnostic(registry, null, metered.Instruments));
    });
    var dispatcher = new ProbeDispatcher(provider);

    await dispatcher.PublishAsync(new BcbEvent(_newId()), new DispatchOptions());

    await Assert.That(metered.Of("whizbang.dispatcher.re_emissions").Sum(m => m.Value)).IsEqualTo(1)
      .Because("the options publish is an emission seam too: publishing a consumed type counts once");
  }

  // ========================================
  // STREAM ID GENERATION: an id the extractor cannot find counts as empty
  // ========================================

  [Test]
  public async Task PublishAsync_GenerateOnlyIfEmpty_ExtractorFindsNoStreamId_GeneratesOneAsync() {
    var extractor = new RecordingStreamIdExtractor {
      OnPolicy = _ => (true, true),
      OnExtract = _ => null
    };
    await using var provider = _provider();
    var dispatcher = new ProbeDispatcher(provider, streamIdExtractor: extractor);

    var receipt = await dispatcher.PublishAsync(new BcbGeneratedEvent(Guid.Empty));

    await Assert.That(extractor.SetCalls).Count().IsEqualTo(1)
      .Because("no stream id at all is treated as an empty one, so 'only if empty' generates");
    await Assert.That(extractor.SetCalls[0].StreamId).IsNotEqualTo(Guid.Empty);
    await Assert.That(receipt.StreamId).IsEqualTo(extractor.SetCalls[0].StreamId);
  }

  // ========================================
  // COMPOSITE FAN-OUT: a null inner event is skipped
  // ========================================

  [Test]
  public async Task FanOutCompositeLocallyAtPublishAsync_AtomicCompositeWithNullInner_SkipsItAndFansOutTheRestAsync() {
    await using var provider = _provider();
    var dispatcher = new ProbeDispatcher(provider);
    var inner = new BcbInnerEvent(_newId(), "kept");
    var composite = new BcbComposite {
      StreamId = _newId(),
      Atomicity = FanoutAtomicity.Atomic,
      Inner = [null!, inner]
    };

    await dispatcher.FanOutCompositeLocallyAtPublishAsync(composite, typeof(BcbComposite), MessageId.New());

    await Assert.That(dispatcher.EventStoreCascades).IsEquivalentTo([(IMessage)inner])
      .Because("an atomic composite would fail on a null child; skipping it lets the real children fan out");
    await Assert.That(dispatcher.OutboxCascades).IsEmpty();
  }

  // ========================================
  // SEND WITH NO RECEPTOR AND NO STRATEGY: the warning explains the failure
  // ========================================

  [Test]
  [Arguments("generic")]
  [Arguments("object")]
  public async Task SendAsync_NoReceptorAndNoStrategy_LogsWhyThenThrowsReceptorNotFoundAsync(string overload) {
    var logs = new List<LogEntry>();
    await using var provider = _provider(logs: logs);
    var dispatcher = new ProbeDispatcher(provider);
    var command = new BcbUnhandledCommand("nowhere");

    Func<Task> send = overload == "generic"
      ? () => dispatcher.SendAsync(command)
      : () => dispatcher.SendAsync((object)command, MessageContext.New());

    await Assert.That(send).ThrowsExactly<ReceptorNotFoundException>();
    var warnings = _snapshot(logs).Where(l => l.Level == LogLevel.Warning
                                           && l.Message.Contains("IWorkCoordinatorStrategy not registered", StringComparison.Ordinal))
      .ToList();
    await Assert.That(warnings).Count().IsEqualTo(1);
    await Assert.That(warnings[0].Category).IsEqualTo(typeof(Core.Dispatcher).FullName);
    await Assert.That(warnings[0].Message).Contains(nameof(BcbUnhandledCommand));
  }

  // ========================================
  // OUTBOX SEND UNDER A CALLER'S SPAN: the span names its parent
  // ========================================

  [Test]
  [Arguments("generic")]
  [Arguments("object")]
  public async Task SendAsync_OutboxPathUnderAParentSpan_TagsTheParentIdAndSourceAsync(string overload) {
    using var spans = new SpanCapture();
    var strategy = new RecordingWorkStrategy();
    await using var provider = _provider(configure: services => services.AddSingleton<IWorkCoordinatorStrategy>(strategy));
    var dispatcher = new ProbeDispatcher(provider, envelopeSerializer: new FakeEnvelopeSerializer());
    var command = new BcbOutboxCommand("under-parent");

    using var parent = _parentSource.StartActivity("Bcb caller operation");
    await Assert.That(parent).IsNotNull();
    var receipt = overload == "generic"
      ? await dispatcher.SendAsync(command)
      : await dispatcher.SendAsync((object)command, MessageContext.New());

    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
    var span = spans.SpanFor("Dispatch BcbOutboxCommand (Outbox)", receipt.MessageId.ToString());
    await Assert.That(span.GetTagItem("whizbang.debug.parent.id")).IsEqualTo(parent!.Id)
      .Because("an outbox send started inside a caller's span records which span that was");
    await Assert.That(span.GetTagItem("whizbang.debug.parent.source")).IsEqualTo(PARENT_SOURCE_NAME);
    await Assert.That(span.GetTagItem("whizbang.correlation.id")).IsEqualTo(receipt.CorrelationId?.ToString());
    await Assert.That((string?)span.GetTagItem("whizbang.message.type")).Contains(nameof(BcbOutboxCommand));
  }

  // ========================================
  // LOCAL INVOKE AND SYNC WITH METRICS: one duration per call, on every overload
  // ========================================

  [Test]
  [Arguments("result")]
  [Arguments("void")]
  [Arguments("result+perspective")]
  [Arguments("syncmode")]
  [Arguments("void+perspective")]
  public async Task LocalInvokeAndSync_WithMetrics_RecordsOneDurationPerCallAsync(string overload) {
    using var metered = new Metered();
    var handled = 0;
    await using var provider = _provider(metered);
    var dispatcher = new ProbeDispatcher(provider) {
      HandledType = typeof(BcbCommand),
      Handler = msg => {
        handled++;
        return new BcbResult(((BcbCommand)msg).Id);
      }
    };
    var command = new BcbCommand(_newId());

    switch (overload) {
      case "result": {
          var result = await dispatcher.LocalInvokeAndSyncAsync<BcbCommand, BcbResult>(command);
          await Assert.That(result.Id).IsEqualTo(command.Id);
          break;
        }
      case "void": {
          var sync = await dispatcher.LocalInvokeAndSyncAsync(command);
          await Assert.That(sync.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
          break;
        }
      case "result+perspective": {
          var result = await dispatcher.LocalInvokeAndSyncAsync<BcbCommand, BcbResult, BcbPerspective>(command);
          await Assert.That(result.Id).IsEqualTo(command.Id);
          break;
        }
      case "syncmode":
        await dispatcher.LocalInvokeAndSyncAsync(command, SyncMode.StreamOnly);
        break;
      default: {
          var sync = await dispatcher.LocalInvokeAndSyncForPerspectiveAsync<BcbCommand, BcbPerspective>(command);
          await Assert.That(sync.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
          break;
        }
    }

    await Assert.That(handled).IsEqualTo(1);
    var durations = metered.Of("whizbang.dispatcher.local_invoke_and_sync.duration");
    await Assert.That(durations).Count().IsEqualTo(1)
      .Because("every invoke-and-sync overload records its duration once");
    await Assert.That(durations[0].Value).IsGreaterThanOrEqualTo(0d);
  }

  // ========================================
  // PER-PERSPECTIVE SYNC: no extractor means no stream to wait on
  // ========================================

  [Test]
  [Arguments(false, SyncOutcome.NoPendingEvents, 0)]
  [Arguments(true, SyncOutcome.Synced, 1)]
  public async Task LocalInvokeAndSyncForPerspectiveAsync_ExtractorPresence_DecidesWhetherThereIsAStreamToAwaitAsync(
      bool withExtractor, SyncOutcome expectedOutcome, int expectedAwaited) {
    await using var provider = _provider();
    var extractor = withExtractor
      ? new RecordingStreamIdExtractor { OnExtract = msg => msg is BcbCommand c ? c.Id : null }
      : null;
    var dispatcher = new ProbeDispatcher(provider, streamIdExtractor: extractor) {
      HandledType = typeof(BcbCommand),
      Handler = _ => null
    };
    SyncDecisionContext? decision = null;

    var sync = await dispatcher.LocalInvokeAndSyncForPerspectiveAsync<BcbCommand, BcbPerspective>(
      new BcbCommand(_newId()), onDecisionMade: d => decision = d);

    await Assert.That(sync.Outcome).IsEqualTo(expectedOutcome)
      .Because("without an extractor there is no stream id, so nothing is pending; with one, the stream is known "
             + "and, with no sync awaiter to ask, it is reported synced");
    await Assert.That(sync.EventsAwaited).IsEqualTo(expectedAwaited);
    await Assert.That(decision).IsNotNull();
    await Assert.That(decision!.PerspectiveType).IsEqualTo(typeof(BcbPerspective));
    await Assert.That(decision.DidWait).IsFalse();
  }

  // ========================================
  // BATCH SEND / PUBLISH WITH METRICS: batch size and duration
  // ========================================

  [Test]
  [Arguments("send-generic")]
  [Arguments("send-object")]
  [Arguments("publish-generic")]
  [Arguments("publish-object")]
  public async Task BatchDispatch_WithMetrics_RecordsTheBatchSizeAndOneDurationAsync(string overload) {
    using var metered = new Metered();
    var strategy = new RecordingWorkStrategy();
    await using var provider = _provider(metered, configure: services => services.AddSingleton<IWorkCoordinatorStrategy>(strategy));
    var dispatcher = new ProbeDispatcher(provider, envelopeSerializer: new FakeEnvelopeSerializer());
    var commands = new[] { new BcbOutboxCommand("one"), new BcbOutboxCommand("two") };
    var events = new[] { new BcbOutboxEvent(_newId()), new BcbOutboxEvent(_newId()) };

    var receipts = overload switch {
      "send-generic" => await dispatcher.SendManyAsync<BcbOutboxCommand>(commands),
      "send-object" => await dispatcher.SendManyAsync(commands.Cast<object>().ToList()),
      "publish-generic" => await dispatcher.PublishManyAsync<BcbOutboxEvent>(events),
      _ => await dispatcher.PublishManyAsync(events.Cast<object>().ToList())
    };

    await Assert.That(receipts.Count()).IsEqualTo(2);
    await Assert.That(strategy.Queued).Count().IsEqualTo(2);
    var batchSizes = metered.Of("whizbang.dispatcher.send_many.batch_size");
    await Assert.That(batchSizes).Count().IsEqualTo(1);
    await Assert.That(batchSizes[0].Value).IsEqualTo(2d)
      .Because("the batch size is the number of messages handed to the call");
    await Assert.That(metered.Of("whizbang.dispatcher.send_many.duration")).Count().IsEqualTo(1);
  }

  // ========================================
  // DEFERRED CHANNEL: event-store-only deferral has no destination
  // ========================================

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task PublishToOutboxAsync_NoStrategyWithDeferredChannel_EventStoreOnlyDecidesTheDestinationAsync(bool eventStoreOnly) {
    var channel = new RecordingDeferredChannel();
    await using var provider = _provider(configure: services => services.AddSingleton<IDeferredOutboxChannel>(channel));
    var dispatcher = new ProbeDispatcher(provider, envelopeSerializer: new FakeEnvelopeSerializer());

    await dispatcher.CallPublishToOutboxAsync(new BcbOutboxEvent(_newId()), MessageId.New(), eventStoreOnly);

    await Assert.That(channel.Queued).Count().IsEqualTo(1);
    var deferred = channel.Queued[0];
    var hopTopic = deferred.Metadata.Hops[0].Topic;
    if (eventStoreOnly) {
      await Assert.That(deferred.Destination).IsNull()
        .Because("an event-store-only event is stored, never sent, so it has nowhere to go");
      await Assert.That(hopTopic).IsEqualTo("(event-store)");
    } else {
      await Assert.That(deferred.Destination).IsNotNull();
      await Assert.That(hopTopic).IsEqualTo(deferred.Destination);
    }
  }
}
