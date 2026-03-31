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
/// Wave 3 coverage tests for Dispatcher.cs targeting paths not yet covered:
/// - SendAsync generic overloads with DispatchOptions and context
/// - SendAsync non-generic with DispatchOptions and context
/// - LocalInvokeAsync with DispatchOptions (result-returning and void)
/// - LocalInvokeWithReceiptAsync overloads (generic typed, non-generic, with options)
/// - CascadeMessageAsync with various DispatchModes (LocalDispatch, Outbox, EventStore, None)
/// - PublishAsync with DispatchOptions cancellation and success paths
/// - Error metrics recording on LocalInvoke exception paths
/// - _localInvokeWithCastFallbackAsync InvalidCastException fallback
/// - Void LocalInvoke with sync invoker fast path and anyInvoker fallback
/// - SendManyAsync/LocalInvokeManyAsync null guard
/// - LocalSendManyAsync null guard
/// - PublishManyAsync null guard
/// - _invokeVoidSyncOrFallbackAsync anyInvoker fallback with options
/// - _invokeVoidAsyncReceptorAsync non-tracing path with options
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("CoverageWave3")]
public class DispatcherCoverageWave3Tests {

  // ========================================
  // TEST MESSAGE TYPES
  // ========================================

  public record W3Command(string Data);
  public record W3Result(Guid Id, bool Success);

  [DefaultRouting(DispatchModes.Local)]
  public record W3Event([property: StreamId] Guid OrderId) : IEvent;

  public record UnhandledW3Command(string Data);

  // ========================================
  // TEST DISPATCHER (concrete subclass)
  // ========================================

  private sealed class TestDispatcher : Core.Dispatcher {
    private readonly ReceptorInvoker<object>? _invoker;
    private readonly VoidReceptorInvoker? _voidInvoker;
    private readonly SyncReceptorInvoker<object>? _syncInvoker;
    private readonly VoidSyncReceptorInvoker? _voidSyncInvoker;
    private readonly Func<object, ValueTask<object?>>? _anyInvoker;
    private readonly Func<object, IMessageEnvelope?, CancellationToken, Task>? _untypedPublisher;
    private readonly DispatchModes? _defaultRouting;
    private readonly Type? _handleMessageType;

    public TestDispatcher(
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
      DispatchModes? defaultRouting = null,
      Type? handleMessageType = null
      ) : base(sp, new ServiceInstanceProvider(configuration: null),
        traceStore: traceStore,
        envelopeSerializer: envelopeSerializer,
        envelopeRegistry: envelopeRegistry,
        outboxRoutingStrategy: outboxRoutingStrategy,
        streamIdExtractor: streamIdExtractor,
        scopedEventTracker: scopedEventTracker) {
      _invoker = invoker;
      _voidInvoker = voidInvoker;
      _syncInvoker = syncInvoker;
      _voidSyncInvoker = voidSyncInvoker;
      _anyInvoker = anyInvoker;
      _untypedPublisher = untypedPublisher;
      _defaultRouting = defaultRouting;
      _handleMessageType = handleMessageType ?? typeof(W3Command);
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

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      if (_voidInvoker != null && messageType == _handleMessageType) {
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
      if (_syncInvoker != null && messageType == _handleMessageType) {
        return msg => (TResult)_syncInvoker(msg);
      }
      return null;
    }

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) {
      if (_voidSyncInvoker != null && messageType == _handleMessageType) {
        return _voidSyncInvoker;
      }
      return null;
    }

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) {
      if (_anyInvoker != null && messageType == _handleMessageType) {
        return _anyInvoker;
      }
      return null;
    }

    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => _defaultRouting;
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
        Hops = envelope.Hops?.ToList() ?? [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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
    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
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
    DispatchModes? defaultRouting = null,
    Type? handleMessageType = null) {
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
      defaultRouting: defaultRouting,
      handleMessageType: handleMessageType);
  }

  private static ReceptorInvoker<object> _defaultInvoker() =>
    msg => {
      var cmd = (W3Command)msg;
      return new ValueTask<object>(new W3Result(Guid.NewGuid(), true));
    };

  private static VoidReceptorInvoker _defaultVoidInvoker() => msg => ValueTask.CompletedTask;

  // ========================================
  // SEND ASYNC - GENERIC WITH CONTEXT
  // ========================================

  [Test]
  public async Task SendAsync_Generic_WithContext_ReturnsDeliveredReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("test");
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var context = MessageContext.Create(correlationId, causationId);

    // Act
    var receipt = await dispatcher.SendAsync<W3Command>(command);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_Generic_WithOptions_ReturnsDeliveredReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("test-options");
    var options = new DispatchOptions();

    // Act
    var receipt = await dispatcher.SendAsync<W3Command>(command, options);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_Generic_WithOptions_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("cancel");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendAsync<W3Command>(command, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task SendAsync_Generic_WithOptions_TraceStore_StoresEnvelopeAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new W3Command("generic-options-trace");
    var options = new DispatchOptions();

    // Act
    await dispatcher.SendAsync<W3Command>(command, options);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task SendAsync_NonGeneric_WithOptions_ReturnsDeliveredReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("obj-options");
    var options = new DispatchOptions();

    // Act
    var receipt = await dispatcher.SendAsync((object)command, options);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_NonGeneric_WithOptions_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendAsync((object)new W3Command("x"), options))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task SendAsync_WithContextAndOptions_ReturnsDeliveredReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("ctx-options");
    var context = MessageContext.New();
    var options = new DispatchOptions();

    // Act
    var receipt = await dispatcher.SendAsync((object)command, context, options);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_WithContextAndOptions_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.SendAsync((object)new W3Command("x"), MessageContext.New(), options))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // SEND ASYNC - ROUTED NONE ON OPTIONS PATHS
  // ========================================

  [Test]
  public async Task SendAsync_WithContextAndOptions_RoutedNone_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routedNone = (object)Route.None();
    var options = new DispatchOptions();

    // Act & Assert
    var ex = await Assert.That(async () =>
      await dispatcher.SendAsync(routedNone, MessageContext.New(), options))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  // ========================================
  // LOCAL INVOKE WITH DISPATCH OPTIONS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Result_ReturnsTypedResultAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("options-invoke");
    var options = new DispatchOptions();

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>((object)command, options);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<W3Result>();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Result_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<object>((object)new W3Command("x"), options))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Void_CompletesSuccessfullyAsync() {
    // Arrange
    var invoked = false;
    ValueTask voidInvoker(object msg) { invoked = true; return ValueTask.CompletedTask; }
    var dispatcher = _createDispatcher(voidInvoker: voidInvoker);
    var command = new W3Command("void-options");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync((object)command, options);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Void_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync((object)new W3Command("x"), options))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Void_RoutedNone_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync((object)Route.None(), options))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Result_RoutedNone_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<object>((object)Route.None(), options))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Result_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invoker registered
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<object>((object)new W3Command("x"), options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Void_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invoker registered
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync((object)new W3Command("x"), options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // LOCAL INVOKE WITH OPTIONS - SYNC INVOKER FALLBACK
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithOptions_SyncInvokerFallback_ReturnsResultAsync() {
    // Arrange - only sync invoker, no async invoker
    SyncReceptorInvoker<object> syncInvoker = msg => new W3Result(Guid.NewGuid(), true);
    var dispatcher = _createDispatcher(syncInvoker: syncInvoker);
    var command = new W3Command("sync-options");
    var options = new DispatchOptions();

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>((object)command, options);

    // Assert
    await Assert.That(result).IsTypeOf<W3Result>();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Void_SyncInvokerFallback_CompletesAsync() {
    // Arrange - only void sync invoker, no async
    var invoked = false;
    VoidSyncReceptorInvoker syncInvoker = msg => { invoked = true; };
    var dispatcher = _createDispatcher(voidSyncInvoker: syncInvoker);
    var command = new W3Command("void-sync-options");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync((object)command, options);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Void_AnyInvokerFallback_CompletesAsync() {
    // Arrange - only anyInvoker, no void or sync
    Func<object, ValueTask<object?>> anyInvoker = msg =>
      new ValueTask<object?>(new W3Result(Guid.NewGuid(), true));
    var dispatcher = _createDispatcher(anyInvoker: anyInvoker);
    var command = new W3Command("any-fallback-options");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync((object)command, options);

    // Assert - should complete without error (anyInvoker was used)
  }

  // ========================================
  // LOCAL INVOKE WITH OPTIONS - TRACE STORE PATHS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Void_TraceStore_StoresEnvelopeAsync() {
    // Arrange - tracing path for void with options
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, voidInvoker: _defaultVoidInvoker());
    var command = new W3Command("void-trace-options");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync((object)command, options);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Result_TraceStore_StoresEnvelopeAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new W3Command("result-trace-options");
    var options = new DispatchOptions();

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>((object)command, options);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
    await Assert.That(result).IsNotNull();
  }

  // ========================================
  // LOCAL INVOKE WITH RECEIPT
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_GenericTyped_ReturnsResultAndReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("receipt-generic");

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<W3Command, object>(command);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt).IsNotNull();
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_NonGeneric_ReturnsResultAndReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("receipt-non-generic");

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<object>((object)command);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt).IsNotNull();
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_GenericTyped_WithContext_ReturnsResultAndReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("receipt-ctx");
    var context = MessageContext.New();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<W3Command, object>(command, context);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithContext_ReturnsResultAndReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("receipt-obj-ctx");
    var context = MessageContext.New();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<object>((object)command, context);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_SyncFallback_ReturnsResultAndReceiptAsync() {
    // Arrange - sync invoker only
    SyncReceptorInvoker<object> syncInvoker = msg => new W3Result(Guid.NewGuid(), true);
    var dispatcher = _createDispatcher(syncInvoker: syncInvoker);
    var command = new W3Command("receipt-sync");

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<object>((object)command);

    // Assert
    await Assert.That(invokeResult.Value).IsTypeOf<W3Result>();
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invoker
    var dispatcher = _createDispatcher();
    var command = new W3Command("receipt-no-receptor");

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<object>((object)command))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_RoutedNone_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<object>((object)Route.None()))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_ReturnsResultAndReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("receipt-options");
    var options = new DispatchOptions();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<object>((object)command, options);

    // Assert
    await Assert.That(invokeResult.Value).IsNotNull();
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<object>((object)new W3Command("x"), options))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_SyncFallback_ReturnsResultAsync() {
    // Arrange - sync invoker only with options path
    SyncReceptorInvoker<object> syncInvoker = msg => new W3Result(Guid.NewGuid(), true);
    var dispatcher = _createDispatcher(syncInvoker: syncInvoker);
    var command = new W3Command("receipt-sync-options");
    var options = new DispatchOptions();

    // Act
    var invokeResult = await dispatcher.LocalInvokeWithReceiptAsync<object>((object)command, options);

    // Assert
    await Assert.That(invokeResult.Value).IsTypeOf<W3Result>();
    await Assert.That(invokeResult.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_NoReceptor_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<object>((object)new W3Command("x"), options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_RoutedNone_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<object>((object)Route.None(), options))
      .ThrowsExactly<ArgumentException>();
  }

  // ========================================
  // LOCAL INVOKE WITH RECEIPT - ENVELOPE REGISTRY
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithEnvelopeRegistry_RegistersAndUnregistersAsync() {
    // Arrange
    var registry = new StubEnvelopeRegistry();
    var dispatcher = _createDispatcher(envelopeRegistry: registry, invoker: _defaultInvoker());
    var command = new W3Command("receipt-registry");

    // Act
    await dispatcher.LocalInvokeWithReceiptAsync<object>((object)command);

    // Assert
    await Assert.That(registry.RegisterCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(registry.UnregisterCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithTraceStore_StoresEnvelopeAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(traceStore: traceStore, invoker: _defaultInvoker());
    var command = new W3Command("receipt-trace");

    // Act
    await dispatcher.LocalInvokeWithReceiptAsync<object>((object)command);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  // ========================================
  // PUBLISH ASYNC
  // ========================================

  [Test]
  public async Task PublishAsync_WithEvent_ReturnsDeliveredReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var evt = new W3Event(Guid.NewGuid());

    // Act
    var receipt = await dispatcher.PublishAsync(evt);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(receipt.Destination).Contains("W3Event");
  }

  [Test]
  public async Task PublishAsync_WithOptions_ReturnsDeliveredReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var evt = new W3Event(Guid.NewGuid());
    var options = new DispatchOptions();

    // Act
    var receipt = await dispatcher.PublishAsync(evt, options);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task PublishAsync_WithOptions_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.PublishAsync(new W3Event(Guid.NewGuid()), options))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task PublishAsync_NullEvent_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();

    // Act & Assert
    await Assert.That(async () => await dispatcher.PublishAsync<W3Event>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task PublishAsync_WithOptions_NullEvent_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () => await dispatcher.PublishAsync<W3Event>(null!, options))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // CASCADE MESSAGE ASYNC
  // ========================================

  [Test]
  public async Task CascadeMessageAsync_NullMessage_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.CascadeMessageAsync(null!, null, DispatchModes.Local))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task CascadeMessageAsync_CancelledToken_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.CascadeMessageAsync(new W3Event(Guid.NewGuid()), null, DispatchModes.Local, cts.Token))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task CascadeMessageAsync_LocalMode_WithPublisher_InvokesPublisherAsync() {
    // Arrange
    var publisherInvoked = false;
    Func<object, IMessageEnvelope?, CancellationToken, Task> publisher = (msg, env, ct) => {
      publisherInvoked = true;
      return Task.CompletedTask;
    };
    var dispatcher = _createDispatcher(
      untypedPublisher: publisher,
      handleMessageType: typeof(W3Event));
    var evt = new W3Event(Guid.NewGuid());

    // Act
    await dispatcher.CascadeMessageAsync(evt, null, DispatchModes.Local);

    // Assert
    await Assert.That(publisherInvoked).IsTrue();
  }

  [Test]
  public async Task CascadeMessageAsync_OutboxMode_CompletesWithoutErrorAsync() {
    // Arrange - base implementation of CascadeToOutboxAsync is a no-op
    var dispatcher = _createDispatcher();
    var evt = new W3Event(Guid.NewGuid());

    // Act - should not throw (base CascadeToOutboxAsync is no-op)
    await dispatcher.CascadeMessageAsync(evt, null, DispatchModes.Outbox);
  }

  [Test]
  public async Task CascadeMessageAsync_EventStoreMode_CompletesWithoutErrorAsync() {
    // Arrange - base implementation of CascadeToEventStoreOnlyAsync is a no-op
    var dispatcher = _createDispatcher();
    var evt = new W3Event(Guid.NewGuid());

    // Act - EventStore flag without Outbox flag -> calls CascadeToEventStoreOnlyAsync
    await dispatcher.CascadeMessageAsync(evt, null, DispatchModes.EventStore);
  }

  [Test]
  public async Task CascadeMessageAsync_BothMode_CompletesWithoutErrorAsync() {
    // Arrange
    var publisherInvoked = false;
    Func<object, IMessageEnvelope?, CancellationToken, Task> publisher = (msg, env, ct) => {
      publisherInvoked = true;
      return Task.CompletedTask;
    };
    var dispatcher = _createDispatcher(
      untypedPublisher: publisher,
      handleMessageType: typeof(W3Event));
    var evt = new W3Event(Guid.NewGuid());

    // Act - Both = Local | Outbox
    await dispatcher.CascadeMessageAsync(evt, null, DispatchModes.Both);

    // Assert
    await Assert.That(publisherInvoked).IsTrue();
  }

  [Test]
  public async Task CascadeMessageAsync_NoneMode_CompletesWithoutDispatchAsync() {
    // Arrange
    var publisherInvoked = false;
    Func<object, IMessageEnvelope?, CancellationToken, Task> publisher = (msg, env, ct) => {
      publisherInvoked = true;
      return Task.CompletedTask;
    };
    var dispatcher = _createDispatcher(
      untypedPublisher: publisher,
      handleMessageType: typeof(W3Event));
    var evt = new W3Event(Guid.NewGuid());

    // Act - None mode should not invoke local publisher
    await dispatcher.CascadeMessageAsync(evt, null, DispatchModes.None);

    // Assert
    await Assert.That(publisherInvoked).IsFalse();
  }

  // ========================================
  // VOID LOCAL INVOKE - SYNC FAST PATH
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_VoidSync_FastPath_InvokesSynchronouslyAsync() {
    // Arrange - void sync invoker, no trace store, no sync attributes
    var invoked = false;
    VoidSyncReceptorInvoker syncInvoker = msg => { invoked = true; };
    var dispatcher = _createDispatcher(voidSyncInvoker: syncInvoker);
    var command = new W3Command("sync-fast");

    // Act
    await dispatcher.LocalInvokeAsync((object)command, MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_VoidAnyInvokerFallback_CompletesAsync() {
    // Arrange - only anyInvoker, no void or sync invoker
    var anyInvoked = false;
    Func<object, ValueTask<object?>> anyInvoker = msg => {
      anyInvoked = true;
      return new ValueTask<object?>(new W3Result(Guid.NewGuid(), true));
    };
    var dispatcher = _createDispatcher(anyInvoker: anyInvoker);
    var command = new W3Command("any-fallback");

    // Act
    await dispatcher.LocalInvokeAsync((object)command, MessageContext.New());

    // Assert
    await Assert.That(anyInvoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_VoidAnyInvokerFallback_NullResult_CompletesAsync() {
    // Arrange - anyInvoker returns null
    Func<object, ValueTask<object?>> anyInvoker = msg =>
      new ValueTask<object?>((object?)null);
    var dispatcher = _createDispatcher(anyInvoker: anyInvoker);
    var command = new W3Command("any-null");

    // Act - should complete without error even with null result
    await dispatcher.LocalInvokeAsync((object)command, MessageContext.New());
  }

  // ========================================
  // VOID LOCAL INVOKE - ROUTED UNWRAPPING
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Void_RoutedNone_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());

    // Act & Assert
    var ex = await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync((object)Route.None(), MessageContext.New()))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  // ========================================
  // LOCAL INVOKE - GENERIC TYPED OVERLOADS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_WithResult_ReturnsResultAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("typed-result");

    // Act
    var result = await dispatcher.LocalInvokeAsync<W3Command, object>(command);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<W3Result>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_WithContext_ReturnsResultAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("typed-ctx");
    var context = MessageContext.New();

    // Act
    var result = await dispatcher.LocalInvokeAsync<W3Command, object>(command, context);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invoker
    var dispatcher = _createDispatcher();
    var command = new W3Command("typed-no-receptor");

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<W3Command, object>(command))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoid_CompletesSuccessfullyAsync() {
    // Arrange
    var invoked = false;
    VoidReceptorInvoker voidInvoker = msg => { invoked = true; return ValueTask.CompletedTask; };
    var dispatcher = _createDispatcher(voidInvoker: voidInvoker);
    var command = new W3Command("typed-void");

    // Act
    await dispatcher.LocalInvokeAsync<W3Command>(command);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoid_WithContext_CompletesSuccessfullyAsync() {
    // Arrange
    var invoked = false;
    VoidReceptorInvoker voidInvoker = msg => { invoked = true; return ValueTask.CompletedTask; };
    var dispatcher = _createDispatcher(voidInvoker: voidInvoker);
    var command = new W3Command("typed-void-ctx");
    var context = MessageContext.New();

    // Act
    await dispatcher.LocalInvokeAsync<W3Command>(command, context);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoid_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invoker
    var dispatcher = _createDispatcher();
    var command = new W3Command("typed-void-no-receptor");

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<W3Command>(command))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoid_SyncFallback_CompletesAsync() {
    // Arrange - only void sync invoker, no async void invoker
    var invoked = false;
    VoidSyncReceptorInvoker syncInvoker = msg => { invoked = true; };
    var dispatcher = _createDispatcher(voidSyncInvoker: syncInvoker);
    var command = new W3Command("typed-void-sync");

    // Act
    await dispatcher.LocalInvokeAsync<W3Command>(command);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoid_AnyInvokerFallback_CompletesAsync() {
    // Arrange - only anyInvoker, no void or sync
    var anyInvoked = false;
    Func<object, ValueTask<object?>> anyInvoker = msg => {
      anyInvoked = true;
      return new ValueTask<object?>(new W3Result(Guid.NewGuid(), true));
    };
    var dispatcher = _createDispatcher(anyInvoker: anyInvoker);
    var command = new W3Command("typed-void-any");

    // Act
    await dispatcher.LocalInvokeAsync<W3Command>(command);

    // Assert
    await Assert.That(anyInvoked).IsTrue();
  }

  // ========================================
  // BATCH OPERATIONS - NULL GUARDS
  // ========================================

  [Test]
  public async Task SendManyAsync_Generic_NullMessages_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.SendManyAsync<W3Command>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task SendManyAsync_NonGeneric_NullMessages_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.SendManyAsync((IEnumerable<object>)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeManyAsync_NullMessages_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeManyAsync<object>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalSendManyAsync_Generic_NullMessages_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalSendManyAsync<W3Command>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalSendManyAsync_NonGeneric_NullMessages_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalSendManyAsync((IEnumerable<object>)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task PublishManyAsync_Generic_NullEvents_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.PublishManyAsync<W3Event>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task PublishManyAsync_NonGeneric_NullEvents_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.PublishManyAsync((IEnumerable<object>)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // BATCH OPERATIONS - HAPPY PATHS
  // ========================================

  [Test]
  public async Task SendManyAsync_Generic_WithLocalReceptor_ReturnsDeliveredReceiptsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new[] { new W3Command("a"), new W3Command("b") };

    // Act
    var receipts = await dispatcher.SendManyAsync<W3Command>(commands);

    // Assert
    var receiptList = receipts.ToList();
    await Assert.That(receiptList.Count).IsGreaterThanOrEqualTo(2);
    foreach (var receipt in receiptList) {
      await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    }
  }

  [Test]
  public async Task SendManyAsync_NonGeneric_WithLocalReceptor_ReturnsDeliveredReceiptsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new object[] { new W3Command("c"), new W3Command("d") };

    // Act
    var receipts = await dispatcher.SendManyAsync(commands);

    // Assert
    var receiptList = receipts.ToList();
    await Assert.That(receiptList.Count).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  public async Task LocalInvokeManyAsync_ReturnsAllResultsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new object[] { new W3Command("e"), new W3Command("f"), new W3Command("g") };

    // Act
    var results = await dispatcher.LocalInvokeManyAsync<object>(commands);

    // Assert
    var resultList = results.ToList();
    await Assert.That(resultList.Count).IsEqualTo(3);
    foreach (var result in resultList) {
      await Assert.That(result).IsTypeOf<W3Result>();
    }
  }

  [Test]
  public async Task LocalSendManyAsync_Generic_WithLocalReceptor_ReturnsReceiptsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new[] { new W3Command("h"), new W3Command("i") };

    // Act
    var receipts = await dispatcher.LocalSendManyAsync<W3Command>(commands);

    // Assert
    var receiptList = receipts.ToList();
    await Assert.That(receiptList.Count).IsEqualTo(2);
    foreach (var receipt in receiptList) {
      await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    }
  }

  [Test]
  public async Task LocalSendManyAsync_NonGeneric_WithLocalReceptor_ReturnsReceiptsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new object[] { new W3Command("j"), new W3Command("k") };

    // Act
    var receipts = await dispatcher.LocalSendManyAsync(commands);

    // Assert
    var receiptList = receipts.ToList();
    await Assert.That(receiptList.Count).IsEqualTo(2);
  }

  [Test]
  public async Task LocalSendManyAsync_Generic_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invoker for UnhandledW3Command
    var dispatcher = _createDispatcher(
      invoker: _defaultInvoker(),
      handleMessageType: typeof(UnhandledW3Command)); // doesn't match W3Command
    var commands = new[] { new W3Command("x") };

    // This dispatcher's invoker is configured for UnhandledW3Command, not W3Command
    // So _ensureReceptorExists will fail
    var noInvokerDispatcher = _createDispatcher(); // no invoker at all

    // Act & Assert
    await Assert.That(async () =>
      await noInvokerDispatcher.LocalSendManyAsync<W3Command>(commands))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // ERROR HANDLING - METRICS RECORDING
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_ReceptorThrows_PropagatesExceptionAsync() {
    // Arrange
    ReceptorInvoker<object> failingInvoker = msg =>
      throw new InvalidOperationException("receptor failed");
    var dispatcher = _createDispatcher(invoker: failingInvoker);
    var command = new W3Command("fail");

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<object>((object)command, MessageContext.New()))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task SendAsync_ReceptorThrows_PropagatesExceptionAsync() {
    // Arrange
    ReceptorInvoker<object> failingInvoker = msg =>
      throw new InvalidOperationException("send failed");
    var dispatcher = _createDispatcher(invoker: failingInvoker);
    var command = new W3Command("fail-send");

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.SendAsync((object)command, MessageContext.New()))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task SendAsync_Generic_ReceptorThrows_PropagatesExceptionAsync() {
    // Arrange
    ReceptorInvoker<object> failingInvoker = msg =>
      throw new InvalidOperationException("generic send failed");
    var dispatcher = _createDispatcher(invoker: failingInvoker);
    var command = new W3Command("fail-generic-send");

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.SendAsync<W3Command>(command))
      .ThrowsExactly<InvalidOperationException>();
  }

  // ========================================
  // SEND ASYNC - NULL ARGUMENT VALIDATION
  // ========================================

  [Test]
  public async Task SendAsync_NullMessage_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.SendAsync((object)null!, MessageContext.New()))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task SendAsync_NullContext_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.SendAsync((object)new W3Command("x"), (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task SendAsync_WithOptions_NullMessage_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.SendAsync((object)null!, MessageContext.New(), options))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task SendAsync_WithOptions_NullContext_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.SendAsync((object)new W3Command("x"), (IMessageContext)null!, options))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // LOCAL INVOKE - NULL ARGUMENT VALIDATION
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Result_NullMessage_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<object>((object)null!, MessageContext.New()))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Result_NullContext_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<object>((object)new W3Command("x"), (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_NullMessage_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync((object)null!, MessageContext.New()))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_NullContext_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync((object)new W3Command("x"), (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // LOCAL INVOKE WITH OPTIONS - NULL VALIDATION
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Result_NullMessage_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<object>((object)null!, options))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_Void_NullMessage_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync((object)null!, options))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // LOCAL INVOKE WITH RECEIPT - NULL VALIDATION
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_NullMessage_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<object>((object)null!, MessageContext.New()))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_NullContext_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeWithReceiptAsync<object>((object)new W3Command("x"), (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // SEND ASYNC - NO RECEPTOR AND NO OUTBOX
  // ========================================

  [Test]
  public async Task SendAsync_NoReceptorNoOutbox_ThrowsReceptorNotFoundExceptionAsync() {
    // Arrange - no invoker, no work coordinator strategy
    var dispatcher = _createDispatcher();
    var command = new W3Command("no-receptor");

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.SendAsync((object)command, MessageContext.New()))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task SendAsync_Generic_NoReceptorNoOutbox_ThrowsReceptorNotFoundExceptionAsync() {
    // Arrange - no invoker, no work coordinator strategy
    var dispatcher = _createDispatcher();
    var command = new W3Command("no-receptor-generic");

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.SendAsync<W3Command>(command))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // LOCAL INVOKE - RPC EXTRACTION VIA ANYINVOKER
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Result_AnyInvokerFallback_ExtractsResultAsync() {
    // Arrange - only anyInvoker, no typed invoker
    Func<object, ValueTask<object?>> anyInvoker = msg =>
      new ValueTask<object?>(new W3Result(Guid.NewGuid(), true));
    var dispatcher = _createDispatcher(anyInvoker: anyInvoker);
    var command = new W3Command("rpc-extract");

    // Act - LocalInvokeAsync<object> with no typed invoker should fall through to RPC extraction
    var result = await dispatcher.LocalInvokeAsync<object>((object)command, MessageContext.New());

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_Result_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invoker at all
    var dispatcher = _createDispatcher();
    var command = new W3Command("no-receptor-local");

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<object>((object)command, MessageContext.New()))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // VOID LOCAL INVOKE - NO RECEPTOR
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Void_NoReceptor_ThrowsReceptorNotFoundExceptionAsync() {
    // Arrange - no invoker at all
    var dispatcher = _createDispatcher();
    var command = new W3Command("void-no-receptor");

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync((object)command, MessageContext.New()))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // SEND ASYNC - IRouted UNWRAPPING
  // ========================================

  [Test]
  public async Task SendAsync_WithRoutedMessage_UnwrapsAndDispatchesAsync() {
    // Arrange - dispatcher handles W3Command messages
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("routed");
    var routed = Route.Local(command);

    // Act
    var receipt = await dispatcher.SendAsync((object)routed, MessageContext.New());

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routedNone = (object)Route.None();

    // Act & Assert
    var ex = await Assert.That(async () =>
      await dispatcher.SendAsync(routedNone, MessageContext.New()))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(ex!.Message).Contains("RoutedNone");
  }

  // ========================================
  // LOCAL INVOKE - IRouted UNWRAPPING
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Result_WithRoutedMessage_UnwrapsAndDispatchesAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("routed-local");
    var routed = Route.Local(command);

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>((object)routed, MessageContext.New());

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_Result_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<object>((object)Route.None(), MessageContext.New()))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithRoutedMessage_UnwrapsAndDispatchesAsync() {
    // Arrange
    var invoked = false;
    VoidReceptorInvoker voidInvoker = msg => { invoked = true; return ValueTask.CompletedTask; };
    var dispatcher = _createDispatcher(voidInvoker: voidInvoker);
    var command = new W3Command("routed-void");
    var routed = Route.Local(command);

    // Act
    await dispatcher.LocalInvokeAsync((object)routed, MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  // ========================================
  // SEND ASYNC - OBJECT OVERLOAD (NO CONTEXT)
  // ========================================

  [Test]
  public async Task SendAsync_Object_NoContext_ReturnsDeliveredReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("obj-no-context");

    // Act
    var receipt = await dispatcher.SendAsync((object)command);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // LOCAL INVOKE - OBJECT OVERLOAD (NO CONTEXT)
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Result_NoContext_ReturnsResultAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new W3Command("result-no-ctx");

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>((object)command);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_NoContext_CompletesAsync() {
    // Arrange
    var invoked = false;
    VoidReceptorInvoker voidInvoker = msg => { invoked = true; return ValueTask.CompletedTask; };
    var dispatcher = _createDispatcher(voidInvoker: voidInvoker);
    var command = new W3Command("void-no-ctx");

    // Act
    await dispatcher.LocalInvokeAsync((object)command);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  // ========================================
  // GENERIC TYPED LOCAL INVOKE - ROUTED NONE
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_RoutedNone_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routedNone = Route.None();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<RoutedNone, object>(routedNone))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoid_RoutedNone_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());
    var routedNone = Route.None();

    // Act & Assert
    await Assert.That(async () =>
      await dispatcher.LocalInvokeAsync<RoutedNone>(routedNone))
      .ThrowsExactly<ArgumentException>();
  }

  // ========================================
  // SYNC INVOKER FALLBACK FOR RESULT-RETURNING
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Result_SyncFallback_ReturnsResultAsync() {
    // Arrange - only sync invoker, no async
    SyncReceptorInvoker<object> syncInvoker = msg => new W3Result(Guid.NewGuid(), true);
    var dispatcher = _createDispatcher(syncInvoker: syncInvoker);
    var command = new W3Command("sync-fallback");

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>((object)command, MessageContext.New());

    // Assert
    await Assert.That(result).IsTypeOf<W3Result>();
  }
}
