// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
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
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)
#pragma warning disable RCS1163 // Unused parameter: fake receptor/lookup members intentionally match interface signatures.

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Branch coverage for the first half of Dispatcher.cs (foreign lookups, owned domains, perspective
/// sync, send and local-invoke instrumentation, RPC extraction diagnostics, envelope registration):
/// each test takes a condition outcome no other test reached and asserts the effect that outcome has,
/// so that a regression in the branch fails the test rather than merely leaving the line unexecuted.
/// </summary>
/// <remarks>
/// Spans are collected by a per-test ActivityListener and filtered to this class's own message types
/// (all named "BranchA..."), and metrics go to a per-test meter factory, so measurements from other
/// tests can never be mistaken for these. The cases run one at a time so each sees only its own spans.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
[NotInParallel(nameof(DispatcherBranchCoverageATests))]
public class DispatcherBranchCoverageATests {

  // ========================================
  // TEST MESSAGE TYPES
  // ========================================

  public record BranchACommand(Guid Id);

  public record BranchAResult(Guid Id);

  public record BranchAResponse(Guid Id);

  public record BranchAOwnedCommand(Guid Id);

  public record BranchAEvent([property: StreamId] Guid Id) : IEvent;

  public record BranchASyncEvent([property: StreamId] Guid StreamId) : IEvent;

  private sealed class BranchAPerspective;

  private const string PARENT_SOURCE_NAME = "Whizbang.Core.Tests.DispatcherBranchCoverageA.Parent";
  private const string TAG_MESSAGE_TYPE = "whizbang.message.type";
  private const string TAG_MESSAGE_ID = "whizbang.message.id";
  private const string TAG_CORRELATION_ID = "whizbang.correlation.id";
  private const string TAG_PARENT_ID = "whizbang.debug.parent.id";
  private const string TAG_PARENT_SOURCE = "whizbang.debug.parent.source";
  private const string METRIC_ERRORS = "whizbang.dispatcher.errors";
  private const string METRIC_DISPATCHED = "whizbang.dispatcher.messages_dispatched";
  private const string METRIC_LOCAL_INVOKE_DURATION = "whizbang.dispatcher.local_invoke.duration";
  private const string DISPATCH_COMMAND_SPAN = "Dispatch BranchACommand";

  private static readonly ActivitySource _parentSource = new(PARENT_SOURCE_NAME);

  // A trailing dot is how an operator may naturally write a namespace prefix.
  private static readonly string[] _trailingDotOwnedDomains = ["Whizbang.Core.Tests."];

  // ========================================
  // TEST DISPATCHER
  // ========================================

  /// <summary>A dispatcher whose own tables answer only what the test configures.</summary>
  private sealed class BranchADispatcher(
    IServiceProvider sp,
    ITraceStore? traceStore = null,
    IEnvelopeSerializer? envelopeSerializer = null,
    IEnvelopeRegistry? envelopeRegistry = null,
    IStreamIdExtractor? streamIdExtractor = null,
    IReceptorRegistry? receptorRegistry = null
    ) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      traceStore: traceStore,
      envelopeSerializer: envelopeSerializer,
      envelopeRegistry: envelopeRegistry,
      streamIdExtractor: streamIdExtractor,
      receptorRegistry: receptorRegistry) {
    private int _defaultRoutingLookups;

    public Type HandledType { get; init; } = typeof(BranchACommand);
    public ReceptorInvoker<object>? Invoker { get; init; }
    public VoidReceptorInvoker? VoidInvoker { get; init; }
    public VoidSyncReceptorInvoker? VoidSyncInvoker { get; init; }
    public Func<object, ValueTask<object?>>? AnyInvoker { get; init; }

    public List<IMessage> OutboxCascades { get; } = [];
    public int DefaultRoutingLookups => Volatile.Read(ref _defaultRoutingLookups);

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      var invoker = Invoker;
      if (invoker is null || messageType != HandledType) {
        return null;
      }
      // Cast mirrors production generated code.
      return async msg => (TResult)await invoker(msg);
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) =>
      VoidInvoker is not null && messageType == HandledType ? VoidInvoker : null;

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) =>
      _ => Task.CompletedTask;

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;

    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) =>
      VoidSyncInvoker is not null && messageType == HandledType ? VoidSyncInvoker : null;

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) =>
      AnyInvoker is not null && messageType == HandledType ? AnyInvoker : null;

    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) {
      Interlocked.Increment(ref _defaultRoutingLookups);
      return null;
    }

    protected override Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      OutboxCascades.Add(message);
      return Task.CompletedTask;
    }
  }

  // ========================================
  // STUBS
  // ========================================

  /// <summary>A foreign assembly's lookup that knows no routing and counts every consultation.</summary>
  private sealed class CountingLookup : IReceptorLookup {
    private int _routingLookups;
    private int _anyLookups;

    /// <summary>When set, the any-invoker answers for this type (so hiding the lookup is observable).</summary>
    public Type? AnswersAnyFor { get; init; }
    public int RoutingLookups => Volatile.Read(ref _routingLookups);
    public int AnyLookups => Volatile.Read(ref _anyLookups);

    public ReceptorInvoker<TResult>? LookupReceptorInvoker<TResult>(object message, Type messageType) => null;
    public VoidReceptorInvoker? LookupVoidReceptorInvoker(object message, Type messageType) => null;
    public Func<object, IMessageEnvelope?, CancellationToken, Task>? LookupUntypedReceptorPublisher(Type eventType) => null;
    public SyncReceptorInvoker<TResult>? LookupSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    public VoidSyncReceptorInvoker? LookupVoidSyncReceptorInvoker(object message, Type messageType) => null;

    public Func<object, ValueTask<object?>>? LookupReceptorInvokerAny(object message, Type messageType) {
      Interlocked.Increment(ref _anyLookups);
      if (AnswersAnyFor is null || messageType != AnswersAnyFor) {
        return null;
      }
      return _ => ValueTask.FromResult<object?>(new BranchAResult(Guid.Empty));
    }

    public DispatchModes? LookupReceptorDefaultRouting(Type messageType) {
      Interlocked.Increment(ref _routingLookups);
      return null;
    }
  }

  /// <summary>A provider that answers null for the foreign-lookup collection and delegates everything else.</summary>
  private sealed class LookupCollectionHidingProvider(IServiceProvider inner) : IServiceProvider {
    private readonly IServiceProvider _inner = inner;
    private int _lookupCollectionRequests;

    public int LookupCollectionRequests => Volatile.Read(ref _lookupCollectionRequests);

    public object? GetService(Type serviceType) {
      if (serviceType == typeof(IEnumerable<IReceptorLookup>)) {
        Interlocked.Increment(ref _lookupCollectionRequests);
        return null;
      }
      return _inner.GetService(serviceType);
    }
  }

  /// <summary>Records register/unregister calls into a journal shared with the receptor, so ordering is observable.</summary>
  private sealed class RecordingEnvelopeRegistry(List<string> journal) : IEnvelopeRegistry {
    private readonly List<string> _journal = journal;

    public List<IMessageEnvelope> Registered { get; } = [];

    public void Register<T>(MessageEnvelope<T> envelope) {
      Registered.Add(envelope);
      _journal.Add($"register:{envelope.MessageId}");
    }

    public MessageEnvelope<T>? TryGetEnvelope<T>(T message) where T : notnull => null;

    public void Unregister<T>(T message) where T : notnull =>
      _journal.Add(message is IMessageEnvelope envelope ? $"unregister:{envelope.MessageId}" : "unregister-by-message");

    public void Unregister<T>(MessageEnvelope<T> envelope) => _journal.Add($"unregister:{envelope.MessageId}");
  }

  private sealed class RecordingTraceStore : ITraceStore {
    public List<IMessageEnvelope> Stored { get; } = [];

    public Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default) {
      Stored.Add(envelope);
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

  private sealed class RecordingSyncAwaiter : IPerspectiveSyncAwaiter {
    public Guid AwaiterId { get; } = Guid.CreateVersion7();
    public List<(Type PerspectiveType, Guid StreamId, Type[]? EventTypes)> StreamWaits { get; } = [];

    public Task<SyncResult> WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      Task.FromResult(new SyncResult(SyncOutcome.Synced, 1, TimeSpan.Zero));

    public Task<bool> IsCaughtUpAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      Task.FromResult(true);

    public Task<SyncResult> WaitForStreamAsync(
        Type perspectiveType,
        Guid streamId,
        Type[]? eventTypes,
        TimeSpan timeout,
        Guid? eventIdToAwait = null,
        CancellationToken ct = default) {
      StreamWaits.Add((perspectiveType, streamId, eventTypes));
      return Task.FromResult(new SyncResult(SyncOutcome.Synced, 1, TimeSpan.Zero));
    }
  }

  private sealed class FixedStreamIdExtractor(Guid streamId) : IStreamIdExtractor {
    private readonly Guid _streamId = streamId;

    public Guid? ExtractStreamId(object message, Type messageType) => _streamId;
  }

  private sealed class RegistryStub : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void AddReceptor(Type messageType, LifecycleStage stage, ReceptorInfo receptor) {
      var key = (messageType, stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(receptor);
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      _receptors.TryGetValue((messageType, stage), out var list) ? list : [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
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

  private sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider {
    private readonly List<string> _sink = sink;

    public ILogger CreateLogger(string categoryName) => new ListLogger(_sink);
    public void Dispose() {
      // Nothing to release: the sink is owned by the test.
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
        // Root-provider-backed scope: nothing to release.
      }
    }
  }

  /// <summary>Per-test span and metric capture: a sampling listener plus an isolated meter factory.</summary>
  private sealed class Observed : IDisposable {
    private readonly ActivityListener _listener;
    private readonly List<Activity> _spans = [];
    private readonly TestMeterFactory _meters = new();

    public DispatcherMetrics DispatcherMetrics { get; }
    public MetricAssertionHelper Metrics { get; }

    public Observed() {
      DispatcherMetrics = new DispatcherMetrics(new WhizbangMetrics(_meters));
      Metrics = new MetricAssertionHelper([.. _meters.CreatedMeters]);
      _listener = new ActivityListener {
        ShouldListenTo = source => source.Name == "Whizbang.Execution" || source.Name == PARENT_SOURCE_NAME,
        Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = activity => {
          if (activity.OperationName.StartsWith("Dispatch BranchA", StringComparison.Ordinal)) {
            lock (_spans) {
              _spans.Add(activity);
            }
          }
        }
      };
      ActivitySource.AddActivityListener(_listener);
    }

    public List<Activity> SpansNamed(string operationName) {
      lock (_spans) {
        return [.. _spans.Where(a => a.OperationName == operationName)];
      }
    }

    public double Sum(string instrument, Func<Dictionary<string, string>, bool> predicate) =>
      Metrics.GetByName(instrument).Where(m => predicate(m.Tags)).Sum(m => m.Value);

    public int Recorded(string instrument) => Metrics.GetByName(instrument).Count;

    public void Dispose() {
      _listener.Dispose();
      Metrics.Dispose();
      _meters.Dispose();
    }
  }

  // ========================================
  // HELPERS
  // ========================================

  private static ServiceProvider _buildProvider(
    DispatcherMetrics? metrics = null,
    IPerspectiveSyncAwaiter? syncAwaiter = null,
    List<string>? logs = null,
    string[]? ownedDomains = null,
    IWorkCoordinatorStrategy? strategy = null,
    IReceptorLookup[]? lookups = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new TestScopeFactory(sp));
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    if (syncAwaiter is not null) {
      services.AddSingleton(syncAwaiter);
    }
    if (logs is not null) {
      services.AddLogging(builder => {
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddProvider(new ListLoggerProvider(logs));
      });
    }
    if (ownedDomains is not null) {
      var routingOptions = new RoutingOptions();
      routingOptions.OwnDomains(ownedDomains);
      services.AddSingleton<IOptions<RoutingOptions>>(Options.Create(routingOptions));
    }
    if (strategy is not null) {
      services.AddSingleton(strategy);
    }
    foreach (var lookup in lookups ?? []) {
      services.AddSingleton(lookup);
    }
    return services.BuildServiceProvider();
  }

  private static RegistryStub _registryWith(Type messageType, IReadOnlyList<ReceptorSyncAttributeInfo>? syncAttributes) {
    var registry = new RegistryStub();
    registry.AddReceptor(
      messageType,
      LifecycleStage.LocalImmediateInline,
      new ReceptorInfo(
        MessageType: messageType,
        ReceptorId: "branch-a-receptor",
        InvokeAsync: (_, _, _, _, _) => ValueTask.FromResult<object?>(null),
        SyncAttributes: syncAttributes));
    return registry;
  }

  private static ReceptorSyncAttributeInfo _syncAttribute(IReadOnlyList<Type>? eventTypes) =>
    new(PerspectiveType: typeof(BranchAPerspective), EventTypes: eventTypes, TimeoutMs: 1000, FireBehavior: SyncFireBehavior.FireAlways);

  private static IReadOnlyList<ReceptorSyncAttributeInfo>? _syncAttributesFor(string shape) {
    IReadOnlyList<ReceptorSyncAttributeInfo>? attributes = null;
    if (shape == "declared") {
      attributes = [_syncAttribute(null)];
    } else if (shape == "empty") {
      attributes = [];
    }
    return attributes;
  }

  private static async Task _assertParentTagsAsync(Activity span, Activity? parent) {
    if (parent is null) {
      await Assert.That((string?)span.GetTagItem(TAG_PARENT_ID)).IsEqualTo("none")
        .Because("with no ambient activity the span says so explicitly");
      await Assert.That((string?)span.GetTagItem(TAG_PARENT_SOURCE)).IsEqualTo("none");
    } else {
      await Assert.That((string?)span.GetTagItem(TAG_PARENT_ID)).IsEqualTo(parent.Id)
        .Because("the span names the ambient activity it was started under");
      await Assert.That((string?)span.GetTagItem(TAG_PARENT_SOURCE)).IsEqualTo(PARENT_SOURCE_NAME);
    }
  }

  private static async Task _assertCommandSpanAsync(Activity span, MessageId messageId, string? correlationId) {
    await Assert.That((string?)span.GetTagItem(TAG_MESSAGE_TYPE)).IsEqualTo(typeof(BranchACommand).FullName);
    await Assert.That((string?)span.GetTagItem(TAG_MESSAGE_ID)).IsEqualTo(messageId.ToString());
    await Assert.That(correlationId).IsNotNull();
    await Assert.That((string?)span.GetTagItem(TAG_CORRELATION_ID)).IsEqualTo(correlationId);
  }

  // ========================================
  // FOREIGN LOOKUPS (lines 155, 237)
  // ========================================

  [Test]
  public async Task ForeignLookups_ProviderAnswersNullForTheLookupCollection_TreatsItAsNoForeignAssembliesAsync() {
    // Arrange - a lookup that WOULD answer is registered, but the provider answers null for the
    // collection: the dispatcher must treat null as "no foreign assemblies", not dereference it.
    var hidden = new CountingLookup { AnswersAnyFor = typeof(BranchACommand) };
    await using var inner = _buildProvider(lookups: [hidden]);
    var provider = new LookupCollectionHidingProvider(inner);
    var dispatcher = new BranchADispatcher(provider);

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<BranchAResult>((object)new BranchACommand(Guid.CreateVersion7()), MessageContext.New()))
      .ThrowsExactly<ReceptorNotFoundException>()
      .Because("a null lookup collection means no foreign receptors, so the message is unhandled (not a NullReferenceException)");
    await Assert.That(provider.LookupCollectionRequests).IsGreaterThanOrEqualTo(1);
    await Assert.That(hidden.AnyLookups).IsEqualTo(0);
  }

  [Test]
  public async Task CascadeRouting_EveryForeignAssemblyAnswersNull_ScansAllThenUsesTheSystemDefaultAsync() {
    // Arrange - two foreign assemblies, neither declares a default routing for the receptor
    var first = new CountingLookup();
    var second = new CountingLookup();
    await using var sp = _buildProvider(lookups: [first, second]);
    var response = new BranchAResponse(Guid.CreateVersion7());
    var cascaded = new BranchAEvent(Guid.CreateVersion7());
    var dispatcher = new BranchADispatcher(sp) {
      AnyInvoker = _ => ValueTask.FromResult<object?>((response, cascaded))
    };

    // Act - RPC extraction cascades the remaining event, looking up the receptor's routing
    var result = await dispatcher.LocalInvokeAsync<BranchAResponse>((object)new BranchACommand(Guid.CreateVersion7()), MessageContext.New());

    // Assert
    await Assert.That(ReferenceEquals(result, response)).IsTrue();
    await Assert.That(first.RoutingLookups).IsGreaterThanOrEqualTo(1);
    await Assert.That(second.RoutingLookups).IsGreaterThanOrEqualTo(1)
      .Because("a null answer from one assembly must not end the scan; the next assembly may declare it");
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(1)
      .Because("with no declared routing anywhere, the system default (outbox) applies");
    await Assert.That(ReferenceEquals(dispatcher.OutboxCascades[0], cascaded)).IsTrue();
  }

  // ========================================
  // OWNED DOMAINS (line 427)
  // ========================================

  [Test]
  public async Task SendAsync_OwnedDomainDeclaredWithTrailingDot_ChildNamespaceIsOwnedAndSkipsTheOutboxAsync() {
    // Arrange - the owned domain already ends in '.', so it must be used as the prefix as-is
    // (appending another '.' would make "whizbang.core.tests.." which matches nothing).
    var strategy = new RecordingWorkStrategy();
    await using var sp = _buildProvider(ownedDomains: _trailingDotOwnedDomains, strategy: strategy);
    var dispatcher = new BranchADispatcher(sp, envelopeSerializer: new StubEnvelopeSerializer());

    // Act
    var receipt = await dispatcher.SendAsync((object)new BranchAOwnedCommand(Guid.CreateVersion7()), MessageContext.New());

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(strategy.Queued).Count().IsEqualTo(0)
      .Because("a command in an owned namespace is never sent to the outbox, however the prefix was written");
  }

  // ========================================
  // PERSPECTIVE SYNC (lines 489, 505)
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_SyncAttributeButNoStreamIdExtractor_SkipsThePerspectiveWaitAsync() {
    // Arrange - no IStreamIdExtractor injected or registered: there is no stream to wait on
    var awaiter = new RecordingSyncAwaiter();
    await using var sp = _buildProvider(syncAwaiter: awaiter);
    var invoked = 0;
    var dispatcher = new BranchADispatcher(
      sp,
      receptorRegistry: _registryWith(typeof(BranchASyncEvent), [_syncAttribute([typeof(BranchASyncEvent)])])) {
      HandledType = typeof(BranchASyncEvent),
      VoidInvoker = _ => {
        invoked++;
        return ValueTask.CompletedTask;
      }
    };

    // Act
    await dispatcher.LocalInvokeAsync((object)new BranchASyncEvent(Guid.CreateVersion7()), MessageContext.New());

    // Assert
    await Assert.That(invoked).IsEqualTo(1);
    await Assert.That(awaiter.StreamWaits).Count().IsEqualTo(0)
      .Because("without a stream id extractor there is no stream id, so the wait is skipped rather than attempted");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task LocalInvokeAsync_SyncAttributeEventTypes_ArePassedToTheAwaiterOrLeftNullAsync(bool declaresEventTypes) {
    // Arrange
    var streamId = Guid.CreateVersion7();
    var awaiter = new RecordingSyncAwaiter();
    await using var sp = _buildProvider(syncAwaiter: awaiter);
    IReadOnlyList<Type>? declared = null;
    if (declaresEventTypes) {
      declared = [typeof(BranchASyncEvent)];
    }
    var dispatcher = new BranchADispatcher(
      sp,
      streamIdExtractor: new FixedStreamIdExtractor(streamId),
      receptorRegistry: _registryWith(typeof(BranchASyncEvent), [_syncAttribute(declared)])) {
      HandledType = typeof(BranchASyncEvent),
      VoidInvoker = _ => ValueTask.CompletedTask
    };

    // Act
    await dispatcher.LocalInvokeAsync((object)new BranchASyncEvent(streamId), MessageContext.New());

    // Assert
    await Assert.That(awaiter.StreamWaits).Count().IsEqualTo(1);
    await Assert.That(awaiter.StreamWaits[0].StreamId).IsEqualTo(streamId);
    var passed = awaiter.StreamWaits[0].EventTypes;
    if (declaresEventTypes) {
      Type[] expectedTypes = [typeof(BranchASyncEvent)];
      await Assert.That(passed).IsNotNull();
      await Assert.That(passed!.SequenceEqual(expectedTypes)).IsTrue()
        .Because("the attribute's event-type filter narrows the wait");
    } else {
      await Assert.That(passed).IsNull()
        .Because("an attribute without event types waits for any event on the stream");
    }
  }

  // ========================================
  // SEND ERROR METRICS BEFORE THE TYPE IS KNOWN (lines 739, 862)
  // ========================================

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task SendAsync_RoutedNone_WithMetrics_CountsTheErrorUnderUnknownMessageTypeAsync(bool withOptions) {
    // Arrange - Route.None() fails before the message type is known
    using var observed = new Observed();
    await using var sp = _buildProvider(metrics: observed.DispatcherMetrics);
    var dispatcher = new BranchADispatcher(sp);
    object none = Route.None();
    Func<Task> send = withOptions
      ? () => dispatcher.SendAsync(none, MessageContext.New(), new DispatchOptions())
      : () => dispatcher.SendAsync(none, MessageContext.New());

    // Act & Assert
    await Assert.That(send).ThrowsExactly<ArgumentException>();
    await Assert.That(observed.Sum(METRIC_ERRORS, t =>
        t.GetValueOrDefault("message_type") == "Unknown"
        && t.GetValueOrDefault("error_type") == nameof(ArgumentException)))
      .IsEqualTo(1)
      .Because("an error raised before the type is resolved is still counted, under the Unknown message type");
  }

  // ========================================
  // PARENT ACTIVITY DEBUG TAGS (lines 930-932, 1822-1824)
  // ========================================

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task SendAsync_Generic_DispatchSpan_NamesTheParentActivityOrNoneAsync(bool withParent) {
    // Arrange
    using var observed = new Observed();
    await using var sp = _buildProvider();
    var dispatcher = new BranchADispatcher(sp) {
      Invoker = msg => ValueTask.FromResult<object>(new BranchAResult(((BranchACommand)msg).Id))
    };
    if (!withParent) {
      Activity.Current = null;
    }
    using var parent = withParent ? _parentSource.StartActivity("BranchA parent") : null;
    await Assert.That(parent is not null).IsEqualTo(withParent);

    // Act
    var receipt = await dispatcher.SendAsync(new BranchACommand(Guid.CreateVersion7()));

    // Assert
    var spans = observed.SpansNamed(DISPATCH_COMMAND_SPAN);
    await Assert.That(spans).Count().IsEqualTo(1);
    await _assertCommandSpanAsync(spans[0], receipt.MessageId, receipt.CorrelationId?.ToString());
    await _assertParentTagsAsync(spans[0], parent);
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task LocalInvokeAsync_GenericWithContext_DispatchSpan_NamesTheParentActivityOrNoneAsync(bool withParent) {
    // Arrange
    using var observed = new Observed();
    var journal = new List<string>();
    var registry = new RecordingEnvelopeRegistry(journal);
    await using var sp = _buildProvider();
    var dispatcher = new BranchADispatcher(sp, envelopeRegistry: registry) {
      Invoker = msg => ValueTask.FromResult<object>(new BranchAResult(((BranchACommand)msg).Id))
    };
    var context = MessageContext.New();
    if (!withParent) {
      Activity.Current = null;
    }
    using var parent = withParent ? _parentSource.StartActivity("BranchA parent") : null;
    await Assert.That(parent is not null).IsEqualTo(withParent);

    // Act
    var command = new BranchACommand(Guid.CreateVersion7());
    var result = await dispatcher.LocalInvokeAsync<BranchACommand, BranchAResult>(command, context);

    // Assert
    await Assert.That(result.Id).IsEqualTo(command.Id);
    await Assert.That(registry.Registered).Count().IsEqualTo(1);
    var spans = observed.SpansNamed(DISPATCH_COMMAND_SPAN);
    await Assert.That(spans).Count().IsEqualTo(1);
    await _assertCommandSpanAsync(spans[0], registry.Registered[0].MessageId, context.CorrelationId.ToString());
    await _assertParentTagsAsync(spans[0], parent);
  }

  // ========================================
  // GENERIC LOCAL INVOKE ERROR METRICS (line 1860)
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_GenericWithContext_ReceptorThrows_CountsTheErrorAndRecordsDurationAsync() {
    // Arrange
    using var observed = new Observed();
    await using var sp = _buildProvider(metrics: observed.DispatcherMetrics);
    var dispatcher = new BranchADispatcher(sp) {
      Invoker = _ => throw new TimeoutException("the downstream call timed out")
    };

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<BranchACommand, BranchAResult>(new BranchACommand(Guid.CreateVersion7()), MessageContext.New()))
      .ThrowsExactly<TimeoutException>();
    await Assert.That(observed.Sum(METRIC_ERRORS, t =>
        t.GetValueOrDefault("message_type") == nameof(BranchACommand)
        && t.GetValueOrDefault("error_type") == nameof(TimeoutException)))
      .IsEqualTo(1);
    await Assert.That(observed.Sum(METRIC_DISPATCHED, t => t.GetValueOrDefault("pattern") == "local_invoke")).IsEqualTo(0)
      .Because("an invocation that failed was not dispatched");
    await Assert.That(observed.Recorded(METRIC_LOCAL_INVOKE_DURATION)).IsEqualTo(1)
      .Because("the duration is recorded whether the invocation succeeded or not");
  }

  // ========================================
  // RPC EXTRACTION SPAN AND DIAGNOSTICS (lines 1437, 1450)
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_RpcExtraction_WithListener_TagsTheDispatchSpanWithTheMessageTypeAsync() {
    // Arrange - only the any-invoker answers, so the RPC extraction path runs
    using var observed = new Observed();
    await using var sp = _buildProvider();
    var response = new BranchAResponse(Guid.CreateVersion7());
    var dispatcher = new BranchADispatcher(sp) {
      AnyInvoker = _ => ValueTask.FromResult<object?>((response, new BranchAEvent(Guid.CreateVersion7())))
    };

    // Act
    var result = await dispatcher.LocalInvokeAsync<BranchAResponse>((object)new BranchACommand(Guid.CreateVersion7()), MessageContext.New());

    // Assert
    await Assert.That(ReferenceEquals(result, response)).IsTrue();
    var spans = observed.SpansNamed(DISPATCH_COMMAND_SPAN);
    await Assert.That(spans).Count().IsEqualTo(1);
    await Assert.That((string?)spans[0].GetTagItem(TAG_MESSAGE_TYPE)).IsEqualTo(typeof(BranchACommand).FullName);
  }

  [Test]
  public async Task LocalInvokeAsync_RpcExtraction_ReceptorReturnsNull_LogsNullResultTypeAndThrowsAsync() {
    // Arrange - debug logging on, receptor returns null
    var logs = new List<string>();
    await using var sp = _buildProvider(logs: logs);
    var dispatcher = new BranchADispatcher(sp) {
      AnyInvoker = _ => ValueTask.FromResult<object?>(null)
    };

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<BranchAResponse>((object)new BranchACommand(Guid.CreateVersion7()), MessageContext.New()))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("of type null");
    await Assert.That(logs.Any(m => m.Contains("RpcExtraction: Receptor returned null, IsNull=True", StringComparison.Ordinal))).IsTrue()
      .Because("the diagnostic names a null result as \"null\" rather than failing to describe it");
  }

  // ========================================
  // CASCADE EXCLUDING RESPONSE (lines 1498, 1509)
  // ========================================

  [Test]
  public async Task CascadeEventsExcludingResponseAsync_NullResult_LogsNullResultTypeAndCascadesNothingAsync() {
    // Arrange
    var logs = new List<string>();
    await using var sp = _buildProvider(logs: logs);
    var dispatcher = new BranchADispatcher(sp);

    // Act
    await dispatcher.CascadeEventsExcludingResponseAsync<BranchAResponse>(null, null, typeof(BranchACommand));

    // Assert
    await Assert.That(logs.Any(m => m.Contains("ResultType=null, ExtractedType=BranchAResponse", StringComparison.Ordinal))).IsTrue();
    await Assert.That(logs.Any(m => m.Contains("Result is null, skipping cascade", StringComparison.Ordinal))).IsTrue();
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(0);
    await Assert.That(dispatcher.DefaultRoutingLookups).IsEqualTo(0)
      .Because("a null result stops before routing is looked up");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task CascadeEventsExcludingResponseAsync_OriginalMessageType_DecidesWhetherReceptorRoutingIsConsultedAsync(bool withOriginalType) {
    // Arrange
    var lookup = new CountingLookup();
    await using var sp = _buildProvider(lookups: [lookup]);
    var dispatcher = new BranchADispatcher(sp);
    var cascaded = new BranchAEvent(Guid.CreateVersion7());

    // Act
    if (withOriginalType) {
      await dispatcher.CascadeEventsExcludingResponseAsync<BranchAResponse>(cascaded, null, typeof(BranchACommand));
    } else {
      await dispatcher.CascadeEventsExcludingResponseAsync<BranchAResponse>(cascaded, null);
    }

    // Assert
    var expectedLookups = withOriginalType ? 1 : 0;
    await Assert.That(dispatcher.DefaultRoutingLookups).IsEqualTo(expectedLookups)
      .Because("without the original message type there is no receptor whose routing could be asked for");
    await Assert.That(lookup.RoutingLookups).IsEqualTo(expectedLookups);
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(1);
    await Assert.That(ReferenceEquals(dispatcher.OutboxCascades[0], cascaded)).IsTrue();
  }

  // ========================================
  // VOID LOCAL INVOKE WITH ANY-INVOKER (lines 1570, 1579-1581, 1590, 1611)
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_VoidAnyInvoker_NullResult_RegistersTracesAndLogsNullAsync() {
    // Arrange - envelope registry, listener and debug logging; the receptor returns null
    using var observed = new Observed();
    var logs = new List<string>();
    var journal = new List<string>();
    var registry = new RecordingEnvelopeRegistry(journal);
    await using var sp = _buildProvider(logs: logs);
    var dispatcher = new BranchADispatcher(sp, envelopeRegistry: registry) {
      AnyInvoker = _ => {
        journal.Add("invoke");
        return ValueTask.FromResult<object?>(null);
      }
    };
    var context = MessageContext.New();

    // Act
    await dispatcher.LocalInvokeAsync((object)new BranchACommand(Guid.CreateVersion7()), context);

    // Assert - the envelope is registered for exactly the receptor's lifetime
    await Assert.That(registry.Registered).Count().IsEqualTo(1);
    var messageId = registry.Registered[0].MessageId;
    string[] expected = [$"register:{messageId}", "invoke", $"unregister:{messageId}"];
    await Assert.That(journal.SequenceEqual(expected)).IsTrue()
      .Because("the receptor must be able to find its envelope while it runs, and it must be gone afterwards");

    var spans = observed.SpansNamed(DISPATCH_COMMAND_SPAN);
    await Assert.That(spans).Count().IsEqualTo(1);
    await _assertCommandSpanAsync(spans[0], messageId, context.CorrelationId.ToString());

    await Assert.That(logs.Any(m => m.Contains("VoidWithAnyInvoker: Receptor returned null, IsNull=True", StringComparison.Ordinal))).IsTrue();
    await Assert.That(logs.Any(m => m.Contains("Receptor returned null, no cascade will occur", StringComparison.Ordinal))).IsTrue();
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(0);
  }

  [Test]
  public async Task LocalInvokeAsync_VoidAnyInvoker_NoRegistry_EventResult_LogsItsTypeAndCascadesItAsync() {
    // Arrange - no envelope registry; the receptor returns an event
    var logs = new List<string>();
    await using var sp = _buildProvider(logs: logs);
    var cascaded = new BranchAEvent(Guid.CreateVersion7());
    var dispatcher = new BranchADispatcher(sp) {
      AnyInvoker = _ => ValueTask.FromResult<object?>(cascaded)
    };

    // Act
    await dispatcher.LocalInvokeAsync((object)new BranchACommand(Guid.CreateVersion7()), MessageContext.New());

    // Assert
    await Assert.That(logs.Any(m => m.Contains("VoidWithAnyInvoker: Receptor returned BranchAEvent, IsNull=False", StringComparison.Ordinal))).IsTrue();
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(1)
      .Because("a void invocation still cascades the events a value-returning receptor produced");
    await Assert.That(ReferenceEquals(dispatcher.OutboxCascades[0], cascaded)).IsTrue();
  }

  // ========================================
  // SYNC-ATTRIBUTE PROBE IN VOID OVERLOADS (lines 1281, 1751, 1946)
  // ========================================

  [Test]
  [Arguments("declared")]
  [Arguments("empty")]
  [Arguments("none")]
  public async Task LocalInvokeAsync_VoidObject_RegistrySyncAttributes_ChooseTheTracedOrFastPathAsync(string syncAttributes) {
    // Arrange - a registry is present; only a non-empty SyncAttributes list forces the traced path,
    // which is the only path that registers an envelope.
    var journal = new List<string>();
    var registry = new RecordingEnvelopeRegistry(journal);
    await using var sp = _buildProvider();
    var dispatcher = new BranchADispatcher(
      sp,
      envelopeRegistry: registry,
      receptorRegistry: _registryWith(typeof(BranchACommand), _syncAttributesFor(syncAttributes))) {
      VoidInvoker = _ => {
        journal.Add("invoke");
        return ValueTask.CompletedTask;
      }
    };

    // Act
    await dispatcher.LocalInvokeAsync((object)new BranchACommand(Guid.CreateVersion7()), MessageContext.New());

    // Assert
    await Assert.That(journal.Contains("invoke")).IsTrue();
    var expectedRegistrations = syncAttributes == "declared" ? 1 : 0;
    await Assert.That(registry.Registered).Count().IsEqualTo(expectedRegistrations)
      .Because("[AwaitPerspectiveSync] on the receptor requires the async path; without it the zero-allocation path is taken");
  }

  [Test]
  [Arguments("declared")]
  [Arguments("empty")]
  [Arguments("none")]
  public async Task LocalInvokeAsync_GenericVoidSyncReceptor_RegistrySyncAttributes_ChooseTheCheckedOrDirectPathAsync(string syncAttributes) {
    // Arrange - a void SYNC receptor: only declared sync attributes route it through the checked
    // path, which is the only one that starts a dispatch span.
    using var observed = new Observed();
    await using var sp = _buildProvider();
    var invoked = 0;
    var dispatcher = new BranchADispatcher(
      sp,
      receptorRegistry: _registryWith(typeof(BranchACommand), _syncAttributesFor(syncAttributes))) {
      VoidSyncInvoker = _ => invoked++
    };

    // Act
    await dispatcher.LocalInvokeAsync<BranchACommand>(new BranchACommand(Guid.CreateVersion7()), MessageContext.New());

    // Assert
    await Assert.That(invoked).IsEqualTo(1);
    var spans = observed.SpansNamed(DISPATCH_COMMAND_SPAN);
    if (syncAttributes == "declared") {
      await Assert.That(spans).Count().IsEqualTo(1);
      await Assert.That((string?)spans[0].GetTagItem(TAG_MESSAGE_TYPE)).IsEqualTo(typeof(BranchACommand).FullName);
    } else {
      await Assert.That(spans).Count().IsEqualTo(0)
        .Because("without sync attributes the sync receptor is invoked directly");
    }
  }

  // ========================================
  // VOID OBJECT LOCAL INVOKE WITH TRACING (lines 1900-1902, 1917)
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_VoidObjectWithTraceStore_TracesAndCountsTheInvocationAsync() {
    // Arrange - a trace store forces the traced void path
    using var observed = new Observed();
    var traceStore = new RecordingTraceStore();
    await using var sp = _buildProvider(metrics: observed.DispatcherMetrics);
    var dispatcher = new BranchADispatcher(sp, traceStore: traceStore) {
      VoidInvoker = _ => ValueTask.CompletedTask
    };
    var context = MessageContext.New();

    // Act
    await dispatcher.LocalInvokeAsync((object)new BranchACommand(Guid.CreateVersion7()), context);

    // Assert
    await Assert.That(traceStore.Stored).Count().IsEqualTo(1);
    var spans = observed.SpansNamed(DISPATCH_COMMAND_SPAN);
    await Assert.That(spans).Count().IsEqualTo(1);
    await _assertCommandSpanAsync(spans[0], traceStore.Stored[0].MessageId, context.CorrelationId.ToString());
    await Assert.That(observed.Sum(METRIC_DISPATCHED, t =>
        t.GetValueOrDefault("pattern") == "local_invoke"
        && t.GetValueOrDefault("message_type") == nameof(BranchACommand)))
      .IsEqualTo(1);
    await Assert.That(observed.Recorded(METRIC_LOCAL_INVOKE_DURATION)).IsEqualTo(1);
  }

  // ========================================
  // GENERIC VOID LOCAL INVOKE WITH TRACING (lines 1992-1994, 2009, 2013, 2019)
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_GenericVoid_TracesAndCountsTheInvocationAsync() {
    // Arrange
    using var observed = new Observed();
    var journal = new List<string>();
    var registry = new RecordingEnvelopeRegistry(journal);
    await using var sp = _buildProvider(metrics: observed.DispatcherMetrics);
    var dispatcher = new BranchADispatcher(sp, envelopeRegistry: registry) {
      VoidInvoker = _ => ValueTask.CompletedTask
    };
    var context = MessageContext.New();

    // Act
    await dispatcher.LocalInvokeAsync<BranchACommand>(new BranchACommand(Guid.CreateVersion7()), context);

    // Assert
    await Assert.That(registry.Registered).Count().IsEqualTo(1);
    var spans = observed.SpansNamed(DISPATCH_COMMAND_SPAN);
    await Assert.That(spans).Count().IsEqualTo(1);
    await _assertCommandSpanAsync(spans[0], registry.Registered[0].MessageId, context.CorrelationId.ToString());
    await Assert.That(observed.Sum(METRIC_DISPATCHED, t =>
        t.GetValueOrDefault("pattern") == "local_invoke"
        && t.GetValueOrDefault("message_type") == nameof(BranchACommand)))
      .IsEqualTo(1);
    await Assert.That(observed.Sum(METRIC_ERRORS, _ => true)).IsEqualTo(0);
    await Assert.That(observed.Recorded(METRIC_LOCAL_INVOKE_DURATION)).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoid_ReceptorThrows_CountsTheErrorAndRecordsDurationAsync() {
    // Arrange
    using var observed = new Observed();
    await using var sp = _buildProvider(metrics: observed.DispatcherMetrics);
    var dispatcher = new BranchADispatcher(sp) {
      VoidInvoker = _ => throw new TimeoutException("the downstream call timed out")
    };

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<BranchACommand>(new BranchACommand(Guid.CreateVersion7()), MessageContext.New()))
      .ThrowsExactly<TimeoutException>();
    await Assert.That(observed.Sum(METRIC_ERRORS, t =>
        t.GetValueOrDefault("message_type") == nameof(BranchACommand)
        && t.GetValueOrDefault("error_type") == nameof(TimeoutException)))
      .IsEqualTo(1);
    await Assert.That(observed.Sum(METRIC_DISPATCHED, t => t.GetValueOrDefault("pattern") == "local_invoke")).IsEqualTo(0)
      .Because("an invocation that failed was not dispatched");
    await Assert.That(observed.Recorded(METRIC_LOCAL_INVOKE_DURATION)).IsEqualTo(1);
  }

  // ========================================
  // LOCAL INVOKE WITH OPTIONS: ENVELOPE REGISTRATION (lines 2178, 2189, 2213)
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithOptions_RegistersTheEnvelopeForTheReceptorsLifetimeAndTracesItAsync() {
    // Arrange
    using var observed = new Observed();
    var journal = new List<string>();
    var registry = new RecordingEnvelopeRegistry(journal);
    await using var sp = _buildProvider();
    var dispatcher = new BranchADispatcher(sp, envelopeRegistry: registry) {
      Invoker = msg => {
        journal.Add("invoke");
        return ValueTask.FromResult<object>(new BranchAResult(((BranchACommand)msg).Id));
      }
    };
    var command = new BranchACommand(Guid.CreateVersion7());

    // Act
    var result = await dispatcher.LocalInvokeAsync<BranchAResult>((object)command, new DispatchOptions());

    // Assert
    await Assert.That(result.Id).IsEqualTo(command.Id);
    await Assert.That(registry.Registered).Count().IsEqualTo(1);
    var envelope = registry.Registered[0];
    string[] expected = [$"register:{envelope.MessageId}", "invoke", $"unregister:{envelope.MessageId}"];
    await Assert.That(journal.SequenceEqual(expected)).IsTrue()
      .Because("the receptor must be able to find its envelope while it runs, and it must be gone afterwards");
    var spans = observed.SpansNamed(DISPATCH_COMMAND_SPAN);
    await Assert.That(spans).Count().IsEqualTo(1);
    await _assertCommandSpanAsync(spans[0], envelope.MessageId, envelope.GetCorrelationId()?.ToString());
  }
}
