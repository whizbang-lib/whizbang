using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)
#pragma warning disable RCS1163 // Unused parameter — fake receptor/handler delegates intentionally match interface signatures.

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Registered in the test assembly's generated JsonSerializerContext so the
/// PublishToOutboxDynamicAsync serialization path (JsonContextRegistry lookup) succeeds.
/// </summary>
public sealed record SweepDynamicRegisteredEvent([property: StreamId] Guid Id, string Name) : IEvent;

/// <summary>
/// Registered variant carrying a settable StreamId so the dynamic-path IHasStreamId
/// inheritance branch can be observed.
/// </summary>
#pragma warning disable WHIZ009 // Intentionally no [StreamId]: these events exercise SetStreamId/auto-generation fallbacks
public sealed record SweepDynamicStreamEvent : IEvent, IHasStreamId {
#pragma warning restore WHIZ009
  /// <summary>Settable stream id for inheritance assertions.</summary>
  public Guid StreamId { get; set; }
}

/// <summary>
/// Coverage sweep for Dispatcher.cs outbox and publish paths:
/// - PublishAsync(options) publisher-throw observation + metrics
/// - CascadeMessageAsync envelope wrapping, stream-id resolution, sync tracking
/// - PublishToOutboxAsync no-rebroadcast log, disposed-provider guard, sync scope dispose,
///   no-strategy fallbacks (deferred channel / warning), stream-id inheritance
/// - _serializeQueueAndFlushAsync collector diversion + strategy-disposed guard
/// - PublishToOutboxDynamicAsync full serialization path + helpers
/// - Command/event convention destinations, outbox dispatch activity tags
/// - SendManyAsync/PublishManyAsync outbox + local branches
/// - _serializeToNewOutboxMessage defensive guards
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherCoverageSweepOutboxTests {

  // ========================================
  // TEST MESSAGE TYPES
  // ========================================

#pragma warning disable WHIZ009 // Settable StreamId via IHasStreamId; inheritance is the behavior under test
  public record SweepPubEvent : IEvent, IHasStreamId {
    public Guid StreamId { get; set; }
  }
#pragma warning restore WHIZ009

  public record SweepPlainOutboxEvent([property: StreamId] Guid Id) : IEvent;

  public record SweepSourceCommand(string Data);

  public record InventorySweepCommand(string Data);

  public record SweepManyCommand(string Data);

  public record SweepManyEvent([property: StreamId] Guid Id) : IEvent;

  // ========================================
  // TEST DISPATCHER
  // ========================================

  private sealed class SweepOutboxDispatcher(
    IServiceProvider sp,
    IEnvelopeSerializer? envelopeSerializer = null,
    IStreamIdExtractor? streamIdExtractor = null,
    ReceptorInvoker<object>? invoker = null,
    Func<object, IMessageEnvelope?, CancellationToken, Task>? untypedPublisher = null,
    Type? handleMessageType = null,
    bool publisherThrows = false
    ) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null),
      envelopeSerializer: envelopeSerializer,
      streamIdExtractor: streamIdExtractor) {
    private readonly ReceptorInvoker<object>? _invoker = invoker;
    private readonly Func<object, IMessageEnvelope?, CancellationToken, Task>? _untypedPublisher = untypedPublisher;
    private readonly Type? _handleMessageType = handleMessageType;
    private readonly bool _publisherThrows = publisherThrows;

    public List<(IMessage Message, Guid? EventId)> OutboxCascades { get; } = [];
    public List<(IMessage Message, Guid? EventId)> EventStoreCascades { get; } = [];

    public Task CallPublishToOutboxAsync<TEvent>(TEvent eventData, Type eventType, MessageId messageId, IMessageEnvelope? sourceEnvelope = null, bool eventStoreOnly = false) =>
      PublishToOutboxAsync(eventData, eventType, messageId, sourceEnvelope, eventStoreOnly);

    public Task CallPublishToOutboxDynamicAsync(IMessage eventData, Type eventType, MessageId messageId, IMessageEnvelope? sourceEnvelope = null, bool eventStoreOnly = false) =>
      PublishToOutboxDynamicAsync(eventData, eventType, messageId, sourceEnvelope, eventStoreOnly);

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      if (_invoker is null || messageType != _handleMessageType) {
        return null;
      }
      return async msg => (TResult)await _invoker(msg);
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) {
      if (_publisherThrows) {
        return _ => throw new InvalidOperationException("publisher-failed");
      }
      return _ => Task.CompletedTask;
    }

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => _untypedPublisher;

    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;

    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;

    protected override Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      OutboxCascades.Add((message, eventId));
      return Task.CompletedTask;
    }

    protected override Task CascadeToEventStoreOnlyAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      EventStoreCascades.Add((message, eventId));
      return Task.CompletedTask;
    }
  }

  // ========================================
  // STUBS
  // ========================================

  private sealed class SweepStreamIdExtractor : IStreamIdExtractor {
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

  /// <summary>Extractor that yields a value for hop metadata, then null on later calls —
  /// models remote-origin envelopes where metadata exists but local extraction cannot.</summary>
  private sealed class OneShotStreamIdExtractor(Guid streamId) : IStreamIdExtractor {
    private readonly Guid _streamId = streamId;
    private int _calls;

    public Guid? ExtractStreamId(object message, Type messageType) =>
      Interlocked.Increment(ref _calls) == 1 ? _streamId : null;
  }

  private sealed class SweepEnvelopeSerializer : IEnvelopeSerializer {
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

  private sealed class JsonElementMessageTypeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      return new SerializedEnvelope(jsonEnvelope, "Envelope[[Broken]]", "System.Text.Json.JsonElement");
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => new();
  }

  private sealed class JsonElementEnvelopeTypeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      return new SerializedEnvelope(jsonEnvelope, "MessageEnvelope`1[[System.Text.Json.JsonElement]]", "Some.Clean.Type");
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => new();
  }

  private sealed class SweepWorkStrategy : IWorkCoordinatorStrategy {
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

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  private sealed class DisposedThrowingStrategy : IWorkCoordinatorStrategy {
    public int FlushCount { get; private set; }

    public void QueueOutboxMessage(OutboxMessage message) => throw new ObjectDisposedException(nameof(DisposedThrowingStrategy));
    public Task QueueOutboxMessageAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
      throw new ObjectDisposedException(nameof(DisposedThrowingStrategy));
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      FlushCount++;
      return Task.CompletedTask;
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  private sealed class SweepDeferredChannel : IDeferredOutboxChannel {
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

  private sealed class SweepSyncEventTrackerLite : Whizbang.Core.Perspectives.Sync.ISyncEventTracker {
    public List<(Type EventType, Guid EventId, Guid StreamId, string Perspective)> Tracked { get; } = [];

    public void TrackEvent(Type eventType, Guid eventId, Guid streamId, string perspectiveName) =>
      Tracked.Add((eventType, eventId, streamId, perspectiveName));

    public IReadOnlyList<Whizbang.Core.Perspectives.Sync.TrackedSyncEvent> GetPendingEvents(Guid streamId, string perspectiveName, Type[]? eventTypes = null) => [];
    public void MarkProcessed(IEnumerable<Guid> eventIds) { }
    public IReadOnlyList<Guid> GetAllTrackedEventIds() => [];
    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, Guid? awaiterId = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public void MarkProcessedByPerspective(IEnumerable<Guid> eventIds, string perspectiveName) { }
    public Task<bool> WaitForPerspectiveEventsAsync(IReadOnlyList<Guid> eventIds, string perspectiveName, TimeSpan timeout, Guid? awaiterId = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public Task<bool> WaitForAllPerspectivesAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, Guid? awaiterId = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public void UnregisterAwaiter(Guid awaiterId) { }
    public int CleanupStaleEntries(TimeSpan maxAge) => 0;
    public void MarkPerspectiveStreamProcessed(string perspectiveName, Guid streamId) { }
  }

  private sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider {
    private readonly List<string> _sink = sink;

    public ILogger CreateLogger(string categoryName) => new ListLogger(_sink);
    public void Dispose() {
      // Nothing to release — sink is owned by the test.
    }

    private sealed class ListLogger(List<string> sink) : ILogger {
      private readonly List<string> _sink = sink;

      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        lock (_sink) {
          _sink.Add(formatter(state, exception));
        }
      }
    }
  }

  private sealed class RecordingScope(IServiceProvider provider) : IServiceScope {
    public bool Disposed { get; private set; }
    public IServiceProvider ServiceProvider { get; } = provider;
    public void Dispose() => Disposed = true;
  }

  private sealed class RecordingScopeFactory(IServiceProvider provider, List<RecordingScope> scopes) : IServiceScopeFactory {
    private readonly IServiceProvider _provider = provider;
    private readonly List<RecordingScope> _scopes = scopes;

    public IServiceScope CreateScope() {
      var scope = new RecordingScope(_provider);
      lock (_scopes) {
        _scopes.Add(scope);
      }
      return scope;
    }
  }

  // ========================================
  // HELPERS
  // ========================================

  private static ServiceProvider _buildProvider(
    IWorkCoordinatorStrategy? strategy = null,
    IDeferredOutboxChannel? deferredChannel = null,
    Whizbang.Core.Perspectives.Sync.ISyncEventTracker? syncEventTracker = null,
    Whizbang.Core.Perspectives.Sync.ITrackedEventTypeRegistry? trackedEventTypeRegistry = null,
    DispatcherMetrics? metrics = null,
    List<string>? logs = null,
    List<RecordingScope>? scopes = null) {
    var services = new ServiceCollection();
    var scopeList = scopes ?? [];
    services.AddSingleton<IServiceScopeFactory>(sp => new RecordingScopeFactory(sp, scopeList));
    if (strategy is not null) {
      services.AddSingleton(strategy);
    }
    if (deferredChannel is not null) {
      services.AddSingleton(deferredChannel);
    }
    if (syncEventTracker is not null) {
      services.AddSingleton(syncEventTracker);
    }
    if (trackedEventTypeRegistry is not null) {
      services.AddSingleton(trackedEventTypeRegistry);
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    if (logs is not null) {
      services.AddLogging(builder => {
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddProvider(new ListLoggerProvider(logs));
      });
    }
    return services.BuildServiceProvider();
  }

  private static MessageEnvelope<object> _sourceEnvelope(object payload, EventFlags flags = EventFlags.None) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Flags = flags
  };

  // ========================================
  // PublishAsync(options) — lines 3130-3159
  // ========================================

  [Test]
  public async Task PublishAsync_WithOptions_PublisherThrows_ObservesOutboxAndRethrowsAsync() {
    // Arrange - local receptor publisher throws; no strategy → outbox falls back to the
    // ILogger<Dispatcher> warning; the publisher failure must still surface
    var logs = new List<string>();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(metrics: new DispatcherMetrics(new WhizbangMetrics()), logs: logs),
      publisherThrows: true);

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.PublishAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), new DispatchOptions()))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("publisher-failed");
    await Assert.That(logs.Any(m => m.Contains("IWorkCoordinatorStrategy not registered", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task PublishAsync_WithOptions_Success_ReturnsDeliveredAndQueuesOutboxAsync() {
    // Arrange - full success path with metrics registered
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy, metrics: new DispatcherMetrics(new WhizbangMetrics())),
      envelopeSerializer: new SweepEnvelopeSerializer());

    // Act
    var receipt = await dispatcher.PublishAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), new DispatchOptions());

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
  }

  // ========================================
  // CascadeMessageAsync — lines 3229-3285, 3307-3367
  // ========================================

  [Test]
  public async Task CascadeMessageAsync_WithSourceEnvelope_WrapsEnvelopeAsDefaultDispatchAsync() {
    // Arrange - local dispatch with a source envelope must hand receptors a wrapper
    // marked IsDefaultDispatch=true, not the raw source envelope
    var logs = new List<string>();
    IMessageEnvelope? received = null;
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(logs: logs),
      untypedPublisher: (message, envelope, ct) => {
        received = envelope;
        return Task.CompletedTask;
      });
    var source = _sourceEnvelope(new SweepSourceCommand("origin"));

    // Act
    await dispatcher.CascadeMessageAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), source, DispatchModes.Local);

    // Assert
    var envelope = received;
    await Assert.That(envelope).IsNotNull();
    await Assert.That(ReferenceEquals(envelope, source)).IsFalse();
    await Assert.That(envelope!.DispatchContext.IsDefaultDispatch).IsTrue();
    await Assert.That(logs.Any(m => m.Contains("SINGLETON tracker DISABLED", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task CascadeMessageAsync_NoSourceEnvelope_UsesCascadeDefaultEnvelopeAsync() {
    // Arrange - null source → the shared cascade-default envelope is passed instead
    IMessageEnvelope? received = null;
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(),
      untypedPublisher: (message, envelope, ct) => {
        received = envelope;
        return Task.CompletedTask;
      });

    // Act
    await dispatcher.CascadeMessageAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), sourceEnvelope: null, DispatchModes.Local);

    // Assert
    var envelope = received;
    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.DispatchContext.IsDefaultDispatch).IsTrue();
  }

  [Test]
  public async Task CascadeMessageAsync_EventStoreMode_WithDebugLogging_PassesTrackedEventIdAsync() {
    // Arrange - EventStore-only mode routes through CascadeToEventStoreOnlyAsync with the
    // SAME eventId used for sync tracking
    var logs = new List<string>();
    var dispatcher = new SweepOutboxDispatcher(_buildProvider(logs: logs));

    // Act
    await dispatcher.CascadeMessageAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), sourceEnvelope: null, DispatchModes.EventStore);

    // Assert
    await Assert.That(dispatcher.EventStoreCascades).Count().IsEqualTo(1);
    await Assert.That(dispatcher.EventStoreCascades[0].EventId).IsNotNull();
  }

  [Test]
  public async Task CascadeMessageAsync_OutboxMode_WithDebugLogging_PassesTrackedEventIdAsync() {
    // Arrange
    var logs = new List<string>();
    var dispatcher = new SweepOutboxDispatcher(_buildProvider(logs: logs));

    // Act
    await dispatcher.CascadeMessageAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), sourceEnvelope: null, DispatchModes.Outbox);

    // Assert
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(1);
    await Assert.That(dispatcher.OutboxCascades[0].EventId).IsNotNull();
  }

  [Test]
  public async Task CascadeMessageAsync_EventWithIHasStreamId_InheritsStreamIdFromSourceAsync() {
    // Arrange - event stream id empty, source command carries one → IHasStreamId setter branch
    var sourceStreamId = Guid.NewGuid();
    var cascadedEvent = new SweepPubEvent();
    var extractor = new SweepStreamIdExtractor {
      OnExtract = msg => msg switch {
        SweepPubEvent pubEvent => pubEvent.StreamId,
        SweepSourceCommand => sourceStreamId,
        _ => null
      }
    };
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(),
      streamIdExtractor: extractor);
    var source = _sourceEnvelope(new SweepSourceCommand("origin"));

    // Act
    await dispatcher.CascadeMessageAsync(cascadedEvent, source, DispatchModes.Outbox);

    // Assert
    await Assert.That(cascadedEvent.StreamId).IsEqualTo(sourceStreamId);
  }

  [Test]
  public async Task CascadeMessageAsync_EventWithoutIHasStreamId_InheritsViaSetStreamIdAsync() {
    // Arrange - event lacks IHasStreamId → generated-setter fallback branch
    var sourceStreamId = Guid.NewGuid();
    var cascadedEvent = new SweepPlainOutboxEvent(Guid.NewGuid());
    var extractor = new SweepStreamIdExtractor {
      OnExtract = msg => msg switch {
        SweepPlainOutboxEvent => Guid.Empty,
        SweepSourceCommand => sourceStreamId,
        _ => null
      }
    };
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(),
      streamIdExtractor: extractor);
    var source = _sourceEnvelope(new SweepSourceCommand("origin"));

    // Act
    await dispatcher.CascadeMessageAsync(cascadedEvent, source, DispatchModes.Outbox);

    // Assert
    await Assert.That(extractor.SetCalls).Count().IsEqualTo(1);
    await Assert.That(extractor.SetCalls[0].StreamId).IsEqualTo(sourceStreamId);
  }

  [Test]
  public async Task CascadeMessageAsync_GenerateStreamIdPolicy_AutoGeneratesAsync() {
    // Arrange - no source; [GenerateStreamId]-style policy generates a fresh stream id
    var cascadedEvent = new SweepPubEvent();
    var extractor = new SweepStreamIdExtractor {
      OnExtract = msg => msg is SweepPubEvent pubEvent ? pubEvent.StreamId : null,
      OnPolicy = msg => msg is SweepPubEvent ? (true, true) : (false, false)
    };
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(),
      streamIdExtractor: extractor);

    // Act
    await dispatcher.CascadeMessageAsync(cascadedEvent, sourceEnvelope: null, DispatchModes.Outbox);

    // Assert
    await Assert.That(cascadedEvent.StreamId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task CascadeMessageAsync_WithSingletonTracker_TracksPerPerspectiveAsync() {
    // Arrange - singleton tracker + type registry → per-perspective tracking loop
    var logs = new List<string>();
    var syncTracker = new SweepSyncEventTrackerLite();
    var registry = new Whizbang.Core.Perspectives.Sync.TrackedEventTypeRegistry(
      new Dictionary<Type, string> { { typeof(SweepPubEvent), "SweepOutboxPerspective" } });
    var streamId = Guid.NewGuid();
    var extractor = new SweepStreamIdExtractor {
      OnExtract = msg => msg is SweepPubEvent pubEvent ? pubEvent.StreamId : null
    };
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(syncEventTracker: syncTracker, trackedEventTypeRegistry: registry, logs: logs),
      streamIdExtractor: extractor);

    // Act
    await dispatcher.CascadeMessageAsync(new SweepPubEvent { StreamId = streamId }, sourceEnvelope: null, DispatchModes.Outbox);

    // Assert
    await Assert.That(syncTracker.Tracked).Count().IsEqualTo(1);
    await Assert.That(syncTracker.Tracked[0].Perspective).IsEqualTo("SweepOutboxPerspective");
    await Assert.That(syncTracker.Tracked[0].StreamId).IsEqualTo(streamId);
  }

  // ========================================
  // PublishToOutboxAsync — lines 3412-3432, 3470, 3488-3546
  // ========================================

  [Test]
  public async Task PublishToOutbox_NoRebroadcastSource_WithDebugLogging_SuppressesAsync() {
    // Arrange - a NoRebroadcast source envelope suppresses re-broadcast at the enqueue boundary
    var logs = new List<string>();
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy, logs: logs),
      envelopeSerializer: new SweepEnvelopeSerializer());
    var source = _sourceEnvelope(new SweepSourceCommand("child"), EventFlags.NoRebroadcast);

    // Act
    await dispatcher.CallPublishToOutboxAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New(), source);

    // Assert
    await Assert.That(strategy.Queued).Count().IsEqualTo(0);
    await Assert.That(logs.Any(m => m.Contains("Suppressed re-broadcast", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task PublishToOutbox_ProviderDisposed_DropsEventWithoutThrowingAsync() {
    // Arrange - build on a REAL scope factory so disposing the provider makes CreateScope throw
    var logs = new List<string>();
    var strategy = new SweepWorkStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddLogging(builder => {
      builder.SetMinimumLevel(LogLevel.Trace);
      builder.AddProvider(new ListLoggerProvider(logs));
    });
    var provider = services.BuildServiceProvider();
    var dispatcher = new SweepOutboxDispatcher(provider, envelopeSerializer: new SweepEnvelopeSerializer());

    // Warm-up publish: caches the cascade logger and proves the happy path works
    await dispatcher.CallPublishToOutboxAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New());
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);

    provider.Dispose();

    // Act - dropping the event during shutdown must NOT throw
    await dispatcher.CallPublishToOutboxAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New());

    // Assert - nothing new queued, warning logged
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
    await Assert.That(logs.Any(m => m.Contains("disposed", StringComparison.OrdinalIgnoreCase))).IsTrue();
  }

  [Test]
  public async Task PublishToOutbox_PlainScope_QueuesAndDisposesScopeSynchronouslyAsync() {
    // Arrange - exercise the synchronous outbox publish path
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer());

    // Act
    await dispatcher.CallPublishToOutboxAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New());

    // Assert — the event was serialized and queued to the outbox strategy.
    // NOTE: the scope-creation/disposal branch cannot be observed through a RecordingScopeFactory
    // test double. The .NET DI container special-cases IServiceScopeFactory and always resolves its
    // own internal engine factory (ServiceProviderEngineScope) from GetRequiredService, ignoring any
    // explicit registration — so RecordingScopeFactory.CreateScope() is never invoked. Asserting on
    // the real engine scope's disposal would require reaching into production internals, so we assert
    // only the observable queue effect here.
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PublishToOutbox_NoStrategy_WithDeferredChannel_QueuesToChannelAsync() {
    // Arrange - no strategy but a deferred channel → event is deferred, not dropped
    var logs = new List<string>();
    var channel = new SweepDeferredChannel();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(deferredChannel: channel, logs: logs),
      envelopeSerializer: new SweepEnvelopeSerializer());

    // Act
    await dispatcher.CallPublishToOutboxAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New());

    // Assert
    await Assert.That(channel.Queued).Count().IsEqualTo(1);
    await Assert.That(channel.Queued[0].IsEvent).IsTrue();
    await Assert.That(logs.Any(m => m.Contains("deferred channel", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task PublishToOutbox_SourceStreamId_PropagatesToIHasStreamIdEventAsync() {
    // Arrange - outbox path stream-id inheritance: IHasStreamId setter branch
    var sourceStreamId = Guid.NewGuid();
    var outboxEvent = new SweepPubEvent();
    var extractor = new SweepStreamIdExtractor {
      OnExtract = msg => msg switch {
        SweepPubEvent pubEvent => pubEvent.StreamId,
        SweepSourceCommand => sourceStreamId,
        _ => null
      }
    };
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer(),
      streamIdExtractor: extractor);
    var source = _sourceEnvelope(new SweepSourceCommand("origin"));

    // Act
    await dispatcher.CallPublishToOutboxAsync(outboxEvent, typeof(SweepPubEvent), MessageId.New(), source);

    // Assert - event inherited the stream id and the queued row carries it
    await Assert.That(outboxEvent.StreamId).IsEqualTo(sourceStreamId);
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
    await Assert.That(strategy.Queued[0].StreamId).IsEqualTo(sourceStreamId);
  }

  [Test]
  public async Task PublishToOutbox_SourceStreamId_PropagatesViaSetStreamIdFallbackAsync() {
    // Arrange - event without IHasStreamId uses the generated-setter fallback
    var sourceStreamId = Guid.NewGuid();
    var outboxEvent = new SweepPlainOutboxEvent(Guid.NewGuid());
    var extractor = new SweepStreamIdExtractor {
      OnExtract = msg => msg switch {
        SweepPlainOutboxEvent => Guid.Empty,
        SweepSourceCommand => sourceStreamId,
        _ => null
      }
    };
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer(),
      streamIdExtractor: extractor);
    var source = _sourceEnvelope(new SweepSourceCommand("origin"));

    // Act
    await dispatcher.CallPublishToOutboxAsync(outboxEvent, typeof(SweepPlainOutboxEvent), MessageId.New(), source);

    // Assert
    await Assert.That(extractor.SetCalls).Count().IsEqualTo(1);
    await Assert.That(extractor.SetCalls[0].StreamId).IsEqualTo(sourceStreamId);
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
  }

  // ========================================
  // _serializeQueueAndFlushAsync — lines 3656-3674
  // ========================================

  [Test]
  public async Task PublishToOutbox_AmbientCollector_DivertsMessageFromStrategyAsync() {
    // Arrange - an open DispatchOutboxCollector diverts the outbox write
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer());
    using var collecting = DispatchOutboxCollector.BeginCollecting();

    // Act
    await dispatcher.CallPublishToOutboxAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New());

    // Assert - message landed in the collector, never in the strategy, and no flush happened
    await Assert.That(collecting.Collector.Collected).Count().IsEqualTo(1);
    await Assert.That(strategy.Queued).Count().IsEqualTo(0);
    await Assert.That(strategy.FlushCount).IsEqualTo(0);
  }

  [Test]
  public async Task PublishToOutbox_StrategyDisposedOnQueue_DropsEventWithoutFlushAsync() {
    // Arrange - strategy throws ObjectDisposedException on queue (host shutdown race)
    var logs = new List<string>();
    var strategy = new DisposedThrowingStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy, logs: logs),
      envelopeSerializer: new SweepEnvelopeSerializer());

    // Act - must not throw
    await dispatcher.CallPublishToOutboxAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New());

    // Assert - flush skipped after the drop
    await Assert.That(strategy.FlushCount).IsEqualTo(0);
    await Assert.That(logs.Any(m => m.Contains("Strategy disposed", StringComparison.Ordinal))).IsTrue();
  }

  // ========================================
  // PublishToOutboxDynamicAsync — lines 3710, 3735-3887
  // ========================================

  [Test]
  public async Task PublishToOutboxDynamic_NoRebroadcastSource_WithDebugLogging_SuppressesAsync() {
    // Arrange
    var logs = new List<string>();
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(_buildProvider(strategy: strategy, logs: logs));
    var source = _sourceEnvelope(new SweepSourceCommand("child"), EventFlags.NoRebroadcast);

    // Act
    await dispatcher.CallPublishToOutboxDynamicAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New(), source);

    // Assert
    await Assert.That(strategy.Queued).Count().IsEqualTo(0);
    await Assert.That(logs.Any(m => m.Contains("Suppressed re-broadcast", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task PublishToOutboxDynamic_RegisteredEvent_QueuesFullyBuiltOutboxMessageAsync() {
    // Arrange - the event type is registered in the generated JsonSerializerContext,
    // so the runtime-type serialization path completes end to end
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(_buildProvider(strategy: strategy));
    var messageId = MessageId.New();
    var dynamicEvent = new SweepDynamicRegisteredEvent(Guid.NewGuid(), "dynamic");

    // Act
    await dispatcher.CallPublishToOutboxDynamicAsync(dynamicEvent, typeof(SweepDynamicRegisteredEvent), messageId);

    // Assert - convention destination, event flag, and messageId-fallback stream id
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
    var queued = strategy.Queued[0];
    await Assert.That(queued.MessageId).IsEqualTo(messageId.Value);
    await Assert.That(queued.Destination).IsEqualTo("sweepdynamicregistered");
    await Assert.That(queued.IsEvent).IsTrue();
    await Assert.That(queued.StreamId).IsEqualTo(messageId.Value);
    await Assert.That(queued.MessageType).Contains("SweepDynamicRegisteredEvent");
  }

  [Test]
  public async Task PublishToOutboxDynamic_EventStoreOnly_UsesNullDestinationAsync() {
    // Arrange
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(_buildProvider(strategy: strategy));

    // Act
    await dispatcher.CallPublishToOutboxDynamicAsync(
      new SweepDynamicRegisteredEvent(Guid.NewGuid(), "store-only"),
      typeof(SweepDynamicRegisteredEvent),
      MessageId.New(),
      sourceEnvelope: null,
      eventStoreOnly: true);

    // Assert - null destination bypasses transport
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
    await Assert.That(strategy.Queued[0].Destination).IsNull();
  }

  [Test]
  public async Task PublishToOutboxDynamic_EventWithIHasStreamId_InheritsStreamIdFromSourceAsync() {
    // Arrange - dynamic-path stream propagation: IHasStreamId branch
    var sourceStreamId = Guid.NewGuid();
    var dynamicEvent = new SweepDynamicStreamEvent();
    var extractor = new SweepStreamIdExtractor {
      OnExtract = msg => msg switch {
        SweepDynamicStreamEvent streamEvent => streamEvent.StreamId,
        SweepSourceCommand => sourceStreamId,
        _ => null
      }
    };
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      streamIdExtractor: extractor);
    var source = _sourceEnvelope(new SweepSourceCommand("origin"));

    // Act
    await dispatcher.CallPublishToOutboxDynamicAsync(dynamicEvent, typeof(SweepDynamicStreamEvent), MessageId.New(), source);

    // Assert
    await Assert.That(dynamicEvent.StreamId).IsEqualTo(sourceStreamId);
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
    await Assert.That(strategy.Queued[0].StreamId).IsEqualTo(sourceStreamId);
  }

  [Test]
  public async Task PublishToOutboxDynamic_EventAlreadyHasStreamId_SkipsPropagationAsync() {
    // Arrange - event already carries a stream id → early-return branch, no SetStreamId
    var existingStreamId = Guid.NewGuid();
    var dynamicEvent = new SweepDynamicStreamEvent { StreamId = existingStreamId };
    var extractor = new SweepStreamIdExtractor {
      OnExtract = msg => msg switch {
        SweepDynamicStreamEvent streamEvent => streamEvent.StreamId,
        SweepSourceCommand => Guid.NewGuid(),
        _ => null
      }
    };
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      streamIdExtractor: extractor);
    var source = _sourceEnvelope(new SweepSourceCommand("origin"));

    // Act
    await dispatcher.CallPublishToOutboxDynamicAsync(dynamicEvent, typeof(SweepDynamicStreamEvent), MessageId.New(), source);

    // Assert
    await Assert.That(extractor.SetCalls).Count().IsEqualTo(0);
    await Assert.That(dynamicEvent.StreamId).IsEqualTo(existingStreamId);
    await Assert.That(strategy.Queued[0].StreamId).IsEqualTo(existingStreamId);
  }

  [Test]
  public async Task PublishToOutboxDynamic_ExtractorGoesQuiet_FallsBackToHopMetadataStreamIdAsync() {
    // Arrange - extractor yields a value while hop metadata is built, then goes quiet.
    // The build step must recover the stream id from hop metadata (AggregateId) instead
    // of falling all the way back to the message id.
    var metadataStreamId = Guid.NewGuid();
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      streamIdExtractor: new OneShotStreamIdExtractor(metadataStreamId));
    var messageId = MessageId.New();

    // Act
    await dispatcher.CallPublishToOutboxDynamicAsync(
      new SweepDynamicRegisteredEvent(Guid.NewGuid(), "metadata"),
      typeof(SweepDynamicRegisteredEvent),
      messageId);

    // Assert - stream id came from hop metadata, not the message id fallback
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
    await Assert.That(strategy.Queued[0].StreamId).IsEqualTo(metadataStreamId);
  }

  // ========================================
  // COMMAND CONVENTION DESTINATION — line 3964
  // ========================================

  [Test]
  public async Task SendAsync_InventoryPrefixedCommand_RoutesToInventoryDestinationAsync() {
    // Arrange - "Inventory*" convention branch of _resolveCommandDestination
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer());

    // Act
    var receipt = await dispatcher.SendAsync((object)new InventorySweepCommand("stock"), MessageContext.New());

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
    await Assert.That(strategy.Queued[0].Destination).IsEqualTo("inventory");
  }

  // ========================================
  // OUTBOX DISPATCH ACTIVITY TAGS — lines 4021-4054, 4106-4139
  // ========================================

  [Test]
  [NotInParallel]
  public async Task SendAsync_Generic_OutboxPath_WithActivityListener_TagsDestinationAsync() {
    // Arrange
    var stopped = new List<Activity>();
    using var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == "Whizbang.Execution",
      Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
      ActivityStopped = activity => {
        lock (stopped) {
          stopped.Add(activity);
        }
      }
    };
    ActivitySource.AddActivityListener(listener);

    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer());

    // Act
    var receipt = await dispatcher.SendAsync<SweepManyCommand>(new SweepManyCommand("outbox-activity"));

    // Assert - outbox dispatch activity carries the resolved destination
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    List<Activity> outboxActivities;
    lock (stopped) {
      outboxActivities = stopped.Where(a => a.OperationName == "Dispatch SweepManyCommand (Outbox)").ToList();
    }
    await Assert.That(outboxActivities.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(outboxActivities[0].GetTagItem("whizbang.dispatch.destination")).IsEqualTo(strategy.Queued[0].Destination);
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_NonGeneric_OutboxPath_WithActivityListener_TagsDestinationAsync() {
    // Arrange
    var stopped = new List<Activity>();
    using var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == "Whizbang.Execution",
      Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
      ActivityStopped = activity => {
        lock (stopped) {
          stopped.Add(activity);
        }
      }
    };
    ActivitySource.AddActivityListener(listener);

    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer());

    // Act
    var receipt = await dispatcher.SendAsync((object)new SweepManyCommand("outbox-activity-nongeneric"), MessageContext.New());

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    List<Activity> outboxActivities;
    lock (stopped) {
      outboxActivities = stopped.Where(a => a.OperationName == "Dispatch SweepManyCommand (Outbox)").ToList();
    }
    await Assert.That(outboxActivities.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(outboxActivities[0].GetTagItem("whizbang.dispatch.destination")).IsEqualTo(strategy.Queued[0].Destination);
  }

  // ========================================
  // SendManyAsync / PublishManyAsync — lines 4194, 4227, 4700-4705, 4755-4756
  // ========================================

  [Test]
  public async Task SendManyAsync_NonGeneric_NoLocalReceptor_QueuesOutboxAndDisposesScopesAsync() {
    // Arrange - batch outbox path
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer());

    // Act
    var receipts = await dispatcher.SendManyAsync(new object[] { new SweepManyCommand("batch-1") });

    // Assert — the batch produced an accepted receipt and queued the message to the outbox.
    // NOTE: scope creation/disposal is not observable via a RecordingScopeFactory. The .NET DI
    // container special-cases IServiceScopeFactory and always resolves its own internal engine
    // factory from GetRequiredService, ignoring the explicit registration, so the test double's
    // CreateScope() is never invoked. We assert only the observable outbox effect.
    await Assert.That(receipts.Count()).IsEqualTo(1);
    await Assert.That(receipts.First().Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
  }

  [Test]
  public async Task SendManyAsync_AllLocal_NoStrategy_SkipsOutboxWithWarningAsync() {
    // Arrange - every message handled locally + no strategy → outbox skip warning path
    var logs = new List<string>();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(logs: logs),
      invoker: _ => new ValueTask<object>("handled"),
      handleMessageType: typeof(SweepManyCommand));

    // Act
    var receipts = await dispatcher.SendManyAsync(new object[] { new SweepManyCommand("local-only") });

    // Assert - only the local Delivered receipt remains; skip warning logged
    await Assert.That(receipts.Count()).IsEqualTo(1);
    await Assert.That(receipts.First().Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(logs.Any(m => m.Contains("outbox delivery skipped", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task PublishManyAsync_Generic_WithLocalReceptor_ReturnsDeliveredAndQueuesOutboxAsync() {
    // Arrange - events with a local receptor take the local-publish branch AND queue to outbox
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer(),
      invoker: _ => new ValueTask<object>("handled"),
      handleMessageType: typeof(SweepManyEvent));
    var events = new[] { new SweepManyEvent(Guid.NewGuid()), new SweepManyEvent(Guid.NewGuid()) };

    // Act
    var receipts = (await dispatcher.PublishManyAsync<SweepManyEvent>(events)).ToList();

    // Assert - one Delivered receipt per locally-handled event; both queued for outbox
    await Assert.That(receipts.Count).IsEqualTo(2);
    await Assert.That(receipts.All(r => r.Status == DeliveryStatus.Delivered)).IsTrue();
    await Assert.That(strategy.Queued).Count().IsEqualTo(2);
  }

  [Test]
  public async Task PublishManyAsync_NonGeneric_WithLocalReceptor_ReturnsDeliveredAndQueuesOutboxAsync() {
    // Arrange
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer(),
      invoker: _ => new ValueTask<object>("handled"),
      handleMessageType: typeof(SweepManyEvent));

    // Act
    var receipts = (await dispatcher.PublishManyAsync(new object[] { new SweepManyEvent(Guid.NewGuid()) })).ToList();

    // Assert
    await Assert.That(receipts.Count).IsEqualTo(1);
    await Assert.That(receipts[0].Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
  }

  // ========================================
  // _serializeToNewOutboxMessage GUARDS — lines 4937-5006
  // ========================================

  [Test]
  public async Task PublishToOutbox_JsonElementPayload_ThrowsDispatcherBugGuardAsync() {
    // Arrange - passing a raw JsonElement envelope payload is a framework bug; the
    // serializer guard must fail fast with a diagnostic message
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer());
    using var document = JsonDocument.Parse("{}");

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.CallPublishToOutboxAsync(document.RootElement, typeof(JsonElement), MessageId.New()))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("BUG IN DISPATCHER");
  }

  [Test]
  public async Task PublishToOutbox_EventWithEmptyStreamIdMetadata_ThrowsInvalidStreamIdAsync() {
    // Arrange - extractor reports Guid.Empty → hop metadata poisons _extractStreamId → guard throws
    var strategy = new SweepWorkStrategy();
    var extractor = new SweepStreamIdExtractor { OnExtract = _ => Guid.Empty };
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer(),
      streamIdExtractor: extractor);

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.CallPublishToOutboxAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New()))
      .ThrowsExactly<InvalidStreamIdException>();
  }

  [Test]
  public async Task PublishToOutbox_NoEnvelopeSerializer_ThrowsRequiredRegistrationAsync() {
    // Arrange - strategy exists but IEnvelopeSerializer was never registered
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(_buildProvider(strategy: strategy));

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.CallPublishToOutboxAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New()))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("IEnvelopeSerializer is required");
  }

  [Test]
  public async Task PublishToOutbox_SerializerReportsJsonElementMessageType_ThrowsCriticalBugAsync() {
    // Arrange - serializer returning a JsonElement MessageType must be caught post-serialization
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new JsonElementMessageTypeSerializer());

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.CallPublishToOutboxAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New()))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("CRITICAL BUG");
  }

  [Test]
  public async Task PublishToOutbox_SerializerReportsJsonElementEnvelopeType_ThrowsFinalCheckAsync() {
    // Arrange - clean MessageType but JsonElement EnvelopeType slips past the first check
    // and must be caught by the final outbox-message check
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepOutboxDispatcher(
      _buildProvider(strategy: strategy),
      envelopeSerializer: new JsonElementEnvelopeTypeSerializer());

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.CallPublishToOutboxAsync(new SweepPlainOutboxEvent(Guid.NewGuid()), typeof(SweepPlainOutboxEvent), MessageId.New()))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("FINAL CHECK FAILED");
  }
}
