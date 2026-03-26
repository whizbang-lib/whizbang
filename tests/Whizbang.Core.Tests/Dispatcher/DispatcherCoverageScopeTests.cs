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
/// Tests for scope propagation, CascadeMessageAsync, and LocalInvokeAsync with DispatchOptions.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherCoverageScopeTests {

  [DefaultRouting(DispatchMode.Local)]
  public record ScopeTestEvent([property: StreamId] Guid OrderId) : IEvent;
  public record ScopeTestCommand(Guid OrderId);
  public record ScopeTestResult(bool Success);

  private static readonly List<object> _localInvocations = [];
  private static readonly List<object> _outboxInvocations = [];
  private static readonly Lock _lock = new();

  private static void _trackLocal(object evt) { lock (_lock) { _localInvocations.Add(evt); } }
  private static void _trackOutbox(object evt) { lock (_lock) { _outboxInvocations.Add(evt); } }
  private static void _reset() { lock (_lock) { _localInvocations.Clear(); _outboxInvocations.Clear(); } }
  private static (int LocalCount, int OutboxCount) _snapshotCounts() { lock (_lock) { return (_localInvocations.Count, _outboxInvocations.Count); } }

  private sealed class ScopeTestDispatcher(IServiceProvider sp) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null)) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      if (messageType == typeof(ScopeTestCommand) && typeof(TResult) == typeof(ScopeTestResult)) {
        return msg => ValueTask.FromResult((TResult)(object)new ScopeTestResult(true));
      }

      return null;
    }
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      if (messageType == typeof(ScopeTestCommand)) {
        return msg => ValueTask.CompletedTask;
      }

      return null;
    }
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => evt => { _trackLocal(evt!); return Task.CompletedTask; };
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => (evt, envelope, ct) => { _trackLocal(evt); return Task.CompletedTask; };
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchMode? GetReceptorDefaultRouting(Type messageType) => null;
    protected override Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) { _trackOutbox(message); return Task.CompletedTask; }
    protected override Task CascadeToEventStoreOnlyAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) { _trackOutbox(message); return Task.CompletedTask; }
  }

  private static ServiceProvider _buildProvider() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new TestServiceScopeFactory(sp));
    return services.BuildServiceProvider();
  }
  private sealed class TestServiceScopeFactory(IServiceProvider provider) : IServiceScopeFactory { public IServiceScope CreateScope() => new TestServiceScope(provider); }
  private sealed class TestServiceScope(IServiceProvider provider) : IServiceScope { public IServiceProvider ServiceProvider { get; } = provider; public void Dispose() { } }

  [Test]
  [NotInParallel]
  public async Task CascadeMessageAsync_WithLocalMode_InvokesLocalReceptorsAsync() {
    _reset();
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    await dispatcher.CascadeMessageAsync(new ScopeTestEvent(Guid.NewGuid()), sourceEnvelope: null, DispatchMode.Local);
    var (LocalCount, _) = _snapshotCounts();
    await Assert.That(LocalCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task CascadeMessageAsync_WithOutboxMode_CallsCascadeToOutboxAsync() {
    _reset();
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    await dispatcher.CascadeMessageAsync(new ScopeTestEvent(Guid.NewGuid()), sourceEnvelope: null, DispatchMode.Outbox);
    var (_, OutboxCount) = _snapshotCounts();
    await Assert.That(OutboxCount).IsEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task CascadeMessageAsync_WithBothMode_InvokesBothLocalAndOutboxAsync() {
    _reset();
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    await dispatcher.CascadeMessageAsync(new ScopeTestEvent(Guid.NewGuid()), sourceEnvelope: null, DispatchMode.Both);
    var (LocalCount, OutboxCount) = _snapshotCounts();
    await Assert.That(LocalCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(OutboxCount).IsEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task CascadeMessageAsync_WithNullMessage_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    await Assert.That(async () => await dispatcher.CascadeMessageAsync(null!, null, DispatchMode.Local)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  [NotInParallel]
  public async Task CascadeMessageAsync_WithCancelledToken_ThrowsOperationCancelledAsync() {
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    await Assert.That(async () => await dispatcher.CascadeMessageAsync(new ScopeTestEvent(Guid.NewGuid()), null, DispatchMode.Local, cts.Token)).ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  [NotInParallel]
  public async Task CascadeMessageAsync_WithEventStoreOnlyMode_CallsCascadeToEventStoreOnlyAsync() {
    _reset();
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    await dispatcher.CascadeMessageAsync(new ScopeTestEvent(Guid.NewGuid()), sourceEnvelope: null, DispatchMode.EventStoreOnly);
    var (_, OutboxCount) = _snapshotCounts();
    await Assert.That(OutboxCount).IsEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidWithDispatchOptions_CompletesSuccessfullyAsync() {
    _reset();
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    var command = new ScopeTestCommand(Guid.NewGuid());
    await dispatcher.LocalInvokeAsync(command, new DispatchOptions());
    var result = await dispatcher.LocalInvokeAsync<ScopeTestResult>(command, new DispatchOptions());
    await Assert.That(result).IsNotNull();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidWithDispatchOptions_CancelledToken_ThrowsAsync() {
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(new ScopeTestCommand(Guid.NewGuid()), new DispatchOptions { CancellationToken = cts.Token })).ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithDispatchOptions_ReturnsResultAsync() {
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    var result = await dispatcher.LocalInvokeAsync<ScopeTestResult>(new ScopeTestCommand(Guid.NewGuid()), new DispatchOptions());
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Success).IsTrue();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithDispatchOptions_CancelledToken_ThrowsAsync() {
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    await Assert.That(async () => await dispatcher.LocalInvokeAsync<ScopeTestResult>(new ScopeTestCommand(Guid.NewGuid()), new DispatchOptions { CancellationToken = cts.Token })).ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_WithContextAndOptions_UnknownMessage_ThrowsReceptorNotFoundAsync() {
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    var context = MessageContext.Create(CorrelationId.New(), MessageId.New());
    await Assert.That(async () => await dispatcher.SendAsync("unknown", context, new DispatchOptions())).ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    object routed = Route.None();
    var context = MessageContext.Create(CorrelationId.New(), MessageId.New());
    await Assert.That(async () => await dispatcher.SendAsync(routed, context)).ThrowsExactly<ArgumentException>();
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_WithRoutedNone_WithOptions_ThrowsArgumentExceptionAsync() {
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    object routed = Route.None();
    var context = MessageContext.Create(CorrelationId.New(), MessageId.New());
    await Assert.That(async () => await dispatcher.SendAsync(routed, context, new DispatchOptions())).ThrowsExactly<ArgumentException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithRoutedNone_WithOptions_ThrowsArgumentExceptionAsync() {
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    object routed = Route.None();
    await Assert.That(async () => await dispatcher.LocalInvokeAsync<ScopeTestResult>(routed, new DispatchOptions())).ThrowsExactly<ArgumentException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_VoidWithRoutedNone_WithOptions_ThrowsArgumentExceptionAsync() {
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    object routed = Route.None();
    await Assert.That(async () => await dispatcher.LocalInvokeAsync(routed, new DispatchOptions())).ThrowsExactly<ArgumentException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithContext_ReturnsResultAsync() {
    _reset();
    var dispatcher = new ScopeTestDispatcher(_buildProvider());
    var context = MessageContext.Create(CorrelationId.New(), MessageId.New());
    var result = await dispatcher.LocalInvokeAsync<ScopeTestResult>(new ScopeTestCommand(Guid.NewGuid()), context);
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Success).IsTrue();
  }
}
