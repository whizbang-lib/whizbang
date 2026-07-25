using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the MessageRegistryGenerator source generator.
/// </summary>
public partial class MessageRegistryGeneratorTests {
  [System.Text.RegularExpressions.GeneratedRegex("OrderWorkflow")]
  private static partial System.Text.RegularExpressions.Regex _orderWorkflowRegex();

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_EmptyCompilation_GeneratesEmptyRegistryAsync() {
    // Arrange
    const string source = @"
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
    await Assert.That(generatedSource).Contains("\"\"messages\"\": [");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_SingleCommand_DiscoversCommandAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = "";
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("CreateOrderCommand");
    await Assert.That(generatedSource).Contains("\"\"isCommand\"\": true");
    await Assert.That(generatedSource).Contains("\"\"isEvent\"\": false");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_SingleEvent_DiscoversEventAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("\"\"isCommand\"\": false");
    await Assert.That(generatedSource).Contains("\"\"isEvent\"\": true");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_CommandWithDispatcher_DiscoversDispatcherAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading.Tasks;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = "";
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
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("CreateOrderCommand");
    await Assert.That(generatedSource).Contains("\"\"dispatchers\"\":");
    await Assert.That(generatedSource).Contains("OrderService");
    await Assert.That(generatedSource).Contains("CreateOrderAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_CommandWithReceptor_DiscoversReceptorAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = "";
  }

  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public class CreateOrderReceptor : IReceptor<CreateOrderCommand, OrderCreatedEvent> {
    public async Task<OrderCreatedEvent> HandleAsync(
        CreateOrderCommand message,
        CancellationToken cancellationToken = default) {
      // Handle command
      return new OrderCreatedEvent { OrderId = message.OrderId };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("CreateOrderCommand");
    await Assert.That(generatedSource).Contains("\"\"receptors\"\":");
    await Assert.That(generatedSource).Contains("CreateOrderReceptor");
    await Assert.That(generatedSource).Contains("HandleAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_EventWithPerspective_DiscoversPerspectiveAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderListModel(string OrderId);

  public class OrderListPerspective : IPerspectiveFor<OrderListModel, OrderCreatedEvent> {
    public OrderListModel Apply(OrderListModel currentData, OrderCreatedEvent @event) {
      return currentData with { OrderId = @event.OrderId };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("\"\"perspectives\"\":");
    await Assert.That(generatedSource).Contains("OrderListPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_EventWithPerspectiveWithActionsFor_DiscoversPerspectiveAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderShippedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderListModel(string OrderId);

  public class ActionsOrderListPerspective : IPerspectiveWithActionsFor<OrderListModel, OrderShippedEvent> {
    public ApplyResult<OrderListModel> Apply(OrderListModel? currentData, OrderShippedEvent @event) {
      return ApplyResult<OrderListModel>.Update((currentData ?? new(@event.OrderId)) with { OrderId = @event.OrderId });
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - IPerspectiveWithActionsFor types must surface in the perspectives list
    // alongside IPerspectiveFor; otherwise the registry silently omits them.
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("OrderShippedEvent");
    await Assert.That(generatedSource).Contains("\"\"perspectives\"\":");
    await Assert.That(generatedSource).Contains("ActionsOrderListPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MultipleMessages_DiscoversAllAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = "";
  }

  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record CancelOrderCommand : ICommand {
    public string OrderId { get; init; } = "";
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("CreateOrderCommand");
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("CancelOrderCommand");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_GeneratedJson_ContainsFilePathsAndLineNumbersAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = "";
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("\"\"filePath\"\":");
    await Assert.That(generatedSource).Contains("\"\"lineNumber\"\":");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_PerspectiveWithMultipleEvents_DiscoversAllEventsAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderUpdatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderListModel(string OrderId);

  public class OrderListPerspective : IPerspectiveFor<OrderListModel, OrderCreatedEvent, OrderUpdatedEvent> {
    public OrderListModel Apply(OrderListModel currentData, OrderCreatedEvent @event) {
      return currentData with { OrderId = @event.OrderId };
    }

    public OrderListModel Apply(OrderListModel currentData, OrderUpdatedEvent @event) {
      return currentData with { OrderId = @event.OrderId };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("OrderUpdatedEvent");
    await Assert.That(generatedSource).Contains("OrderListPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_NoCompilationErrors_GeneratesValidCodeAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = "";
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    // Should have no diagnostics
    var diagnostics = result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    await Assert.That(diagnostics).IsEmpty();

    // Should generate exactly one file
    var generatedFiles = GeneratorTestHelper.GetAllGeneratedSources(result).ToList();
    await Assert.That(generatedFiles).Count().IsEqualTo(1);
    await Assert.That(generatedFiles[0].FileName).IsEqualTo("MessageRegistry.g.cs");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_VoidReceptor_DiscoversReceptorAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record LogCommand : ICommand {
    public string Message { get; init; } = "";
  }

  public class LogReceptor : IReceptor<LogCommand> {
    public ValueTask HandleAsync(LogCommand message, CancellationToken ct = default) {
      return ValueTask.CompletedTask;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("LogCommand");
    await Assert.That(generatedSource).Contains("\"\"receptors\"\":");
    await Assert.That(generatedSource).Contains("LogReceptor");
    await Assert.That(generatedSource).Contains("HandleAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MixedReceptorTypes_DiscoversAllAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  // Command with regular receptor (returns result)
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = "";
  }

  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
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
    public string Message { get; init; } = "";
  }

  public class LogReceptor : IReceptor<LogCommand> {
    public ValueTask HandleAsync(LogCommand message, CancellationToken ct = default) {
      return ValueTask.CompletedTask;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Both commands should be discovered
    await Assert.That(generatedSource).Contains("CreateOrderCommand");
    await Assert.That(generatedSource).Contains("LogCommand");

    // Both receptors should be discovered
    await Assert.That(generatedSource).Contains("CreateOrderReceptor");
    await Assert.That(generatedSource).Contains("LogReceptor");

    // Both should be in receptors array
    var receptorOccurrences = MyRegex().Matches(generatedSource);
    await Assert.That(receptorOccurrences.Count).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MultipleVoidReceptors_DiscoversAllAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record LogCommand : ICommand {
    public string Message { get; init; } = "";
  }

  public record NotifyCommand : ICommand {
    public string UserId { get; init; } = "";
  }

  public record CacheUpdateCommand : ICommand {
    public string Key { get; init; } = "";
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
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // All commands should be discovered
    await Assert.That(generatedSource).Contains("LogCommand");
    await Assert.That(generatedSource).Contains("NotifyCommand");
    await Assert.That(generatedSource).Contains("CacheUpdateCommand");

    // All receptors should be discovered
    await Assert.That(generatedSource).Contains("LogReceptor");
    await Assert.That(generatedSource).Contains("NotifyReceptor");
    await Assert.That(generatedSource).Contains("CacheUpdateReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_PublishAsyncWithGeneric_DiscoversDispatcherAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
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
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("\"\"dispatchers\"\":");
    await Assert.That(generatedSource).Contains("OrderEventPublisher");
    await Assert.That(generatedSource).Contains("PublishOrderCreatedAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MultipleDispatchesInSameMethod_DiscoversAllAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading.Tasks;

namespace TestNamespace {
  public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; } = "";
  }

  public record SendEmailCommand : ICommand {
    public string Email { get; init; } = "";
  }

  public record LogCommand : ICommand {
    public string Message { get; init; } = "";
  }

  public class OrderWorkflow {
    private IDispatcher _dispatcher;

    public OrderWorkflow(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task ProcessOrderAsync(string orderId) {
      await _dispatcher.SendAsync(new CreateOrderCommand { OrderId = orderId });
      await _dispatcher.SendAsync(new SendEmailCommand { Email = "test@test.com" });
      await _dispatcher.SendAsync(new LogCommand { Message = "Order processed" });
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // All three commands should be discovered
    await Assert.That(generatedSource).Contains("CreateOrderCommand");
    await Assert.That(generatedSource).Contains("SendEmailCommand");
    await Assert.That(generatedSource).Contains("LogCommand");

    // All three dispatches should be tracked
    var dispatcherOccurrences = _orderWorkflowRegex().Matches(generatedSource!);
    await Assert.That(dispatcherOccurrences.Count).IsGreaterThanOrEqualTo(3);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ConditionalDispatch_DiscoversDispatcherAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading.Tasks;

namespace TestNamespace {
  public record ProcessOrderCommand : ICommand {
    public string OrderId { get; init; } = "";
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
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("ProcessOrderCommand");
    await Assert.That(generatedSource).Contains("OrderService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_DispatcherVariableName_DiscoversDispatcherAsync() {
    // Arrange - Test with different variable names
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = "";
  }

  public class TestService {
    private IDispatcher dispatcher;

    public TestService(IDispatcher d) {
      dispatcher = d;
    }

    public async Task ExecuteAsync() {
      await dispatcher.SendAsync(new TestCommand { Value = "test" });
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("TestCommand");
    await Assert.That(generatedSource).Contains("TestService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_StructMessageType_SkipsAsync() {
    // Arrange - Tests ExtractMessageType with struct (default case in switch expression)
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;

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
    await Assert.That(generatedSource).DoesNotContain("CreateOrderStruct");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ClassWithoutMessageInterface_SkipsAsync() {
    // Arrange - Tests ExtractMessageType when class doesn't implement ICommand or IEvent
    const string source = """

using System;

namespace TestNamespace {
  public class OrderDto : ICloneable {
    public string OrderId { get; set; } = "";
    public object Clone() => new OrderDto { OrderId = OrderId };
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Non-message class should be skipped
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).DoesNotContain("OrderDto");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_WrongMethodName_SkipsAsync() {
    // Arrange - Tests ExtractDispatcher when method name is not SendAsync/PublishAsync
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = "";
  }

  public class TestService {
    private IDispatcher _dispatcher;

    public TestService(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task ExecuteAsync() {
      await _dispatcher.DispatchAsync(new TestCommand { Value = "test" });
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover message but not dispatcher (wrong method name)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("TestCommand");
    await Assert.That(generatedSource).DoesNotContain("TestService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_SendAsyncWithNoArguments_SkipsAsync() {
    // Arrange - Tests ExtractDispatcher when SendAsync has no arguments
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = "";
  }

  public class TestService {
    public async Task ExecuteAsync() {
      // Invalid call with no arguments
      var result = await OtherMethod.SendAsync();
    }
  }

  public static class OtherMethod {
    public static Task<string> SendAsync() => Task.FromResult("test");
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should not discover dispatcher (no arguments)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).DoesNotContain("TestService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ReceptorWithWrongTypeArguments_SkipsAsync() {
    // Arrange - Tests ExtractReceptor with wrong number of type arguments
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = "";
  }

  // This won't compile but tests the generator's defensive code
  // Generator should skip this if it somehow encounters it
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover message only
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("TestCommand");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ReferencedAssemblyMessage_InfersTypeAsync() {
    // Arrange - Tests GenerateMessageRegistry when message is from referenced assembly
    // Message is used but not defined in this project
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
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
    await Assert.That(generatedSource).Contains("ExternalCommand");
    await Assert.That(generatedSource).Contains("\"\"isCommand\"\": true");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_OnlyDispatcherForMessage_InfersEventAsync() {
    // Arrange - Tests GenerateMessageRegistry when only dispatcher exists (no receptor, no perspective)
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
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
    await Assert.That(generatedSource).Contains("ExternalEvent");
    await Assert.That(generatedSource).Contains("\"\"isEvent\"\": true");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_DispatcherInTopLevelStatement_HandlesGracefullyAsync() {
    // Arrange - Tests ExtractDispatcher when no containing class (edge case)
    // This tests the containingClass null check
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = "";
  }

  public class TestService {
    public async Task ExecuteAsync(IDispatcher dispatcher) {
      await dispatcher.SendAsync(new TestCommand { Value = "test" });
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover both message and dispatcher
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("TestCommand");
    await Assert.That(generatedSource).Contains("TestService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ReceptorWithoutHandleAsync_DiscoversWithFallbackLineAsync() {
    // Arrange - Tests ExtractReceptor when HandleAsync method is missing
    // This tests the handleMethod null fallback for line number
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = "";
  }

  public class TestReceptor : IReceptor<TestCommand> {
    // Explicit interface implementation (no public HandleAsync method visible)
    ValueTask IReceptor<TestCommand>.HandleAsync(TestCommand message, CancellationToken ct) {
      return ValueTask.CompletedTask;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover receptor and use class location for line number
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("TestCommand");
    await Assert.That(generatedSource).Contains("TestReceptor");
    await Assert.That(generatedSource).Contains("\"\"lineNumber\"\":");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_PublishAsyncWithDefaultExpression_InfersFromGenericAsync() {
    // Arrange - Tests ExtractDispatcher fallback to generic type argument (line 129)
    // Need to pass default(TEvent) or null with explicit generic to force fallback
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestEvent : IEvent {
    public string Value { get; init; } = "";
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
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover dispatcher with generic method fallback
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("TestEvent");
    await Assert.That(generatedSource).Contains("TestService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_NonMethodInvocation_SkipsAsync() {
    // Arrange - Tests ExtractDispatcher when symbolInfo.Symbol is not IMethodSymbol
    // Line 108-110: if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = "";
  }

  public class TestService {
    private IDispatcher _dispatcher;

    public TestService(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task ExecuteAsync() {
      // This is a valid SendAsync call
      await _dispatcher.SendAsync(new TestCommand { Value = "test" });

      // This tests invocations that aren't method symbols
      var action = new Action(() => Console.WriteLine("Not a method symbol"));
      action();  // Invocation but not a method symbol in the semantic sense we care about
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should discover the valid dispatcher, skip the non-method invocation
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("TestCommand");
    await Assert.That(generatedSource).Contains("TestService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_SendAsyncOnNonDispatcherStaticClass_SkipsAsync() {
    // History: this test previously asserted "Generator discovers any SendAsync call,
    // even non-IDispatcher ones" — codifying the bug that the Rocks-interop
    // fix corrects. Under the corrected semantic, only invocations whose method's
    // ContainingType IS, or implements, Whizbang.Core.IDispatcher are registered.
    // `SomeMethod.SendAsync(string)` is a static helper on a non-dispatcher class
    // and must be skipped.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading.Tasks;

namespace TestNamespace {
  public record TestCommand : ICommand {
    public string Value { get; init; } = "";
  }

  public class TestService {
    public async Task ExecuteAsync() {
      // SendAsync with a string — different method, not IDispatcher.SendAsync.
      // Pre-fix the generator picked this up; post-fix the symbol-based filter
      // rejects it because `SomeMethod` does not implement Whizbang.Core.IDispatcher.
      await SomeMethod.SendAsync("not a message");
    }
  }

  public static class SomeMethod {
    public static Task SendAsync(string value) => Task.CompletedTask;
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert — the registry must not include the non-dispatcher call site.
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).DoesNotContain("\"\"TestService\"\"")
      .Because("TestService.ExecuteAsync calls SomeMethod.SendAsync (a non-IDispatcher static method); the dispatchers section must exclude it.");
    await Assert.That(generatedSource).DoesNotContain("\"\"ExecuteAsync\"\"")
      .Because("ExecuteAsync is the call-site method for a non-IDispatcher SendAsync; it must not be registered as a dispatch site.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ReceptorWithoutHandleAsyncMethod_UsesClassLineNumberAsync() {
    // Arrange - Tests line 220 null coalescing when HandleAsync method is not found
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

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
    await Assert.That(generatedSource).Contains("TestCommand");
    await Assert.That(generatedSource).Contains("TestReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MessageOnlyDispatchedNoDefinition_InfersTypeAsync() {
    // Arrange - Tests lines 326-335: message from referenced assembly (inferred type logic)
    // When a message is dispatched but not defined in this project, we infer its type
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
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
      await _dispatcher.SendAsync("some string");
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should infer message type from dispatcher usage
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("string");
    await Assert.That(generatedSource).Contains("TestService");
    // Should show inferred type with empty filePath (line 333)
    await Assert.That(generatedSource).Contains("\"\"filePath\"\": \"\"\"\"");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_PerspectiveOnly_InfersEventTypeAsync() {
    // Arrange - Tests line 329 event type inference when only perspectives exist
    const string source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace;

public record TestEvent : IEvent;

public record TestModel(Guid Id);

public class TestPerspective : IPerspectiveFor<TestModel, TestEvent> {
  public TestModel Apply(TestModel currentData, TestEvent @event) {
    return currentData;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should infer TestEvent as an event from perspective
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("TestEvent");
    await Assert.That(generatedSource).Contains("TestPerspective");
    await Assert.That(generatedSource).Contains("\"\"isEvent\"\": true");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_EmptyProject_GeneratesEmptyRegistryAsync() {
    // Arrange - Tests line 310 where clause with no messages, dispatchers, receptors, or perspectives
    const string source = @"
namespace TestNamespace;

public class SomeClass {
  public string Name { get; set; } = string.Empty;
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should generate empty message registry
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("\"\"messages\"\":");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_SendAsyncWithZeroArguments_ReturnsNullAsync() {
    // Arrange - Tests ExtractDispatcher line 121: if (invocation.ArgumentList.Arguments.Count > 0)
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
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
    const string source = @"
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
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
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
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

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
    await Assert.That(generatedSource).DoesNotContain("TestReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MessageWithBothReceptorAndPerspective_MarksAsBothCommandAndEventAsync() {
    // Arrange - Tests line 321 and 326-327: Message with receptors AND perspectives
    // isCommand = group.Receptors.Count > 0 = true
    // isEvent = group.Perspectives.Count > 0 = true
    const string source = """

using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace;

public record HybridMessage : ICommand, IEvent {
  public string Data { get; init} = "";
}

public record HybridModel(Guid Id, string Data);

public class HybridReceptor : IReceptor<HybridMessage, string> {
  public ValueTask<string> HandleAsync(HybridMessage message, CancellationToken ct = default) {
    return ValueTask.FromResult("OK");
  }
}

public class HybridPerspective : IPerspectiveFor<HybridModel, HybridMessage> {
  public HybridModel Apply(HybridModel currentData, HybridMessage @event) {
    return currentData with { Data = @event.Data };
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should mark as both command and event
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("HybridMessage");
    await Assert.That(generatedSource).Contains("\"\"isCommand\"\": true");
    await Assert.That(generatedSource).Contains("\"\"isEvent\"\": true");
    await Assert.That(generatedSource).Contains("HybridReceptor");
    await Assert.That(generatedSource).Contains("HybridPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_EventWithOnlyPerspectives_InfersAsEventAsync() {
    // Arrange - Tests line 321: isEvent when only perspectives exist (no receptors or dispatchers)
    // isEvent = group.Perspectives.Count > 0 || ... = true
    const string source = """

using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace;

public record PerspectiveOnlyEvent : IEvent {
  public string EventData { get; init; } = "";
}

public record EventModel(Guid Id, string EventData);

public class EventPerspective : IPerspectiveFor<EventModel, PerspectiveOnlyEvent> {
  public EventModel Apply(EventModel currentData, PerspectiveOnlyEvent @event) {
    return currentData with { EventData = @event.EventData };
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should infer as event based on perspective
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("PerspectiveOnlyEvent");
    await Assert.That(generatedSource).Contains("\"\"isCommand\"\": false");
    await Assert.That(generatedSource).Contains("\"\"isEvent\"\": true");
    await Assert.That(generatedSource).Contains("EventPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_ReceptorImplementsBothInterfaces_UsesRegularNotVoidAsync() {
    // Arrange - Tests line 172-174: When regular receptor exists, void receptor interface should be null
    // This tests the `: null` branch in the ternary operator
    const string source = """

using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace;

public record DualCommand : ICommand {
  public string Data { get; init; } = "";
}

// Unusual but valid: implements both IReceptor<TMessage, TResponse> AND IReceptor<TMessage>
// Should use the regular receptor interface, not the void one
public class DualReceptor : IReceptor<DualCommand, string>, IReceptor<DualCommand> {
  public ValueTask<string> HandleAsync(DualCommand message, CancellationToken ct = default) {
    return ValueTask.FromResult("Response");
  }

  ValueTask IReceptor<DualCommand>.HandleAsync(DualCommand message, CancellationToken ct) {
    return ValueTask.CompletedTask;
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should use regular receptor (with TResponse), not void receptor
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("DualCommand");
    await Assert.That(generatedSource).Contains("DualReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MessageWithMultipleDispatchers_FormatsJsonCorrectlyAsync() {
    // Arrange - Tests line 339: ternary for trailing comma in dispatchers array
    const string source = """
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;
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
    await Assert.That(generatedJson).Contains("FirstDispatcher");
    await Assert.That(generatedJson).Contains("SecondDispatcher");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MessageWithMultipleReceptors_FormatsJsonCorrectlyAsync() {
    // Arrange - Tests line 354: ternary for trailing comma in receptors array
    const string source = """
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;
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
    await Assert.That(generatedJson).Contains("FirstReceptor");
    await Assert.That(generatedJson).Contains("SecondReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MessageWithMultiplePerspectives_FormatsJsonCorrectlyAsync() {
    // Arrange - Tests line 369: ternary for trailing comma in perspectives array
    const string source = """
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            namespace MyApp {
              public record TestEvent : IEvent;

              public record PerspectiveModel(Guid Id);

              public class FirstPerspective : IPerspectiveFor<PerspectiveModel, TestEvent> {
                public PerspectiveModel Apply(PerspectiveModel currentData, TestEvent @event) => currentData;
              }

              public class SecondPerspective : IPerspectiveFor<PerspectiveModel, TestEvent> {
                public PerspectiveModel Apply(PerspectiveModel currentData, TestEvent @event) => currentData;
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - Should include both perspectives with correct JSON formatting
    var generatedJson = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedJson).IsNotNull();
    await Assert.That(generatedJson).Contains("FirstPerspective");
    await Assert.That(generatedJson).Contains("SecondPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_EventWithOnlyDispatchers_InfersAsEventAsync() {
    // Arrange - Tests line 321: isEvent when no perspectives and no receptors but has dispatchers
    const string source = """
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;
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
    await Assert.That(generatedJson).Contains("EventWithNoHandlers");
    await Assert.That(generatedJson).Contains("isEvent");
    await Assert.That(generatedJson).Contains(": true");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_DispatcherInFieldInitializer_HandlesNullContainingMethodAsync() {
    // Arrange - Tests line 140: containingMethod null check where fallback to "<unknown>" happens
    // This scenario creates a dispatcher call in a context where the containing method might not be identified
    const string source = """
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;
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
    await Assert.That(generatedJson).Contains("ServiceWithFieldDispatch");
    await Assert.That(generatedJson).Contains("TestCommand");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_RocksLikeLookAlikePublishAsync_DoesNotCrashAsync() {
    // Regression for Rocks-mock interop crash.
    //
    // The generator's dispatcher discovery filtered invocations purely by method name
    // ("SendAsync" / "PublishAsync") and then assumed every match resolved to an
    // IMethodSymbol via a hard `??-throw` (RoslynGuards.GetMethodSymbolOrThrow).
    //
    // Rocks generates expectations classes that include a same-named method on an
    // unrelated type (e.g.
    //   class IDispatcherCreateExpectations.SetupsExpectations { void PublishAsync<T>(Rocks.Argument<T> e) }
    // ). When test code calls `exp.Setups.PublishAsync<SomeEvent>(...)`, Roslyn
    // legitimately resolves to that look-alike method (or to a candidate-set with no
    // best match). The `??-throw` then fired as CS8785 generator failure and broke
    // every consumer test project that uses Rocks against IDispatcher.
    //
    // The fix is two-part: (1) treat an unresolved IMethodSymbol as "skip, not crash"
    // and (2) only register invocations whose containing type is (or implements)
    // Whizbang.Core.IDispatcher. This RED test exercises both: a look-alike type with
    // a PublishAsync<T>(Argument<T>) method, called from user code, must not appear
    // in the registry AND must not crash the generator.
    const string source = """

using System.Threading.Tasks;
using Whizbang.Core;

namespace RocksLikeLib {
  // Stand-in for a Rocks-generated argument-wrapper struct.
  public readonly struct Argument<T> {
    public Argument(T value) { Value = value; }
    public T Value { get; }
  }

  // Same-named method on an unrelated, NOT-IDispatcher type.
  public sealed class IDispatcherCreateExpectations {
    public SetupsExpectations Setups { get; } = new();
  }

  public sealed class SetupsExpectations {
    public void PublishAsync<TEvent>(Argument<TEvent> arg) { }
    public void SendAsync<TCommand>(Argument<TCommand> arg) { }
  }
}

namespace TestNamespace {
  public record SomeEvent : IEvent { public string Id { get; init; } = ""; }

  public class TestSetup {
    public void Configure() {
      var exp = new RocksLikeLib.IDispatcherCreateExpectations();
      // These two calls must NOT be treated as dispatcher invocations and must NOT
      // crash the generator on symbol-info dereference.
      exp.Setups.PublishAsync(new RocksLikeLib.Argument<SomeEvent>(new SomeEvent()));
      exp.Setups.SendAsync(new RocksLikeLib.Argument<SomeEvent>(new SomeEvent()));
    }
  }
}
""";

    // Pre-fix: this `RunGenerator` call throws InvalidOperationException from
    // RoslynGuards.GetMethodSymbolOrThrow, surfacing as CS8785 in real builds.
    // Post-fix: the generator skips the look-alike and produces a valid registry.
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    var generatedJson = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedJson).IsNotNull()
      .Because("Generator must complete without throwing when a non-Whizbang type has same-named SendAsync/PublishAsync methods (Rocks scenario).");

    // The look-alike calls happen inside TestSetup.Configure. Pre-fix the generator
    // walks them as if they were real dispatcher calls and lists Configure/TestSetup
    // in the dispatchers section of MessageRegistry. Post-fix the symbol-based filter
    // recognises that the methods are not on Whizbang.Core.IDispatcher and skips them.
    await Assert.That(generatedJson).DoesNotContain("\"\"TestSetup\"\"")
      .Because("TestSetup.Configure invokes look-alike PublishAsync/SendAsync that are NOT on IDispatcher; symbol-based filter must exclude it from the dispatchers section.");
    await Assert.That(generatedJson).DoesNotContain("\"\"Configure\"\"")
      .Because("Configure is the method that holds the look-alike calls; it must not be registered as a dispatch site.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_NonDispatcherSendAsyncOnUnrelatedType_DoesNotRegisterAsync() {
    // Symbol-based filter must also exclude same-named methods on classes the user
    // wrote themselves (e.g. an HttpClient helper named SendAsync). Locks in the
    // "only IDispatcher counts" semantic of the post-fix generator.
    const string source = """

using System.Threading.Tasks;
using Whizbang.Core;

namespace TestNamespace {
  public record SomeCommand : ICommand;

  // NOT a dispatcher — but exposes SendAsync<T>.
  public class CustomMessenger {
    public Task SendAsync<T>(T message) => Task.CompletedTask;
    public Task PublishAsync<T>(T message) => Task.CompletedTask;
  }

  public class TestService {
    public async Task GoAsync() {
      var m = new CustomMessenger();
      // Same names as IDispatcher; not actually dispatcher calls.
      await m.SendAsync(new SomeCommand());
      await m.PublishAsync(new SomeCommand());
    }
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    var generatedJson = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedJson).IsNotNull();

    // Pre-fix, TestService.GoAsync (the call-site) lands in the dispatchers section
    // because the generator filtered purely by method name. Post-fix, the symbol-
    // based check that `methodSymbol.ContainingType` is IDispatcher excludes it.
    await Assert.That(generatedJson).DoesNotContain("\"\"TestService\"\"")
      .Because("TestService.GoAsync calls SendAsync/PublishAsync on a class that does not implement Whizbang.Core.IDispatcher; symbol-based filter must exclude it.");
    await Assert.That(generatedJson).DoesNotContain("\"\"GoAsync\"\"")
      .Because("GoAsync holds the look-alike call sites and must not be registered as a dispatch method.");
  }

  [System.Text.RegularExpressions.GeneratedRegex("\"\"receptors\"\":")]
  private static partial System.Text.RegularExpressions.Regex MyRegex();
}
