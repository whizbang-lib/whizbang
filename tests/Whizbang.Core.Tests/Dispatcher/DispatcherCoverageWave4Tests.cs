using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)
#pragma warning disable RCS1163 // Unused parameter — fake receptor/handler delegates intentionally match interface signatures even when the test body doesn't use every arg.

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Registered in the test assembly's generated JsonSerializerContext so the
/// PublishToOutboxDynamicAsync serialization path (JsonContextRegistry lookup) succeeds.
/// Deliberately does NOT implement IHasStreamId, so the dynamic-path stream-id-propagation
/// tests exercise the IStreamIdExtractor.SetStreamId fallback branch instead of the
/// direct-property-assignment branch.
/// </summary>
public sealed record Wave4DynamicEvent([property: StreamId] Guid Id) : IEvent;

/// <summary>
/// Wave 4 coverage tests for Dispatcher.cs targeting specific uncovered lines identified in the
/// 2026-09-05 coverage report:
/// - _lookupReceptorDefaultRouting: own-routing hit and foreign-lookup-exhausted fallback (231, 237)
/// - PublishAsync / PublishAsync(options): receptor-publisher-throws rethrow after outbox drain (3235, 3337)
/// - PublishToOutboxDynamicAsync: ambient collector diversion, sync scope dispose, source-has-no-stream-id
///   guard, and the non-IHasStreamId SetStreamId fallback (4004-4005, 4013-4014, 4033, 4039-4040)
/// - _sendToOutboxViaScopeAsync (generic and non-generic) and _sendManyToOutboxAsync: sync scope dispose
///   when the resolved IServiceScope is not IAsyncDisposable (4312-4313, 4397-4398, 4452-4453)
///
/// Several other lines from the same report were confirmed, by reading the surrounding call graph,
/// to be unreachable through any current caller (see the class-level remarks below for the specific
/// lines and reasoning) and are intentionally left without a test rather than exercised via reflection.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("CoverageWave4")]
public class DispatcherCoverageWave4Tests {

  // ========================================
  // TEST MESSAGE TYPES
  // ========================================

  public sealed record Wave4RoutingCommand(string Data);
  public sealed record Wave4Result(bool Success);
  public sealed record Wave4CascadeEvent([property: StreamId] Guid Id) : IEvent;

  public sealed record Wave4PublishEvent([property: StreamId] Guid Id) : IEvent;

  public sealed record Wave4SourceCommand(string Data);

  public sealed record Wave4GenericOutboxCommand(string Data);
  public sealed record Wave4ObjectOutboxCommand(string Data);
  public sealed record Wave4ManyOutboxCommand(string Data);

  // ========================================
  // TEST DISPATCHER (concrete subclass)
  // ========================================

  private sealed class TestDispatcher(
    IServiceProvider sp,
    IEnvelopeSerializer? envelopeSerializer = null,
    IStreamIdExtractor? streamIdExtractor = null,
    ReceptorInvoker<object>? invoker = null,
    Type? handleMessageType = null,
    Func<object, IMessageEnvelope?, CancellationToken, Task>? untypedPublisher = null,
    DispatchModes? defaultRouting = null,
    bool publisherThrows = false,
    string publisherFailureMessage = "wave4-publisher-failed"
    ) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null),
      envelopeSerializer: envelopeSerializer,
      streamIdExtractor: streamIdExtractor) {
    private readonly ReceptorInvoker<object>? _invoker = invoker;
    private readonly Type? _handleMessageType = handleMessageType;
    private readonly Func<object, IMessageEnvelope?, CancellationToken, Task>? _untypedPublisher = untypedPublisher;
    private readonly DispatchModes? _defaultRouting = defaultRouting;
    private readonly bool _publisherThrows = publisherThrows;
    private readonly string _publisherFailureMessage = publisherFailureMessage;
    private readonly List<Type> _cascadeToOutboxCalls = [];

    /// <summary>Message types for which CascadeToOutboxAsync was invoked (i.e., the resolved
    /// dispatch mode included the Outbox flag).</summary>
    public IReadOnlyList<Type> CascadeToOutboxCalls => _cascadeToOutboxCalls;

    public Task CallPublishToOutboxDynamicAsync(
      IMessage eventData, Type eventType, MessageId messageId, IMessageEnvelope? sourceEnvelope = null, bool eventStoreOnly = false) =>
      PublishToOutboxDynamicAsync(eventData, eventType, messageId, sourceEnvelope, eventStoreOnly);

    protected override Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      _cascadeToOutboxCalls.Add(messageType);
      return Task.CompletedTask;
    }

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      if (_invoker != null && messageType == _handleMessageType) {
        return msg => {
          var task = _invoker(msg);
          return new ValueTask<TResult>(task.AsTask().ContinueWith(t => (TResult)t.Result));
        };
      }
      return null;
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) {
      if (_publisherThrows) {
        var failureMessage = _publisherFailureMessage;
        return _ => throw new InvalidOperationException(failureMessage);
      }
      return _ => Task.CompletedTask;
    }

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => _untypedPublisher;

    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;

    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => _defaultRouting;
  }

  // ========================================
  // FAKES
  // ========================================

  private sealed class TestServiceScope(IServiceProvider provider) : IServiceScope {
    public IServiceProvider ServiceProvider { get; } = provider;

    /// <summary>Never IAsyncDisposable — every test in this file that reaches a scope-dispose
    /// finally block therefore exercises the synchronous scope.Dispose() branch.</summary>
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
  }

  private sealed class TestServiceScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public List<TestServiceScope> CreatedScopes { get; } = [];

    public IServiceScope CreateScope() {
      var scope = new TestServiceScope(provider);
      CreatedScopes.Add(scope);
      return scope;
    }
  }

  private sealed class Wave4WorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> Queued { get; } = [];
    public int FlushCount { get; private set; }

    public void QueueOutboxMessage(OutboxMessage message) => Queued.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      FlushCount++;
      return Task.CompletedTask;
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      FlushCount++;
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }
  }

  private sealed class Wave4EnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = envelope.Hops?.ToList() ?? [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      var messageType = typeof(TMessage).AssemblyQualifiedName ?? typeof(TMessage).FullName ?? typeof(TMessage).Name;
      var envelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{messageType}]], Whizbang.Core";
      return new SerializedEnvelope(jsonEnvelope, envelopeType, messageType);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => new();
  }

  /// <summary>Extractor whose own-message and source-message extraction results are independently
  /// controllable, and which records every SetStreamId call so tests can assert propagation happened
  /// through the extractor rather than through a direct IHasStreamId property assignment.</summary>
  private sealed class Wave4StreamIdExtractor : IStreamIdExtractor {
    private readonly Dictionary<object, Guid> _assigned = new(ReferenceEqualityComparer.Instance);

    public Func<object, Guid?>? OnExtract { get; init; }
    public List<(object Message, Guid StreamId)> SetCalls { get; } = [];

    public Guid? ExtractStreamId(object message, Type messageType) =>
      _assigned.TryGetValue(message, out var assigned) ? assigned : OnExtract?.Invoke(message);

    public bool SetStreamId(object message, Guid streamId) {
      SetCalls.Add((message, streamId));
      _assigned[message] = streamId;
      return true;
    }
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  // Dispatcher resolves IServiceScopeFactory from the IServiceProvider it is handed, and the
  // built-in container supplies its own as a built-in service that a ServiceCollection
  // registration cannot override -- resolving it back out yields a ServiceProviderEngineScope,
  // never the double. So the double is injected by wrapping the provider rather than registering
  // it, which is the only way a test can see the scopes the dispatcher actually opened and closed.
  private sealed class _scopeFactoryOverrideProvider(IServiceProvider inner, IServiceScopeFactory factory)
      : IServiceProvider {
    public object? GetService(Type serviceType) =>
      serviceType == typeof(IServiceScopeFactory) ? factory : inner.GetService(serviceType);
  }

  private static readonly ConditionalWeakTable<IServiceProvider, TestServiceScopeFactory> _scopeFactories = [];

  private static _scopeFactoryOverrideProvider _buildProvider(IWorkCoordinatorStrategy? strategy = null) {
    var services = new ServiceCollection();
    if (strategy != null) {
      services.AddSingleton(strategy);
    }
    var inner = services.BuildServiceProvider();
    var factory = new TestServiceScopeFactory(inner);
    var provider = new _scopeFactoryOverrideProvider(inner, factory);
    _scopeFactories.Add(provider, factory);
    return provider;
  }

  private static TestServiceScopeFactory _scopeFactoryOf(IServiceProvider provider) =>
    _scopeFactories.TryGetValue(provider, out var factory)
      ? factory
      : throw new InvalidOperationException("Provider was not created by _buildProvider.");

  private static MessageEnvelope<object> _sourceEnvelope(object payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  /// <summary>Receptor result carrying one business result plus one un-wrapped cascade event, used
  /// to observe how the resolved receptor-default routing mode affects the cascaded event.</summary>
  private static ValueTask<object> _cascadingInvoker(object message) =>
    new((object)(new Wave4Result(true), new Wave4CascadeEvent(Guid.NewGuid())));

  // ========================================
  // _lookupReceptorDefaultRouting — lines 231, 237
  // ========================================

  // If a receptor's own [DefaultRouting] resolution stopped winning here, every event cascaded from
  // that receptor's return value would silently fall back to the system default (Both) — turning a
  // deliberately local-only side effect into unwanted cross-service outbox traffic.
  [Test]
  public async Task SendAsync_ReceptorHasOwnDefaultRouting_CascadesEventLocallyWithoutOutboxAsync() {
    // Arrange
    var localPublishes = new List<Type>();
    Task untypedPublisher(object msg, IMessageEnvelope? env, CancellationToken ct) {
      localPublishes.Add(msg.GetType());
      return Task.CompletedTask;
    }

    var dispatcher = new TestDispatcher(
      _buildProvider(),
      invoker: _cascadingInvoker,
      handleMessageType: typeof(Wave4RoutingCommand),
      untypedPublisher: untypedPublisher,
      defaultRouting: DispatchModes.Local);

    // Act
    await dispatcher.SendAsync<Wave4RoutingCommand>(new Wave4RoutingCommand("own-routing"));

    // Assert
    await Assert.That(dispatcher.CascadeToOutboxCalls).IsEmpty()
      .Because("DispatchModes.Local carries no Outbox flag, so a receptor-level default of Local must never reach CascadeToOutboxAsync");
    await Assert.That(localPublishes).Contains(typeof(Wave4CascadeEvent))
      .Because("DispatchModes.Local still includes LocalDispatch, so the cascaded event must still reach the in-process publisher");
  }

  // If the loop over foreign receptor lookups stopped falling through cleanly when nothing declares a
  // default, a message cascaded with no receptor-level or foreign-declared routing would either throw
  // or silently drop instead of deferring to the system default (Both: local + outbox).
  [Test]
  public async Task SendAsync_NoOwnOrForeignDefaultRouting_CascadesEventUsingSystemDefaultAsync() {
    // Arrange
    var localPublishes = new List<Type>();
    Task untypedPublisher(object msg, IMessageEnvelope? env, CancellationToken ct) {
      localPublishes.Add(msg.GetType());
      return Task.CompletedTask;
    }

    var dispatcher = new TestDispatcher(
      _buildProvider(),
      invoker: _cascadingInvoker,
      handleMessageType: typeof(Wave4RoutingCommand),
      untypedPublisher: untypedPublisher,
      defaultRouting: null);

    // Act
    await dispatcher.SendAsync<Wave4RoutingCommand>(new Wave4RoutingCommand("system-default"));

    // Assert
    await Assert.That(dispatcher.CascadeToOutboxCalls).Contains(typeof(Wave4CascadeEvent))
      .Because("with no receptor-level or foreign default, the system default (Both) includes Outbox and must reach CascadeToOutboxAsync");
    await Assert.That(localPublishes).Contains(typeof(Wave4CascadeEvent))
      .Because("the system default (Both) also includes LocalDispatch");
  }

  // ========================================
  // PublishAsync / PublishAsync(options) — receptor publisher throws — lines 3235, 3337
  // (also exercises the PublishToOutboxAsync sync scope.Dispose() branch at 3683-3684, since the
  // outbox task is drained inside the catch before the original exception is rethrown)
  // ========================================

  // If the original receptor-publisher failure got swallowed or replaced here instead of rethrown once
  // the concurrently-started outbox write is drained, a broken local event handler would fail silently
  // and PublishAsync would report success for an event nobody actually handled.
  [Test]
  public async Task PublishAsync_ReceptorPublisherThrows_PropagatesOriginalExceptionAsync() {
    // Arrange
    var provider = _buildProvider();
    var scopeFactory = _scopeFactoryOf(provider);
    var dispatcher = new TestDispatcher(provider, publisherThrows: true, publisherFailureMessage: "wave4-publish-boom");

    // Act & Assert
    await Assert.That(async () => await dispatcher.PublishAsync(new Wave4PublishEvent(Guid.NewGuid())))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("wave4-publish-boom");
    await Assert.That(scopeFactory.CreatedScopes.Single().Disposed).IsTrue()
      .Because("the outbox scope opened concurrently with the failing local publish must still be disposed, not leaked, when the publish rethrows");
  }

  // Same rethrow shape as the no-options overload, but on the DispatchOptions overload's own copy of
  // the try/catch — if that copy diverged and stopped rethrowing, a caller who explicitly passed
  // DispatchOptions would be the one left unaware their handler failed.
  [Test]
  public async Task PublishAsync_WithDispatchOptions_ReceptorPublisherThrows_PropagatesOriginalExceptionAsync() {
    // Arrange
    var provider = _buildProvider();
    var dispatcher = new TestDispatcher(provider, publisherThrows: true, publisherFailureMessage: "wave4-publish-options-boom");

    // Act & Assert
    await Assert.That(async () => await dispatcher.PublishAsync(new Wave4PublishEvent(Guid.NewGuid()), new DispatchOptions()))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("wave4-publish-options-boom");
  }

  // ========================================
  // PublishToOutboxDynamicAsync — lines 4004-4005, 4013-4014, 4033, 4039-4040
  // ========================================

  // If a composite fan-out's ambient collector stopped being checked here, the diverted message would
  // both land in the collector's buffered batch AND get queued straight to the live strategy — a
  // double-write that escapes the collector's whole point (folding the fan-out into one transaction).
  [Test]
  public async Task PublishToOutboxDynamic_AmbientCollectorOpen_DivertsMessageAndStillDisposesScopeAsync() {
    // Arrange
    var strategy = new Wave4WorkCoordinatorStrategy();
    var provider = _buildProvider(strategy);
    var scopeFactory = _scopeFactoryOf(provider);
    var dispatcher = new TestDispatcher(provider);
    using var collecting = DispatchOutboxCollector.BeginCollecting();

    // Act
    await dispatcher.CallPublishToOutboxDynamicAsync(new Wave4DynamicEvent(Guid.NewGuid()), typeof(Wave4DynamicEvent), MessageId.New());

    // Assert
    await Assert.That(collecting.Collector.Collected).Count().IsEqualTo(1)
      .Because("an open ambient collector must receive the fully-built outbox message");
    await Assert.That(strategy.Queued).IsEmpty()
      .Because("a message diverted into the collector must never also reach the real work-coordinator strategy");
    await Assert.That(scopeFactory.CreatedScopes.Single().Disposed).IsTrue()
      .Because("the scope opened to resolve the strategy must still be disposed even though the write short-circuited into the collector");
  }

  // If this guard stopped short-circuiting when the source itself carries no usable stream id, the
  // event would look like propagation happened even though nothing valid was ever found, masking the
  // fallback-to-message-id path that keeps every outbox row on SOME stream.
  [Test]
  public async Task PublishToOutboxDynamic_SourceEnvelopeAlsoHasNoStreamId_SkipsPropagationAsync() {
    // Arrange
    var extractor = new Wave4StreamIdExtractor { OnExtract = _ => null };
    var strategy = new Wave4WorkCoordinatorStrategy();
    var dispatcher = new TestDispatcher(_buildProvider(strategy), streamIdExtractor: extractor);
    var source = _sourceEnvelope(new Wave4SourceCommand("origin"));
    var messageId = MessageId.New();

    // Act
    await dispatcher.CallPublishToOutboxDynamicAsync(new Wave4DynamicEvent(Guid.NewGuid()), typeof(Wave4DynamicEvent), messageId, source);

    // Assert
    await Assert.That(extractor.SetCalls).IsEmpty()
      .Because("with no extractable stream id on either the event or its source, there is nothing to propagate");
    await Assert.That(strategy.Queued.Single().StreamId).IsEqualTo(messageId.Value)
      .Because("lacking any stream id at all, the outbox row must fall back to the message id");
  }

  // If an event type that doesn't implement IHasStreamId stopped going through the extractor's
  // SetStreamId here, it would silently keep no stream id when inheriting from a source command,
  // scrambling per-stream ordering for any consumer that groups delivery by StreamId.
  [Test]
  public async Task PublishToOutboxDynamic_EventWithoutIHasStreamId_PropagatesSourceIdViaExtractorAsync() {
    // Arrange
    var sourceStreamId = Guid.NewGuid();
    var extractor = new Wave4StreamIdExtractor {
      OnExtract = msg => msg is Wave4SourceCommand ? sourceStreamId : null
    };
    var strategy = new Wave4WorkCoordinatorStrategy();
    var dispatcher = new TestDispatcher(_buildProvider(strategy), streamIdExtractor: extractor);
    var source = _sourceEnvelope(new Wave4SourceCommand("origin"));
    var dynamicEvent = new Wave4DynamicEvent(Guid.NewGuid());

    // Act
    await dispatcher.CallPublishToOutboxDynamicAsync(dynamicEvent, typeof(Wave4DynamicEvent), MessageId.New(), source);

    // Assert
    await Assert.That(extractor.SetCalls).Count().IsEqualTo(1)
      .Because("an event without IHasStreamId must inherit the source's stream id through the extractor's SetStreamId, not a direct property set");
    await Assert.That(extractor.SetCalls[0].StreamId).IsEqualTo(sourceStreamId);
    await Assert.That(strategy.Queued.Single().StreamId).IsEqualTo(sourceStreamId)
      .Because("the propagated source stream id must end up on the queued outbox row");
  }

  // ========================================
  // Outbox-routed Send / SendMany — sync scope.Dispose() — lines 4312-4313, 4397-4398, 4452-4453
  // (the same shape covers PublishToOutboxAsync's own copy at 3683-3684 via the PublishAsync tests above)
  // ========================================

  // If a sync-only IServiceScope stopped being disposed on this path, every generic Send<T> call with
  // no local receptor would leak a DI scope for the life of the process.
  [Test]
  public async Task SendAsync_Generic_NoLocalReceptor_RoutesToOutboxAndDisposesScopeSynchronouslyAsync() {
    // Arrange
    var strategy = new Wave4WorkCoordinatorStrategy();
    var provider = _buildProvider(strategy);
    var scopeFactory = _scopeFactoryOf(provider);
    var dispatcher = new TestDispatcher(provider, envelopeSerializer: new Wave4EnvelopeSerializer());

    // Act
    var receipt = await dispatcher.SendAsync<Wave4GenericOutboxCommand>(new Wave4GenericOutboxCommand("generic"));

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted)
      .Because("with no local receptor and a strategy registered, the command must route to the outbox");
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
    await Assert.That(scopeFactory.CreatedScopes.Single().Disposed).IsTrue()
      .Because("a sync-only IServiceScope must still be disposed via the else branch, not silently leaked");
  }

  // Same leak risk as the generic overload, but on the object-typed overload's own copy of the
  // scope-resolution/dispose logic.
  [Test]
  public async Task SendAsync_NonGeneric_NoLocalReceptor_RoutesToOutboxAndDisposesScopeSynchronouslyAsync() {
    // Arrange
    var strategy = new Wave4WorkCoordinatorStrategy();
    var provider = _buildProvider(strategy);
    var scopeFactory = _scopeFactoryOf(provider);
    var dispatcher = new TestDispatcher(provider, envelopeSerializer: new Wave4EnvelopeSerializer());

    // Act
    var receipt = await dispatcher.SendAsync((object)new Wave4ObjectOutboxCommand("object"));

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
    await Assert.That(scopeFactory.CreatedScopes.Single().Disposed).IsTrue()
      .Because("the non-generic outbox path must dispose its sync-only scope just like the generic overload");
  }

  // SendManyAsync opens exactly one scope for the whole batch; if that scope stopped being disposed
  // when it isn't IAsyncDisposable, every high-volume SendManyAsync call in a busy service would leak
  // one scope per call instead of zero.
  [Test]
  public async Task SendManyAsync_NoLocalReceptors_RoutesAllToOutboxAndDisposesScopeSynchronouslyAsync() {
    // Arrange
    var strategy = new Wave4WorkCoordinatorStrategy();
    var provider = _buildProvider(strategy);
    var scopeFactory = _scopeFactoryOf(provider);
    var dispatcher = new TestDispatcher(provider, envelopeSerializer: new Wave4EnvelopeSerializer());

    // Act
    var receipts = (await dispatcher.SendManyAsync<Wave4ManyOutboxCommand>([
      new Wave4ManyOutboxCommand("a"),
      new Wave4ManyOutboxCommand("b")
    ])).ToList();

    // Assert
    await Assert.That(receipts).Count().IsEqualTo(2);
    await Assert.That(receipts.All(r => r.Status == DeliveryStatus.Accepted)).IsTrue()
      .Because("neither message has a local receptor, so both must come back Accepted (outbox-routed)");
    await Assert.That(strategy.Queued).Count().IsEqualTo(2);
    await Assert.That(scopeFactory.CreatedScopes.Single().Disposed).IsTrue()
      .Because("SendManyAsync creates exactly one scope for the whole batch and must dispose it even though it's sync-only");
  }
}
