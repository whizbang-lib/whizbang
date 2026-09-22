using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// The dispatcher mints the identity of every event a handler emits. When the emission is driven by an
/// inbound message, that identity must be derived from the handling so a retry re-derives it; the store's
/// primary key then turns the second copy into a no-op instead of a republish. Root emissions with no
/// source keep fresh time-ordered ids.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Messaging")]
public class DispatcherEmissionIdentityTests {

  public record ProbeEvent([property: StreamId] Guid Id) : IEvent;

  public record OtherProbeEvent([property: StreamId] Guid Id) : IEvent;

  public record ProbeSourceCommand(string Data);

  public record ProbeCascadedCommand(string Data) : IMessage;

  // ---- test dispatcher ----

  private sealed class ProbeDispatcher(IServiceProvider sp, IServiceInstanceProvider instanceProvider)
    : Core.Dispatcher(sp, instanceProvider) {
    public List<(IMessage Message, Guid? EventId)> OutboxCascades { get; } = [];

    /// <summary>Envelopes handed to in-process receptors by a local cascade dispatch.</summary>
    public List<IMessageEnvelope?> LocalDispatchEnvelopes { get; } = [];

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) =>
      (_, envelope, _) => {
        LocalDispatchEnvelopes.Add(envelope);
        return Task.CompletedTask;
      };
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;

    protected override Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      OutboxCascades.Add((message, eventId));
      return Task.CompletedTask;
    }

    protected override Task CascadeToEventStoreOnlyAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) =>
      Task.CompletedTask;
  }

  private sealed class FakeServiceInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new ListLogger(sink);
    public void Dispose() {
      // The sink is owned by the test.
    }

    private sealed class ListLogger(List<string> sink) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        lock (sink) {
          sink.Add(formatter(state, exception));
        }
      }
    }
  }

  private static ServiceProvider _buildProvider(List<string>? logs = null) {
    var services = new ServiceCollection();
    if (logs is not null) {
      services.AddLogging(b => {
        b.SetMinimumLevel(LogLevel.Trace);
        b.AddProvider(new ListLoggerProvider(logs));
      });
    }
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// The envelope an inbox worker hands the dispatcher for one handler row: the inbound message's id,
  /// the row's handler name on the dispatch context. A retry builds a NEW instance with the same values.
  /// </summary>
  private static MessageEnvelope<object> _handlingEnvelope(Guid sourceMessageId, string handlerName) => new() {
    MessageId = MessageId.From(sourceMessageId),
    Payload = new ProbeSourceCommand("origin"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = handlerName },
  };

  // ---- tests ----

  [Test]
  public async Task CascadeMessageAsync_RetryOfSameHandling_DerivesTheSameEventIdsAsync() {
    await using var sp = _buildProvider();
    var dispatcher = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var sourceMessageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    // First run: the handler emits two events of the same type.
    var firstRun = _handlingEnvelope(sourceMessageId, "OrderHandler");
    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), firstRun, DispatchModes.Outbox);
    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), firstRun, DispatchModes.Outbox);

    // Retry: the row is re-dispatched, a fresh envelope instance carrying the same identity.
    var retry = _handlingEnvelope(sourceMessageId, "OrderHandler");
    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), retry, DispatchModes.Outbox);
    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), retry, DispatchModes.Outbox);

    var ids = dispatcher.OutboxCascades.ConvertAll(c => c.EventId!.Value);
    await Assert.That(ids).Count().IsEqualTo(4);
    await Assert.That(ids[2]).IsEqualTo(ids[0])
      .Because("the retry's first emission must carry the id the first run's first emission had, so the outbox primary key absorbs it");
    await Assert.That(ids[3]).IsEqualTo(ids[1]);
    await Assert.That(ids[1]).IsNotEqualTo(ids[0])
      .Because("two emissions within one handling are two messages; the ordinal keeps them apart");
  }

  [Test]
  public async Task CascadeMessageAsync_SiblingHandlerRowOfSameMessage_DerivesDistinctEventIdsAsync() {
    await using var sp = _buildProvider();
    var dispatcher = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var sourceMessageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), _handlingEnvelope(sourceMessageId, "FirstHandler"), DispatchModes.Outbox);
    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), _handlingEnvelope(sourceMessageId, "SecondHandler"), DispatchModes.Outbox);

    await Assert.That(dispatcher.OutboxCascades[1].EventId).IsNotEqualTo(dispatcher.OutboxCascades[0].EventId)
      .Because("two handler rows of one message in one service both emit ordinal zero of the same type; the handler name keeps them apart");
  }

  [Test]
  public async Task CascadeMessageAsync_TwoServicesHandlingSameMessage_DeriveDistinctEventIdsAsync() {
    await using var sp = _buildProvider();
    var orders = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var billing = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("billing"));
    var sourceMessageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await orders.CascadeMessageAsync(new ProbeEvent(streamId), _handlingEnvelope(sourceMessageId, "Handler"), DispatchModes.Outbox);
    await billing.CascadeMessageAsync(new ProbeEvent(streamId), _handlingEnvelope(sourceMessageId, "Handler"), DispatchModes.Outbox);

    await Assert.That(billing.OutboxCascades[0].EventId).IsNotEqualTo(orders.OutboxCascades[0].EventId);
  }

  [Test]
  public async Task CascadeMessageAsync_DifferentEmittedTypes_DeriveDistinctEventIdsAsync() {
    await using var sp = _buildProvider();
    var dispatcher = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var source = _handlingEnvelope((Guid)TrackedGuid.NewMedo(), "Handler");
    var streamId = (Guid)TrackedGuid.NewMedo();

    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), source, DispatchModes.Outbox);
    await dispatcher.CascadeMessageAsync(new OtherProbeEvent(streamId), source, DispatchModes.Outbox);

    await Assert.That(dispatcher.OutboxCascades[1].EventId).IsNotEqualTo(dispatcher.OutboxCascades[0].EventId);
  }

  [Test]
  public async Task CascadeMessageAsync_DerivedEventId_IsVersion7ShapedAndKeepsSourceTimePrefixAsync() {
    await using var sp = _buildProvider();
    var dispatcher = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var sourceMessageId = (Guid)TrackedGuid.NewMedo();

    await dispatcher.CascadeMessageAsync(new ProbeEvent((Guid)TrackedGuid.NewMedo()), _handlingEnvelope(sourceMessageId, "Handler"), DispatchModes.Outbox);

    var eventId = dispatcher.OutboxCascades[0].EventId!.Value;
    await Assert.That(eventId.Version).IsEqualTo(7)
      .Because("downstream id value objects accept only v7; a derived id must pass where a minted one passes");
    var prefixMatches = eventId.ToByteArray(bigEndian: true).AsSpan(0, 6).SequenceEqual(sourceMessageId.ToByteArray(bigEndian: true).AsSpan(0, 6));
    await Assert.That(prefixMatches).IsTrue()
      .Because("derived ids inherit the source's millisecond prefix so they index next to the message that caused them");
  }

  [Test]
  public async Task CascadeMessageAsync_NoSourceEnvelope_MintsFreshTimeOrderedIdsAsync() {
    await using var sp = _buildProvider();
    var dispatcher = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var streamId = (Guid)TrackedGuid.NewMedo();

    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), sourceEnvelope: null, DispatchModes.Outbox);
    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), sourceEnvelope: null, DispatchModes.Outbox);

    var first = dispatcher.OutboxCascades[0].EventId!.Value;
    var second = dispatcher.OutboxCascades[1].EventId!.Value;
    await Assert.That(second).IsNotEqualTo(first)
      .Because("a root emission has no handling to derive from; each is a new message");
    await Assert.That(first.Version).IsEqualTo(7);
  }

  [Test]
  public async Task CascadeMessageAsync_SourceWithoutMessageId_MintsFreshIdsAsync() {
    await using var sp = _buildProvider();
    var dispatcher = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var streamId = (Guid)TrackedGuid.NewMedo();
    // A default-initialized id carries an empty value: the shape of an envelope built without one.
    var anonymous = new MessageEnvelope<object> {
      MessageId = default,
      Payload = new ProbeSourceCommand("origin"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };

    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), anonymous, DispatchModes.Outbox);
    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), anonymous, DispatchModes.Outbox);

    await Assert.That(dispatcher.OutboxCascades[1].EventId).IsNotEqualTo(dispatcher.OutboxCascades[0].EventId)
      .Because("an empty source id cannot anchor a derivation; falling back to fresh ids is the only safe choice");
    await Assert.That(dispatcher.OutboxCascades[0].EventId!.Value.Version).IsEqualTo(7);
  }

  // ---- nested cascades ----

  [Test]
  public async Task CascadeMessageAsync_LocalDispatch_HandsReceptorsAWrapperAnchoredOnTheCascadedEventAsync() {
    await using var sp = _buildProvider();
    var dispatcher = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var sourceMessageId = (Guid)TrackedGuid.NewMedo();
    var handling = _handlingEnvelope(sourceMessageId, "OrderHandler");

    await dispatcher.CascadeMessageAsync(new ProbeEvent((Guid)TrackedGuid.NewMedo()), handling, DispatchModes.Local);

    await Assert.That(dispatcher.LocalDispatchEnvelopes).Count().IsEqualTo(1);
    var wrapper = dispatcher.LocalDispatchEnvelopes[0] as CascadeEnvelopeWrapper;
    await Assert.That(wrapper).IsNotNull();
    var expectedEventId = EmissionIdentity.Derive(sourceMessageId, "orders", "OrderHandler", TypeNameFormatter.Format(typeof(ProbeEvent)), ordinal: 0);
    await Assert.That(wrapper!.EmissionAnchor).IsEqualTo(expectedEventId)
      .Because("the nested receivers' emissions must hang off the cascaded event, not off the inbound message they see through the wrapper");
  }

  [Test]
  public async Task CascadeMessageAsync_LocalDispatchOfACommand_AnchorsOnAnOrdinalOfTheHandlingAsync() {
    await using var sp = _buildProvider();
    var dispatcher = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var sourceMessageId = (Guid)TrackedGuid.NewMedo();

    await dispatcher.CascadeMessageAsync(new ProbeCascadedCommand("first"), _handlingEnvelope(sourceMessageId, "OrderHandler"), DispatchModes.Local);
    await dispatcher.CascadeMessageAsync(new ProbeCascadedCommand("retry"), _handlingEnvelope(sourceMessageId, "OrderHandler"), DispatchModes.Local);

    var first = (dispatcher.LocalDispatchEnvelopes[0] as CascadeEnvelopeWrapper)!.EmissionAnchor;
    var retry = (dispatcher.LocalDispatchEnvelopes[1] as CascadeEnvelopeWrapper)!.EmissionAnchor;
    await Assert.That(first.HasValue).IsTrue();
    await Assert.That(first).IsEqualTo(retry)
      .Because("a cascaded command has no event id, so its anchor is derived from the handling and must survive a retry too");
    await Assert.That(first!.Value).IsEqualTo(EmissionIdentity.Derive(sourceMessageId, "orders", "OrderHandler", TypeNameFormatter.Format(typeof(ProbeCascadedCommand)), ordinal: 0));
  }

  [Test]
  public async Task CascadeMessageAsync_EmissionThroughAnAnchoredWrapper_DerivesFromTheAnchorNotTheInboundMessageAsync() {
    await using var sp = _buildProvider();
    var dispatcher = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var sourceMessageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var handling = _handlingEnvelope(sourceMessageId, "OrderHandler");

    // Top-level: the handler emits a ProbeEvent (ordinal 0 of the handling).
    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), handling, DispatchModes.Outbox);
    var topLevelId = dispatcher.OutboxCascades[0].EventId!.Value;

    // Nested: a receptor handling that ProbeEvent (through the wrapper) emits a ProbeEvent of its own.
    var wrapper = new CascadeEnvelopeWrapper(handling) { EmissionAnchor = topLevelId };
    await dispatcher.CascadeMessageAsync(new ProbeEvent(streamId), wrapper, DispatchModes.Outbox);
    var nestedId = dispatcher.OutboxCascades[1].EventId!.Value;

    await Assert.That(nestedId).IsNotEqualTo(topLevelId)
      .Because("the wrapper reports the same inbound id and handler as the top level; without the anchor the nested emission would be deduplicated away");
    await Assert.That(nestedId).IsEqualTo(EmissionIdentity.Derive(topLevelId, "orders", "OrderHandler", TypeNameFormatter.Format(typeof(ProbeEvent)), ordinal: 0));
  }

  [Test]
  public async Task CascadeMessageAsync_EmissionThroughAnUnanchoredWrapper_FallsBackToTheInboundMessageIdAsync() {
    await using var sp = _buildProvider();
    var dispatcher = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var sourceMessageId = (Guid)TrackedGuid.NewMedo();
    var wrapper = new CascadeEnvelopeWrapper(_handlingEnvelope(sourceMessageId, "OrderHandler"));

    await dispatcher.CascadeMessageAsync(new ProbeEvent((Guid)TrackedGuid.NewMedo()), wrapper, DispatchModes.Outbox);

    await Assert.That(dispatcher.OutboxCascades[0].EventId).IsEqualTo(
      EmissionIdentity.Derive(sourceMessageId, "orders", "OrderHandler", TypeNameFormatter.Format(typeof(ProbeEvent)), ordinal: 0));
  }

  [Test]
  public async Task CascadeMessageAsync_DerivedId_IsLoggedAtDebugWithSourceAndOrdinalAsync() {
    var logs = new List<string>();
    await using var sp = _buildProvider(logs);
    var dispatcher = new ProbeDispatcher(sp, new FakeServiceInstanceProvider("orders"));
    var sourceMessageId = (Guid)TrackedGuid.NewMedo();

    await dispatcher.CascadeMessageAsync(new ProbeEvent((Guid)TrackedGuid.NewMedo()), _handlingEnvelope(sourceMessageId, "Handler"), DispatchModes.Outbox);

    List<string> lines;
    lock (logs) { lines = [.. logs]; }
    var derived = lines.FirstOrDefault(l => l.Contains("Emission id derived", StringComparison.Ordinal));
    await Assert.That(derived).IsNotNull();
    await Assert.That(derived).Contains(sourceMessageId.ToString());
    await Assert.That(derived).Contains(dispatcher.OutboxCascades[0].EventId!.Value.ToString());
  }
}
