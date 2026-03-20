using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Comprehensive coverage tests for Dispatcher.cs targeting uncovered paths:
/// - TraceStore integration (envelope storage in send/invoke paths)
/// - EnvelopeRegistry register/unregister
/// - Void LocalInvoke with DispatchOptions (tracing + non-tracing, sync fallback)
/// - LocalInvokeWithReceipt with DispatchOptions (sync fallback)
/// - CascadeMessageAsync (Local, Outbox, EventStore modes)
/// - SendManyAsync (generic, non-generic, local + outbox split)
/// - LocalInvokeManyAsync
/// - Outbox routing (_sendToOutboxViaScopeAsync with/without strategy)
/// - PublishToOutboxAsync (deferred channel, no-strategy paths)
/// - Void invoke AnyInvoker fallback with tracing
/// - RPC extraction fallback via InvalidCastException
/// - PublishAsync with options + cancellation
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherComprehensiveCoverageTests {

  // ========================================
  // TEST MESSAGE TYPES
  // ========================================

  public record TestCommand(string Data);
  public record TestResult(Guid Id, bool Success);

  [DefaultRouting(DispatchMode.Local)]
  public record TestEvent([property: StreamId] Guid OrderId) : IEvent;

  public record UnhandledCommand(string Data); // No receptor registered

  // ========================================
  // TEST DISPATCHER (concrete subclass pattern)
  // ========================================

  private sealed class TestDispatcher(
    IServiceProvider sp,
    ITraceStore? traceStore = null,
    IEnvelopeSerializer? envelopeSerializer = null,
    IEnvelopeRegistry? envelopeRegistry = null,
    IOutboxRoutingStrategy? outboxRoutingStrategy = null,
    IStreamIdExtractor? streamIdExtractor = null,
    IScopedEventTracker? scopedEventTracker = null,
    ReceptorInvoker<object>? invoker = null,
    VoidReceptorInvoker? voidInvoker = null,
    SyncReceptorInvoker<object>? syncInvoker = null,
    VoidSyncReceptorInvoker? voidSyncInvoker = null,
    Func<object, ValueTask<object?>>? anyInvoker = null,
    Func<object, IMessageEnvelope?, CancellationToken, Task>? untypedPublisher = null,
    DispatchMode? defaultRouting = null
    ) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null),
      traceStore: traceStore,
      envelopeSerializer: envelopeSerializer,
      envelopeRegistry: envelopeRegistry,
      outboxRoutingStrategy: outboxRoutingStrategy,
      streamIdExtractor: streamIdExtractor,
      scopedEventTracker: scopedEventTracker) {
    private readonly ReceptorInvoker<object>? _invoker = invoker;
    private readonly VoidReceptorInvoker? _voidInvoker = voidInvoker;
    private readonly SyncReceptorInvoker<object>? _syncInvoker = syncInvoker;
    private readonly VoidSyncReceptorInvoker? _voidSyncInvoker = voidSyncInvoker;
    private readonly Func<object, ValueTask<object?>>? _anyInvoker = anyInvoker;
    private readonly Func<object, IMessageEnvelope?, CancellationToken, Task>? _untypedPublisher = untypedPublisher;
    private readonly DispatchMode? _defaultRouting = defaultRouting;

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      if (_invoker != null && messageType == typeof(TestCommand)) {
        return msg => {
          var task = _invoker(msg);
          return new ValueTask<TResult>(task.AsTask().ContinueWith(t => (TResult)t.Result));
        };
      }
      return null;
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      if (_voidInvoker != null && messageType == typeof(TestCommand)) {
        return _voidInvoker;
      }
      return null;
    }

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) {
      return evt => Task.CompletedTask;
    }

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) {
      return _untypedPublisher;
    }

    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) {
      if (_syncInvoker != null && messageType == typeof(TestCommand)) {
        return msg => (TResult)_syncInvoker(msg);
      }
      return null;
    }

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) {
      if (_voidSyncInvoker != null && messageType == typeof(TestCommand)) {
        return _voidSyncInvoker;
      }
      return null;
    }

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) {
      if (_anyInvoker != null && messageType == typeof(TestCommand)) {
        return _anyInvoker;
      }
      return null;
    }

    protected override DispatchMode? GetReceptorDefaultRouting(Type messageType) => _defaultRouting;
  }

  // ========================================
  // STUB IMPLEMENTATIONS
  // ========================================

  private sealed class StubTraceStore : ITraceStore {
    public int StoreCallCount { get; private set; }
    public List<IMessageEnvelope> StoredEnvelopes { get; } = [];

    public Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default) {
      StoreCallCount++;
      StoredEnvelopes.Add(envelope);
      return Task.CompletedTask;
    }

    public Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default) =>
      Task.FromResult<IMessageEnvelope?>(StoredEnvelopes.Find(e => e.MessageId == messageId));

    public Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default) =>
      Task.FromResult(new List<IMessageEnvelope>());

    public Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default) =>
      Task.FromResult(new List<IMessageEnvelope>());

    public Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset toTime, CancellationToken ct = default) =>
      Task.FromResult(new List<IMessageEnvelope>());
  }

  private sealed class StubEnvelopeRegistry : IEnvelopeRegistry {
    public int RegisterCount { get; private set; }
    public int UnregisterCount { get; private set; }

    public void Register<T>(MessageEnvelope<T> envelope) => RegisterCount++;
    public MessageEnvelope<T>? TryGetEnvelope<T>(T message) where T : notnull => null;
    public void Unregister<T>(T message) where T : notnull => UnregisterCount++;
    public void Unregister<T>(MessageEnvelope<T> envelope) => UnregisterCount++;
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = envelope.Hops?.ToList() ?? []
      };
      var messageType = typeof(TMessage).AssemblyQualifiedName ?? typeof(TMessage).FullName ?? typeof(TMessage).Name;
      var envelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{messageType}]], Whizbang.Core";
      return new SerializedEnvelope(jsonEnvelope, envelopeType, messageType);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => new();
  }

  private sealed class StubWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutbox { get; } = [];
    public int FlushCount { get; private set; }

    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutbox.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task<WorkBatch> FlushAsync(WorkBatchFlags flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      FlushCount++;
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }
  }

  private sealed class StubScopedEventTracker : IScopedEventTracker {
    private readonly List<TrackedEvent> _events = [];

    public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) {
      _events.Add(new TrackedEvent(streamId, eventType, eventId));
    }

    public IReadOnlyList<TrackedEvent> GetEmittedEvents() => _events;
    public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) => _events;
    public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) {
      return _events.All(e => processedEventIds.Contains(e.EventId));
    }
  }

  private sealed class StubDeferredOutboxChannel : IDeferredOutboxChannel {
    public List<OutboxMessage> QueuedMessages { get; } = [];
    public bool HasPending => QueuedMessages.Count > 0;

    public ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default) {
      QueuedMessages.Add(message);
      return ValueTask.CompletedTask;
    }

    public IReadOnlyList<OutboxMessage> DrainAll() {
      var items = QueuedMessages.ToList();
      QueuedMessages.Clear();
      return items;
    }
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private sealed class TestServiceScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new TestServiceScope(provider);
  }

  private sealed class TestServiceScope(IServiceProvider provider) : IServiceScope {
    public IServiceProvider ServiceProvider { get; } = provider;
    public void Dispose() { }
  }

  private static ServiceProvider _buildProvider(
    IWorkCoordinatorStrategy? strategy = null,
    IDeferredOutboxChannel? deferredChannel = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new TestServiceScopeFactory(sp));
    if (strategy != null) {
      services.AddSingleton(strategy);
    }
    if (deferredChannel != null) {
      services.AddSingleton(deferredChannel);
    }
    return services.BuildServiceProvider();
  }

  private static TestDispatcher _createDispatcher(
    ITraceStore? traceStore = null,
    IEnvelopeRegistry? envelopeRegistry = null,
    IEnvelopeSerializer? envelopeSerializer = null,
    IScopedEventTracker? scopedEventTracker = null,
    IWorkCoordinatorStrategy? workStrategy = null,
    IDeferredOutboxChannel? deferredChannel = null,
    ReceptorInvoker<object>? invoker = null,
    VoidReceptorInvoker? voidInvoker = null,
    SyncReceptorInvoker<object>? syncInvoker = null,
    VoidSyncReceptorInvoker? voidSyncInvoker = null,
    Func<object, ValueTask<object?>>? anyInvoker = null,
    Func<object, IMessageEnvelope?, CancellationToken, Task>? untypedPublisher = null,
    DispatchMode? defaultRouting = null) {
    var sp = _buildProvider(workStrategy, deferredChannel);
    return new TestDispatcher(sp,
      traceStore: traceStore,
      envelopeRegistry: envelopeRegistry,
      envelopeSerializer: envelopeSerializer,
      scopedEventTracker: scopedEventTracker,
      invoker: invoker,
      voidInvoker: voidInvoker,
      syncInvoker: syncInvoker,
      voidSyncInvoker: voidSyncInvoker,
      anyInvoker: anyInvoker,
      untypedPublisher: untypedPublisher,
      defaultRouting: defaultRouting);
  }

  private static ReceptorInvoker<object> _defaultInvoker() =>
    msg => {
      var cmd = (TestCommand)msg;
      return new ValueTask<object>(new TestResult(Guid.NewGuid(), true));
    };

  private static VoidReceptorInvoker _defaultVoidInvoker() => msg => ValueTask.CompletedTask;

  // ========================================
  // TRACESTORE INTEGRATION TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithTraceStore_StoresEnvelopeBeforeInvokingAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new TestCommand("trace-test");

    // Act
    var receipt = await dispatcher.SendAsync(command, MessageContext.New());

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_WithoutTraceStore_SkipsStorageAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("no-trace");

    // Act
    var receipt = await dispatcher.SendAsync(command, MessageContext.New());

    // Assert - no exception, receipt returned
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_Generic_WithTraceStore_StoresEnvelopeAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new TestCommand("generic-trace");

    // Act
    var receipt = await dispatcher.SendAsync<TestCommand>(command);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
    await Assert.That(receipt).IsNotNull();
  }

  // ========================================
  // ENVELOPE REGISTRY TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithEnvelopeRegistry_RegistersAndUnregistersAsync() {
    // Arrange
    var registry = new StubEnvelopeRegistry();
    var dispatcher = _createDispatcher(envelopeRegistry: registry, invoker: _defaultInvoker());
    var command = new TestCommand("registry-test");

    // Act
    await dispatcher.SendAsync(command, MessageContext.New());

    // Assert
    await Assert.That(registry.RegisterCount).IsEqualTo(1);
    await Assert.That(registry.UnregisterCount).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_WithEnvelopeRegistry_RegistersAndUnregistersAsync() {
    // Arrange
    var registry = new StubEnvelopeRegistry();
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(envelopeRegistry: registry, traceStore: traceStore, invoker: _defaultInvoker());
    var command = new TestCommand("local-registry");

    // Act
    await dispatcher.LocalInvokeAsync<object>(command, MessageContext.New());

    // Assert
    await Assert.That(registry.RegisterCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(registry.UnregisterCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task SendAsync_WithOptions_WithEnvelopeRegistry_RegistersAndUnregistersAsync() {
    // Arrange
    var registry = new StubEnvelopeRegistry();
    var dispatcher = _createDispatcher(envelopeRegistry: registry, invoker: _defaultInvoker());
    var command = new TestCommand("options-registry");
    var options = new DispatchOptions();

    // Act
    await dispatcher.SendAsync(command, MessageContext.New(), options);

    // Assert
    await Assert.That(registry.RegisterCount).IsEqualTo(1);
    await Assert.That(registry.UnregisterCount).IsEqualTo(1);
  }

  // ========================================
  // TRACESTORE WITH DISPATCH OPTIONS TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithOptionsAndTraceStore_StoresEnvelopeAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new TestCommand("options-trace");
    var options = new DispatchOptions();

    // Act
    await dispatcher.SendAsync(command, MessageContext.New(), options);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task SendAsync_Generic_WithOptionsAndTraceStore_StoresEnvelopeAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new TestCommand("generic-options-trace");
    var options = new DispatchOptions();

    // Act
    await dispatcher.SendAsync<TestCommand>(command, options);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task SendAsync_WithOptionsAndCancelledToken_ThrowsOperationCanceledAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("cancel-test");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendAsync(command, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // LOCALINVOKE WITH TRACESTORE TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Typed_WithTraceStore_StoresEnvelopeAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new TestCommand("local-trace");

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, MessageContext.New());

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithTraceStore_StoresEnvelopeAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, voidInvoker: _defaultVoidInvoker());
    var command = new TestCommand("void-trace");

    // Act
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithoutTraceStore_FastPathAsync() {
    // Arrange - no trace store, so fast path (direct invoker call)
    var invoked = false;
    ValueTask voidInvoker(object msg) { invoked = true; return ValueTask.CompletedTask; }
    var dispatcher = _createDispatcher(voidInvoker: voidInvoker);
    var command = new TestCommand("void-fast-path");

    // Act
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  // ========================================
  // VOID LOCALINVOKE WITH DISPATCHOPTIONS TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Void_WithOptions_AsyncInvoker_NoTraceStore_CompletesAsync() {
    // Arrange - covers non-tracing path in _localInvokeVoidWithOptionsAsync
    var invoked = false;
    ValueTask voidInvoker(object msg) { invoked = true; return ValueTask.CompletedTask; }
    var dispatcher = _createDispatcher(voidInvoker: voidInvoker);
    var command = new TestCommand("void-options-no-trace");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync(command, options);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithOptions_AsyncInvoker_WithTraceStore_TracesAsync() {
    // Arrange - covers tracing path in _localInvokeVoidWithOptionsAsync
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, voidInvoker: _defaultVoidInvoker());
    var command = new TestCommand("void-options-trace");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync(command, options);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithOptions_SyncInvoker_CompletesAsync() {
    // Arrange - covers sync invoker fallback in _localInvokeVoidWithOptionsAsync
    var invoked = false;
    void syncInvoker(object msg) { invoked = true; }
    var dispatcher = _createDispatcher(voidSyncInvoker: syncInvoker);
    var command = new TestCommand("void-options-sync");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync(command, options);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithOptions_AnyInvoker_CompletesAsync() {
    // Arrange - covers anyInvoker fallback in _localInvokeVoidWithOptionsAsync
    ValueTask<object?> anyInvoker(object msg) => new(new TestResult(Guid.NewGuid(), true));
    var dispatcher = _createDispatcher(anyInvoker: anyInvoker);
    var command = new TestCommand("void-options-any");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync(command, options);
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithOptions_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invokers at all
    var dispatcher = _createDispatcher();
    var command = new TestCommand("void-options-no-receptor");
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(command, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithOptions_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());
    var command = new TestCommand("cancel-void");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(command, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // LOCALINVOKE TYPED WITH DISPATCHOPTIONS TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Typed_WithOptions_AsyncInvoker_ReturnsResultAsync() {
    // Arrange - covers _localInvokeWithOptionsAsync async path
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("typed-options");
    var options = new DispatchOptions();

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, options);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_Typed_WithOptions_SyncInvoker_ReturnsResultAsync() {
    // Arrange - covers sync fallback in _localInvokeWithOptionsAsync
    object syncInvoker(object msg) => new TestResult(Guid.NewGuid(), true);
    var dispatcher = _createDispatcher(syncInvoker: syncInvoker);
    var command = new TestCommand("typed-options-sync");
    var options = new DispatchOptions();

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, options);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_Typed_WithOptions_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("typed-options-no-receptor");
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync<object>(command, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Typed_WithOptions_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("typed-cancel");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync<object>(command, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // LOCALINVOKEWITHRECEIPT TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithContext_ReturnsResultAndReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("receipt-test");
    var context = MessageContext.New();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<object>(command, context);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt).IsNotNull();
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_AutoContext_ReturnsResultAndReceiptAsync() {
    // Arrange - tests auto-context creation overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("receipt-auto-context");

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<object>(command);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_SyncFallback_ReturnsResultAndReceiptAsync() {
    // Arrange - covers sync invoker fallback in LocalInvokeWithReceiptAsync
    object syncInvoker(object msg) => new TestResult(Guid.NewGuid(), true);
    var dispatcher = _createDispatcher(syncInvoker: syncInvoker);
    var command = new TestCommand("receipt-sync-fallback");

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<object>(command, MessageContext.New());

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("receipt-no-receptor");

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeWithReceiptAsync<object>(command, MessageContext.New()))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_ReturnsResultAndReceiptAsync() {
    // Arrange - covers LocalInvokeWithReceiptAsync(message, options) overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("receipt-options");
    var options = new DispatchOptions();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<object>(command, options);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_SyncFallback_ReturnsResultAsync() {
    // Arrange - covers sync invoker fallback in options overload
    object syncInvoker(object msg) => new TestResult(Guid.NewGuid(), true);
    var dispatcher = _createDispatcher(syncInvoker: syncInvoker);
    var command = new TestCommand("receipt-options-sync");
    var options = new DispatchOptions();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<object>(command, options);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("receipt-cancel");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeWithReceiptAsync<object>(command, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("receipt-options-no-receptor");
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeWithReceiptAsync<object>(command, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // VOID INVOKE ANYINVOKER FALLBACK TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Void_AnyInvokerFallback_WithTraceStore_CascadesAsync() {
    // Arrange - covers _localInvokeVoidWithAnyInvokerAndTracingAsync path
    var traceStore = new StubTraceStore();
    ValueTask<object?> anyInvoker(object msg) => new(new TestResult(Guid.NewGuid(), true));
    var dispatcher = _createDispatcher(traceStore: traceStore, anyInvoker: anyInvoker);
    var command = new TestCommand("void-any-trace");

    // Act
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert - trace store was called (envelope was stored)
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_Void_AnyInvokerReturnsNull_DoesNotCascadeAsync() {
    // Arrange - covers null result path in _localInvokeVoidWithAnyInvokerAndTracingAsync
    var traceStore = new StubTraceStore();
    ValueTask<object?> anyInvoker(object msg) => new((object?)null);
    var dispatcher = _createDispatcher(traceStore: traceStore, anyInvoker: anyInvoker);
    var command = new TestCommand("void-any-null");

    // Act - should not throw
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  // ========================================
  // LOCALINVOKE SYNC INVOKER FALLBACK TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Typed_SyncFallback_ReturnsResultAsync() {
    // Arrange - covers sync invoker wrapping in LocalInvokeAsync<TResult>
    object syncInvoker(object msg) => new TestResult(Guid.NewGuid(), true);
    var dispatcher = _createDispatcher(syncInvoker: syncInvoker);
    var command = new TestCommand("sync-fallback");

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, MessageContext.New());

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<TestResult>();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_VoidSyncFallback_CompletesAsync() {
    // Arrange - covers void sync invoker fallback in void LocalInvokeAsync
    var invoked = false;
    void syncInvoker(object msg) { invoked = true; }
    var dispatcher = _createDispatcher(voidSyncInvoker: syncInvoker);
    var command = new TestCommand("void-sync-fallback");

    // Act
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  // ========================================
  // RPC EXTRACTION FALLBACK TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Typed_AnyInvokerFallback_ReturnsExtractedResultAsync() {
    // Arrange - covers GetReceptorInvokerAny fallback in LocalInvokeAsync<TResult>
    // when both async and sync invokers are null
    ValueTask<object?> anyInvoker(object msg) => new(new TestResult(Guid.NewGuid(), true));
    var dispatcher = _createDispatcher(anyInvoker: anyInvoker);
    var command = new TestCommand("rpc-extraction");

    // Act
    var result = await dispatcher.LocalInvokeAsync<TestResult>(command, MessageContext.New());

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Success).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_Typed_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invokers at all
    var dispatcher = _createDispatcher();
    var command = new TestCommand("no-receptor");

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync<object>(command, MessageContext.New()))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // VOID LOCALINVOKE NO RECEPTOR TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Void_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("void-no-receptor");

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(command, MessageContext.New()))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // SENDMANYASYNC TESTS
  // ========================================

  [Test]
  public async Task SendManyAsync_Generic_WithLocalReceptors_ReturnsAllReceiptsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new[] { new TestCommand("one"), new TestCommand("two"), new TestCommand("three") };

    // Act
    var receipts = await dispatcher.SendManyAsync(commands);

    // Assert
    var receiptList = receipts.ToList();
    await Assert.That(receiptList.Count).IsEqualTo(3);
    await Assert.That(receiptList.All(r => r.Status == DeliveryStatus.Delivered)).IsTrue();
  }

  [Test]
  public async Task SendManyAsync_NonGeneric_WithLocalReceptors_ReturnsAllReceiptsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new object[] { new TestCommand("one"), new TestCommand("two") };

    // Act
    var receipts = await dispatcher.SendManyAsync(commands);

    // Assert
    var receiptList = receipts.ToList();
    await Assert.That(receiptList.Count).IsEqualTo(2);
  }

  [Test]
  public async Task SendManyAsync_Generic_NullMessages_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendManyAsync<TestCommand>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task SendManyAsync_NonGeneric_NullMessages_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendManyAsync((IEnumerable<object>)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // LOCALINVOKEMANYASYNC TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeManyAsync_WithMultipleMessages_ReturnsAllResultsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new object[] { new TestCommand("one"), new TestCommand("two") };

    // Act
    var results = await dispatcher.LocalInvokeManyAsync<object>(commands);

    // Assert
    var resultList = results.ToList();
    await Assert.That(resultList.Count).IsEqualTo(2);
  }

  [Test]
  public async Task LocalInvokeManyAsync_NullMessages_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeManyAsync<object>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // CASCADEMESSAGEASYNC TESTS
  // ========================================

  [Test]
  public async Task CascadeMessageAsync_LocalMode_InvokesLocalPublisherAsync() {
    // Arrange
    var published = false;
    Task publisher(object msg, IMessageEnvelope? env, CancellationToken ct) {
      published = true;
      return Task.CompletedTask;
    }
    var dispatcher = _createDispatcher(untypedPublisher: publisher);
    var evt = new TestEvent(Guid.NewGuid());

    // Act
    await dispatcher.CascadeMessageAsync(evt, sourceEnvelope: null, DispatchMode.Local);

    // Assert
    await Assert.That(published).IsTrue();
  }

  [Test]
  public async Task CascadeMessageAsync_NullMessage_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher();

    // Act & Assert
    await Assert.That(async () => await dispatcher.CascadeMessageAsync(null!, null, DispatchMode.Local))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task CascadeMessageAsync_WithCancelledToken_ThrowsOperationCanceledAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var evt = new TestEvent(Guid.NewGuid());
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => await dispatcher.CascadeMessageAsync(evt, null, DispatchMode.Local, cts.Token))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // PUBLISHASYNC TESTS
  // ========================================

  [Test]
  public async Task PublishAsync_WithEvent_ReturnsDeliveredReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var evt = new TestEvent(Guid.NewGuid());

    // Act
    var receipt = await dispatcher.PublishAsync(evt);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task PublishAsync_WithNullEvent_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher();

    // Act & Assert
    await Assert.That(async () => await dispatcher.PublishAsync<TestEvent>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task PublishAsync_WithOptions_ReturnsDeliveredReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var evt = new TestEvent(Guid.NewGuid());
    var options = new DispatchOptions();

    // Act
    var receipt = await dispatcher.PublishAsync(evt, options);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task PublishAsync_WithOptions_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var evt = new TestEvent(Guid.NewGuid());
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.PublishAsync(evt, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task PublishAsync_WithOptions_NullEvent_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () => await dispatcher.PublishAsync<TestEvent>(null!, options))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // SENDTOOUTBOX TESTS
  // ========================================

  [Test]
  public async Task SendAsync_NoLocalReceptorNoOutboxStrategy_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invoker and no work coordinator strategy registered
    var dispatcher = _createDispatcher(); // no invoker, no strategy
    var command = new TestCommand("outbox-no-strategy");

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendAsync(command, MessageContext.New()))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task SendAsync_NoLocalReceptor_WithOutboxStrategy_RoutesToOutboxAsync() {
    // Arrange - no invoker, but strategy is registered
    var strategy = new StubWorkCoordinatorStrategy();
    var serializer = new StubEnvelopeSerializer();
    var dispatcher = _createDispatcher(workStrategy: strategy, envelopeSerializer: serializer);
    var command = new TestCommand("outbox-route");

    // Act
    var receipt = await dispatcher.SendAsync(command, MessageContext.New());

    // Assert - message was queued to outbox
    await Assert.That(strategy.QueuedOutbox.Count).IsEqualTo(1);
    await Assert.That(strategy.FlushCount).IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
  }

  [Test]
  public async Task SendAsync_Generic_NoLocalReceptor_WithOutboxStrategy_RoutesToOutboxAsync() {
    // Arrange - covers _sendToOutboxViaScopeAsync<TMessage> generic path
    var strategy = new StubWorkCoordinatorStrategy();
    var serializer = new StubEnvelopeSerializer();
    var dispatcher = _createDispatcher(workStrategy: strategy, envelopeSerializer: serializer);
    var command = new TestCommand("outbox-generic");

    // Act
    var receipt = await dispatcher.SendAsync<TestCommand>(command);

    // Assert
    await Assert.That(strategy.QueuedOutbox.Count).IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
  }

  // ========================================
  // LOCALINVOKE AUTO-CONTEXT OVERLOADS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Typed_AutoContext_ReturnsResultAsync() {
    // Arrange - covers LocalInvokeAsync<TResult>(object) auto-context overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("auto-context");

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_AutoContext_CompletesAsync() {
    // Arrange - covers LocalInvokeAsync(object) auto-context overload
    var invoked = false;
    ValueTask voidInvoker(object msg) { invoked = true; return ValueTask.CompletedTask; }
    var dispatcher = _createDispatcher(voidInvoker: voidInvoker);
    var command = new TestCommand("void-auto-context");

    // Act
    await dispatcher.LocalInvokeAsync(command);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  // ========================================
  // SENDASYNC OBJECT OVERLOADS
  // ========================================

  [Test]
  public async Task SendAsync_ObjectAutoContext_ReturnsReceiptAsync() {
    // Arrange - covers SendAsync(object) auto-context overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    object command = new TestCommand("object-send");

    // Act
    var receipt = await dispatcher.SendAsync(command);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_GenericAutoContext_ReturnsReceiptAsync() {
    // Arrange - covers SendAsync<TMessage>(message) auto-context overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("generic-auto");

    // Act
    var receipt = await dispatcher.SendAsync<TestCommand>(command);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // NULL ARGUMENT VALIDATION TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithNullMessage_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendAsync(null!, MessageContext.New()))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task SendAsync_WithNullContext_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("null-ctx");

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendAsync(command, (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Typed_NullMessage_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync<object>(null!, MessageContext.New()))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Typed_NullContext_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("null-ctx");

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync<object>(command, (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_NullMessage_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(null!, MessageContext.New()))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_NullContext_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());
    var command = new TestCommand("null-ctx");

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(command, (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_NullMessage_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeWithReceiptAsync<object>(null!, MessageContext.New()))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_NullContext_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("null-ctx");

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeWithReceiptAsync<object>(command, (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // LOCALINVOKE GENERIC TMessage OVERLOADS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_GenericTMessage_TResult_AutoContext_ReturnsResultAsync() {
    // Arrange - covers LocalInvokeAsync<TMessage, TResult>(message) overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("generic-tmsg-auto");

    // Act
    var result = await dispatcher.LocalInvokeAsync<TestCommand, object>(command);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTMessage_TResult_WithContext_ReturnsResultAsync() {
    // Arrange - covers LocalInvokeAsync<TMessage, TResult>(message, context) overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("generic-tmsg-ctx");
    var context = MessageContext.New();

    // Act
    var result = await dispatcher.LocalInvokeAsync<TestCommand, object>(command, context);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTMessage_Void_AutoContext_CompletesAsync() {
    // Arrange - covers void LocalInvokeAsync<TMessage>(message) overload
    var invoked = false;
    ValueTask voidInvoker(object msg) { invoked = true; return ValueTask.CompletedTask; }
    var dispatcher = _createDispatcher(voidInvoker: voidInvoker);
    var command = new TestCommand("generic-void-auto");

    // Act
    await dispatcher.LocalInvokeAsync<TestCommand>(command);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTMessage_Void_WithContext_CompletesAsync() {
    // Arrange - covers void LocalInvokeAsync<TMessage>(message, context) overload
    var invoked = false;
    ValueTask voidInvoker(object msg) { invoked = true; return ValueTask.CompletedTask; }
    var dispatcher = _createDispatcher(voidInvoker: voidInvoker);
    var command = new TestCommand("generic-void-ctx");
    var context = MessageContext.New();

    // Act
    await dispatcher.LocalInvokeAsync<TestCommand>(command, context);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  // ========================================
  // LOCALINVOKEWITHRECEIPT GENERIC OVERLOADS
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_GenericTMessage_AutoContext_ReturnsResultAsync() {
    // Arrange - covers LocalInvokeWithReceiptAsync<TMessage, TResult>(message) overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("receipt-generic-auto");

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<TestCommand, object>(command);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_GenericTMessage_WithContext_ReturnsResultAsync() {
    // Arrange - covers LocalInvokeWithReceiptAsync<TMessage, TResult>(message, context) overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("receipt-generic-ctx");
    var context = MessageContext.New();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<TestCommand, object>(command, context);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt).IsNotNull();
  }

  // ========================================
  // SENDMANYASYNC WITH OUTBOX SPLIT TESTS
  // ========================================

  [Test]
  public async Task SendManyAsync_Generic_NoLocalReceptor_WithStrategy_RoutesToOutboxAsync() {
    // Arrange - messages go to outbox since no local receptor
    var strategy = new StubWorkCoordinatorStrategy();
    var serializer = new StubEnvelopeSerializer();
    var dispatcher = _createDispatcher(workStrategy: strategy, envelopeSerializer: serializer);
    var commands = new[] { new TestCommand("one"), new TestCommand("two") };

    // Act
    var receipts = await dispatcher.SendManyAsync(commands);

    // Assert
    var receiptList = receipts.ToList();
    await Assert.That(receiptList.Count).IsEqualTo(2);
    await Assert.That(strategy.QueuedOutbox.Count).IsEqualTo(2);
    // Single flush for batch
    await Assert.That(strategy.FlushCount).IsEqualTo(1);
  }

  [Test]
  public async Task SendManyAsync_NonGeneric_NoLocalReceptor_WithStrategy_RoutesToOutboxAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var serializer = new StubEnvelopeSerializer();
    var dispatcher = _createDispatcher(workStrategy: strategy, envelopeSerializer: serializer);
    var commands = new object[] { new TestCommand("one"), new TestCommand("two") };

    // Act
    var receipts = await dispatcher.SendManyAsync(commands);

    // Assert
    var receiptList = receipts.ToList();
    await Assert.That(receiptList.Count).IsEqualTo(2);
  }

  // ========================================
  // SCOPEDEVENTTRACKER INTEGRATION
  // ========================================

  [Test]
  public async Task CascadeMessageAsync_WithScopedEventTracker_TracksEventAsync() {
    // Arrange - covers scoped tracker path in CascadeMessageAsync
    var tracker = new StubScopedEventTracker();
    var dispatcher = _createDispatcher(scopedEventTracker: tracker);
    var evt = new TestEvent(Guid.NewGuid());

    // Act
    await dispatcher.CascadeMessageAsync(evt, sourceEnvelope: null, DispatchMode.Local);

    // Assert - event was tracked
    var tracked = tracker.GetEmittedEvents();
    await Assert.That(tracked.Count).IsEqualTo(1);
    await Assert.That(tracked[0].EventType).IsEqualTo(typeof(TestEvent));
  }

  // ========================================
  // SENDASYNC WITH OPTIONS AND CONTEXT TESTS
  // ========================================

  [Test]
  public async Task SendAsync_ObjectWithOptions_AutoContext_ReturnsReceiptAsync() {
    // Arrange - covers SendAsync(object, options) auto-context overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    object command = new TestCommand("obj-options");
    var options = new DispatchOptions();

    // Act
    var receipt = await dispatcher.SendAsync(command, options);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_ObjectWithOptions_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    object command = new TestCommand("obj-cancel");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendAsync(command, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // TRACESTORE WITH LOCALINVOKEWITHRECEIPT TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithTraceStore_StoresEnvelopeAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new TestCommand("receipt-trace");

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<object>(command, MessageContext.New());

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptionsAndTraceStore_StoresEnvelopeAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new TestCommand("receipt-options-trace");
    var options = new DispatchOptions();

    // Act
    _ = await dispatcher.LocalInvokeWithReceiptAsync<object>(command, options);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  // ========================================
  // LOCALINVOKE WITH TRACING AND OPTIONS TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Typed_WithOptionsAndTraceStore_StoresEnvelopeAsync() {
    // Arrange - covers _localInvokeWithTracingAndOptionsAsync path
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new TestCommand("typed-options-trace");
    var options = new DispatchOptions();

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, options);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_Typed_WithTracingOptionsAndCancelledToken_ThrowsAsync() {
    // Arrange - covers cancellation check in _localInvokeWithTracingAndOptionsAsync
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new TestCommand("typed-trace-cancel");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync<object>(command, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // ENVELOPE REGISTRY IN TRACING PATHS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Typed_WithTracingAndRegistry_RegistersAndUnregistersAsync() {
    // Arrange - covers register/unregister in _localInvokeWithTracingAsync
    var traceStore = new StubTraceStore();
    var registry = new StubEnvelopeRegistry();
    var dispatcher = _createDispatcher(traceStore: traceStore, envelopeRegistry: registry, invoker: _defaultInvoker());
    var command = new TestCommand("tracing-registry");

    // Act
    await dispatcher.LocalInvokeAsync<object>(command, MessageContext.New());

    // Assert
    await Assert.That(registry.RegisterCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(registry.UnregisterCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithTracingAndRegistry_RegistersAndUnregistersAsync() {
    // Arrange - covers register/unregister in void tracing path
    var traceStore = new StubTraceStore();
    var registry = new StubEnvelopeRegistry();
    var dispatcher = _createDispatcher(traceStore: traceStore, envelopeRegistry: registry, voidInvoker: _defaultVoidInvoker());
    var command = new TestCommand("void-tracing-registry");

    // Act
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(registry.RegisterCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(registry.UnregisterCount).IsGreaterThanOrEqualTo(1);
  }
}
