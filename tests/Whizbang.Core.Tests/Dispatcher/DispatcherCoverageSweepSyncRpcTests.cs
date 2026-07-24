using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)
#pragma warning disable RCS1163 // Unused parameter — fake receptor/handler delegates intentionally match interface signatures.

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Coverage sweep for Dispatcher.cs perspective-sync and RPC-extraction paths:
/// - _awaitPerspectiveSyncIfNeededAsync full body (stream extraction, awaiter resolution,
///   SyncContext, FireOnSuccess timeout throw, FireAlways continue)
/// - SyncAttributes lambda checks in void LocalInvokeAsync overloads
/// - _localInvokeWithCastFallbackAsync InvalidCastException catch (RPC fallback + rethrow)
/// - _localInvokeWithRpcExtractionAsync debug logging + extraction failure
/// - _cascadeEventsExcludingResponseAsync skip counting + outbox cascade of remaining values
/// - _waitForSpecificPerspectiveAsync awaiter-present and awaiter-absent branches
/// - Obsolete LocalInvokeAndSyncAsync perspective timeout throw
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherCoverageSweepSyncRpcTests {

  // ========================================
  // TEST MESSAGE TYPES
  // ========================================

  private sealed class SweepPerspective;

  public record SweepSyncEvent([property: StreamId] Guid StreamId) : IEvent;

  public record SweepSyncCommand(Guid StreamId);

  public record SweepRpcCommand(string Data);

  public record SweepRpcResponse(Guid Id) : ICommand;

#pragma warning disable WHIZ009 // Intentionally no [StreamId]: these events exercise SetStreamId/auto-generation fallbacks
  public record SweepRpcCascadeEvent(Guid Id) : IEvent;
#pragma warning restore WHIZ009

  // ========================================
  // TEST DISPATCHER
  // ========================================

  private sealed class SweepDispatcher(
    IServiceProvider sp,
    ITraceStore? traceStore = null,
    IStreamIdExtractor? streamIdExtractor = null,
    IReceptorRegistry? receptorRegistry = null,
    ReceptorInvoker<object>? invoker = null,
    VoidReceptorInvoker? voidInvoker = null,
    VoidSyncReceptorInvoker? voidSyncInvoker = null,
    Func<object, ValueTask<object?>>? anyInvoker = null,
    Type? handleMessageType = null
    ) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null),
      traceStore: traceStore,
      streamIdExtractor: streamIdExtractor,
      receptorRegistry: receptorRegistry) {
    private readonly ReceptorInvoker<object>? _invoker = invoker;
    private readonly VoidReceptorInvoker? _voidInvoker = voidInvoker;
    private readonly VoidSyncReceptorInvoker? _voidSyncInvoker = voidSyncInvoker;
    private readonly Func<object, ValueTask<object?>>? _anyInvoker = anyInvoker;
    private readonly Type _handleMessageType = handleMessageType ?? typeof(SweepRpcCommand);

    public List<IMessage> OutboxCascades { get; } = [];

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      if (_invoker is null || messageType != _handleMessageType) {
        return null;
      }
      // Cast mirrors production generated code: mismatched TResult surfaces as InvalidCastException.
      return async msg => (TResult)await _invoker(msg);
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) =>
      _voidInvoker is not null && messageType == _handleMessageType ? _voidInvoker : null;

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) =>
      _ => Task.CompletedTask;

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;

    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) =>
      _voidSyncInvoker is not null && messageType == _handleMessageType ? _voidSyncInvoker : null;

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) =>
      _anyInvoker is not null && messageType == _handleMessageType ? _anyInvoker : null;

    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;

    protected override Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      OutboxCascades.Add(message);
      return Task.CompletedTask;
    }
  }

  // ========================================
  // STUBS
  // ========================================

  private sealed class SweepSyncAwaiter(SyncOutcome outcome) : IPerspectiveSyncAwaiter {
    private readonly SyncOutcome _outcome = outcome;

    public Guid AwaiterId { get; } = Guid.NewGuid();
    public List<(Type PerspectiveType, Guid StreamId)> StreamWaits { get; } = [];

    public Task<SyncResult> WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      Task.FromResult(new SyncResult(_outcome, 1, TimeSpan.Zero));

    public Task<bool> IsCaughtUpAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      Task.FromResult(true);

    public Task<SyncResult> WaitForStreamAsync(
        Type perspectiveType,
        Guid streamId,
        Type[]? eventTypes,
        TimeSpan timeout,
        Guid? eventIdToAwait = null,
        CancellationToken ct = default) {
      StreamWaits.Add((perspectiveType, streamId));
      return Task.FromResult(new SyncResult(_outcome, 1, TimeSpan.Zero));
    }
  }

  private sealed class SweepReceptorRegistry : IReceptorRegistry {
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

  private sealed class SweepStreamIdExtractor : IStreamIdExtractor {
    public Func<object, Guid?>? OnExtract { get; init; }

    public Guid? ExtractStreamId(object message, Type messageType) => OnExtract?.Invoke(message);
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
    IPerspectiveSyncAwaiter? syncAwaiter = null,
    List<string>? logs = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new TestScopeFactory(sp));
    if (syncAwaiter is not null) {
      services.AddSingleton(syncAwaiter);
    }
    if (logs is not null) {
      services.AddLogging(builder => {
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddProvider(new ListLoggerProvider(logs));
      });
    }
    return services.BuildServiceProvider();
  }

  private static SweepReceptorRegistry _registryWithSyncAttribute(Type messageType, SyncFireBehavior fireBehavior) {
    var registry = new SweepReceptorRegistry();
    registry.AddReceptor(
      messageType,
      LifecycleStage.LocalImmediateInline,
      new ReceptorInfo(
        MessageType: messageType,
        ReceptorId: "sweep-sync-receptor",
        InvokeAsync: (_, _, _, _, _) => ValueTask.FromResult<object?>(null),
        SyncAttributes: [
          new ReceptorSyncAttributeInfo(
            PerspectiveType: typeof(SweepPerspective),
            EventTypes: [typeof(SweepSyncEvent)],
            TimeoutMs: 1000,
            FireBehavior: fireBehavior)
        ]
      ));
    return registry;
  }

  // ========================================
  // _awaitPerspectiveSyncIfNeededAsync — lines 303-350, 1090
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_Void_SyncAttributeSynced_AwaitsPerspectiveThenInvokesReceptorAsync() {
    // Arrange - IEvent message, receptor with [AwaitPerspectiveSync], awaiter returns Synced
    var streamId = Guid.NewGuid();
    var awaiter = new SweepSyncAwaiter(SyncOutcome.Synced);
    var invoked = false;
    var dispatcher = new SweepDispatcher(
      _buildProvider(syncAwaiter: awaiter),
      streamIdExtractor: new SweepStreamIdExtractor { OnExtract = _ => streamId },
      receptorRegistry: _registryWithSyncAttribute(typeof(SweepSyncEvent), SyncFireBehavior.FireOnSuccess),
      voidInvoker: _ => { invoked = true; return ValueTask.CompletedTask; },
      handleMessageType: typeof(SweepSyncEvent));

    // Act
    await dispatcher.LocalInvokeAsync((object)new SweepSyncEvent(streamId), MessageContext.New());

    // Assert - awaiter consulted before receptor ran, with the extracted stream id
    await Assert.That(invoked).IsTrue();
    await Assert.That(awaiter.StreamWaits).Count().IsEqualTo(1);
    await Assert.That(awaiter.StreamWaits[0].PerspectiveType).IsEqualTo(typeof(SweepPerspective));
    await Assert.That(awaiter.StreamWaits[0].StreamId).IsEqualTo(streamId);
  }

  [Test]
  public async Task LocalInvokeAsync_Void_SyncAttributeTimedOut_FireOnSuccess_ThrowsAndSkipsReceptorAsync() {
    // Arrange - awaiter times out; FireOnSuccess must throw before invoking the receptor
    var streamId = Guid.NewGuid();
    var awaiter = new SweepSyncAwaiter(SyncOutcome.TimedOut);
    var invoked = false;
    var dispatcher = new SweepDispatcher(
      _buildProvider(syncAwaiter: awaiter),
      streamIdExtractor: new SweepStreamIdExtractor { OnExtract = _ => streamId },
      receptorRegistry: _registryWithSyncAttribute(typeof(SweepSyncEvent), SyncFireBehavior.FireOnSuccess),
      voidInvoker: _ => { invoked = true; return ValueTask.CompletedTask; },
      handleMessageType: typeof(SweepSyncEvent));

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync((object)new SweepSyncEvent(streamId), MessageContext.New()))
      .ThrowsExactly<PerspectiveSyncTimeoutException>();
    await Assert.That(invoked).IsFalse();
  }

  [Test]
  public async Task LocalInvokeAsync_Void_SyncAttributeTimedOut_FireAlways_StillInvokesReceptorAsync() {
    // Arrange - awaiter times out; FireAlways continues regardless
    var streamId = Guid.NewGuid();
    var awaiter = new SweepSyncAwaiter(SyncOutcome.TimedOut);
    var invoked = false;
    var dispatcher = new SweepDispatcher(
      _buildProvider(syncAwaiter: awaiter),
      streamIdExtractor: new SweepStreamIdExtractor { OnExtract = _ => streamId },
      receptorRegistry: _registryWithSyncAttribute(typeof(SweepSyncEvent), SyncFireBehavior.FireAlways),
      voidInvoker: _ => { invoked = true; return ValueTask.CompletedTask; },
      handleMessageType: typeof(SweepSyncEvent));

    // Act
    await dispatcher.LocalInvokeAsync((object)new SweepSyncEvent(streamId), MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
    await Assert.That(awaiter.StreamWaits).Count().IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAsync_Void_SyncAttribute_NoStreamId_SkipsAwaiterAsync() {
    // Arrange - extractor returns null → stream-based sync impossible → skip the wait
    var awaiter = new SweepSyncAwaiter(SyncOutcome.TimedOut);
    var invoked = false;
    var dispatcher = new SweepDispatcher(
      _buildProvider(syncAwaiter: awaiter),
      streamIdExtractor: new SweepStreamIdExtractor { OnExtract = _ => null },
      receptorRegistry: _registryWithSyncAttribute(typeof(SweepSyncEvent), SyncFireBehavior.FireOnSuccess),
      voidInvoker: _ => { invoked = true; return ValueTask.CompletedTask; },
      handleMessageType: typeof(SweepSyncEvent));

    // Act
    await dispatcher.LocalInvokeAsync((object)new SweepSyncEvent(Guid.NewGuid()), MessageContext.New());

    // Assert - receptor ran, awaiter never consulted
    await Assert.That(invoked).IsTrue();
    await Assert.That(awaiter.StreamWaits).Count().IsEqualTo(0);
  }

  [Test]
  public async Task LocalInvokeAsync_Void_SyncAttribute_NoAwaiterRegistered_InvokesReceptorAsync() {
    // Arrange - no IPerspectiveSyncAwaiter in DI → wait is skipped after scope resolution
    var streamId = Guid.NewGuid();
    var invoked = false;
    var dispatcher = new SweepDispatcher(
      _buildProvider(),
      streamIdExtractor: new SweepStreamIdExtractor { OnExtract = _ => streamId },
      receptorRegistry: _registryWithSyncAttribute(typeof(SweepSyncEvent), SyncFireBehavior.FireOnSuccess),
      voidInvoker: _ => { invoked = true; return ValueTask.CompletedTask; },
      handleMessageType: typeof(SweepSyncEvent));

    // Act
    await dispatcher.LocalInvokeAsync((object)new SweepSyncEvent(streamId), MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_GenericVoid_SyncAttributeWithSyncInvoker_AwaitsPerspectiveAsync() {
    // Arrange - generic void overload + void SYNC receptor + sync attributes forces
    // _localInvokeVoidSyncWithSyncCheckAsync (covers the generic-path SyncAttributes lambda)
    var streamId = Guid.NewGuid();
    var awaiter = new SweepSyncAwaiter(SyncOutcome.Synced);
    var invoked = false;
    var dispatcher = new SweepDispatcher(
      _buildProvider(syncAwaiter: awaiter),
      streamIdExtractor: new SweepStreamIdExtractor { OnExtract = _ => streamId },
      receptorRegistry: _registryWithSyncAttribute(typeof(SweepSyncEvent), SyncFireBehavior.FireOnSuccess),
      voidSyncInvoker: _ => invoked = true,
      handleMessageType: typeof(SweepSyncEvent));

    // Act - strongly-typed generic void overload
    await dispatcher.LocalInvokeAsync<SweepSyncEvent>(new SweepSyncEvent(streamId), MessageContext.New());

    // Assert
    await Assert.That(invoked).IsTrue();
    await Assert.That(awaiter.StreamWaits).Count().IsEqualTo(1);
  }

  // ========================================
  // CAST FALLBACK / RPC EXTRACTION — lines 1202-1215, 1250-1273, 1301-1330, 1350
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_TypedInvokerCastFails_FallsBackToRpcExtractionAsync() {
    // Arrange - typed invoker returns a tuple; requesting SweepRpcResponse forces an
    // InvalidCastException, then the anyInvoker RPC-extraction fallback extracts the response
    // and cascades the remaining event. Debug logging exercises the diagnostic branches.
    var logs = new List<string>();
    var response = new SweepRpcResponse(Guid.NewGuid());
    var cascadeEvent = new SweepRpcCascadeEvent(Guid.NewGuid());
    var dispatcher = new SweepDispatcher(
      _buildProvider(logs: logs),
      invoker: _ => new ValueTask<object>((response, cascadeEvent)),
      anyInvoker: _ => new ValueTask<object?>((response, cascadeEvent)));

    // Act
    var result = await dispatcher.LocalInvokeAsync<SweepRpcResponse>(new SweepRpcCommand("cast-fallback"), MessageContext.New());

    // Assert - the exact response instance came back; the event cascaded to outbox; the
    // response itself was excluded from the cascade (ReferenceEquals skip branch)
    await Assert.That(ReferenceEquals(result, response)).IsTrue();
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(1);
    await Assert.That(ReferenceEquals(dispatcher.OutboxCascades[0], cascadeEvent)).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_TypedInvokerCastFails_NoAnyInvoker_RethrowsInvalidCastAsync() {
    // Arrange - InvalidCastException with no anyInvoker fallback must rethrow the original
    var logs = new List<string>();
    var dispatcher = new SweepDispatcher(
      _buildProvider(logs: logs),
      invoker: _ => new ValueTask<object>((new SweepRpcResponse(Guid.NewGuid()), new SweepRpcCascadeEvent(Guid.NewGuid()))));

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<SweepRpcResponse>(new SweepRpcCommand("no-fallback"), MessageContext.New()))
      .ThrowsExactly<InvalidCastException>();
  }

  [Test]
  public async Task LocalInvokeAsync_AnyInvokerOnly_RpcExtraction_WithDebugLogging_ExtractsResponseAsync() {
    // Arrange - no typed/sync invoker at all → direct RPC extraction path with debug logging
    var logs = new List<string>();
    var response = new SweepRpcResponse(Guid.NewGuid());
    var cascadeEvent = new SweepRpcCascadeEvent(Guid.NewGuid());
    var dispatcher = new SweepDispatcher(
      _buildProvider(logs: logs),
      anyInvoker: _ => new ValueTask<object?>((response, cascadeEvent)));

    // Act
    var result = await dispatcher.LocalInvokeAsync<SweepRpcResponse>(new SweepRpcCommand("rpc-direct"), MessageContext.New());

    // Assert
    await Assert.That(ReferenceEquals(result, response)).IsTrue();
    await Assert.That(dispatcher.OutboxCascades).Count().IsEqualTo(1);
    await Assert.That(logs.Any(m => m.Contains("RpcExtraction", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAsync_AnyInvokerOnly_ResultLacksRequestedType_ThrowsInvalidOperationAsync() {
    // Arrange - receptor result contains no SweepRpcResponse → extraction failure branch
    var logs = new List<string>();
    var dispatcher = new SweepDispatcher(
      _buildProvider(logs: logs),
      anyInvoker: _ => new ValueTask<object?>("not-extractable"));

    // Act & Assert
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<SweepRpcResponse>(new SweepRpcCommand("rpc-miss"), MessageContext.New()))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessageContaining("Could not extract");
  }

  // ========================================
  // _waitForSpecificPerspectiveAsync — lines 4479-4499
  // ========================================

  [Test]
  public async Task LocalInvokeAndSyncForPerspectiveAsync_NoAwaiterRegistered_ReturnsSyncedAsync() {
    // Arrange - stream id resolves but no IPerspectiveSyncAwaiter is registered
    var streamId = Guid.NewGuid();
    var invoked = false;
    var dispatcher = new SweepDispatcher(
      _buildProvider(),
      streamIdExtractor: new SweepStreamIdExtractor { OnExtract = _ => streamId },
      voidInvoker: _ => { invoked = true; return ValueTask.CompletedTask; },
      handleMessageType: typeof(SweepSyncCommand));

    // Act
    var result = await dispatcher.LocalInvokeAndSyncForPerspectiveAsync<SweepSyncCommand, SweepPerspective>(
      new SweepSyncCommand(streamId));

    // Assert - "can't verify either way" contract returns Synced without waiting
    await Assert.That(invoked).IsTrue();
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }

  [Test]
  public async Task LocalInvokeAndSyncForPerspectiveAsync_WithAwaiter_WaitsOnStreamAsync() {
    // Arrange - awaiter registered → the wait path runs and reports the awaiter outcome
    var streamId = Guid.NewGuid();
    var awaiter = new SweepSyncAwaiter(SyncOutcome.Synced);
    var dispatcher = new SweepDispatcher(
      _buildProvider(syncAwaiter: awaiter),
      streamIdExtractor: new SweepStreamIdExtractor { OnExtract = _ => streamId },
      voidInvoker: _ => ValueTask.CompletedTask,
      handleMessageType: typeof(SweepSyncCommand));

    // Act
    var result = await dispatcher.LocalInvokeAndSyncForPerspectiveAsync<SweepSyncCommand, SweepPerspective>(
      new SweepSyncCommand(streamId));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(awaiter.StreamWaits).Count().IsEqualTo(1);
    await Assert.That(awaiter.StreamWaits[0].PerspectiveType).IsEqualTo(typeof(SweepPerspective));
    await Assert.That(awaiter.StreamWaits[0].StreamId).IsEqualTo(streamId);
  }

  // ========================================
  // Obsolete LocalInvokeAndSyncAsync<TMessage, TResult, TPerspective> timeout — lines 4322-4324
  // ========================================

  [Test]
  public async Task LocalInvokeAndSyncAsync_TypedPerspective_TimedOut_ThrowsTimeoutExceptionAsync() {
    // Arrange - awaiter reports TimedOut → the obsolete typed overload wraps it in TimeoutException
    var streamId = Guid.NewGuid();
    var awaiter = new SweepSyncAwaiter(SyncOutcome.TimedOut);
    var response = new SweepRpcResponse(Guid.NewGuid());
    var dispatcher = new SweepDispatcher(
      _buildProvider(syncAwaiter: awaiter),
      streamIdExtractor: new SweepStreamIdExtractor { OnExtract = _ => streamId },
      invoker: _ => new ValueTask<object>(response),
      handleMessageType: typeof(SweepSyncCommand));

    // Act & Assert
#pragma warning disable CS0618 // Intentionally exercising the obsolete overload for coverage
    await Assert.That(async () =>
        await dispatcher.LocalInvokeAndSyncAsync<SweepSyncCommand, SweepRpcResponse, SweepPerspective>(
          new SweepSyncCommand(streamId),
          timeout: TimeSpan.FromMilliseconds(10)))
      .ThrowsExactly<TimeoutException>();
#pragma warning restore CS0618
    await Assert.That(awaiter.StreamWaits).Count().IsEqualTo(1);
  }
}
