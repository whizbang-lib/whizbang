using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the MessageRegistryGenerator source generator.
/// </summary>
public class MessageRegistryGeneratorTests {

  [Test]
  public async Task MessageRegistryGenerator_EmptyCompilation_GeneratesEmptyRegistryAsync() {
    // Arrange
    var source = @"
using System;

namespace TestNamespace {
  public class SomeClass {
    public void SomeMethod() { }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("\"\"messages\"\": [");
  }

  [Test]
  public async Task MessageRegistryGenerator_SingleCommand_DiscoversCommandAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = """";
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("CreateOrderCommand");
    await Assert.That(generatedSource!).Contains("\"\"isCommand\"\": true");
    await Assert.That(generatedSource!).Contains("\"\"isEvent\"\": false");
  }

  [Test]
  public async Task MessageRegistryGenerator_SingleEvent_DiscoversEventAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource!).Contains("\"\"isCommand\"\": false");
    await Assert.That(generatedSource!).Contains("\"\"isEvent\"\": true");
  }

  [Test]
  public async Task MessageRegistryGenerator_CommandWithDispatcher_DiscoversDispatcherAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = """";
  }

  public class OrderService {
    private IDispatcher _dispatcher;

    public OrderService(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task CreateOrderAsync(string orderId) {
      await _dispatcher.SendAsync(new CreateOrderCommand { OrderId = orderId });
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("CreateOrderCommand");
    await Assert.That(generatedSource!).Contains("\"\"dispatchers\"\":");
    await Assert.That(generatedSource!).Contains("OrderService");
    await Assert.That(generatedSource!).Contains("CreateOrderAsync");
  }

  [Test]
  public async Task MessageRegistryGenerator_CommandWithReceptor_DiscoversReceptorAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = """";
  }

  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public class CreateOrderReceptor : IReceptor<CreateOrderCommand, OrderCreatedEvent> {
    public async Task<OrderCreatedEvent> HandleAsync(
        CreateOrderCommand message,
        CancellationToken cancellationToken = default) {
      // Handle command
      return new OrderCreatedEvent { OrderId = message.OrderId };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("CreateOrderCommand");
    await Assert.That(generatedSource!).Contains("\"\"receptors\"\":");
    await Assert.That(generatedSource!).Contains("CreateOrderReceptor");
    await Assert.That(generatedSource!).Contains("HandleAsync");
  }

  [Test]
  public async Task MessageRegistryGenerator_EventWithPerspective_DiscoversPerspectiveAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public class OrderListPerspective : IPerspectiveOf<OrderCreatedEvent> {
    public async Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      // Update read model
      await Task.CompletedTask;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource!).Contains("\"\"perspectives\"\":");
    await Assert.That(generatedSource!).Contains("OrderListPerspective");
  }

  [Test]
  public async Task MessageRegistryGenerator_MultipleMessages_DiscoversAllAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = """";
  }

  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record CancelOrderCommand : ICommand {
    public string OrderId { get; init; } = """";
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("CreateOrderCommand");
    await Assert.That(generatedSource!).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource!).Contains("CancelOrderCommand");
  }

  [Test]
  public async Task MessageRegistryGenerator_GeneratedJson_ContainsFilePathsAndLineNumbersAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = """";
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("\"\"filePath\"\":");
    await Assert.That(generatedSource!).Contains("\"\"lineNumber\"\":");
  }

  [Test]
  public async Task MessageRegistryGenerator_PerspectiveWithMultipleEvents_DiscoversAllEventsAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderUpdatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public class OrderListPerspective :
      IPerspectiveOf<OrderCreatedEvent>,
      IPerspectiveOf<OrderUpdatedEvent> {

    public async Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
    }

    public async Task Update(OrderUpdatedEvent @event, CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource!).Contains("OrderUpdatedEvent");
    await Assert.That(generatedSource!).Contains("OrderListPerspective");
  }

  [Test]
  public async Task MessageRegistryGenerator_NoCompilationErrors_GeneratesValidCodeAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = """";
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    // Should have no diagnostics
    var diagnostics = result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    await Assert.That(diagnostics).IsEmpty();

    // Should generate exactly one file
    var generatedFiles = GeneratorTestHelper.GetAllGeneratedSources(result).ToList();
    await Assert.That(generatedFiles).HasCount().EqualTo(1);
    await Assert.That(generatedFiles[0].FileName).IsEqualTo("MessageRegistry.g.cs");
  }

  [Test]
  public async Task MessageRegistryGenerator_VoidReceptor_DiscoversReceptorAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record LogCommand : ICommand {
    public string Message { get; init; } = """";
  }

  public class LogReceptor : IReceptor<LogCommand> {
    public ValueTask HandleAsync(LogCommand message, CancellationToken ct = default) {
      return ValueTask.CompletedTask;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("LogCommand");
    await Assert.That(generatedSource!).Contains("\"\"receptors\"\":");
    await Assert.That(generatedSource!).Contains("LogReceptor");
    await Assert.That(generatedSource!).Contains("HandleAsync");
  }

  [Test]
  public async Task MessageRegistryGenerator_MixedReceptorTypes_DiscoversAllAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  // Command with regular receptor (returns result)
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = """";
  }

  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public class CreateOrderReceptor : IReceptor<CreateOrderCommand, OrderCreatedEvent> {
    public async Task<OrderCreatedEvent> HandleAsync(
        CreateOrderCommand message,
        CancellationToken ct = default) {
      return new OrderCreatedEvent { OrderId = message.OrderId };
    }
  }

  // Command with void receptor (returns nothing)
  public record LogCommand : ICommand {
    public string Message { get; init; } = """";
  }

  public class LogReceptor : IReceptor<LogCommand> {
    public ValueTask HandleAsync(LogCommand message, CancellationToken ct = default) {
      return ValueTask.CompletedTask;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Both commands should be discovered
    await Assert.That(generatedSource!).Contains("CreateOrderCommand");
    await Assert.That(generatedSource!).Contains("LogCommand");

    // Both receptors should be discovered
    await Assert.That(generatedSource!).Contains("CreateOrderReceptor");
    await Assert.That(generatedSource!).Contains("LogReceptor");

    // Both should be in receptors array
    var receptorOccurrences = System.Text.RegularExpressions.Regex.Matches(
        generatedSource!,
        "\"\"receptors\"\":");
    await Assert.That(receptorOccurrences.Count).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  public async Task MessageRegistryGenerator_MultipleVoidReceptors_DiscoversAllAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record LogCommand : ICommand {
    public string Message { get; init; } = """";
  }

  public record NotifyCommand : ICommand {
    public string UserId { get; init; } = """";
  }

  public record CacheUpdateCommand : ICommand {
    public string Key { get; init; } = """";
  }

  public class LogReceptor : IReceptor<LogCommand> {
    public ValueTask HandleAsync(LogCommand message, CancellationToken ct = default) {
      return ValueTask.CompletedTask;
    }
  }

  public class NotifyReceptor : IReceptor<NotifyCommand> {
    public ValueTask HandleAsync(NotifyCommand message, CancellationToken ct = default) {
      return ValueTask.CompletedTask;
    }
  }

  public class CacheUpdateReceptor : IReceptor<CacheUpdateCommand> {
    public ValueTask HandleAsync(CacheUpdateCommand message, CancellationToken ct = default) {
      return ValueTask.CompletedTask;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // All commands should be discovered
    await Assert.That(generatedSource!).Contains("LogCommand");
    await Assert.That(generatedSource!).Contains("NotifyCommand");
    await Assert.That(generatedSource!).Contains("CacheUpdateCommand");

    // All receptors should be discovered
    await Assert.That(generatedSource!).Contains("LogReceptor");
    await Assert.That(generatedSource!).Contains("NotifyReceptor");
    await Assert.That(generatedSource!).Contains("CacheUpdateReceptor");
  }

  [Test]
  public async Task MessageRegistryGenerator_PublishAsyncWithGeneric_DiscoversDispatcherAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public class OrderEventPublisher {
    private IDispatcher _dispatcher;

    public OrderEventPublisher(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task PublishOrderCreatedAsync(string orderId) {
      await _dispatcher.PublishAsync(new OrderCreatedEvent { OrderId = orderId });
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource!).Contains("\"\"dispatchers\"\":");
    await Assert.That(generatedSource!).Contains("OrderEventPublisher");
    await Assert.That(generatedSource!).Contains("PublishOrderCreatedAsync");
  }

  [Test]
  public async Task MessageRegistryGenerator_MultipleDispatchesInSameMethod_DiscoversAllAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = """";
  }

  public record SendEmailCommand : ICommand {
    public string Email { get; init; } = """";
  }

  public record LogCommand : ICommand {
    public string Message { get; init; } = """";
  }

  public class OrderWorkflow {
    private IDispatcher _dispatcher;

    public OrderWorkflow(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task ProcessOrderAsync(string orderId) {
      await _dispatcher.SendAsync(new CreateOrderCommand { OrderId = orderId });
      await _dispatcher.SendAsync(new SendEmailCommand { Email = ""test@test.com"" });
      await _dispatcher.SendAsync(new LogCommand { Message = ""Order processed"" });
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // All three commands should be discovered
    await Assert.That(generatedSource!).Contains("CreateOrderCommand");
    await Assert.That(generatedSource!).Contains("SendEmailCommand");
    await Assert.That(generatedSource!).Contains("LogCommand");

    // All three dispatches should be tracked
    var dispatcherOccurrences = System.Text.RegularExpressions.Regex.Matches(
        generatedSource!,
        "OrderWorkflow");
    await Assert.That(dispatcherOccurrences.Count).IsGreaterThanOrEqualTo(3);
  }

  [Test]
  public async Task MessageRegistryGenerator_ConditionalDispatch_DiscoversDispatcherAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record ProcessOrderCommand : ICommand {
    public string OrderId { get; init; } = """";
  }

  public class OrderService {
    private IDispatcher _dispatcher;

    public OrderService(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task ProcessAsync(string orderId, bool shouldProcess) {
      if (shouldProcess) {
        await _dispatcher.SendAsync(new ProcessOrderCommand { OrderId = orderId });
      }
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("ProcessOrderCommand");
    await Assert.That(generatedSource!).Contains("OrderService");
  }

  [Test]
  public async Task MessageRegistryGenerator_DispatcherVariableName_DiscoversDispatcherAsync() {
    // Arrange - Test with different variable names
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = """";
  }

  public class TestService {
    private IDispatcher dispatcher;

    public TestService(IDispatcher d) {
      dispatcher = d;
    }

    public async Task ExecuteAsync() {
      await dispatcher.SendAsync(new TestCommand { Value = ""test"" });
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("TestCommand");
    await Assert.That(generatedSource!).Contains("TestService");
  }
}
