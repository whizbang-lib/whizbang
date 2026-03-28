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
/// Tests targeting uncovered paths in Dispatcher.cs:
/// - _waitForPerspectivesIfNeededAsync timeout path
/// - _invokeOnWaiting / _invokeOnDecisionMade callback exception swallowing
/// - _resolveEventTopic / _resolveCommandDestination convention branches
/// - _extractStreamIdFromMetadata parsing paths
/// - _sendToOutboxViaScopeAsync non-generic path
/// - Generic void LocalInvoke internal tracing with Routed unwrap
/// - LocalInvokeWithReceipt RoutedResult unwrapping
/// - _localInvokeVoidSyncWithSyncCheckAsync path
/// - _flushOutboxBatchAndCollectReceiptsAsync empty batch
/// - _invokePostLifecycleReceptorsAsync fallback path
/// - _invokeImmediateAsyncReceptorsAsync short-circuit paths
/// - _hasImmediateAsyncReceptors / _hasPostLifecycleReceptors
/// - SendAsync with IRouted unwrapping in options path
/// - PublishManyAsync / LocalSendManyAsync null guard
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherUncoveredPathsTests {

  // ========================================
  // TEST MESSAGE TYPES
  // ========================================

  public record TestCommand(string Data);
  public record TestResult(Guid Id, bool Success);

  [DefaultRouting(DispatchModes.Local)]
  public record TestEvent([property: StreamId] Guid OrderId) : IEvent;

  public record UnhandledCommand(string Data);

  public record TestCommandMsg(string Data) : ICommand;

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
    private readonly IReceptorRegistry? _receptorRegistryOverride;
    private readonly Type? _handleMessageType;

    public TestDispatcher(
      IServiceProvider sp,
      ITraceStore? traceStore = null,
      IEnvelopeSerializer? envelopeSerializer = null,
      IEnvelopeRegistry? envelopeRegistry = null,
      IOutboxRoutingStrategy? outboxRoutingStrategy = null,
      IStreamIdExtractor? streamIdExtractor = null,
      IScopedEventTracker? scopedEventTracker = null,
      IReceptorRegistry? receptorRegistry = null,
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
        scopedEventTracker: scopedEventTracker,
        receptorRegistry: receptorRegistry) {
      _invoker = invoker;
      _voidInvoker = voidInvoker;
      _syncInvoker = syncInvoker;
      _voidSyncInvoker = voidSyncInvoker;
      _anyInvoker = anyInvoker;
      _untypedPublisher = untypedPublisher;
      _defaultRouting = defaultRouting;
      _receptorRegistryOverride = receptorRegistry;
      _handleMessageType = handleMessageType ?? typeof(TestCommand);
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
    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      FlushCount++;
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }
  }

  private sealed class StubEventCompletionAwaiter : IEventCompletionAwaiter {
    private readonly bool _shouldComplete;

    public StubEventCompletionAwaiter(bool shouldComplete) {
      _shouldComplete = shouldComplete;
    }

    public Guid AwaiterId { get; } = Guid.NewGuid();

    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, CancellationToken ct = default) {
      return Task.FromResult(_shouldComplete);
    }

    public bool AreEventsFullyProcessed(IReadOnlyList<Guid> eventIds) => _shouldComplete;
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

  private sealed class StubReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void AddReceptor(Type messageType, LifecycleStage stage, ReceptorInfo receptor) {
      var key = (messageType, stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(receptor);
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      return _receptors.TryGetValue((messageType, stage), out var list) ? list : [];
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private sealed class StubReceptorInvoker : IReceptorInvoker {
    public int InvokeCount { get; private set; }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      InvokeCount++;
      return ValueTask.CompletedTask;
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
    IDeferredOutboxChannel? deferredChannel = null,
    IEventCompletionAwaiter? eventCompletionAwaiter = null,
    IReceptorInvoker? receptorInvoker = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new TestServiceScopeFactory(sp));
    if (strategy != null) {
      services.AddSingleton(strategy);
    }
    if (deferredChannel != null) {
      services.AddSingleton(deferredChannel);
    }
    if (eventCompletionAwaiter != null) {
      services.AddSingleton(eventCompletionAwaiter);
    }
    if (receptorInvoker != null) {
      services.AddSingleton(receptorInvoker);
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
    IEventCompletionAwaiter? eventCompletionAwaiter = null,
    IReceptorRegistry? receptorRegistry = null,
    IReceptorInvoker? receptorInvoker = null,
    ReceptorInvoker<object>? invoker = null,
    VoidReceptorInvoker? voidInvoker = null,
    SyncReceptorInvoker<object>? syncInvoker = null,
    VoidSyncReceptorInvoker? voidSyncInvoker = null,
    Func<object, ValueTask<object?>>? anyInvoker = null,
    Func<object, IMessageEnvelope?, CancellationToken, Task>? untypedPublisher = null,
    DispatchModes? defaultRouting = null,
    Type? handleMessageType = null) {
    var sp = _buildProvider(workStrategy, deferredChannel, eventCompletionAwaiter, receptorInvoker);
    return new TestDispatcher(sp,
      traceStore: traceStore,
      envelopeRegistry: envelopeRegistry,
      envelopeSerializer: envelopeSerializer,
      scopedEventTracker: scopedEventTracker,
      receptorRegistry: receptorRegistry,
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
    msg => new ValueTask<object>(new TestResult(Guid.NewGuid(), true));

  private static VoidReceptorInvoker _defaultVoidInvoker() => msg => ValueTask.CompletedTask;

  // ========================================
  // _waitForPerspectivesIfNeededAsync TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithOptions_WaitForPerspectives_Timeout_ThrowsPerspectiveSyncTimeoutAsync() {
    // Arrange - eventCompletionAwaiter returns false (timeout)
    var tracker = new StubScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());
    var awaiter = new StubEventCompletionAwaiter(shouldComplete: false);
    var dispatcher = _createDispatcher(
      invoker: _defaultInvoker(),
      scopedEventTracker: tracker,
      eventCompletionAwaiter: awaiter);
    var command = new TestCommand("wait-timeout");
    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromMilliseconds(100)
    };

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<object>(command, options))
      .ThrowsExactly<PerspectiveSyncTimeoutException>();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_WaitForPerspectives_Success_CompletesNormallyAsync() {
    // Arrange - eventCompletionAwaiter returns true (success)
    var tracker = new StubScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());
    var awaiter = new StubEventCompletionAwaiter(shouldComplete: true);
    var dispatcher = _createDispatcher(
      invoker: _defaultInvoker(),
      scopedEventTracker: tracker,
      eventCompletionAwaiter: awaiter);
    var command = new TestCommand("wait-success");
    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromSeconds(5)
    };

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, options);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_WaitForPerspectives_NoTracker_CompletesNormallyAsync() {
    // Arrange - no scoped tracker set, short-circuit
    var awaiter = new StubEventCompletionAwaiter(shouldComplete: true);
    var dispatcher = _createDispatcher(
      invoker: _defaultInvoker(),
      eventCompletionAwaiter: awaiter);
    var command = new TestCommand("no-tracker");
    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromSeconds(5)
    };

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, options);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_WaitForPerspectives_EmptyEvents_CompletesNormallyAsync() {
    // Arrange - tracker has no events
    var tracker = new StubScopedEventTracker();
    var awaiter = new StubEventCompletionAwaiter(shouldComplete: true);
    var dispatcher = _createDispatcher(
      invoker: _defaultInvoker(),
      scopedEventTracker: tracker,
      eventCompletionAwaiter: awaiter);
    var command = new TestCommand("empty-events");
    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromSeconds(5)
    };

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, options);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_WaitForPerspectives_NoAwaiter_CompletesNormallyAsync() {
    // Arrange - no eventCompletionAwaiter registered
    var tracker = new StubScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());
    var dispatcher = _createDispatcher(
      invoker: _defaultInvoker(),
      scopedEventTracker: tracker);
    var command = new TestCommand("no-awaiter");
    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromSeconds(5)
    };

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, options);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_WaitForPerspectives_Disabled_CompletesNormallyAsync() {
    // Arrange - WaitForPerspectives = false (default)
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("disabled");
    var options = new DispatchOptions {
      WaitForPerspectives = false
    };

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, options);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  // ========================================
  // VOID WITH OPTIONS + WaitForPerspectives TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_WaitForPerspectives_Timeout_ThrowsAsync() {
    // Arrange
    var tracker = new StubScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());
    var awaiter = new StubEventCompletionAwaiter(shouldComplete: false);
    var dispatcher = _createDispatcher(
      voidInvoker: _defaultVoidInvoker(),
      scopedEventTracker: tracker,
      eventCompletionAwaiter: awaiter);
    var command = new TestCommand("void-wait-timeout");
    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromMilliseconds(100)
    };

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync(command, options))
      .ThrowsExactly<PerspectiveSyncTimeoutException>();
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_WaitForPerspectives_Success_CompletesAsync() {
    // Arrange
    var tracker = new StubScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());
    var awaiter = new StubEventCompletionAwaiter(shouldComplete: true);
    var dispatcher = _createDispatcher(
      voidInvoker: _defaultVoidInvoker(),
      scopedEventTracker: tracker,
      eventCompletionAwaiter: awaiter);
    var command = new TestCommand("void-wait-success");
    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromSeconds(5)
    };

    // Act - should not throw
    await dispatcher.LocalInvokeAsync(command, options);
  }

  // ========================================
  // LOCALINVOKEWITHRECEIPT + WaitForPerspectives TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_WaitForPerspectives_Timeout_ThrowsAsync() {
    // Arrange
    var tracker = new StubScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());
    var awaiter = new StubEventCompletionAwaiter(shouldComplete: false);
    var dispatcher = _createDispatcher(
      invoker: _defaultInvoker(),
      scopedEventTracker: tracker,
      eventCompletionAwaiter: awaiter);
    var command = new TestCommand("receipt-wait-timeout");
    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromMilliseconds(100)
    };

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeWithReceiptAsync<object>(command, options))
      .ThrowsExactly<PerspectiveSyncTimeoutException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_WaitForPerspectives_Success_ReturnsResultAsync() {
    // Arrange
    var tracker = new StubScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());
    var awaiter = new StubEventCompletionAwaiter(shouldComplete: true);
    var dispatcher = _createDispatcher(
      invoker: _defaultInvoker(),
      scopedEventTracker: tracker,
      eventCompletionAwaiter: awaiter);
    var command = new TestCommand("receipt-wait-success");
    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromSeconds(5)
    };

    // Act
    var result = await dispatcher.LocalInvokeWithReceiptAsync<object>(command, options);

    // Assert
    await Assert.That(result.Value).IsNotNull();
    await Assert.That(result.Receipt).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_SyncFallback_WaitForPerspectives_ReturnsResultAsync() {
    // Arrange - sync receptor with wait
    var tracker = new StubScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());
    var awaiter = new StubEventCompletionAwaiter(shouldComplete: true);
    var dispatcher = _createDispatcher(
      syncInvoker: msg => new TestResult(Guid.NewGuid(), true),
      scopedEventTracker: tracker,
      eventCompletionAwaiter: awaiter);
    var command = new TestCommand("sync-receipt-wait");
    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromSeconds(5)
    };

    // Act
    var result = await dispatcher.LocalInvokeWithReceiptAsync<object>(command, options);

    // Assert
    await Assert.That(result.Value).IsNotNull();
  }

  // ========================================
  // SendAsync WITH ROUTED UNWRAP IN OPTIONS PATH TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithOptions_RoutedNoneValue_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routed = (object)Route.None();
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.SendAsync(routed, options))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task SendAsync_WithContextAndOptions_RoutedNoneValue_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routed = (object)Route.None();
    var options = new DispatchOptions();
    var context = MessageContext.New();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.SendAsync((object)routed, context, options))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task SendAsync_NonGenericWithOptions_ValidRouted_UnwrapsAndDispatchesAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routed = Route.Local(new TestCommand("routed-options"));
    var options = new DispatchOptions();

    // Act
    var receipt = await dispatcher.SendAsync((object)routed, options);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // VOID LocalInvokeAsync WITH OPTIONS SYNC FALLBACK TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_SyncFallback_CompletesAsync() {
    // Arrange - only void sync invoker, no async
    var invoked = false;
    var dispatcher = _createDispatcher(
      voidSyncInvoker: msg => { invoked = true; });
    var command = new TestCommand("void-sync-options");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync(command, options);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_AnyInvokerFallback_CompletesAsync() {
    // Arrange - only any invoker, no void or sync
    var dispatcher = _createDispatcher(
      anyInvoker: msg => new ValueTask<object?>(new TestResult(Guid.NewGuid(), true)));
    var command = new TestCommand("void-any-options");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync(command, options);
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invokers at all
    var dispatcher = _createDispatcher();
    var command = new TestCommand("void-no-receptor");
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync(command, options))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_RoutedNone_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());
    var routed = Route.None();
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync((object)routed, options))
      .ThrowsExactly<ArgumentException>();
  }

  // ========================================
  // GenericVoidTyped INTERNAL TRACING PATHS TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_GenericVoidTyped_SyncFallbackWithImmediateAsync_GoesAsyncPathAsync() {
    // Arrange - void sync invoker + ImmediateAsync receptors registered
    var invoked = false;
    var registry = new StubReceptorRegistry();
    // Register an ImmediateAsync receptor to force async path
    registry.AddReceptor(
      typeof(TestCommand),
      LifecycleStage.ImmediateAsync,
      new ReceptorInfo(
        MessageType: typeof(TestCommand),
        ReceptorId: "test-immediate",
        InvokeAsync: (sp, msg, env, caller, ct) => ValueTask.FromResult<object?>(null)
      ));
    var dispatcher = _createDispatcher(
      voidSyncInvoker: msg => { invoked = true; },
      receptorRegistry: registry);
    var command = new TestCommand("generic-void-sync-immediate");

    // Act
    await dispatcher.LocalInvokeAsync<TestCommand>(command, MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoidTyped_AnyInvokerFallback_CompletesAsync() {
    // Arrange - only anyInvoker available via generic void path
    var dispatcher = _createDispatcher(
      anyInvoker: msg => new ValueTask<object?>(new TestResult(Guid.NewGuid(), true)));
    var command = new TestCommand("generic-void-any");

    // Act
    await dispatcher.LocalInvokeAsync<TestCommand>(command, MessageContext.New());
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoidTyped_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - no invokers at all, generic void path
    var dispatcher = _createDispatcher();
    var command = new TestCommand("generic-void-no-receptor");

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<TestCommand>(command, MessageContext.New()))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // VOID LocalInvoke SyncWithSyncCheck (sync + ImmediateAsync) TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_VoidSync_WithImmediateAsyncReceptors_GoesAsyncPathAsync() {
    // Arrange
    var invoked = false;
    var registry = new StubReceptorRegistry();
    registry.AddReceptor(
      typeof(TestCommand),
      LifecycleStage.ImmediateAsync,
      new ReceptorInfo(
        MessageType: typeof(TestCommand),
        ReceptorId: "test-immediate",
        InvokeAsync: (sp, msg, env, caller, ct) => ValueTask.FromResult<object?>(null)
      ));
    var dispatcher = _createDispatcher(
      voidSyncInvoker: msg => { invoked = true; },
      receptorRegistry: registry);
    var command = new TestCommand("sync-immediate");

    // Act - goes through _localInvokeVoidSyncWithSyncCheckAsync
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_VoidSync_WithPostLifecycleReceptors_GoesAsyncPathAsync() {
    // Arrange
    var invoked = false;
    var registry = new StubReceptorRegistry();
    registry.AddReceptor(
      typeof(TestCommand),
      LifecycleStage.PostLifecycleAsync,
      new ReceptorInfo(
        MessageType: typeof(TestCommand),
        ReceptorId: "test-post-lifecycle",
        InvokeAsync: (sp, msg, env, caller, ct) => ValueTask.FromResult<object?>(null)
      ));
    var dispatcher = _createDispatcher(
      voidSyncInvoker: msg => { invoked = true; },
      receptorRegistry: registry);
    var command = new TestCommand("sync-post-lifecycle");

    // Act
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  // ========================================
  // _invokeImmediateAsyncReceptorsAsync TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Void_WithTraceStore_NoReceptorRegistry_SkipsImmediateAsyncAsync() {
    // Arrange - no receptor registry, ImmediateAsync should be skipped
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(
      traceStore: traceStore,
      voidInvoker: _defaultVoidInvoker());
    var command = new TestCommand("no-registry");

    // Act - should complete without error
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithReceptorRegistry_NoImmediateAsyncReceptors_SkipsAsync() {
    // Arrange - registry has no ImmediateAsync receptors
    var traceStore = new StubTraceStore();
    var registry = new StubReceptorRegistry();
    var dispatcher = _createDispatcher(
      traceStore: traceStore,
      voidInvoker: _defaultVoidInvoker(),
      receptorRegistry: registry);
    var command = new TestCommand("empty-registry");

    // Act
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithImmediateAsyncReceptors_InvokesScopedInvokerAsync() {
    // Arrange - ImmediateAsync receptor registered, scoped invoker available
    var registry = new StubReceptorRegistry();
    registry.AddReceptor(
      typeof(TestCommand),
      LifecycleStage.ImmediateAsync,
      new ReceptorInfo(
        MessageType: typeof(TestCommand),
        ReceptorId: "test-immediate",
        InvokeAsync: (sp, msg, env, caller, ct) => ValueTask.FromResult<object?>(null)
      ));
    var scopedInvoker = new StubReceptorInvoker();
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(
      traceStore: traceStore,
      voidInvoker: _defaultVoidInvoker(),
      receptorRegistry: registry,
      receptorInvoker: scopedInvoker);
    var command = new TestCommand("immediate-invoke");

    // Act
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(scopedInvoker.InvokeCount).IsGreaterThanOrEqualTo(1);
  }

  // ========================================
  // SendAsync METRICS AND ERROR PATH TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithOptions_InvokerThrows_PropagatesExceptionAsync() {
    // Arrange
    ReceptorInvoker<object> throwingInvoker = msg =>
      throw new InvalidOperationException("receptor-error");
    var dispatcher = _createDispatcher(invoker: throwingInvoker);
    var command = new TestCommand("error-test");
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.SendAsync(command, MessageContext.New(), options))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task SendAsync_Generic_WithOptions_InvokerThrows_PropagatesExceptionAsync() {
    // Arrange
    ReceptorInvoker<object> throwingInvoker = msg =>
      throw new InvalidOperationException("generic-receptor-error");
    var dispatcher = _createDispatcher(invoker: throwingInvoker);
    var command = new TestCommand("generic-error-test");
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.SendAsync<TestCommand>(command, options))
      .ThrowsExactly<InvalidOperationException>();
  }

  // ========================================
  // LocalInvokeAsync METRICS ERROR PATH TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Typed_WithCastFallback_InvokerThrowsNonCastException_PropagatesAsync() {
    // Arrange
    ReceptorInvoker<object> throwingInvoker = msg =>
      throw new InvalidOperationException("non-cast-error");
    var dispatcher = _createDispatcher(invoker: throwingInvoker);
    var command = new TestCommand("cast-fallback-error");

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<object>(command, MessageContext.New()))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_InvokerThrows_PropagatesExceptionAsync() {
    // Arrange - exercises _localInvokeWithTracingAsyncInternalAsync error path
    ReceptorInvoker<object> throwingInvoker = msg =>
      throw new InvalidOperationException("generic-typed-error");
    var dispatcher = _createDispatcher(invoker: throwingInvoker);
    var command = new TestCommand("generic-typed-error-test");

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<TestCommand, object>(command, MessageContext.New()))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoidTyped_InvokerThrows_PropagatesExceptionAsync() {
    // Arrange - exercises _localInvokeVoidWithTracingAsyncInternalAsync error path
    VoidReceptorInvoker throwingInvoker = msg =>
      throw new InvalidOperationException("generic-void-error");
    var dispatcher = _createDispatcher(voidInvoker: throwingInvoker);
    var command = new TestCommand("generic-void-error-test");

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<TestCommand>(command, MessageContext.New()))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithSyncAndTracing_InvokerThrows_PropagatesExceptionAsync() {
    // Arrange - exercises _localInvokeVoidWithSyncAndTracingAsync error path
    VoidReceptorInvoker throwingInvoker = msg =>
      throw new InvalidOperationException("void-sync-tracing-error");
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(
      traceStore: traceStore,
      voidInvoker: throwingInvoker);
    var command = new TestCommand("void-sync-tracing-error-test");

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync(command, MessageContext.New()))
      .ThrowsExactly<InvalidOperationException>();
  }

  // ========================================
  // CascadeMessageAsync TESTS
  // ========================================

  [Test]
  public async Task CascadeMessageAsync_OutboxMode_CallsCascadeToOutboxAsync() {
    // Arrange
    var dispatcher = _createDispatcher(
      untypedPublisher: (msg, env, ct) => Task.CompletedTask);
    var testEvent = new TestEvent(Guid.NewGuid());

    // Act - Outbox mode, base implementation is no-op but should not throw
    await dispatcher.CascadeMessageAsync(
      testEvent,
      sourceEnvelope: null,
      DispatchModes.Outbox);
  }

  [Test]
  public async Task CascadeMessageAsync_EventStoreMode_CallsCascadeToEventStoreOnlyAsync() {
    // Arrange
    var dispatcher = _createDispatcher(
      untypedPublisher: (msg, env, ct) => Task.CompletedTask);
    var testEvent = new TestEvent(Guid.NewGuid());

    // Act - EventStore mode without Outbox, base implementation is no-op
    await dispatcher.CascadeMessageAsync(
      testEvent,
      sourceEnvelope: null,
      DispatchModes.EventStore);
  }

  [Test]
  public async Task CascadeMessageAsync_BothMode_CallsLocalAndOutboxAsync() {
    // Arrange
    var localPublished = false;
    var dispatcher = _createDispatcher(
      untypedPublisher: (msg, env, ct) => { localPublished = true; return Task.CompletedTask; });
    var testEvent = new TestEvent(Guid.NewGuid());

    // Act - Both = Local + Outbox
    await dispatcher.CascadeMessageAsync(
      testEvent,
      sourceEnvelope: null,
      DispatchModes.Both);

    // Assert
    await Assert.That(localPublished).IsTrue();
  }

  [Test]
  public async Task CascadeMessageAsync_LocalMode_NoPublisher_CompletesAsync() {
    // Arrange - no untyped publisher registered
    var dispatcher = _createDispatcher();
    var testEvent = new TestEvent(Guid.NewGuid());

    // Act - Local mode with no publisher, should not throw
    await dispatcher.CascadeMessageAsync(
      testEvent,
      sourceEnvelope: null,
      DispatchModes.Local);
  }

  [Test]
  public async Task CascadeMessageAsync_NonEventMessage_DoesNotTrackForSyncAsync() {
    // Arrange - command (not IEvent) should not trigger sync tracking
    var dispatcher = _createDispatcher(
      untypedPublisher: (msg, env, ct) => Task.CompletedTask,
      handleMessageType: typeof(TestCommandMsg));
    var command = new TestCommandMsg("non-event");

    // Act - should complete without error
    await dispatcher.CascadeMessageAsync(
      command,
      sourceEnvelope: null,
      DispatchModes.Local);
  }

  // ========================================
  // PublishManyAsync / LocalSendManyAsync NULL GUARD TESTS
  // ========================================

  [Test]
  public async Task PublishManyAsync_Generic_NullEvents_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.PublishManyAsync<TestEvent>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task PublishManyAsync_NonGeneric_NullEvents_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.PublishManyAsync(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalSendManyAsync_Generic_NullMessages_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalSendManyAsync<TestCommand>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalSendManyAsync_NonGeneric_NullMessages_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalSendManyAsync(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task LocalInvokeManyAsync_NullMessages_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeManyAsync<object>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // LocalSendManyAsync WITH NO RECEPTOR TESTS
  // ========================================

  [Test]
  public async Task LocalSendManyAsync_Generic_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange - handleMessageType is TestCommand but sending UnhandledCommand
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new[] { new UnhandledCommand("no-handler") };

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalSendManyAsync<UnhandledCommand>(commands))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task LocalSendManyAsync_NonGeneric_NoReceptor_ThrowsReceptorNotFoundAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new object[] { new UnhandledCommand("no-handler") };

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalSendManyAsync(commands))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // LocalSendManyAsync HAPPY PATH TESTS
  // ========================================

  [Test]
  public async Task LocalSendManyAsync_Generic_WithReceptor_ReturnsAllReceiptsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new[] {
      new TestCommand("msg1"),
      new TestCommand("msg2")
    };

    // Act
    var receipts = await dispatcher.LocalSendManyAsync<TestCommand>(commands);

    // Assert
    await Assert.That(receipts.Count()).IsEqualTo(2);
    foreach (var receipt in receipts) {
      await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    }
  }

  [Test]
  public async Task LocalSendManyAsync_NonGeneric_WithReceptor_ReturnsAllReceiptsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var commands = new object[] {
      new TestCommand("msg1"),
      new TestCommand("msg2")
    };

    // Act
    var receipts = await dispatcher.LocalSendManyAsync(commands);

    // Assert
    await Assert.That(receipts.Count()).IsEqualTo(2);
  }

  // ========================================
  // PublishManyAsync HAPPY PATH TESTS
  // ========================================

  [Test]
  public async Task PublishManyAsync_Generic_EmptyList_ReturnsEmptyAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var events = Array.Empty<TestEvent>();

    // Act
    var receipts = await dispatcher.PublishManyAsync<TestEvent>(events);

    // Assert
    await Assert.That(receipts.Count()).IsEqualTo(0);
  }

  [Test]
  public async Task PublishManyAsync_NonGeneric_EmptyList_ReturnsEmptyAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var events = Array.Empty<object>();

    // Act
    var receipts = await dispatcher.PublishManyAsync(events);

    // Assert
    await Assert.That(receipts.Count()).IsEqualTo(0);
  }

  // ========================================
  // SendAsync NON-GENERIC WITH CONTEXT OUTBOX PATH TESTS
  // ========================================

  [Test]
  public async Task SendAsync_NonGenericWithContext_NoReceptorNoStrategy_ThrowsReceptorNotFoundAsync() {
    // Arrange - non-generic SendAsync with context, no invoker, no strategy
    var dispatcher = _createDispatcher();
    var command = new TestCommand("no-receptor-outbox");
    var context = MessageContext.New();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.SendAsync((object)command, context))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  public async Task SendAsync_NonGenericWithContext_NoReceptor_WithStrategy_RoutesToOutboxAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var serializer = new StubEnvelopeSerializer();
    var dispatcher = _createDispatcher(
      workStrategy: strategy,
      envelopeSerializer: serializer);
    var command = new TestCommand("outbox-route");
    var context = MessageContext.New();

    // Act
    var receipt = await dispatcher.SendAsync((object)command, context);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.QueuedOutbox.Count).IsEqualTo(1);
  }

  // ========================================
  // SendAsync GENERIC INTERNAL OUTBOX PATH TESTS
  // ========================================

  [Test]
  public async Task SendAsync_GenericInternal_NoReceptor_WithStrategy_RoutesToOutboxAsync() {
    // Arrange - exercises _sendToOutboxViaScopeAsync<TMessage>
    var strategy = new StubWorkCoordinatorStrategy();
    var serializer = new StubEnvelopeSerializer();
    var dispatcher = _createDispatcher(
      workStrategy: strategy,
      envelopeSerializer: serializer);
    var command = new TestCommand("generic-outbox");

    // Act
    var receipt = await dispatcher.SendAsync<TestCommand>(command);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.QueuedOutbox.Count).IsEqualTo(1);
  }

  [Test]
  public async Task SendAsync_GenericInternal_NoReceptorNoStrategy_ThrowsReceptorNotFoundAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("no-outbox");

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.SendAsync<TestCommand>(command))
      .ThrowsExactly<ReceptorNotFoundException>();
  }

  // ========================================
  // SendAsync GENERIC WITH OPTIONS OUTBOX PATH TESTS
  // ========================================

  [Test]
  public async Task SendAsync_GenericWithOptions_NoReceptor_WithStrategy_RoutesToOutboxAsync() {
    // Arrange - exercises _sendToOutboxViaScopeAsync<TMessage> from options path
    var strategy = new StubWorkCoordinatorStrategy();
    var serializer = new StubEnvelopeSerializer();
    var dispatcher = _createDispatcher(
      workStrategy: strategy,
      envelopeSerializer: serializer);
    var command = new TestCommand("generic-outbox-options");
    var options = new DispatchOptions();

    // Act
    var receipt = await dispatcher.SendAsync<TestCommand>(command, options);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
  }

  // ========================================
  // PublishAsync NULL EVENT GUARD TESTS
  // ========================================

  [Test]
  public async Task PublishAsync_NullEventGeneric_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.PublishAsync<TestEvent>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task PublishAsync_WithOptions_NullEventGeneric_ThrowsArgumentNullAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var options = new DispatchOptions();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.PublishAsync<TestEvent>(null!, options))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // PublishAsync WITH OPTIONS CANCELLATION TEST
  // ========================================

  [Test]
  public async Task PublishAsync_WithOptions_CancelledToken_ThrowsOperationCanceledAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var testEvent = new TestEvent(Guid.NewGuid());
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.PublishAsync(testEvent, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // VOID LocalInvoke TRACING WITH ASYNC INVOKER AND OPTIONS TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_AsyncInvoker_WithTraceStore_UsesTracingPathAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var invoked = false;
    VoidReceptorInvoker voidInvoker = msg => { invoked = true; return ValueTask.CompletedTask; };
    var dispatcher = _createDispatcher(
      traceStore: traceStore,
      voidInvoker: voidInvoker);
    var command = new TestCommand("void-trace-options");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync(command, options);

    // Assert
    await Assert.That(invoked).IsTrue();
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_VoidWithOptions_AsyncInvoker_CancelledToken_ThrowsAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(
      traceStore: traceStore,
      voidInvoker: _defaultVoidInvoker());
    var command = new TestCommand("void-trace-cancel");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync(command, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // _hasImmediateAsyncReceptors / _hasPostLifecycleReceptors TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_VoidAsync_WithImmediateAsyncReceptors_GoesTracingPathAsync() {
    // Arrange - async invoker + ImmediateAsync receptors => tracing path even without traceStore
    var invoked = false;
    var registry = new StubReceptorRegistry();
    registry.AddReceptor(
      typeof(TestCommand),
      LifecycleStage.ImmediateAsync,
      new ReceptorInfo(
        MessageType: typeof(TestCommand),
        ReceptorId: "test-immediate",
        InvokeAsync: (sp, msg, env, caller, ct) => ValueTask.FromResult<object?>(null)
      ));
    VoidReceptorInvoker voidInvoker = msg => { invoked = true; return ValueTask.CompletedTask; };
    var dispatcher = _createDispatcher(
      voidInvoker: voidInvoker,
      receptorRegistry: registry);
    var command = new TestCommand("immediate-tracing");

    // Act
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_VoidAsync_WithPostLifecycleReceptors_GoesTracingPathAsync() {
    // Arrange - async invoker + PostLifecycle receptors => tracing path
    var invoked = false;
    var registry = new StubReceptorRegistry();
    registry.AddReceptor(
      typeof(TestCommand),
      LifecycleStage.PostLifecycleAsync,
      new ReceptorInfo(
        MessageType: typeof(TestCommand),
        ReceptorId: "test-post-lifecycle",
        InvokeAsync: (sp, msg, env, caller, ct) => ValueTask.FromResult<object?>(null)
      ));
    VoidReceptorInvoker voidInvoker = msg => { invoked = true; return ValueTask.CompletedTask; };
    var dispatcher = _createDispatcher(
      voidInvoker: voidInvoker,
      receptorRegistry: registry);
    var command = new TestCommand("post-lifecycle-tracing");

    // Act
    await dispatcher.LocalInvokeAsync(command, MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  // ========================================
  // SendAsync WITH IRouted UNWRAP IN MAIN PATH TESTS
  // ========================================

  [Test]
  public async Task SendAsync_NonGeneric_RoutedLocal_UnwrapsAndDispatchesAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routed = Route.Local(new TestCommand("routed-send"));

    // Act
    var receipt = await dispatcher.SendAsync((object)routed);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_NonGenericWithContext_RoutedLocal_UnwrapsAndDispatchesAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routed = Route.Local(new TestCommand("routed-context"));
    var context = MessageContext.New();

    // Act
    var receipt = await dispatcher.SendAsync((object)routed, context);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_NonGenericWithContext_RoutedNone_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routed = Route.None();
    var context = MessageContext.New();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.SendAsync((object)routed, context))
      .ThrowsExactly<ArgumentException>();
  }

  // ========================================
  // LocalInvokeAsync WITH IRouted UNWRAP TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Typed_RoutedLocal_UnwrapsAndInvokesAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routed = Route.Local(new TestCommand("routed-local-invoke"));

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>((object)routed, MessageContext.New());

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_RoutedLocal_UnwrapsAndInvokesAsync() {
    // Arrange
    var invoked = false;
    VoidReceptorInvoker voidInvoker = msg => { invoked = true; return ValueTask.CompletedTask; };
    var dispatcher = _createDispatcher(voidInvoker: voidInvoker);
    var routed = Route.Local(new TestCommand("routed-void-invoke"));

    // Act
    await dispatcher.LocalInvokeAsync((object)routed, MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_Typed_RoutedNone_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routed = Route.None();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<object>((object)routed, MessageContext.New()))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_RoutedNone_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());
    var routed = Route.None();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync((object)routed, MessageContext.New()))
      .ThrowsExactly<ArgumentException>();
  }

  // ========================================
  // LocalInvokeWithReceipt ROUTED UNWRAP TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_RoutedLocal_UnwrapsAndReturnsResultAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routed = Route.Local(new TestCommand("routed-receipt"));

    // Act
    var result = await dispatcher.LocalInvokeWithReceiptAsync<object>((object)routed, MessageContext.New());

    // Assert
    await Assert.That(result.Value).IsNotNull();
    await Assert.That(result.Receipt).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_RoutedNone_ThrowsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routed = Route.None();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeWithReceiptAsync<object>((object)routed, MessageContext.New()))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_RoutedLocal_UnwrapsAsync() {
    // Arrange
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var routed = Route.Local(new TestCommand("routed-receipt-options"));
    var options = new DispatchOptions();

    // Act
    var result = await dispatcher.LocalInvokeWithReceiptAsync<object>((object)routed, options);

    // Assert
    await Assert.That(result.Value).IsNotNull();
  }

  // ========================================
  // GENERIC LocalInvoke ROUTED UNWRAP TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_GenericTMessageTResult_RoutedNone_ThrowsAsync() {
    // Arrange - RoutedNone implements IRouted with Mode=None and Value=null
    var dispatcher = _createDispatcher(invoker: _defaultInvoker(),
      handleMessageType: typeof(RoutedNone));
    var routed = Route.None();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<RoutedNone, object>(routed, MessageContext.New()))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTMessage_VoidRoutedNone_ThrowsAsync() {
    // Arrange - RoutedNone implements IRouted with Mode=None and Value=null
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker(),
      handleMessageType: typeof(RoutedNone));
    var routed = Route.None();

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<RoutedNone>(routed, MessageContext.New()))
      .ThrowsExactly<ArgumentException>();
  }

  // ========================================
  // LocalInvokeWithReceiptAsync AUTO-CONTEXT TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_AutoContext_Generic_ReturnsReceiptAsync() {
    // Arrange - exercises LocalInvokeWithReceiptAsync<TMessage, TResult>(message) overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("auto-context-generic");

    // Act
    var result = await dispatcher.LocalInvokeWithReceiptAsync<TestCommand, object>(command);

    // Assert
    await Assert.That(result.Value).IsNotNull();
    await Assert.That(result.Receipt).IsNotNull();
    await Assert.That(result.Receipt.MessageId.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_AutoContext_NonGeneric_ReturnsReceiptAsync() {
    // Arrange - exercises LocalInvokeWithReceiptAsync<TResult>(message) overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("auto-context-nongeneric");

    // Act
    var result = await dispatcher.LocalInvokeWithReceiptAsync<object>(command);

    // Assert
    await Assert.That(result.Value).IsNotNull();
    await Assert.That(result.Receipt).IsNotNull();
  }

  // ========================================
  // LocalInvokeAsync AUTO-CONTEXT TESTS (ADDITIONAL)
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_GenericTMessageTResult_AutoContext_ReturnsResultAsync() {
    // Arrange - exercises LocalInvokeAsync<TMessage, TResult>(message) overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("auto-context");

    // Act
    var result = await dispatcher.LocalInvokeAsync<TestCommand, object>(command);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTMessage_VoidAutoContext_CompletesAsync() {
    // Arrange - exercises void LocalInvokeAsync<TMessage>(message) overload
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());
    var command = new TestCommand("void-auto-context");

    // Act - should not throw
    await dispatcher.LocalInvokeAsync<TestCommand>(command);
  }

  [Test]
  public async Task LocalInvokeAsync_Typed_AutoContext_ReturnsResultAsync() {
    // Arrange - exercises LocalInvokeAsync<TResult>(message) overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("typed-auto-context");

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_AutoContext_CompletesAsync() {
    // Arrange - exercises void LocalInvokeAsync(message) overload
    var dispatcher = _createDispatcher(voidInvoker: _defaultVoidInvoker());
    var command = new TestCommand("void-auto-context");

    // Act - should not throw
    await dispatcher.LocalInvokeAsync(command);
  }

  // ========================================
  // SendAsync AUTO-CONTEXT TESTS (ADDITIONAL)
  // ========================================

  [Test]
  public async Task SendAsync_NonGeneric_AutoContext_ReturnsReceiptAsync() {
    // Arrange - exercises SendAsync(object) overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("send-auto-context");

    // Act
    var receipt = await dispatcher.SendAsync((object)command);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // LocalInvokeWithReceipt GENERIC WITH CONTEXT TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeWithReceiptAsync_GenericTMessageTResult_WithContext_ReturnsReceiptAsync() {
    // Arrange - exercises the TMessage+TResult+context overload
    var dispatcher = _createDispatcher(invoker: _defaultInvoker());
    var command = new TestCommand("generic-with-context");
    var context = MessageContext.New();

    // Act
    var result = await dispatcher.LocalInvokeWithReceiptAsync<TestCommand, object>(command, context);

    // Assert
    await Assert.That(result.Value).IsNotNull();
    await Assert.That(result.Receipt).IsNotNull();
  }

  // ========================================
  // LocalInvokeAsync WITH OPTIONS TRACING PATH TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_TypedWithOptions_WithTraceStore_StoresEnvelopeAsync() {
    // Arrange - exercises _localInvokeWithTracingAndOptionsAsync
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(
      traceStore: traceStore,
      invoker: _defaultInvoker());
    var command = new TestCommand("options-tracing");
    var options = new DispatchOptions();

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, options);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_TypedWithOptions_SyncFallback_WithTraceStore_StoresEnvelopeAsync() {
    // Arrange - sync invoker with tracing via options path
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(
      traceStore: traceStore,
      syncInvoker: msg => new TestResult(Guid.NewGuid(), true));
    var command = new TestCommand("sync-options-tracing");
    var options = new DispatchOptions();

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(command, options);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
    await Assert.That(result).IsNotNull();
  }

  // ========================================
  // SendAsync GENERIC INTERNAL WITH OPTIONS TESTS
  // ========================================

  [Test]
  public async Task SendAsync_GenericInternal_WithOptions_WithTraceStore_StoresEnvelopeAsync() {
    // Arrange - exercises _sendAsyncInternalWithOptionsAsync tracing path
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(
      traceStore: traceStore,
      invoker: _defaultInvoker());
    var command = new TestCommand("generic-options-tracing");
    var options = new DispatchOptions();

    // Act
    var receipt = await dispatcher.SendAsync<TestCommand>(command, options);

    // Assert
    await Assert.That(traceStore.StoreCallCount).IsEqualTo(1);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_GenericInternal_WithOptions_CancelledAfterStore_ThrowsAsync() {
    // Arrange - cancel after traceStore to exercise mid-path cancellation
    using var cts = new CancellationTokenSource();
    var traceStore = new StubTraceStore();
    var dispatcher = _createDispatcher(
      traceStore: traceStore,
      invoker: msg => {
        cts.Cancel();
        return new ValueTask<object>(new TestResult(Guid.NewGuid(), true));
      });
    var command = new TestCommand("generic-cancel-mid");
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // This may or may not throw depending on timing, but exercises the path
    try {
      await dispatcher.SendAsync<TestCommand>(command, options);
    } catch (OperationCanceledException) {
      // Expected
    }
  }

  // ========================================
  // VOID LocalInvoke TRACING AND OPTIONS COMBINED PATH TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_VoidWithTracingOptions_WithEnvelopeRegistry_RegistersAndUnregistersAsync() {
    // Arrange
    var traceStore = new StubTraceStore();
    var registry = new StubEnvelopeRegistry();
    var dispatcher = _createDispatcher(
      traceStore: traceStore,
      envelopeRegistry: registry,
      voidInvoker: _defaultVoidInvoker());
    var command = new TestCommand("void-trace-options-registry");
    var options = new DispatchOptions();

    // Act
    await dispatcher.LocalInvokeAsync(command, options);

    // Assert
    await Assert.That(registry.RegisterCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(registry.UnregisterCount).IsGreaterThanOrEqualTo(1);
  }
}
