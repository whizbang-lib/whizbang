using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Increment 6, the write seam: a dispatch needs the event store and the outbox, which Migrate
/// provides — so <see cref="Core.Dispatcher"/> refuses while the schema gate is closed, and every
/// dispatch surface inherits the one check. A fixture without a gate stays ungated, exactly as
/// every existing test in this suite demonstrates by still passing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Startup")]
public class DispatcherSchemaGateTests {

  private sealed record PlaceOrder(Guid OrderId);

  private sealed class _seamDispatcher(IServiceProvider sp) : Core.Dispatcher(
      sp, new ServiceInstanceProvider(configuration: null)) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType)
      => msg => ValueTask.FromResult<TResult>(default!);
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType)
      => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType)
      => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType)
      => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType)
      => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType)
      => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType)
      => msg => ValueTask.FromResult<object?>(null);
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType)
      => DispatchModes.LocalDispatch;
  }

  [Test]
  public async Task Send_WithTheGateClosed_RefusesInsteadOfWritingToAMissingSchemaAsync() {
    var gate = new SchemaReadyGate();   // NOT ready — migrations in flight
    var services = new ServiceCollection();
    services.AddSingleton<ISchemaReadyGate>(gate);
    await using var sp = services.BuildServiceProvider();
    var dispatcher = new _seamDispatcher(sp);

    await Assert.ThrowsAsync<WhizbangNotReadyException>(async () =>
      await dispatcher.SendAsync(new PlaceOrder(Guid.NewGuid())));
  }

  [Test]
  public async Task Publish_WithTheGateClosed_RefusesTooAsync() {
    var gate = new SchemaReadyGate();
    var services = new ServiceCollection();
    services.AddSingleton<ISchemaReadyGate>(gate);
    await using var sp = services.BuildServiceProvider();
    var dispatcher = new _seamDispatcher(sp);

    await Assert.ThrowsAsync<WhizbangNotReadyException>(async () =>
      await dispatcher.PublishAsync(new PlaceOrder(Guid.NewGuid())));
  }

  [Test]
  public async Task Send_OnceTheGateOpens_ProceedsAsync() {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var services = new ServiceCollection();
    services.AddSingleton<ISchemaReadyGate>(gate);
    await using var sp = services.BuildServiceProvider();
    var dispatcher = new _seamDispatcher(sp);

    var receipt = await dispatcher.SendAsync(new PlaceOrder(Guid.NewGuid()));

    await Assert.That(receipt).IsNotNull()
      .Because("the seam holds during Migrate and only during Migrate — writes resume when it completes");
  }

  [Test]
  public async Task Send_WithNoGateRegistered_StaysUngatedAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var dispatcher = new _seamDispatcher(sp);

    var receipt = await dispatcher.SendAsync(new PlaceOrder(Guid.NewGuid()));

    await Assert.That(receipt).IsNotNull()
      .Because("fixtures and hosts without the worker pipeline behave exactly as before");
  }
}
