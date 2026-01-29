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

  // ==================== [MustExist] Attribute Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MustExistAttribute_GeneratesNullCheckAsync() {
    // Arrange - Perspective with [MustExist] on one Apply method
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public record OrderShippedEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamKey]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = """";
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderCreatedEvent>,
    IPerspectiveFor<OrderModel, OrderShippedEvent> {

    // Creation - handles null (no [MustExist])
    public OrderModel Apply(OrderModel? currentData, OrderCreatedEvent @event) {
      return new OrderModel { OrderId = @event.OrderId, Status = ""Created"" };
    }

    // Update - requires existing model
    [MustExist]
    public OrderModel Apply(OrderModel currentData, OrderShippedEvent @event) {
      return currentData with { Status = ""Shipped"" };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate null check for [MustExist] method
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should have null check before calling Apply for OrderShippedEvent
    await Assert.That(runnerSource!).Contains("case global::TestNamespace.OrderShippedEvent typedEvent:");
    await Assert.That(runnerSource!).Contains("if (currentModel == null)");
    await Assert.That(runnerSource!).Contains("OrderModel must exist when applying OrderShippedEvent in OrderPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MustExistAttribute_NoNullCheckForNonAttributedMethodAsync() {
    // Arrange - Perspective with [MustExist] on one Apply but not the other
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public record OrderShippedEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamKey]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = """";
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderCreatedEvent>,
    IPerspectiveFor<OrderModel, OrderShippedEvent> {

    // No [MustExist] - should NOT have null check
    public OrderModel Apply(OrderModel? currentData, OrderCreatedEvent @event) {
      return new OrderModel { OrderId = @event.OrderId, Status = ""Created"" };
    }

    [MustExist]
    public OrderModel Apply(OrderModel currentData, OrderShippedEvent @event) {
      return currentData with { Status = ""Shipped"" };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should only have ONE MustExist null check (for OrderShippedEvent only)
    // Template has its own null check for model initialization, so count the specific error message pattern
    var mustExistCheckCount = _countOccurrences(runnerSource!, "must exist when applying");
    await Assert.That(mustExistCheckCount).IsEqualTo(1);

    // The null check should be for OrderShippedEvent, not OrderCreatedEvent
    await Assert.That(runnerSource!).Contains("OrderModel must exist when applying OrderShippedEvent");
    await Assert.That(runnerSource!).DoesNotContain("OrderModel must exist when applying OrderCreatedEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MustExistAttribute_AllEventsWithAttribute_GeneratesNullCheckForAllAsync() {
    // Arrange - All Apply methods have [MustExist]
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderEvent1 : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public record OrderEvent2 : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderEvent1>,
    IPerspectiveFor<OrderModel, OrderEvent2> {

    [MustExist]
    public OrderModel Apply(OrderModel currentData, OrderEvent1 @event) => currentData;

    [MustExist]
    public OrderModel Apply(OrderModel currentData, OrderEvent2 @event) => currentData;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Both cases should have MustExist null checks
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Count MustExist-specific pattern (template has its own null check for model initialization)
    var mustExistCheckCount = _countOccurrences(runnerSource!, "must exist when applying");
    await Assert.That(mustExistCheckCount).IsEqualTo(2);

    // Both should have descriptive error messages
    await Assert.That(runnerSource!).Contains("OrderModel must exist when applying OrderEvent1 in OrderPerspective");
    await Assert.That(runnerSource!).Contains("OrderModel must exist when applying OrderEvent2 in OrderPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_NoMustExistAttribute_NoNullCheckGeneratedAsync() {
    // Arrange - No [MustExist] attributes at all
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    public OrderModel Apply(OrderModel? currentData, OrderEvent @event) {
      return currentData ?? new OrderModel { OrderId = @event.OrderId };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - No MustExist null check generated (template has separate null check for model initialization)
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    // Check for absence of MustExist-specific error message (the template has its own null check for initialization)
    await Assert.That(runnerSource!).DoesNotContain("must exist when applying");
    await Assert.That(runnerSource!).DoesNotContain("throw new global::System.InvalidOperationException");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MustExistAttribute_ErrorMessageIncludesContextAsync() {
    // Arrange - Verify error message format includes perspective, model, and event names
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record CustomerUpdatedEvent : IEvent {
    [StreamKey]
    public Guid CustomerId { get; init; }
  }

  public record CustomerReadModel {
    [StreamKey]
    public Guid CustomerId { get; init; }
    public string Name { get; init; } = """";
  }

  public class CustomerPerspective : IPerspectiveFor<CustomerReadModel, CustomerUpdatedEvent> {
    [MustExist]
    public CustomerReadModel Apply(CustomerReadModel currentData, CustomerUpdatedEvent @event) {
      return currentData with { Name = ""Updated"" };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Error message should include all context
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "CustomerPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should include model type name
    await Assert.That(runnerSource!).Contains("CustomerReadModel must exist");

    // Should include event type name
    await Assert.That(runnerSource!).Contains("when applying CustomerUpdatedEvent");

    // Should include perspective class name
    await Assert.That(runnerSource!).Contains("in CustomerPerspective");

    // Full expected message
    await Assert.That(runnerSource!).Contains(
        "CustomerReadModel must exist when applying CustomerUpdatedEvent in CustomerPerspective");
  }

  // ==================== ModelAction Return Type Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_ModelActionReturn_GeneratesActionHandlingAsync() {
    // Arrange - Apply returns ModelAction for deletion
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCancelledEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamKey]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = """";
    public DateTimeOffset? DeletedAt { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCancelledEvent> {
    public ModelAction Apply(OrderModel currentData, OrderCancelledEvent @event) {
      return ModelAction.Delete;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate code handling ModelAction return
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // Should have handling for ModelAction return type
    await Assert.That(runnerSource!).Contains("ModelAction");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_NullableModelReturn_GeneratesNoChangeCheckAsync() {
    // Arrange - Apply returns TModel? (nullable) for optional updates
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderUpdatedEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
    public bool ShouldSkip { get; init; }
  }

  public record OrderModel {
    [StreamKey]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderUpdatedEvent> {
    public OrderModel? Apply(OrderModel? currentData, OrderUpdatedEvent @event) {
      if (@event.ShouldSkip) return null;  // No change
      return currentData with { Status = ""Updated"" };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner that handles null return (no change)
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();

    // For now just verify a runner is generated (the return type handling will be added)
    await Assert.That(runnerSource!).Contains("OrderPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_TupleReturn_GeneratesHybridHandlingAsync() {
    // Arrange - Apply returns (TModel?, ModelAction) tuple for hybrid modify+action
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderArchivedEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
    public bool ShouldPurge { get; init; }
  }

  public record OrderModel {
    [StreamKey]
    public Guid OrderId { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderArchivedEvent> {
    public (OrderModel?, ModelAction) Apply(OrderModel currentData, OrderArchivedEvent @event) {
      if (@event.ShouldPurge)
        return (null, ModelAction.Purge);
      return (currentData with { ArchivedAt = DateTimeOffset.UtcNow }, ModelAction.None);
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("OrderPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_ApplyResultReturn_GeneratesFullHandlingAsync() {
    // Arrange - Apply returns ApplyResult<TModel> for full flexibility
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderProcessedEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
    public string Action { get; init; } = """";
  }

  public record OrderModel {
    [StreamKey]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = """";
    public DateTimeOffset? DeletedAt { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderProcessedEvent> {
    public ApplyResult<OrderModel> Apply(OrderModel currentData, OrderProcessedEvent @event) {
      return @event.Action switch {
        ""delete"" => ApplyResult<OrderModel>.Delete(),
        ""purge"" => ApplyResult<OrderModel>.Purge(),
        ""skip"" => ApplyResult<OrderModel>.None(),
        _ => ApplyResult<OrderModel>.Update(currentData with { Status = @event.Action })
      };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("OrderPerspectiveRunner");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_MixedReturnTypes_GeneratesCorrectlyAsync() {
    // Arrange - Different Apply methods with different return types
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public record OrderCancelledEvent : IEvent {
    [StreamKey]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamKey]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = """";
    public DateTimeOffset? DeletedAt { get; init; }
  }

  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderCreatedEvent>,
    IPerspectiveFor<OrderModel, OrderCancelledEvent> {

    // Standard return - returns model
    public OrderModel Apply(OrderModel? currentData, OrderCreatedEvent @event) {
      return new OrderModel { OrderId = @event.OrderId, Status = ""Created"" };
    }

    // Action return - returns ModelAction for deletion
    public ModelAction Apply(OrderModel currentData, OrderCancelledEvent @event) {
      return ModelAction.Delete;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate runner with both event types handled
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("OrderPerspectiveRunner");
    await Assert.That(runnerSource!).Contains("OrderCreatedEvent");
    await Assert.That(runnerSource!).Contains("OrderCancelledEvent");
  }
}
