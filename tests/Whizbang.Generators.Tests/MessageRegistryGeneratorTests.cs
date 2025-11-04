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
      await _dispatcher.SendAsync<object>(new CreateOrderCommand { OrderId = orderId });
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
}
