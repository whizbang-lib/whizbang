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
/// Tests covering PublishAsync, SendAsync with DispatchOptions, SendManyAsync, and LocalInvokeManyAsync paths.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherCoveragePublishTests {

  [DefaultRouting(DispatchMode.Local)]
  public record TestEvent([property: StreamId] Guid OrderId) : IEvent;
  public record TestCommand(Guid OrderId);
  public record TestResult(Guid OrderId, bool Success);

  private static readonly List<object> _publishedEvents = [];
  private static readonly Lock _lock = new();
  private static void _trackEvent(object evt) { lock (_lock) { _publishedEvents.Add(evt); } }
  private static void _reset() { lock (_lock) { _publishedEvents.Clear(); } }
  private static int _snapshotCount() { lock (_lock) { return _publishedEvents.Count; } }

  private sealed class PublishTestDispatcher(IServiceProvider sp) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null)) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      if (messageType == typeof(TestCommand) && typeof(TResult) == typeof(TestResult)) {
        return msg => { var cmd = (TestCommand)msg; return ValueTask.FromResult((TResult)(object)new TestResult(cmd.OrderId, true)); };
      }

      return null;
    }
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => evt => { _trackEvent(evt!); return Task.CompletedTask; };
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => (evt, envelope, ct) => { _trackEvent(evt); return Task.CompletedTask; };
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchMode? GetReceptorDefaultRouting(Type messageType) => null;
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
  public async Task PublishAsync_WithEvent_ReturnsDeliveryReceiptAsync() {
    _reset();
    var dispatcher = new PublishTestDispatcher(_buildProvider());
    var receipt = await dispatcher.PublishAsync(new TestEvent(Guid.NewGuid()));
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(receipt.Destination).Contains("TestEvent");
  }

  [Test]
  [NotInParallel]
  public async Task PublishAsync_WithEvent_InvokesLocalHandlersAsync() {
    _reset();
    var dispatcher = new PublishTestDispatcher(_buildProvider());
    await dispatcher.PublishAsync(new TestEvent(Guid.NewGuid()));
    await Assert.That(_snapshotCount()).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  [NotInParallel]
  public async Task PublishAsync_WithNullEvent_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = new PublishTestDispatcher(_buildProvider());
    await Assert.That(async () => await dispatcher.PublishAsync<TestEvent>(null!)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  [NotInParallel]
  public async Task PublishAsync_WithDispatchOptions_ReturnsDeliveryReceiptAsync() {
    _reset();
    var dispatcher = new PublishTestDispatcher(_buildProvider());
    var receipt = await dispatcher.PublishAsync(new TestEvent(Guid.NewGuid()), new DispatchOptions());
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  [NotInParallel]
  public async Task PublishAsync_WithCancelledToken_ThrowsOperationCancelledExceptionAsync() {
    var dispatcher = new PublishTestDispatcher(_buildProvider());
    var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    await Assert.That(async () => await dispatcher.PublishAsync(new TestEvent(Guid.NewGuid()), new DispatchOptions { CancellationToken = cts.Token })).ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  [NotInParallel]
  public async Task PublishAsync_WithNullEvent_WithDispatchOptions_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = new PublishTestDispatcher(_buildProvider());
    await Assert.That(async () => await dispatcher.PublishAsync<TestEvent>(null!, new DispatchOptions())).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_WithDispatchOptions_Generic_ThrowsReceptorNotFoundAsync() {
    var dispatcher = new PublishTestDispatcher(_buildProvider());
    await Assert.That(async () => await dispatcher.SendAsync(new TestCommand(Guid.NewGuid()), new DispatchOptions())).ThrowsExactly<ReceptorNotFoundException>();
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_WithDispatchOptions_CancelledToken_ThrowsAsync() {
    var dispatcher = new PublishTestDispatcher(_buildProvider());
    var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    await Assert.That(async () => await dispatcher.SendAsync(new TestCommand(Guid.NewGuid()), new DispatchOptions { CancellationToken = cts.Token })).ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_ObjectOverload_WithDispatchOptions_CancelledToken_ThrowsAsync() {
    var dispatcher = new PublishTestDispatcher(_buildProvider());
    object command = new TestCommand(Guid.NewGuid());
    var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    await Assert.That(async () => await dispatcher.SendAsync(command, new DispatchOptions { CancellationToken = cts.Token })).ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeManyAsync_WithMultipleMessages_ReturnsAllResultsAsync() {
    var dispatcher = new PublishTestDispatcher(_buildProvider());
    var commands = new object[] { new TestCommand(Guid.NewGuid()), new TestCommand(Guid.NewGuid()) };
    var results = await dispatcher.LocalInvokeManyAsync<TestResult>(commands);
    var resultList = results.ToList();
    await Assert.That(resultList.Count).IsEqualTo(2);
    await Assert.That(resultList[0].Success).IsTrue();
    await Assert.That(resultList[1].Success).IsTrue();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeManyAsync_WithNullMessages_ThrowsArgumentNullExceptionAsync() {
    var dispatcher = new PublishTestDispatcher(_buildProvider());
    await Assert.That(async () => await dispatcher.LocalInvokeManyAsync<TestResult>(null!)).ThrowsExactly<ArgumentNullException>();
  }
}
