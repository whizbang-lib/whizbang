using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Common;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for Dispatcher invocation of synchronous receptors via ISyncReceptor interface.
/// These tests verify that the Dispatcher can invoke sync receptors and auto-cascade events.
/// </summary>
/// <docs>core-concepts/dispatcher#synchronous-invocation</docs>
[Category("Dispatcher")]
public class DispatcherSyncTests : DiagnosticTestBase {
  protected override DiagnosticCategory DiagnosticCategories => DiagnosticCategory.ReceptorDiscovery;

  // Test Messages - unique names to avoid conflicts with other tests
  public record DispatcherSyncCreateOrderCommand(Guid CustomerId, decimal Amount);
  public record DispatcherSyncOrderCreatedResult(Guid OrderId);
  public record DispatcherSyncOrderCreatedEvent([property: StreamKey] Guid OrderId, Guid CustomerId, decimal Amount) : IEvent;
  public record DispatcherSyncLogCommand(string Message);

  /// <summary>
  /// Tests that LocalInvokeAsync correctly invokes a sync receptor.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_SyncReceptor_InvokesSynchronouslyAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<ISyncReceptor<DispatcherSyncCreateOrderCommand, DispatcherSyncOrderCreatedResult>, SyncOrderReceptor>();
    var provider = services.BuildServiceProvider();

    var dispatcher = new TestSyncDispatcher(provider);
    var command = new DispatcherSyncCreateOrderCommand(Guid.NewGuid(), 100.00m);
    var context = MessageContext.Create(CorrelationId.New());

    // Act
    var result = await dispatcher.LocalInvokeAsync<DispatcherSyncOrderCreatedResult>(command, context);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
  }

  /// <summary>
  /// Tests that LocalInvokeAsync with sync receptor returns a pre-completed ValueTask.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_SyncReceptor_ReturnsCompletedValueTaskAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<ISyncReceptor<DispatcherSyncCreateOrderCommand, DispatcherSyncOrderCreatedResult>, SyncOrderReceptor>();
    var provider = services.BuildServiceProvider();

    var dispatcher = new TestSyncDispatcher(provider);
    var command = new DispatcherSyncCreateOrderCommand(Guid.NewGuid(), 100.00m);
    var context = MessageContext.Create(CorrelationId.New());

    // Act
    var task = dispatcher.LocalInvokeAsync<DispatcherSyncOrderCreatedResult>(command, context);

    // Assert - Task should already be completed (sync execution)
    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
  }

  /// <summary>
  /// Tests that sync receptor with tuple return auto-cascades events.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_SyncReceptorWithTuple_AutoCascadesEventsAsync() {
    // Arrange
    var publishedEvents = new List<object>();
    var services = new ServiceCollection();
    services.AddSingleton<ISyncReceptor<DispatcherSyncCreateOrderCommand, (DispatcherSyncOrderCreatedResult, DispatcherSyncOrderCreatedEvent)>, SyncTupleReceptor>();
    var provider = services.BuildServiceProvider();

    var dispatcher = new TestSyncDispatcher(provider, publishedEvents);
    var command = new DispatcherSyncCreateOrderCommand(Guid.NewGuid(), 100.00m);
    var context = MessageContext.Create(CorrelationId.New());

    // Act
    var (result, @event) = await dispatcher.LocalInvokeAsync<(DispatcherSyncOrderCreatedResult, DispatcherSyncOrderCreatedEvent)>(command, context);

    // Assert - Result returned
    await Assert.That(result).IsNotNull();
    await Assert.That(@event).IsNotNull();

    // Assert - Event was auto-published (auto-cascade)
    await Assert.That(publishedEvents).Count().IsEqualTo(1);
    await Assert.That(publishedEvents[0]).IsTypeOf<DispatcherSyncOrderCreatedEvent>();
  }

  /// <summary>
  /// Tests that when both async and sync receptors exist, async takes precedence.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_BothSyncAndAsync_PrefersAsyncAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IReceptor<DispatcherSyncCreateOrderCommand, DispatcherSyncOrderCreatedResult>, AsyncOrderReceptor>();
    services.AddSingleton<ISyncReceptor<DispatcherSyncCreateOrderCommand, DispatcherSyncOrderCreatedResult>, SyncOrderReceptor>();
    var provider = services.BuildServiceProvider();

    var dispatcher = new TestSyncDispatcher(provider);
    var command = new DispatcherSyncCreateOrderCommand(Guid.NewGuid(), 100.00m);
    var context = MessageContext.Create(CorrelationId.New());

    // Act
    var task = dispatcher.LocalInvokeAsync<DispatcherSyncOrderCreatedResult>(command, context);

    // Assert - Should NOT be immediately completed (async receptor used)
    // Note: This test verifies the async receptor was chosen by checking it's not pre-completed
    await Assert.That(task.IsCompletedSuccessfully).IsFalse();

    var result = await task;
    await Assert.That(result).IsNotNull();
  }

  /// <summary>
  /// Tests that LocalInvokeAsync with a typed result works with sync receptors.
  /// Note: SendAsync is for transport/outbox dispatch, not local invocation.
  /// Sync receptors are designed for local invocation via LocalInvokeAsync.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_TypedResult_SyncReceptor_ReturnsResultAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<ISyncReceptor<DispatcherSyncCreateOrderCommand, DispatcherSyncOrderCreatedResult>, SyncOrderReceptor>();
    var provider = services.BuildServiceProvider();

    var dispatcher = new TestSyncDispatcher(provider);
    var command = new DispatcherSyncCreateOrderCommand(Guid.NewGuid(), 100.00m);
    var context = MessageContext.Create(CorrelationId.New());

    // Act
    var result = await dispatcher.LocalInvokeAsync<DispatcherSyncOrderCreatedResult>(command, context);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
  }

  /// <summary>
  /// Tests that void sync receptors can be invoked.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_VoidSyncReceptor_ExecutesSynchronouslyAsync() {
    // Arrange
    var executed = false;
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.ISyncReceptor<DispatcherSyncLogCommand>>(new VoidSyncLogReceptor(() => executed = true));
    var provider = services.BuildServiceProvider();

    var dispatcher = new TestSyncDispatcher(provider);
    var command = new DispatcherSyncLogCommand("Test message");
    var context = MessageContext.Create(CorrelationId.New());

    // Act
    await dispatcher.LocalInvokeAsync(command, context);

    // Assert
    await Assert.That(executed).IsTrue();
  }

  // Test receptor implementations

  public class SyncOrderReceptor : ISyncReceptor<DispatcherSyncCreateOrderCommand, DispatcherSyncOrderCreatedResult> {
    public DispatcherSyncOrderCreatedResult Handle(DispatcherSyncCreateOrderCommand message) {
      return new DispatcherSyncOrderCreatedResult(Guid.NewGuid());
    }
  }

  public class SyncTupleReceptor : ISyncReceptor<DispatcherSyncCreateOrderCommand, (DispatcherSyncOrderCreatedResult, DispatcherSyncOrderCreatedEvent)> {
    public (DispatcherSyncOrderCreatedResult, DispatcherSyncOrderCreatedEvent) Handle(DispatcherSyncCreateOrderCommand message) {
      var orderId = Guid.NewGuid();
      return (
          new DispatcherSyncOrderCreatedResult(orderId),
          new DispatcherSyncOrderCreatedEvent(orderId, message.CustomerId, message.Amount)
      );
    }
  }

  public class AsyncOrderReceptor : IReceptor<DispatcherSyncCreateOrderCommand, DispatcherSyncOrderCreatedResult> {
    public async ValueTask<DispatcherSyncOrderCreatedResult> HandleAsync(DispatcherSyncCreateOrderCommand message, CancellationToken cancellationToken = default) {
      await Task.Delay(10, cancellationToken); // Ensure async
      return new DispatcherSyncOrderCreatedResult(Guid.NewGuid());
    }
  }

  public class VoidSyncLogReceptor : ISyncReceptor<DispatcherSyncLogCommand> {
    private readonly Action _onExecute;

    public VoidSyncLogReceptor(Action onExecute) {
      _onExecute = onExecute;
    }

    public void Handle(DispatcherSyncLogCommand message) {
      _onExecute();
    }
  }

  /// <summary>
  /// Test dispatcher that supports sync receptor invocation.
  /// This will fail until we implement GetSyncReceptorInvoker in the base Dispatcher.
  /// </summary>
  public class TestSyncDispatcher : Core.Dispatcher {
    private readonly List<object>? _publishedEvents;

    public TestSyncDispatcher(IServiceProvider serviceProvider, List<object>? publishedEvents = null)
        : base(serviceProvider, new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null)) {
      _publishedEvents = publishedEvents;
    }

    // These abstract methods need to be implemented for the test dispatcher
    // They will delegate to the generated code patterns

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      // Check for async receptor
      var receptorType = typeof(IReceptor<,>).MakeGenericType(messageType, typeof(TResult));
      var receptor = ServiceProvider.GetService(receptorType);

      if (receptor == null) {
        return null;
      }

      // Return invoker delegate
      return async msg => {
        var method = receptorType.GetMethod("HandleAsync");
        var task = (ValueTask<TResult>)method!.Invoke(receptor, [msg, CancellationToken.None])!;
        return await task;
      };
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      return null;
    }

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) {
      return null!; // Tests don't use event publishing via this path
    }

    protected override Func<object, Task>? GetUntypedReceptorPublisher(Type eventType) {
      if (_publishedEvents != null) {
        return evt => {
          _publishedEvents.Add(evt);
          return Task.CompletedTask;
        };
      }
      return null;
    }

    // Sync receptor support - implemented for testing
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) {
      var receptorType = typeof(ISyncReceptor<,>).MakeGenericType(messageType, typeof(TResult));
      var receptor = ServiceProvider.GetService(receptorType);

      if (receptor == null) {
        return null;
      }

      return msg => {
        var method = receptorType.GetMethod("Handle");
        return (TResult)method!.Invoke(receptor, [msg])!;
      };
    }

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) {
      // Use reflection to build the correct generic type for void sync receptor
      // ISyncReceptor<TMessage> has arity 1, different from ISyncReceptor<TMessage, TResponse> which has arity 2
      var voidSyncInterface = typeof(Whizbang.Core.ISyncReceptor<DispatcherSyncLogCommand>).GetGenericTypeDefinition();
      var receptorType = voidSyncInterface.MakeGenericType(messageType);
      var receptor = ServiceProvider.GetService(receptorType);

      if (receptor == null) {
        return null;
      }

      return msg => {
        var method = receptorType.GetMethod("Handle");
        method!.Invoke(receptor, [msg]);
      };
    }
  }
}
