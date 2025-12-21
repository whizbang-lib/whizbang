using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the PerspectiveDiscoveryGenerator source generator.
/// Ensures 100% code coverage and correct perspective discovery and registration.
/// </summary>
public class PerspectiveDiscoveryGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_EmptyCompilation_GeneratesNothingAsync() {
    // Arrange
    var source = @"
using System;

namespace TestNamespace {
  public class SomeClass {
    public void SomeMethod() { }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should not generate any files when no perspectives exist
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_SinglePerspectiveOneEvent_GeneratesRegistrationAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderModel {
    public string OrderId { get; set; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("AddWhizbangPerspectives");
    await Assert.That(generatedSource!).Contains("OrderPerspective");
    await Assert.That(generatedSource!).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource!).Contains("AddScoped");
    await Assert.That(generatedSource!).Contains("1 discovered perspective(s)");
    await Assert.That(generatedSource!).Contains("1 event handler(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_SinglePerspectiveMultipleEvents_GeneratesMultipleRegistrationsAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record PaymentProcessedEvent : IEvent {
    public string PaymentId { get; init; } = """";
  }

  public record OrderModel {
    public string OrderId { get; set; } = """";
    public string PaymentId { get; set; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent>, IPerspectiveFor<OrderModel, PaymentProcessedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }

    public OrderModel Apply(OrderModel currentData, PaymentProcessedEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderPerspective");
    await Assert.That(generatedSource!).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource!).Contains("PaymentProcessedEvent");
    await Assert.That(generatedSource!).Contains("2 discovered perspective(s)");
    await Assert.That(generatedSource!).Contains("2 event handler(s)");

    // Verify both registrations exist
    var orderCreatedCount = CountOccurrences(generatedSource!, "OrderCreatedEvent");
    var paymentProcessedCount = CountOccurrences(generatedSource!, "PaymentProcessedEvent");
    await Assert.That(orderCreatedCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(paymentProcessedCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_MultiplePerspectives_GeneratesAllRegistrationsAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record InventoryReservedEvent : IEvent {
    public string ReservationId { get; init; } = """";
  }

  public record OrderModel {
    public string OrderId { get; set; } = """";
  }

  public record InventoryModel {
    public string ReservationId { get; set; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }

  public class InventoryPerspective : IPerspectiveFor<InventoryModel, InventoryReservedEvent> {
    public InventoryModel Apply(InventoryModel currentData, InventoryReservedEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderPerspective");
    await Assert.That(generatedSource!).Contains("InventoryPerspective");
    await Assert.That(generatedSource!).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource!).Contains("InventoryReservedEvent");
    await Assert.That(generatedSource!).Contains("2 discovered perspective(s)");
    await Assert.That(generatedSource!).Contains("2 event handler(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_AbstractClass_IsIgnoredAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderModel {
    public string OrderId { get; set; } = """";
  }

  public abstract class BasePerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public abstract OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event);
  }

  public class ConcretePerspective : BasePerspective {
    public override OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should only register the concrete class, not the abstract base
    await Assert.That(generatedSource!).Contains("ConcretePerspective");
    await Assert.That(generatedSource!).DoesNotContain("BasePerspective");
    await Assert.That(generatedSource!).Contains("1 discovered perspective(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_GeneratesDiagnosticForDiscoveredPerspectiveAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderModel {
    public string OrderId { get; set; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should report WHIZ007 diagnostic
    var diagnostics = result.Diagnostics;
    var whiz007 = diagnostics.FirstOrDefault(d => d.Id == "WHIZ007");
    await Assert.That(whiz007).IsNotNull();
    await Assert.That(whiz007!.Severity).IsEqualTo(DiagnosticSeverity.Info);
    await Assert.That(whiz007.GetMessage()).Contains("OrderPerspective");
    await Assert.That(whiz007.GetMessage()).Contains("OrderCreatedEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_GeneratedCodeUsesCorrectNamespaceAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent { }

  public record OrderModel {
    public string OrderId { get; set; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("namespace TestAssembly.Generated");
    await Assert.That(generatedSource!).Contains("using Microsoft.Extensions.DependencyInjection");
    await Assert.That(generatedSource!).Contains("using Whizbang.Core");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_UsesFullyQualifiedTypeNamesAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent { }

  public record OrderModel {
    public string OrderId { get; set; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should use global:: qualified names to avoid ambiguity
    await Assert.That(generatedSource!).Contains("global::TestNamespace.OrderCreatedEvent");
    await Assert.That(generatedSource!).Contains("global::TestNamespace.OrderPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_ReturnsServiceCollectionForMethodChainingAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent { }

  public record OrderModel {
    public string OrderId { get; set; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("return new WhizbangPerspectiveBuilder(services);");
    await Assert.That(generatedSource!).Contains("WhizbangPerspectiveBuilder AddWhizbangPerspectives(this IServiceCollection services)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_ClassWithoutIPerspectiveOf_SkipsAsync() {
    // Arrange - Tests ExtractPerspectiveInfo when class doesn't implement IPerspectiveFor
    var source = @"
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public class NonPerspectiveClass : IDisposable {
    public void Dispose() { }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should not generate registrations (no perspectives found)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_ArrayEventType_HandlesCorrectlyAsync() {
    // Arrange - Tests GetSimpleName with array type (fullyQualifiedName.EndsWith("[]"))
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  // Array event type to test GetSimpleName array handling
  public record OrderBatchEvent : IEvent {
    public string[] OrderIds { get; init; } = Array.Empty<string>();
  }

  public record OrderBatchModel {
    public string[] OrderIds { get; set; } = Array.Empty<string>();
  }

  public class OrderBatchPerspective : IPerspectiveFor<OrderBatchModel, OrderBatchEvent> {
    public OrderBatchModel Apply(OrderBatchModel currentData, OrderBatchEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should discover perspective successfully
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderBatchPerspective");
    await Assert.That(generatedSource!).Contains("OrderBatchEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_TypeInGlobalNamespace_HandlesCorrectlyAsync() {
    // Arrange - Tests GetSimpleName when type has no dots (lastDot < 0)
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

public record GlobalEvent : IEvent {
  public string Id { get; init; } = """";
}

public record GlobalModel {
  public string Id { get; set; } = """";
}

public class GlobalPerspective : IPerspectiveFor<GlobalModel, GlobalEvent> {
  public GlobalModel Apply(GlobalModel currentData, GlobalEvent @event) {
    return currentData;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should discover perspective in global namespace
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("GlobalPerspective");
    await Assert.That(generatedSource!).Contains("GlobalEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_NestedClass_DiscoversCorrectlyAsync() {
    // Arrange - Tests perspective discovery with nested classes
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderModel {
    public string OrderId { get; set; } = """";
  }

  public class Perspectives {
    public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
      public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
        return currentData;
      }
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should discover nested perspective class
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderPerspective");
    await Assert.That(generatedSource!).Contains("OrderEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_InterfaceWithPerspectiveOf_SkipsAsync() {
    // Arrange - Tests that interfaces are skipped (can't instantiate)
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record OrderModel {
    public string OrderId { get; set; } = """";
  }

  public interface IOrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    // Interface can't be instantiated
  }

  public class ConcreteOrderPerspective : IOrderPerspective {
    public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should only discover the concrete class
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("ConcreteOrderPerspective");
    await Assert.That(generatedSource!).DoesNotContain("IOrderPerspective");
    await Assert.That(generatedSource!).Contains("1 discovered perspective(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithArrayEventType_SimplifiesInDiagnosticAsync() {
    // Arrange - Tests GetSimpleName with array event types (lines 157-158)
    var source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    [StreamKey]
    public string OrderId { get; init; } = """";
  }

  public record OrderBatchModel {
    public Guid Id { get; set; }
  }

  public class OrderBatchPerspective : IPerspectiveFor<OrderBatchModel, OrderEvent[]> {
    public OrderBatchModel Apply(OrderBatchModel currentData, OrderEvent[] @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should handle array event type
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("OrderBatchPerspective");

    // Verify diagnostic message simplified array type name
    var infoDiagnostics = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    var whiz007 = infoDiagnostics.FirstOrDefault(d => d.Id == "WHIZ007");
    await Assert.That(whiz007).IsNotNull();
    // GetSimpleName should simplify "global::TestNamespace.OrderEvent[]" to "OrderEvent[]"
    await Assert.That(whiz007!.GetMessage()).Contains("OrderEvent[]");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_EventWithStreamKey_ExtractsStreamKeyPropertyAsync() {
    // Arrange
    var source = @"
using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record ProductCreatedEvent : IEvent {
    [StreamKey]  // Using Whizbang.Core.StreamKeyAttribute
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = """";
  }

  public record ProductModel {
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = """";
  }

  public class ProductPerspective : IPerspectiveFor<ProductModel, ProductCreatedEvent> {
    public ProductModel Apply(ProductModel currentData, ProductCreatedEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("ProductPerspective");

    // Should not have any errors about missing StreamKey
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    await Assert.That(errors).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_EventMissingStreamKey_ReportsWHIZ030DiagnosticAsync() {
    // Arrange
    var source = @"
using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public Guid OrderId { get; init; }  // No [StreamKey] attribute!
    public string CustomerName { get; init; } = """";
  }

  public record OrderModel {
    public Guid OrderId { get; set; }
    public string CustomerName { get; set; } = """";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should report WHIZ030 error
    var whiz030 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ030");
    await Assert.That(whiz030).IsNotNull();
    await Assert.That(whiz030!.Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz030.GetMessage()).Contains("OrderCreatedEvent");
    await Assert.That(whiz030.GetMessage()).Contains("StreamKey");
  }

  /// <summary>
  /// Helper method to count occurrences of a substring in a string.
  /// </summary>
  private static int CountOccurrences(string text, string substring) {
    int count = 0;
    int index = 0;
    while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) != -1) {
      count++;
      index += substring.Length;
    }
    return count;
  }
}
