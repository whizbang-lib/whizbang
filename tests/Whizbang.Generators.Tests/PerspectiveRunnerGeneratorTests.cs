using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the PerspectiveRunnerGenerator source generator.
/// Ensures correct perspective runner generation for perspectives with IPerspectiveModel.
/// </summary>
public class PerspectiveRunnerGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_EmptyCompilation_GeneratesNothingAsync() {
    // Arrange
    var source = @"
using System;

namespace TestNamespace {
  public class SomeClass {
    public void SomeMethod() { }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should not generate any runner files when no perspectives with models exist
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_PerspectiveWithoutModel_GeneratesNothingAsync() {
    // Arrange - Perspective without IPerspectiveModel should not generate runner
    var source = @"
using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public class OrderPerspective {
    public Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - No runner should be generated (perspective doesn't implement IPerspectiveModel)
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_PerspectiveWithModel_GeneratesRunnerAsync() {
    // Arrange - Perspective with IPerspectiveModel<TModel> should generate runner
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderReadModel {
    [StreamKey]
    public string OrderId { get; init; } = """";
    public string Status { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
      return currentData with { Status = ""Created"" };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate OrderPerspectiveRunner
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("class OrderPerspectiveRunner");
    await Assert.That(runnerSource!).Contains("IPerspectiveRunner");
    await Assert.That(runnerSource!).Contains("OrderPerspective");
    await Assert.That(runnerSource!).Contains("OrderReadModel");
    await Assert.That(runnerSource!).Contains("OrderId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_PerspectiveWithModelNoStreamKey_GeneratesNothingAsync() {
    // Arrange - Model without [StreamKey] attribute should not generate runner
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderReadModel {
    // Missing [StreamKey] attribute
    public string OrderId { get; init; } = """";
    public string Status { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
      return currentData with { Status = ""Created"" };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should not generate runner (model missing [StreamKey])
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_AbstractPerspective_IsIgnoredAsync() {
    // Arrange - Abstract perspectives should not generate runners
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderReadModel {
    [StreamKey]
    public string OrderId { get; init; } = """";
  }

  public abstract class BasePerspective : IPerspectiveFor<OrderReadModel, OrderEvent> {
    public abstract OrderReadModel Apply(OrderReadModel currentData, OrderEvent @event);
  }

  public class ConcretePerspective : BasePerspective {
    public override OrderReadModel Apply(OrderReadModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should only generate runner for concrete class
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "ConcretePerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("ConcretePerspectiveRunner");
    await Assert.That(runnerSource!).DoesNotContain("BasePerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_GeneratesDiagnosticAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderReadModel {
    [StreamKey]
    public string OrderId { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should report WHIZ027 diagnostic
    var diagnostics = result.Diagnostics;
    var whiz027 = diagnostics.FirstOrDefault(d => d.Id == "WHIZ027");
    await Assert.That(whiz027).IsNotNull();
    await Assert.That(whiz027!.Severity).IsEqualTo(DiagnosticSeverity.Info);
    await Assert.That(whiz027.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderPerspective");
    await Assert.That(whiz027.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_UsesFullyQualifiedTypeNamesAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderReadModel {
    [StreamKey]
    public string OrderId { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should use global:: qualified names
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("global::TestNamespace.OrderPerspective");
    await Assert.That(runnerSource!).Contains("global::TestNamespace.OrderReadModel");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_CorrectRunnerNameAsync() {
    // Arrange - Test runner naming convention
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record InventoryEvent : IEvent {
    public string InventoryId { get; init; } = """";
  }

  public record InventoryModel {
    [StreamKey]
    public string InventoryId { get; init; } = """";
    public int Quantity { get; init; }
  }

  public class InventoryPerspective : IPerspectiveFor<InventoryModel, InventoryEvent> {
    public InventoryModel Apply(InventoryModel currentData, InventoryEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Runner name should be InventoryPerspectiveRunner
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "InventoryPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("class InventoryPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_ImplementsIPerspectiveRunnerAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderReadModel {
    [StreamKey]
    public string OrderId { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should implement IPerspectiveRunner interface
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("IPerspectiveRunner");
    await Assert.That(runnerSource!).Contains("RunAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MultiplePerspectives_GeneratesMultipleRunnersAsync() {
    // Arrange - Multiple perspectives with models should generate multiple runners
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record InventoryEvent : IEvent {
    public string InventoryId { get; init; } = """";
  }

  public record OrderModel {
    [StreamKey]
    public string OrderId { get; init; } = """";
  }

  public record InventoryModel {
    [StreamKey]
    public string InventoryId { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
      return currentData;
    }
  }

  public class InventoryPerspective : IPerspectiveFor<InventoryModel, InventoryEvent> {
    public InventoryModel Apply(InventoryModel currentData, InventoryEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate two runners
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(2);

    var orderRunner = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    var inventoryRunner = GeneratorTestHelper.GetGeneratedSource(result, "InventoryPerspectiveRunner.g.cs");

    await Assert.That(orderRunner).IsNotNull();
    await Assert.That(inventoryRunner).IsNotNull();

    await Assert.That(orderRunner!).Contains("OrderPerspectiveRunner");
    await Assert.That(inventoryRunner!).Contains("InventoryPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_GeneratedCodeUsesCorrectNamespaceAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent { }

  public record OrderModel {
    [StreamKey]
    public string OrderId { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should use TestAssembly.Generated namespace
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("namespace TestAssembly.Generated");
    await Assert.That(runnerSource!).Contains("using System");
    await Assert.That(runnerSource!).Contains("using Whizbang.Core");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_StreamKeyPropertyNameIncludedAsync() {
    // Arrange - Test that stream key property name is used in generated runner
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent { }

  public record OrderModel {
    [StreamKey]
    public string CustomOrderIdentifier { get; init; } = """";
    public string Status { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should include stream key property name in generated code
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("CustomOrderIdentifier");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_GeneratesExtractStreamIdMethod_UsingEventStreamKeyAsync() {
    // Arrange - Test that runner generates ExtractStreamId method using event's [StreamKey]
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record ProductCreatedEvent : IEvent {
    [StreamKey]
    public Guid ProductId { get; init; }  // Event's stream key
    public string ProductName { get; init; } = """";
  }

  public record ProductModel {
    [StreamKey]
    public Guid ProductId { get; init; }  // Model's stream key (same property)
    public string ProductName { get; init; } = """";
  }

  public class ProductPerspective : IPerspectiveFor<ProductModel, ProductCreatedEvent> {
    public ProductModel Apply(ProductModel currentData, ProductCreatedEvent @event) {
      return currentData with { ProductName = @event.ProductName };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate ExtractStreamId method
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // DEBUG: Print generated source
    Console.WriteLine("=== GENERATED SOURCE ===");
    Console.WriteLine(runnerSource);
    Console.WriteLine("=== END GENERATED SOURCE ===");

    // Should have ExtractStreamId method
    await Assert.That(runnerSource!).Contains("ExtractStreamId");

    // Should access event's ProductId property (the [StreamKey] property)
    await Assert.That(runnerSource!).Contains("@event.ProductId");

    // Should return the stream ID as string
    await Assert.That(runnerSource!).Contains("private static string ExtractStreamId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MultipleEvents_GeneratesExtractStreamIdForEachAsync() {
    // Arrange - Perspective with multiple events should generate ExtractStreamId for each
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
    public string CustomerName { get; init; } = """";
  }

  public record OrderShippedEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }  // Same property name, different event
    public string TrackingNumber { get; init; } = """";
  }

  public record OrderModel {
    [StreamKey]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = """";
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderCreatedEvent>,
    IPerspectiveFor<OrderModel, OrderShippedEvent> {

    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData with { Status = ""Created"" };
    }

    public OrderModel Apply(OrderModel currentData, OrderShippedEvent @event) {
      return currentData with { Status = ""Shipped"" };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should have multiple ExtractStreamId overloads (one per event type)
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should have ExtractStreamId for OrderCreatedEvent
    await Assert.That(runnerSource!).Contains("ExtractStreamId(global::TestNamespace.OrderCreatedEvent @event)");

    // Should have ExtractStreamId for OrderShippedEvent
    await Assert.That(runnerSource!).Contains("ExtractStreamId(global::TestNamespace.OrderShippedEvent @event)");

    // Both should access OrderId property
    var orderIdCount = _countOccurrences(runnerSource!, "@event.OrderId");
    await Assert.That(orderIdCount).IsGreaterThanOrEqualTo(2); // At least one for each event type
  }

  /// <summary>
  /// Helper method to count occurrences of a substring in a string.
  /// </summary>
  private static int _countOccurrences(string text, string substring) {
    var count = 0;
    var index = 0;
    while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) != -1) {
      count++;
      index += substring.Length;
    }
    return count;
  }
}
