using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
#pragma warning disable RCS1163 // Unused parameter — fake receptor/handler delegates intentionally match interface signatures.

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Coverage sweep for Dispatcher.cs routing, Routed&lt;T&gt; unwrap corners, and cascade tracking:
/// - _isOwnedNamespace child-prefix match and non-match fallthrough
/// - SendAsync owned-domain Accepted short-circuit (no outbox)
/// - Routed&lt;T&gt; result unwrapping in typed/generic/options/receipt LocalInvoke paths
/// - Routed&lt;T&gt; message unwrapping in the generic typed internal path
/// - Activity tag block in the generic tracing path (parent id tags)
/// - Error metrics recording in the void sync+tracing path
/// - Null-result cascade skip, no-messages-extracted warning
/// - _generateEventIdAndTrack / _autoGenerateStreamIdIfNeeded / scoped+singleton tracker branches
/// - _cascadeStreamIdFromSourceIfNeeded SetStreamId fallback
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherCoverageSweepRoutedCascadeTests {

  // ========================================
  // TEST MESSAGE TYPES
  // ========================================

  public record SweepRoutedCommand(string Data);

#pragma warning disable WHIZ009 // Intentionally no [StreamId]: these events exercise SetStreamId/auto-generation fallbacks
  public record SweepRoutedEvent(Guid Id) : IEvent;
#pragma warning restore WHIZ009

  public record SweepOwnedCommand(string Data);

#pragma warning disable WHIZ009 // Settable StreamId via IHasStreamId; inheritance is the behavior under test
  public record SweepGenStreamEvent : IEvent, IHasStreamId {
    public Guid StreamId { get; set; }
  }
#pragma warning restore WHIZ009

#pragma warning disable WHIZ009 // Intentionally no [StreamId]: these events exercise SetStreamId/auto-generation fallbacks
  public record SweepNoStreamPropEvent(Guid Id) : IEvent;
#pragma warning restore WHIZ009

  public record SweepCascadedOwnedCommand(string Data) : ICommand;

  private static readonly string[] _parentOwnedDomains = ["Whizbang.Core.Tests"];
  private static readonly string[] _unrelatedOwnedDomains = ["Unrelated.Domain"];

  // ========================================
  // TEST DISPATCHER
  // ========================================

  private sealed class SweepRoutedDispatcher(
    IServiceProvider sp,
    ITraceStore? traceStore = null,
    IEnvelopeSerializer? envelopeSerializer = null,
    IStreamIdExtractor? streamIdExtractor = null,
    IScopedEventTracker? scopedEventTracker = null,
    ReceptorInvoker<object>? invoker = null,
    VoidReceptorInvoker? voidInvoker = null,
    Type? handleMessageType = null
    ) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null),
      traceStore: traceStore,
      envelopeSerializer: envelopeSerializer,
      streamIdExtractor: streamIdExtractor,
      scopedEventTracker: scopedEventTracker) {
    private readonly ReceptorInvoker<object>? _invoker = invoker;
    private readonly VoidReceptorInvoker? _voidInvoker = voidInvoker;
    private readonly Type _handleMessageType = handleMessageType ?? typeof(SweepRoutedCommand);

    public List<IMessage> OutboxCascades { get; } = [];

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      if (_invoker is null || messageType != _handleMessageType) {
        return null;
      }
      return async msg => (TResult)await _invoker(msg);
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) =>
      _voidInvoker is not null && messageType == _handleMessageType ? _voidInvoker : null;

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) =>
      _ => Task.CompletedTask;

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;

    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;

    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;

    protected override Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      OutboxCascades.Add(message);
      return Task.CompletedTask;
    }
  }

  // ========================================
  // STUBS
  // ========================================

  private sealed class SweepStreamIdExtractor : IStreamIdExtractor {
    public Func<object, Guid?>? OnExtract { get; init; }
    public Func<object, (bool ShouldGenerate, bool OnlyIfEmpty)>? OnPolicy { get; init; }
    public List<(object Message, Guid StreamId)> SetCalls { get; } = [];

    public Guid? ExtractStreamId(object message, Type messageType) => OnExtract?.Invoke(message);

    public (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) =>
      OnPolicy?.Invoke(message) ?? (false, false);

    public bool SetStreamId(object message, Guid streamId) {
      SetCalls.Add((message, streamId));
      return true;
    }
  }

  private sealed class SweepScopedEventTracker : IScopedEventTracker {
    private readonly List<TrackedEvent> _events = [];

    public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) =>
      _events.Add(new TrackedEvent(streamId, eventType, eventId));

    public IReadOnlyList<TrackedEvent> GetEmittedEvents() => _events;
    public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) => _events;
    public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) =>
      _events.All(e => processedEventIds.Contains(e.EventId));
  }

  private sealed class SweepSyncEventTracker : ISyncEventTracker {
    public List<(Type EventType, Guid EventId, Guid StreamId, string Perspective)> Tracked { get; } = [];

    public void TrackEvent(Type eventType, Guid eventId, Guid streamId, string perspectiveName) =>
      Tracked.Add((eventType, eventId, streamId, perspectiveName));

    public IReadOnlyList<TrackedSyncEvent> GetPendingEvents(Guid streamId, string perspectiveName, Type[]? eventTypes = null) => [];
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

  private sealed class SweepWorkStrategy : IWorkCoordinatorStrategy {
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

  private sealed class SweepTraceStore : ITraceStore {
    public int StoreCallCount { get; private set; }

    public Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default) {
      StoreCallCount++;
      return Task.CompletedTask;
    }

    public Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default) =>
      Task.FromResult<IMessageEnvelope?>(null);
    public Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default) =>
      Task.FromResult(new List<IMessageEnvelope>());
    public Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default) =>
      Task.FromResult(new List<IMessageEnvelope>());
    public Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset toTime, CancellationToken ct = default) =>
      Task.FromResult(new List<IMessageEnvelope>());
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

  private sealed class TestScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    private readonly IServiceProvider _provider = provider;

    public IServiceScope CreateScope() => new TestScope(_provider);

    private sealed class TestScope(IServiceProvider provider) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = provider;
      public void Dispose() {
        // Root-provider-backed scope — nothing to release.
      }
    }
  }

  // ========================================
  // HELPERS
  // ========================================

  private static ServiceProvider _buildProvider(
    string[]? ownedDomains = null,
    IWorkCoordinatorStrategy? strategy = null,
    ISyncEventTracker? syncEventTracker = null,
    ITrackedEventTypeRegistry? trackedEventTypeRegistry = null,
    DispatcherMetrics? metrics = null,
    List<string>? logs = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new TestScopeFactory(sp));
    if (ownedDomains is not null) {
      var routingOptions = new RoutingOptions();
      routingOptions.OwnDomains(ownedDomains);
      services.AddSingleton<IOptions<RoutingOptions>>(Options.Create(routingOptions));
    }
    if (strategy is not null) {
      services.AddSingleton(strategy);
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

  // ========================================
  // _isOwnedNamespace — lines 246, 251, 486
  // ========================================

  [Test]
  public async Task SendAsync_OwnedChildNamespaceCommand_NoReceptor_ReturnsAcceptedWithoutOutboxAsync() {
    // Arrange - owned domain "Whizbang.Core.Tests" is a PARENT of this test namespace,
    // so the child-prefix matching branch fires (not the exact-match branch)
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(ownedDomains: _parentOwnedDomains, strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer());

    // Act
    var receipt = await dispatcher.SendAsync((object)new SweepOwnedCommand("owned"), MessageContext.New());

    // Assert - owned commands are Accepted without touching the outbox
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.Queued).Count().IsEqualTo(0);
  }

  [Test]
  public async Task SendAsync_NonMatchingOwnedDomains_CommandRoutesToOutboxAsync() {
    // Arrange - owned domains exist but do NOT match → the prefix loop falls through to false
    var strategy = new SweepWorkStrategy();
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(ownedDomains: _unrelatedOwnedDomains, strategy: strategy),
      envelopeSerializer: new SweepEnvelopeSerializer());

    // Act
    var receipt = await dispatcher.SendAsync((object)new SweepOwnedCommand("not-owned"), MessageContext.New());

    // Assert - non-owned command goes to the outbox
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.Queued).Count().IsEqualTo(1);
  }

  // ========================================
  // ROUTED RESULT UNWRAP — lines 1467, 1653, 2010, 2240, 2302
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Object_InvokerReturnsRoutedLocal_UnwrapsResultAsync() {
    // Arrange - receptor returns Route.Local(event); caller receives the unwrapped event
    var inner = new SweepRoutedEvent(Guid.NewGuid());
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(),
      invoker: _ => new ValueTask<object>(Route.Local(inner)));

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(new SweepRoutedCommand("unwrap"), MessageContext.New());

    // Assert
    await Assert.That(ReferenceEquals(result, inner)).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_InvokerReturnsRoutedLocal_UnwrapsResultAsync() {
    // Arrange - generic <TMessage, TResult> path unwraps Routed results too
    var inner = new SweepRoutedEvent(Guid.NewGuid());
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(),
      invoker: _ => new ValueTask<object>(Route.Local(inner)));

    // Act
    var result = await dispatcher.LocalInvokeAsync<SweepRoutedCommand, object>(
      new SweepRoutedCommand("generic-unwrap"), MessageContext.New());

    // Assert
    await Assert.That(ReferenceEquals(result, inner)).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericTyped_RoutedMessage_UnwrapsBeforeInvokingAsync() {
    // Arrange - TMessage is Routed<T>: the generic internal path must unwrap the inner
    // message before receptor lookup and invocation
    object? received = null;
    var command = new SweepRoutedCommand("routed-message");
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(),
      invoker: msg => {
        received = msg;
        return new ValueTask<object>(new SweepRoutedEvent(Guid.NewGuid()));
      });

    // Act
    var result = await dispatcher.LocalInvokeAsync<Routed<SweepRoutedCommand>, object>(
      Route.Local(command), MessageContext.New());

    // Assert - receptor saw the unwrapped inner command
    await Assert.That(ReferenceEquals(received, command)).IsTrue();
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task LocalInvokeAsync_WithOptions_InvokerReturnsRoutedLocal_UnwrapsResultAsync() {
    // Arrange - options overload routes through _localInvokeWithTracingAndOptionsAsync
    var inner = new SweepRoutedEvent(Guid.NewGuid());
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(),
      invoker: _ => new ValueTask<object>(Route.Local(inner)));

    // Act
    var result = await dispatcher.LocalInvokeAsync<object>(new SweepRoutedCommand("options-unwrap"), new DispatchOptions());

    // Assert
    await Assert.That(ReferenceEquals(result, inner)).IsTrue();
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_InvokerReturnsRoutedLocal_UnwrapsValueAsync() {
    // Arrange - receipt path must also hand back the unwrapped inner value
    var inner = new SweepRoutedEvent(Guid.NewGuid());
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(),
      invoker: _ => new ValueTask<object>(Route.Local(inner)));

    // Act
    var result = await dispatcher.LocalInvokeWithReceiptAsync<object>(
      new SweepRoutedCommand("receipt-unwrap"), MessageContext.New());

    // Assert
    await Assert.That(ReferenceEquals(result.Value, inner)).IsTrue();
    await Assert.That(result.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task LocalInvokeWithReceiptAsync_WithOptions_InvokerReturnsRoutedLocal_UnwrapsValueAsync() {
    // Arrange - receipt + options path unwrap branch
    var inner = new SweepRoutedEvent(Guid.NewGuid());
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(),
      invoker: _ => new ValueTask<object>(Route.Local(inner)));

    // Act
    var result = await dispatcher.LocalInvokeWithReceiptAsync<object>(
      new SweepRoutedCommand("receipt-options-unwrap"), new DispatchOptions());

    // Assert
    await Assert.That(ReferenceEquals(result.Value, inner)).IsTrue();
    await Assert.That(result.Receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  // ========================================
  // ACTIVITY TAGS IN GENERIC TRACING PATH — lines 1622-1626
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_GenericTyped_WithActivityListener_SetsParentDebugTagsAsync() {
    // Arrange - a sampling listener makes StartActivity return a real activity,
    // exercising the tag block including the parent-id debug tags
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

    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(),
      invoker: _ => new ValueTask<object>(new SweepRoutedEvent(Guid.NewGuid())));

    // Act
    var result = await dispatcher.LocalInvokeAsync<SweepRoutedCommand, object>(
      new SweepRoutedCommand("activity"), MessageContext.New());

    // Assert - the dispatch activity carried the message-type and parent debug tags
    await Assert.That(result).IsNotNull();
    List<Activity> dispatchActivities;
    lock (stopped) {
      dispatchActivities = stopped.Where(a =>
        a.OperationName == "Dispatch SweepRoutedCommand" &&
        Equals(a.GetTagItem("whizbang.message.type"), typeof(SweepRoutedCommand).FullName)).ToList();
    }
    await Assert.That(dispatchActivities.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(dispatchActivities[0].GetTagItem("whizbang.debug.parent.id")).IsNotNull();
    await Assert.That(dispatchActivities[0].GetTagItem("whizbang.debug.parent.source")).IsNotNull();
  }

  // ========================================
  // ERROR METRICS IN VOID SYNC+TRACING PATH — lines 1723-1726
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidWithTracing_InvokerThrows_RecordsErrorMetricAsync() {
    // Arrange - DispatcherMetrics registered + trace store forces the tracing path;
    // a throwing receptor must record an error measurement and rethrow
    var errorCount = 0L;
    using var meterListener = new MeterListener();
    meterListener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == DispatcherMetrics.METER_NAME && instrument.Name == "whizbang.dispatcher.errors") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
      Interlocked.Add(ref errorCount, measurement));
    meterListener.Start();

    var metrics = new DispatcherMetrics(new WhizbangMetrics());
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(metrics: metrics),
      traceStore: new SweepTraceStore(),
      voidInvoker: _ => throw new InvalidOperationException("void-receptor-failed"));

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync((object)new SweepRoutedCommand("metric-error"), MessageContext.New()))
      .ThrowsExactly<InvalidOperationException>();
    await Assert.That(Interlocked.Read(ref errorCount)).IsGreaterThanOrEqualTo(1L);
  }

  // ========================================
  // CASCADE NULL / NO-MESSAGE RESULTS — lines 2493-2494, 2529-2534
  // ========================================

  [Test]
  public async Task SendAsync_InvokerReturnsNull_SkipsCascadeAndReturnsDeliveredAsync() {
    // Arrange - null receptor result short-circuits the cascade with a warning
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(),
      invoker: _ => new ValueTask<object>(Task.FromResult<object>(null!)));

    // Act
    var receipt = await dispatcher.SendAsync((object)new SweepRoutedCommand("null-result"), MessageContext.New());

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(0);
  }

  [Test]
  public async Task SendAsync_InvokerReturnsNonMessage_LogsNoMessagesExtractedWarningAsync() {
    // Arrange - a plain string result extracts zero messages → warning branch
    var logs = new List<string>();
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(logs: logs),
      invoker: _ => new ValueTask<object>("plain-string-result"));

    // Act
    var receipt = await dispatcher.SendAsync((object)new SweepRoutedCommand("no-messages"), MessageContext.New());

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(logs.Any(m => m.Contains("No messages extracted", StringComparison.Ordinal))).IsTrue();
  }

  // ========================================
  // EVENT TRACKING + STREAM ID GENERATION — lines 2530-2584, 2653-2698
  // ========================================

  [Test]
  public async Task SendAsync_CascadedEvent_GeneratesStreamIdAndTracksInBothTrackersAsync() {
    // Arrange - cascaded event has [GenerateStreamId]-style policy (via extractor stub),
    // scoped tracker + singleton tracker + type registry all present, debug logging on
    var logs = new List<string>();
    var cascaded = new SweepGenStreamEvent();
    var scopedTracker = new SweepScopedEventTracker();
    var syncTracker = new SweepSyncEventTracker();
    var registry = new TrackedEventTypeRegistry(
      new Dictionary<Type, string> { { typeof(SweepGenStreamEvent), "SweepPerspective" } });
    var extractor = new SweepStreamIdExtractor {
      OnExtract = msg => msg is IHasStreamId hasStreamId ? hasStreamId.StreamId : null,
      OnPolicy = msg => msg is SweepGenStreamEvent ? (true, true) : (false, false)
    };
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(syncEventTracker: syncTracker, trackedEventTypeRegistry: registry, logs: logs),
      streamIdExtractor: extractor,
      scopedEventTracker: scopedTracker,
      invoker: _ => new ValueTask<object>(cascaded));

    // Act
    var receipt = await dispatcher.SendAsync((object)new SweepRoutedCommand("track-me"), MessageContext.New());

    // Assert - StreamId was auto-generated on the event and both trackers saw it
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(cascaded.StreamId).IsNotEqualTo(Guid.Empty);
    await Assert.That(scopedTracker.GetEmittedEvents()).Count().IsEqualTo(1);
    await Assert.That(scopedTracker.GetEmittedEvents()[0].StreamId).IsEqualTo(cascaded.StreamId);
    await Assert.That(syncTracker.Tracked).Count().IsEqualTo(1);
    await Assert.That(syncTracker.Tracked[0].Perspective).IsEqualTo("SweepPerspective");
    await Assert.That(syncTracker.Tracked[0].StreamId).IsEqualTo(cascaded.StreamId);
  }

  [Test]
  public async Task SendAsync_CascadedEvent_NoTrackers_WithDebugLogging_LogsDisabledBranchesAsync() {
    // Arrange - no scoped/singleton trackers → the "tracker is NULL/DISABLED" debug branches
    var logs = new List<string>();
    var extractor = new SweepStreamIdExtractor {
      OnExtract = msg => msg is IHasStreamId hasStreamId ? hasStreamId.StreamId : null
    };
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(logs: logs),
      streamIdExtractor: extractor,
      invoker: _ => new ValueTask<object>(new SweepGenStreamEvent { StreamId = Guid.NewGuid() }));

    // Act
    var receipt = await dispatcher.SendAsync((object)new SweepRoutedCommand("no-trackers"), MessageContext.New());

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(logs.Any(m => m.Contains("SCOPED tracker is NULL", StringComparison.Ordinal))).IsTrue();
    await Assert.That(logs.Any(m => m.Contains("SINGLETON tracker DISABLED", StringComparison.Ordinal))).IsTrue();
  }

  // ========================================
  // OWNED-COMMAND CASCADE DOWNGRADE — _dispatchByModeAsync and CascadeMessageAsync
  // ========================================

  [Test]
  public async Task SendAsync_CascadedOwnedCommandRoutedOutbox_IsDowngradedToLocalAsync() {
    // Arrange - receptor cascades an owned-namespace COMMAND with explicit Outbox routing;
    // owned commands must stay local (no transport), so the outbox cascade never fires
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(ownedDomains: _parentOwnedDomains),
      invoker: _ => new ValueTask<object>(Route.Outbox(new SweepCascadedOwnedCommand("owned-cascade"))));

    // Act
    var receipt = await dispatcher.SendAsync((object)new SweepRoutedCommand("cascade-owned"), MessageContext.New());

    // Assert - downgraded to Local: nothing reached the outbox cascade seam
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(0);
  }

  [Test]
  public async Task SendAsync_CascadedNonOwnedCommandRoutedOutbox_StaysOnOutboxAsync() {
    // Arrange - control: same cascade with non-matching owned domains keeps Outbox routing
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(ownedDomains: _unrelatedOwnedDomains),
      invoker: _ => new ValueTask<object>(Route.Outbox(new SweepCascadedOwnedCommand("not-owned"))));

    // Act
    await dispatcher.SendAsync((object)new SweepRoutedCommand("cascade-not-owned"), MessageContext.New());

    // Assert
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(1);
  }

  [Test]
  public async Task CascadeMessageAsync_OwnedCommandOutboxMode_IsDowngradedToLocalAsync() {
    // Arrange - CascadeMessageAsync applies the same owned-command downgrade
    var dispatcher = new SweepRoutedDispatcher(_buildProvider(ownedDomains: _parentOwnedDomains));

    // Act
    await dispatcher.CascadeMessageAsync(
      new SweepCascadedOwnedCommand("owned-direct"),
      sourceEnvelope: null,
      DispatchModes.Outbox);

    // Assert - downgraded: no outbox cascade for the owned command
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(0);
  }

  [Test]
  public async Task CascadeMessageAsync_NonOwnedCommandOutboxMode_StaysOnOutboxAsync() {
    // Arrange - control for the downgrade branch
    var dispatcher = new SweepRoutedDispatcher(_buildProvider(ownedDomains: _unrelatedOwnedDomains));

    // Act
    await dispatcher.CascadeMessageAsync(
      new SweepCascadedOwnedCommand("not-owned-direct"),
      sourceEnvelope: null,
      DispatchModes.Outbox);

    // Assert
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(1);
  }

  // ========================================
  // STREAM ID INHERITANCE VIA SetStreamId — line 2635
  // ========================================

  [Test]
  public async Task SendAsync_CascadedEventWithoutIHasStreamId_InheritsStreamIdViaSetStreamIdAsync() {
    // Arrange - cascaded event does NOT implement IHasStreamId and its own StreamId is empty;
    // the source command has one → the generated-setter fallback (SetStreamId) must be used
    var sourceStreamId = Guid.NewGuid();
    var cascaded = new SweepNoStreamPropEvent(Guid.NewGuid());
    var extractor = new SweepStreamIdExtractor {
      OnExtract = msg => msg switch {
        SweepNoStreamPropEvent => Guid.Empty,
        SweepRoutedCommand => sourceStreamId,
        _ => null
      }
    };
    var dispatcher = new SweepRoutedDispatcher(
      _buildProvider(),
      streamIdExtractor: extractor,
      invoker: _ => new ValueTask<object>(cascaded));

    // Act
    await dispatcher.SendAsync((object)new SweepRoutedCommand("inherit-stream"), MessageContext.New());

    // Assert - SetStreamId fallback received the source command's stream id
    await Assert.That(extractor.SetCalls).Count().IsEqualTo(1);
    await Assert.That(ReferenceEquals(extractor.SetCalls[0].Message, cascaded)).IsTrue();
    await Assert.That(extractor.SetCalls[0].StreamId).IsEqualTo(sourceStreamId);
  }
}
