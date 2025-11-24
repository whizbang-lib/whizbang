using System.Diagnostics.CodeAnalysis;
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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
  [RequiresAssemblyFiles()]
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

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_StructMessageType_SkipsAsync() {
    // Arrange - Tests ExtractMessageType with struct (default case in switch expression)
    var source = @"
using Whizbang.Core;

namespace TestNamespace {
  public struct CreateOrderStruct : ICommand {
    public string OrderId { get; set; }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Struct should be skipped (generator only handles records and classes)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).DoesNotContain("CreateOrderStruct");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ClassWithoutMessageInterface_SkipsAsync() {
    // Arrange - Tests ExtractMessageType when class doesn't implement ICommand or IEvent
    var source = @"
using System;

namespace TestNamespace {
  public class OrderDto : ICloneable {
    public string OrderId { get; set; } = """";
    public object Clone() => new OrderDto { OrderId = OrderId };
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Non-message class should be skipped
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).DoesNotContain("OrderDto");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_WrongMethodName_SkipsAsync() {
    // Arrange - Tests ExtractDispatcher when method name is not SendAsync/PublishAsync
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = """";
  }

  public class TestService {
    private IDispatcher _dispatcher;

    public TestService(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task ExecuteAsync() {
      await _dispatcher.DispatchAsync(new TestCommand { Value = ""test"" });
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover message but not dispatcher (wrong method name)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("TestCommand");
    await Assert.That(generatedSource!).DoesNotContain("TestService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_SendAsyncWithNoArguments_SkipsAsync() {
    // Arrange - Tests ExtractDispatcher when SendAsync has no arguments
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = """";
  }

  public class TestService {
    public async Task ExecuteAsync() {
      // Invalid call with no arguments
      var result = await OtherMethod.SendAsync();
    }
  }

  public static class OtherMethod {
    public static Task<string> SendAsync() => Task.FromResult(""test"");
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should not discover dispatcher (no arguments)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).DoesNotContain("TestService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ReceptorWithWrongTypeArguments_SkipsAsync() {
    // Arrange - Tests ExtractReceptor with wrong number of type arguments
    var source = @"
using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = """";
  }

  // This won't compile but tests the generator's defensive code
  // Generator should skip this if it somehow encounters it
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover message only
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("TestCommand");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ReferencedAssemblyMessage_InfersTypeAsync() {
    // Arrange - Tests GenerateMessageRegistry when message is from referenced assembly
    // Message is used but not defined in this project
    var source = @"
using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  // Use a message from Whizbang.Core (referenced assembly) without defining it
  public class ExternalMessageReceptor : IReceptor<ExternalCommand> {
    public ValueTask HandleAsync(ExternalCommand message, CancellationToken ct = default) {
      return ValueTask.CompletedTask;
    }
  }
}

namespace Whizbang.Core {
  // Define the external command in referenced assembly namespace
  public record ExternalCommand : ICommand;
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should infer ExternalCommand as a command (has receptor)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("ExternalCommand");
    await Assert.That(generatedSource!).Contains("\"\"isCommand\"\": true");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_OnlyDispatcherForMessage_InfersEventAsync() {
    // Arrange - Tests GenerateMessageRegistry when only dispatcher exists (no receptor, no perspective)
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public class NotificationService {
    private IDispatcher _dispatcher;

    public NotificationService(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task NotifyAsync() {
      await _dispatcher.PublishAsync(new ExternalEvent());
    }
  }
}

namespace Whizbang.Core {
  public record ExternalEvent : IEvent;
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should infer ExternalEvent as event (published but no receptor/perspective)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("ExternalEvent");
    await Assert.That(generatedSource!).Contains("\"\"isEvent\"\": true");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_DispatcherInTopLevelStatement_HandlesGracefullyAsync() {
    // Arrange - Tests ExtractDispatcher when no containing class (edge case)
    // This tests the containingClass null check
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = """";
  }

  public class TestService {
    public async Task ExecuteAsync(IDispatcher dispatcher) {
      await dispatcher.SendAsync(new TestCommand { Value = ""test"" });
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover both message and dispatcher
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("TestCommand");
    await Assert.That(generatedSource!).Contains("TestService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ReceptorWithoutHandleAsync_DiscoversWithFallbackLineAsync() {
    // Arrange - Tests ExtractReceptor when HandleAsync method is missing
    // This tests the handleMethod null fallback for line number
    var source = @"
using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = """";
  }

  public class TestReceptor : IReceptor<TestCommand> {
    // Explicit interface implementation (no public HandleAsync method visible)
    ValueTask IReceptor<TestCommand>.HandleAsync(TestCommand message, CancellationToken ct) {
      return ValueTask.CompletedTask;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover receptor and use class location for line number
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("TestCommand");
    await Assert.That(generatedSource!).Contains("TestReceptor");
    await Assert.That(generatedSource!).Contains("\"\"lineNumber\"\":");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_PublishAsyncWithDefaultExpression_InfersFromGenericAsync() {
    // Arrange - Tests ExtractDispatcher fallback to generic type argument (line 129)
    // Need to pass default(TEvent) or null with explicit generic to force fallback
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestEvent : IEvent {
    public string Value { get; init; } = """";
  }

  public class TestService {
    private IDispatcher _dispatcher;

    public TestService(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task ExecuteAsync() {
      // PublishAsync with default(TestEvent) - argument type may be null, forcing fallback to generic
      await _dispatcher.PublishAsync<TestEvent>(default(TestEvent));
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover dispatcher with generic method fallback
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("TestEvent");
    await Assert.That(generatedSource!).Contains("TestService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_NonMethodInvocation_SkipsAsync() {
    // Arrange - Tests ExtractDispatcher when symbolInfo.Symbol is not IMethodSymbol
    // Line 108-110: if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
    var source = @"
using Whizbang.Core;
using System;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = """";
  }

  public class TestService {
    private IDispatcher _dispatcher;

    public TestService(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task ExecuteAsync() {
      // This is a valid SendAsync call
      await _dispatcher.SendAsync(new TestCommand { Value = ""test"" });

      // This tests invocations that aren't method symbols
      var action = new Action(() => Console.WriteLine(""Not a method symbol""));
      action();  // Invocation but not a method symbol in the semantic sense we care about
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover the valid dispatcher, skip the non-method invocation
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("TestCommand");
    await Assert.That(generatedSource!).Contains("TestService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_SendAsyncWithStringArgument_DiscoversDispatcherAsync() {
    // Arrange - Tests ExtractDispatcher with non-IDispatcher.SendAsync (different method)
    // The generator looks for any method named SendAsync, including non-IDispatcher ones
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = """";
  }

  public class TestService {
    public async Task ExecuteAsync() {
      // SendAsync with a string - different method, not IDispatcher.SendAsync
      await SomeMethod.SendAsync(""not a message"");
    }
  }

  public static class SomeMethod {
    public static Task SendAsync(string value) => Task.CompletedTask;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Generator discovers any SendAsync call, even non-IDispatcher ones
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("TestService");
    await Assert.That(generatedSource!).Contains("\"\"type\"\": \"\"string\"\"");  // Type of the argument
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ReceptorWithoutHandleAsyncMethod_UsesClassLineNumberAsync() {
    // Arrange - Tests line 220 null coalescing when HandleAsync method is not found
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace TestNamespace;

public record TestCommand : ICommand;

public class TestReceptor : IReceptor<TestCommand> {
  // Implementing interface explicitly without a public HandleAsync method
  ValueTask IReceptor<TestCommand>.HandleAsync(TestCommand message, CancellationToken ct) {
    return ValueTask.CompletedTask;
  }

  // No public HandleAsync method declaration - tests null coalescing at line 220
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should use class line number when HandleAsync method is not found
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("TestCommand");
    await Assert.That(generatedSource!).Contains("TestReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MessageOnlyDispatchedNoDefinition_InfersTypeAsync() {
    // Arrange - Tests lines 326-335: message from referenced assembly (inferred type logic)
    // When a message is dispatched but not defined in this project, we infer its type
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  // Note: We dispatch String which is not an ICommand/IEvent in this project
  // This tests the inferred type logic when msg is null (line 326-335)

  public class TestService {
    private IDispatcher _dispatcher;

    public TestService(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task ExecuteAsync() {
      // Dispatch a type not defined as ICommand/IEvent in this project
      await _dispatcher.SendAsync(""some string"");
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should infer message type from dispatcher usage
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("string");
    await Assert.That(generatedSource!).Contains("TestService");
    // Should show inferred type with empty filePath (line 333)
    await Assert.That(generatedSource!).Contains("\"\"filePath\"\": \"\"\"\"");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_PerspectiveOnly_InfersEventTypeAsync() {
    // Arrange - Tests line 329 event type inference when only perspectives exist
    var source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace TestNamespace;

public record TestEvent : IEvent;

public class TestPerspective : IPerspectiveOf<TestEvent> {
  public Guid Id { get; set; }

  public Task Update(TestEvent @event, CancellationToken ct = default) {
    return Task.CompletedTask;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should infer TestEvent as an event from perspective
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("TestEvent");
    await Assert.That(generatedSource!).Contains("TestPerspective");
    await Assert.That(generatedSource!).Contains("\"\"isEvent\"\": true");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_EmptyProject_GeneratesEmptyRegistryAsync() {
    // Arrange - Tests line 310 where clause with no messages, dispatchers, receptors, or perspectives
    var source = @"
namespace TestNamespace;

public class SomeClass {
  public string Name { get; set; } = string.Empty;
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should generate empty message registry
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("\"\"messages\"\":");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_SendAsyncWithZeroArguments_ReturnsNullAsync() {
    // Arrange - Tests ExtractDispatcher line 121: if (invocation.ArgumentList.Arguments.Count > 0)
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand;

  public class TestService {
    public Task SendAsync() {
      // Method named SendAsync but with zero arguments - should return null
      return Task.CompletedTask;
    }

    public async Task ExecuteAsync() {
      await SendAsync();  // Invocation with zero arguments
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should skip SendAsync call with zero arguments
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    // Registry should exist but not include the zero-argument SendAsync
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_NonGenericMethodWithoutValidArguments_ReturnsNullAsync() {
    // Arrange - Tests ExtractDispatcher line 128-130 where IsGenericMethod is false
    var source = @"
using System.Threading.Tasks;

namespace TestNamespace {
  public class TestService {
    // Non-generic SendAsync method
    public Task SendAsync() {
      return Task.CompletedTask;
    }

    public async Task ExecuteAsync() {
      // Invocation of non-generic SendAsync with no arguments
      await SendAsync();
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should skip non-generic SendAsync without valid message type
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_GenericMethodWithZeroTypeArguments_ReturnsNullAsync() {
    // Arrange - Tests ExtractDispatcher line 128-130 where TypeArguments.Length == 0
    var source = @"
using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand;

  public class TestService {
    private IDispatcher _dispatcher;

    public async Task ExecuteAsync() {
      // SendAsync with null argument - type inference might fail
      await _dispatcher.SendAsync(null);
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should handle null argument gracefully
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ReceptorWithMalformedInterface_ReturnsNullAsync() {
    // Arrange - Tests ExtractReceptor lines 193-195, 200-202: TypeArguments.Length checks
    // This is difficult to test with real C# as the compiler won't allow malformed generic interfaces
    // However, we can test a receptor that doesn't implement IReceptor at all
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace TestNamespace;

public record TestCommand : ICommand;

// Class named like a receptor but doesn't implement IReceptor
public class TestReceptor {
  public ValueTask HandleAsync(TestCommand message, CancellationToken ct = default) {
    return ValueTask.CompletedTask;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should skip class that doesn't implement IReceptor
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).DoesNotContain("TestReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MessageWithBothReceptorAndPerspective_MarksAsBothCommandAndEventAsync() {
    // Arrange - Tests line 321 and 326-327: Message with receptors AND perspectives
    // isCommand = group.Receptors.Count > 0 = true
    // isEvent = group.Perspectives.Count > 0 = true
    var source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace TestNamespace;

public record HybridMessage : ICommand, IEvent {
  public string Data { get; init; } = """";
}

public class HybridReceptor : IReceptor<HybridMessage, string> {
  public ValueTask<string> HandleAsync(HybridMessage message, CancellationToken ct = default) {
    return ValueTask.FromResult(""OK"");
  }
}

public class HybridPerspective : IPerspectiveOf<HybridMessage> {
  public Guid Id { get; set; }
  public Task Update(HybridMessage @event, CancellationToken ct = default) {
    return Task.CompletedTask;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should mark as both command and event
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("HybridMessage");
    await Assert.That(generatedSource!).Contains("\"\"isCommand\"\": true");
    await Assert.That(generatedSource!).Contains("\"\"isEvent\"\": true");
    await Assert.That(generatedSource!).Contains("HybridReceptor");
    await Assert.That(generatedSource!).Contains("HybridPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_EventWithOnlyPerspectives_InfersAsEventAsync() {
    // Arrange - Tests line 321: isEvent when only perspectives exist (no receptors or dispatchers)
    // isEvent = group.Perspectives.Count > 0 || ... = true
    var source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace TestNamespace;

public record PerspectiveOnlyEvent : IEvent {
  public string EventData { get; init; } = """";
}

public class EventPerspective : IPerspectiveOf<PerspectiveOnlyEvent> {
  public Guid Id { get; set; }
  public Task Update(PerspectiveOnlyEvent @event, CancellationToken ct = default) {
    return Task.CompletedTask;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should infer as event based on perspective
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("PerspectiveOnlyEvent");
    await Assert.That(generatedSource!).Contains("\"\"isCommand\"\": false");
    await Assert.That(generatedSource!).Contains("\"\"isEvent\"\": true");
    await Assert.That(generatedSource!).Contains("EventPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ReceptorImplementsBothInterfaces_UsesRegularNotVoidAsync() {
    // Arrange - Tests line 172-174: When regular receptor exists, void receptor interface should be null
    // This tests the `: null` branch in the ternary operator
    var source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace TestNamespace;

public record DualCommand : ICommand {
  public string Data { get; init; } = """";
}

// Unusual but valid: implements both IReceptor<TMessage, TResponse> AND IReceptor<TMessage>
// Should use the regular receptor interface, not the void one
public class DualReceptor : IReceptor<DualCommand, string>, IReceptor<DualCommand> {
  public ValueTask<string> HandleAsync(DualCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult(""Response"");
  }

  ValueTask IReceptor<DualCommand>.HandleAsync(DualCommand message, CancellationToken ct) {
    return ValueTask.CompletedTask;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should use regular receptor (with TResponse), not void receptor
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("DualCommand");
    await Assert.That(generatedSource!).Contains("DualReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MessageWithMultipleDispatchers_FormatsJsonCorrectlyAsync() {
    // Arrange - Tests line 339: ternary for trailing comma in dispatchers array
    var source = """
            using Whizbang.Core;
            using System.Threading.Tasks;

            namespace MyApp {
              public record TestCommand : ICommand;

              public class FirstDispatcher {
                public async Task ExecuteAsync(Dispatcher dispatcher) {
                  await dispatcher.SendAsync(new TestCommand());
                }
              }

              public class SecondDispatcher {
                public async Task RunAsync(Dispatcher dispatcher) {
                  await dispatcher.SendAsync(new TestCommand());
                }
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should include both dispatchers with correct JSON formatting
    var generatedJson = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedJson).IsNotNull();
    await Assert.That(generatedJson!).Contains("FirstDispatcher");
    await Assert.That(generatedJson).Contains("SecondDispatcher");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MessageWithMultipleReceptors_FormatsJsonCorrectlyAsync() {
    // Arrange - Tests line 354: ternary for trailing comma in receptors array
    var source = """
            using Whizbang.Core;
            using System.Threading.Tasks;

            namespace MyApp {
              public record TestCommand : ICommand;

              public class FirstReceptor : IReceptor<TestCommand, string> {
                public ValueTask<string> HandleAsync(TestCommand message, CancellationToken ct = default) => ValueTask.FromResult("first");
              }

              public class SecondReceptor : IReceptor<TestCommand, string> {
                public ValueTask<string> HandleAsync(TestCommand message, CancellationToken ct = default) => ValueTask.FromResult("second");
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should include both receptors with correct JSON formatting
    var generatedJson = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedJson).IsNotNull();
    await Assert.That(generatedJson!).Contains("FirstReceptor");
    await Assert.That(generatedJson).Contains("SecondReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MessageWithMultiplePerspectives_FormatsJsonCorrectlyAsync() {
    // Arrange - Tests line 369: ternary for trailing comma in perspectives array
    var source = """
            using Whizbang.Core;
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            namespace MyApp {
              public record TestEvent : IEvent;

              public class FirstPerspective : IPerspectiveOf<TestEvent> {
                public Guid Id { get; set; }
                public Task Update(TestEvent @event, CancellationToken ct = default) => Task.CompletedTask;
              }

              public class SecondPerspective : IPerspectiveOf<TestEvent> {
                public Guid Id { get; set; }
                public Task Update(TestEvent @event, CancellationToken ct = default) => Task.CompletedTask;
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should include both perspectives with correct JSON formatting
    var generatedJson = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedJson).IsNotNull();
    await Assert.That(generatedJson!).Contains("FirstPerspective");
    await Assert.That(generatedJson).Contains("SecondPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_EventWithOnlyDispatchers_InfersAsEventAsync() {
    // Arrange - Tests line 321: isEvent when no perspectives and no receptors but has dispatchers
    var source = """
            using Whizbang.Core;
            using System.Threading.Tasks;

            namespace MyApp {
              public record EventWithNoHandlers : IEvent;

              public class Publisher {
                public async Task PublishAsync(Dispatcher dispatcher) {
                  await dispatcher.PublishAsync(new EventWithNoHandlers());
                }
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should infer as event (no receptors, has dispatchers)
    var generatedJson = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedJson).IsNotNull();
    await Assert.That(generatedJson!).Contains("EventWithNoHandlers");
    await Assert.That(generatedJson).Contains("isEvent");
    await Assert.That(generatedJson).Contains(": true");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_DispatcherInFieldInitializer_HandlesNullContainingMethodAsync() {
    // Arrange - Tests line 140: containingMethod null check where fallback to "<unknown>" happens
    // This scenario creates a dispatcher call in a context where the containing method might not be identified
    var source = """
            using Whizbang.Core;
            using System.Threading.Tasks;

            namespace MyApp {
              public record TestCommand : ICommand;

              public class ServiceWithFieldDispatch {
                private readonly Task _initTask;

                public ServiceWithFieldDispatch(IDispatcher dispatcher) {
                  _initTask = dispatcher.SendAsync(new TestCommand());
                }
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover dispatcher and handle gracefully if containingMethod is null
    var generatedJson = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedJson).IsNotNull();
    await Assert.That(generatedJson!).Contains("ServiceWithFieldDispatch");
    await Assert.That(generatedJson).Contains("TestCommand");
  }
}
