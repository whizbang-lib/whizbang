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

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests covering void LocalInvokeAsync paths including:
/// - Generic typed void invoke (LocalInvokeAsync&lt;TMessage&gt;)
/// - Sync receptor fallback paths
/// - Any invoker fallback for cascade from non-void receptors
/// - Various tracing paths
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherCoverageVoidInvokeTests {

  public record VoidAsyncCommand(string Data);
  public record VoidSyncCommand(string Data);
  public record VoidAnyCommand(string Data);
  public record VoidAnyResult(string Data);

  private static readonly List<string> _invocations = [];
  private static readonly Lock _lock = new();

  private static void _track(string invocation) {
    lock (_lock) { _invocations.Add(invocation); }
  }

  private static void _reset() {
    lock (_lock) { _invocations.Clear(); }
  }

  private static List<string> _snapshot() {
    lock (_lock) { return [.. _invocations]; }
  }

  private sealed class VoidAsyncDispatcher(IServiceProvider sp) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null)) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      return null;
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      if (messageType == typeof(VoidAsyncCommand)) {
        return msg => {
          _track("async-void");
          return ValueTask.CompletedTask;
        };
      }
      return null;
    }

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) {
      return evt => Task.CompletedTask;
    }

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) {
      return null;
    }

    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) {
      return null;
    }

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) {
      if (messageType == typeof(VoidSyncCommand)) {
        return msg => _track("sync-void");
      }
      return null;
    }

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) {
      if (messageType == typeof(VoidAnyCommand)) {
        return msg => {
          _track("any-invoker");
          return ValueTask.FromResult<object?>(new VoidAnyResult("result"));
        };
      }
      return null;
    }

    protected override DispatchMode? GetReceptorDefaultRouting(Type messageType) {
      return null;
    }
  }

  private sealed class VoidTracingDispatcher(IServiceProvider sp, ITraceStore traceStore) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null), traceStore: traceStore) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      return null;
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      if (messageType == typeof(VoidAsyncCommand)) {
        return msg => {
          _track("async-void-traced");
          return ValueTask.CompletedTask;
        };
      }
      return null;
    }

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) {
      return evt => Task.CompletedTask;
    }

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) {
      return null;
    }

    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) {
      return null;
    }

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) {
      return null;
    }

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) {
      return null;
    }

    protected override DispatchMode? GetReceptorDefaultRouting(Type messageType) {
      return null;
    }
  }

  private static ServiceProvider _buildProvider() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new TestServiceScopeFactory(sp));
    return services.BuildServiceProvider();
  }

  private sealed class TestServiceScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new TestServiceScope(provider);
  }

  private sealed class TestServiceScope(IServiceProvider provider) : IServiceScope {
    public IServiceProvider ServiceProvider { get; } = provider;
    public void Dispose() { }
  }

  private sealed class NoOpTraceStore : ITraceStore {
    public Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default) => Task.FromResult<IMessageEnvelope?>(null);
    public Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default) => Task.FromResult(new List<IMessageEnvelope>());
    public Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default) => Task.FromResult(new List<IMessageEnvelope>());
    public Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset toTime, CancellationToken ct = default) => Task.FromResult(new List<IMessageEnvelope>());
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidAsyncReceptor_FastPath_InvokesAsync() {
    _reset();
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var command = new VoidAsyncCommand("data");
    await dispatcher.LocalInvokeAsync(command);
    await Assert.That(_snapshot()).Contains("async-void");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidAsyncReceptor_WithContext_InvokesAsync() {
    _reset();
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var command = new VoidAsyncCommand("data");
    var context = MessageContext.Create(CorrelationId.New(), MessageId.New());
    await dispatcher.LocalInvokeAsync(command, context);
    await Assert.That(_snapshot()).Contains("async-void");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidAsyncReceptor_WithTracing_InvokesAsync() {
    _reset();
    var provider = _buildProvider();
    var dispatcher = new VoidTracingDispatcher(provider, new NoOpTraceStore());
    var command = new VoidAsyncCommand("data");
    await dispatcher.LocalInvokeAsync(command);
    await Assert.That(_snapshot()).Contains("async-void-traced");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidSyncReceptor_InvokesSynchronouslyAsync() {
    _reset();
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var command = new VoidSyncCommand("sync-data");
    await dispatcher.LocalInvokeAsync(command);
    await Assert.That(_snapshot()).Contains("sync-void");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_AnyInvokerFallback_InvokesAndCascadesAsync() {
    _reset();
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var command = new VoidAnyCommand("any-data");
    await dispatcher.LocalInvokeAsync(command);
    await Assert.That(_snapshot()).Contains("any-invoker");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_GenericTyped_VoidAsync_InvokesAsync() {
    _reset();
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var command = new VoidAsyncCommand("typed-data");
    await dispatcher.LocalInvokeAsync<VoidAsyncCommand>(command);
    await Assert.That(_snapshot()).Contains("async-void");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_GenericTyped_WithContext_InvokesAsync() {
    _reset();
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var command = new VoidAsyncCommand("typed-data");
    var context = MessageContext.Create(CorrelationId.New(), MessageId.New());
    await dispatcher.LocalInvokeAsync<VoidAsyncCommand>(command, context);
    await Assert.That(_snapshot()).Contains("async-void");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_GenericTyped_SyncFallback_InvokesAsync() {
    _reset();
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var command = new VoidSyncCommand("sync-typed");
    await dispatcher.LocalInvokeAsync<VoidSyncCommand>(command);
    await Assert.That(_snapshot()).Contains("sync-void");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_GenericTyped_AnyFallback_InvokesAsync() {
    _reset();
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var command = new VoidAnyCommand("any-typed");
    await dispatcher.LocalInvokeAsync<VoidAnyCommand>(command);
    await Assert.That(_snapshot()).Contains("any-invoker");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_Void_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    object routed = Route.None();
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(routed))
        .ThrowsExactly<ArgumentException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_Void_WithContext_RoutedNone_ThrowsArgumentExceptionAsync() {
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    object routed = Route.None();
    var context = MessageContext.Create(CorrelationId.New(), MessageId.New());
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(routed, context))
        .ThrowsExactly<ArgumentException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidUnknown_ThrowsReceptorNotFoundExceptionAsync() {
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var unknown = "unknown-message";
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(unknown))
        .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_GenericTyped_Unknown_ThrowsReceptorNotFoundExceptionAsync() {
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var unknown = "unknown-message";
    await Assert.That(async () => await dispatcher.LocalInvokeAsync<string>(unknown))
        .ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidWithOptions_SyncFallback_InvokesAsync() {
    _reset();
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var command = new VoidSyncCommand("sync-options");
    var options = new DispatchOptions();
    await dispatcher.LocalInvokeAsync(command, options);
    await Assert.That(_snapshot()).Contains("sync-void");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidWithOptions_AnyFallback_InvokesAsync() {
    _reset();
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var command = new VoidAnyCommand("any-options");
    var options = new DispatchOptions();
    await dispatcher.LocalInvokeAsync(command, options);
    await Assert.That(_snapshot()).Contains("any-invoker");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidWithOptions_Unknown_ThrowsAsync() {
    var provider = _buildProvider();
    var dispatcher = new VoidAsyncDispatcher(provider);
    var unknown = "unknown-message";
    var options = new DispatchOptions();
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(unknown, options))
        .ThrowsExactly<ReceptorNotFoundException>();
  }
}
