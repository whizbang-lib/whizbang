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
/// Tests covering generic typed SendAsync, LocalInvokeAsync, SendManyAsync overloads.
/// Also covers edge cases like IRouted unwrapping in Send/LocalInvoke paths.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Dispatcher")]
[Category("Coverage")]
public class DispatcherCoverageGenericTests {

  public record GenericCommand(Guid OrderId);
  public record GenericResult(Guid OrderId, bool Success);

  public record GenericCommand2(Guid Id);
  public record GenericResult2(Guid Id, string Status);

  [DefaultRouting(DispatchModes.Local)]
  public record GenericEvent([property: StreamId] Guid OrderId) : IEvent;

  private sealed class GenericTestDispatcher(IServiceProvider sp) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null)) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      if (messageType == typeof(GenericCommand) && typeof(TResult) == typeof(GenericResult)) {
        return msg => {
          var cmd = (GenericCommand)msg;
          return ValueTask.FromResult((TResult)(object)new GenericResult(cmd.OrderId, true));
        };
      }
      if (messageType == typeof(GenericCommand) && typeof(TResult) == typeof(object)) {
        return msg => {
          var cmd = (GenericCommand)msg;
          return ValueTask.FromResult((TResult)(object)new GenericResult(cmd.OrderId, true));
        };
      }
      if (messageType == typeof(GenericCommand2) && typeof(TResult) == typeof(GenericResult2)) {
        return msg => {
          var cmd = (GenericCommand2)msg;
          return ValueTask.FromResult((TResult)(object)new GenericResult2(cmd.Id, "done"));
        };
      }
      if (messageType == typeof(GenericCommand2) && typeof(TResult) == typeof(object)) {
        return msg => {
          var cmd = (GenericCommand2)msg;
          return ValueTask.FromResult((TResult)(object)new GenericResult2(cmd.Id, "done"));
        };
      }
      return null;
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
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

    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) {
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

  [Test]
  [NotInParallel]
  public async Task SendAsync_Generic_ReturnsDeliveryReceiptAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    var command = new GenericCommand(Guid.NewGuid());

    var receipt = await dispatcher.SendAsync<GenericCommand>(command);

    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(receipt.MessageId.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_Generic_WithDispatchOptions_ReturnsReceiptAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    var command = new GenericCommand(Guid.NewGuid());
    var options = new DispatchOptions();

    var receipt = await dispatcher.SendAsync<GenericCommand>(command, options);

    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_Generic_CancelledToken_ThrowsAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    var command = new GenericCommand(Guid.NewGuid());
    var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    await Assert.That(async () => await dispatcher.SendAsync<GenericCommand>(command, options))
        .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_GenericTyped_ReturnsResultAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    var command = new GenericCommand(Guid.NewGuid());

    var result = await dispatcher.LocalInvokeAsync<GenericCommand, GenericResult>(command);

    await Assert.That(result).IsNotNull();
    await Assert.That(result.Success).IsTrue();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_GenericTyped_WithContext_ReturnsResultAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    var command = new GenericCommand(Guid.NewGuid());
    var context = MessageContext.Create(CorrelationId.New(), MessageId.New());

    var result = await dispatcher.LocalInvokeAsync<GenericCommand, GenericResult>(command, context);

    await Assert.That(result).IsNotNull();
    await Assert.That(result.Success).IsTrue();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_GenericTyped_RoutedNone_ThrowsAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    object routed = Route.None();
    var context = MessageContext.Create(CorrelationId.New(), MessageId.New());

    await Assert.That(async () => await dispatcher.LocalInvokeAsync<GenericResult>(routed, context))
        .ThrowsExactly<ArgumentException>();
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_Object_NoContext_ReturnsReceiptAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    object command = new GenericCommand(Guid.NewGuid());

    var receipt = await dispatcher.SendAsync(command);

    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_Object_NoContext_ReturnsResultAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    object command = new GenericCommand(Guid.NewGuid());

    var result = await dispatcher.LocalInvokeAsync<GenericResult>(command);

    await Assert.That(result).IsNotNull();
    await Assert.That(result.Success).IsTrue();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeManyAsync_Generic_WithMultipleCommands_ReturnsAllResultsAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    var commands = new object[] {
      new GenericCommand(Guid.NewGuid()),
      new GenericCommand(Guid.NewGuid()),
      new GenericCommand(Guid.NewGuid())
    };

    var results = await dispatcher.LocalInvokeManyAsync<GenericResult>(commands);

    var resultList = results.ToList();
    await Assert.That(resultList.Count).IsEqualTo(3);
    foreach (var result in resultList) {
      await Assert.That(result.Success).IsTrue();
    }
  }

  [Test]
  [NotInParallel]
  public async Task SendManyAsync_Generic_WithNullMessages_ThrowsArgumentNullExceptionAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);

    await Assert.That(async () => await dispatcher.SendManyAsync<GenericCommand>(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeManyAsync_Object_WithMultipleCommands_ReturnsAllResultsAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    var commands = new object[] {
      new GenericCommand(Guid.NewGuid()),
      new GenericCommand(Guid.NewGuid())
    };

    var results = await dispatcher.LocalInvokeManyAsync<GenericResult>(commands);

    var resultList = results.ToList();
    await Assert.That(resultList.Count).IsEqualTo(2);
  }

  [Test]
  [NotInParallel]
  public async Task SendManyAsync_Object_WithNullMessages_ThrowsArgumentNullExceptionAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);

    await Assert.That(async () => await dispatcher.SendManyAsync((IEnumerable<object>)null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeManyAsync_EmptyList_ReturnsEmptyResultsAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    var commands = Array.Empty<object>();

    var results = await dispatcher.LocalInvokeManyAsync<GenericResult>(commands);

    var resultList = results.ToList();
    await Assert.That(resultList.Count).IsEqualTo(0);
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_Generic_WithRoutedNone_ThrowsArgumentExceptionAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericTestDispatcher(provider);
    object routed = Route.None();
    var context = MessageContext.Create(CorrelationId.New(), MessageId.New());

    await Assert.That(async () => await dispatcher.SendAsync(routed, context))
        .ThrowsExactly<ArgumentException>();
  }

  private sealed class GenericSyncDispatcher(IServiceProvider sp) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null)) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
      return null;
    }

    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) {
      return null;
    }

    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) {
      return evt => Task.CompletedTask;
    }

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) {
      return null;
    }

    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) {
      if (messageType == typeof(GenericCommand) && typeof(TResult) == typeof(GenericResult)) {
        return msg => {
          var cmd = (GenericCommand)msg;
          return (TResult)(object)new GenericResult(cmd.OrderId, true);
        };
      }
      return null;
    }

    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) {
      return null;
    }

    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) {
      return null;
    }

    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) {
      return null;
    }
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_SyncFallback_ReturnsResultAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericSyncDispatcher(provider);
    var command = new GenericCommand(Guid.NewGuid());

    var result = await dispatcher.LocalInvokeAsync<GenericResult>(command);

    await Assert.That(result).IsNotNull();
    await Assert.That(result.Success).IsTrue();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithOptions_SyncFallback_ReturnsResultAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericSyncDispatcher(provider);
    var command = new GenericCommand(Guid.NewGuid());
    var options = new DispatchOptions();

    var result = await dispatcher.LocalInvokeAsync<GenericResult>(command, options);

    await Assert.That(result).IsNotNull();
    await Assert.That(result.Success).IsTrue();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithOptions_NoReceptor_ThrowsAsync() {
    var provider = _buildProvider();
    var dispatcher = new GenericSyncDispatcher(provider);
    const string unknownMsg = "unknown";
    var options = new DispatchOptions();

    await Assert.That(async () => await dispatcher.LocalInvokeAsync<string>(unknownMsg, options))
        .ThrowsExactly<ReceptorNotFoundException>();
  }
}
