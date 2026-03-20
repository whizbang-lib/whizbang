using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    const string source = @"
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
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("AddWhizbangPerspectives");
    await Assert.That(generatedSource).Contains("OrderPerspective");
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("AddScoped");
    await Assert.That(generatedSource).Contains("1 discovered perspective(s)");
    await Assert.That(generatedSource).Contains("1 event handler(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_SinglePerspectiveMultipleEvents_GeneratesMultipleRegistrationsAsync() {
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

  public record PaymentProcessedEvent : IEvent {
    public string PaymentId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
    public string PaymentId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent>, IPerspectiveFor<OrderModel, PaymentProcessedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }

    public OrderModel Apply(OrderModel currentData, PaymentProcessedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("OrderPerspective");
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("PaymentProcessedEvent");
    await Assert.That(generatedSource).Contains("2 discovered perspective(s)");
    await Assert.That(generatedSource).Contains("2 event handler(s)");

    // Verify both registrations exist
    var orderCreatedCount = _countOccurrences(generatedSource!, "OrderCreatedEvent");
    var paymentProcessedCount = _countOccurrences(generatedSource!, "PaymentProcessedEvent");
    await Assert.That(orderCreatedCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(paymentProcessedCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_MultiplePerspectives_GeneratesAllRegistrationsAsync() {
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

  public record InventoryReservedEvent : IEvent {
    public string ReservationId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public record InventoryModel {
    public string ReservationId { get; set; } = "";
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
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("OrderPerspective");
    await Assert.That(generatedSource).Contains("InventoryPerspective");
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("InventoryReservedEvent");
    await Assert.That(generatedSource).Contains("2 discovered perspective(s)");
    await Assert.That(generatedSource).Contains("2 event handler(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_AbstractClass_IsIgnoredAsync() {
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

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public abstract class BasePerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public abstract OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event);
  }

  public class ConcretePerspective : BasePerspective {
    public override OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should only register the concrete class, not the abstract base
    await Assert.That(generatedSource).Contains("ConcretePerspective");
    await Assert.That(generatedSource).DoesNotContain("BasePerspective");
    await Assert.That(generatedSource).Contains("1 discovered perspective(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_GeneratesDiagnosticForDiscoveredPerspectiveAsync() {
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

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should report WHIZ007 diagnostic
    var diagnostics = result.Diagnostics;
    var whiz007 = diagnostics.FirstOrDefault(d => d.Id == "WHIZ007");
    await Assert.That(whiz007).IsNotNull();
    await Assert.That(whiz007!.Severity).IsEqualTo(DiagnosticSeverity.Info);
    await Assert.That(whiz007.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderPerspective");
    await Assert.That(whiz007.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderCreatedEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_GeneratedCodeUsesCorrectNamespaceAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent { }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("namespace TestAssembly.Generated");
    await Assert.That(generatedSource).Contains("using Microsoft.Extensions.DependencyInjection");
    await Assert.That(generatedSource).Contains("using Whizbang.Core");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_UsesFullyQualifiedTypeNamesAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent { }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should use global:: qualified names to avoid ambiguity
    await Assert.That(generatedSource).Contains("global::TestNamespace.OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("global::TestNamespace.OrderPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_ReturnsServiceCollectionForMethodChainingAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent { }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("return new WhizbangPerspectiveBuilder(services);");
    await Assert.That(generatedSource).Contains("WhizbangPerspectiveBuilder AddWhizbangPerspectives(this IServiceCollection services)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_ClassWithoutIPerspectiveOf_SkipsAsync() {
    // Arrange - Tests ExtractPerspectiveInfo when class doesn't implement IPerspectiveFor
    const string source = @"
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
    const string source = @"
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
    await Assert.That(generatedSource).Contains("OrderBatchPerspective");
    await Assert.That(generatedSource).Contains("OrderBatchEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_TypeInGlobalNamespace_HandlesCorrectlyAsync() {
    // Arrange - Tests GetSimpleName when type has no dots (lastDot < 0)
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

public record GlobalEvent : IEvent {
  public string Id { get; init; } = "";
}

public record GlobalModel {
  public string Id { get; set; } = "";
}

public class GlobalPerspective : IPerspectiveFor<GlobalModel, GlobalEvent> {
  public GlobalModel Apply(GlobalModel currentData, GlobalEvent @event) {
    return currentData;
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should discover perspective in global namespace
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("GlobalPerspective");
    await Assert.That(generatedSource).Contains("GlobalEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_NestedClass_DiscoversCorrectlyAsync() {
    // Arrange - Tests perspective discovery with nested classes
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class Perspectives {
    public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
      public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
        return currentData;
      }
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should discover nested perspective class
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("OrderPerspective");
    await Assert.That(generatedSource).Contains("OrderEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_InterfaceWithPerspectiveOf_SkipsAsync() {
    // Arrange - Tests that interfaces are skipped (can't instantiate)
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public interface IOrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    // Interface can't be instantiated
  }

  public class ConcreteOrderPerspective : IOrderPerspective {
    public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should only discover the concrete class
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("ConcreteOrderPerspective");
    await Assert.That(generatedSource).DoesNotContain("IOrderPerspective");
    await Assert.That(generatedSource).Contains("1 discovered perspective(s)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithArrayEventType_SimplifiesInDiagnosticAsync() {
    // Arrange - Tests GetSimpleName with array event types (lines 157-158)
    const string source = """

using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    [StreamId]
    public string OrderId { get; init; } = "";
  }

  public record OrderBatchModel {
    public Guid Id { get; set; }
  }

  public class OrderBatchPerspective : IPerspectiveFor<OrderBatchModel, OrderEvent[]> {
    public OrderBatchModel Apply(OrderBatchModel currentData, OrderEvent[] @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should handle array event type
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("OrderBatchPerspective");

    // Verify diagnostic message simplified array type name
    var infoDiagnostics = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToArray();
    var whiz007 = infoDiagnostics.FirstOrDefault(d => d.Id == "WHIZ007");
    await Assert.That(whiz007).IsNotNull();
    // GetSimpleName should simplify "global::TestNamespace.OrderEvent[]" to "OrderEvent[]"
    await Assert.That(whiz007!.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderEvent[]");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_EventWithStreamId_ExtractsStreamIdPropertyAsync() {
    // Arrange
    const string source = """

using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record ProductCreatedEvent : IEvent {
    [StreamId]  // Using Whizbang.Core.StreamIdAttribute
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = "";
  }

  public record ProductModel {
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";
  }

  public class ProductPerspective : IPerspectiveFor<ProductModel, ProductCreatedEvent> {
    public ProductModel Apply(ProductModel currentData, ProductCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("ProductPerspective");

    // Should not have any errors about missing StreamId
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    await Assert.That(errors).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_EventMissingStreamId_ReportsWHIZ030DiagnosticAsync() {
    // Arrange
    const string source = """

using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public Guid OrderId { get; init; }  // No [StreamId] attribute!
    public string CustomerName { get; init; } = "";
  }

  public record OrderModel {
    public Guid OrderId { get; set; }
    public string CustomerName { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should report WHIZ030 error
    var whiz030 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ030");
    await Assert.That(whiz030).IsNotNull();
    await Assert.That(whiz030!.Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz030.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderCreatedEvent");
    await Assert.That(whiz030.GetMessage(CultureInfo.InvariantCulture)).Contains("StreamId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_EventWithMultipleStreamIds_ReportsWHIZ031DiagnosticAsync() {
    // Arrange
    const string source = """

using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }  // First StreamId

    [StreamId]
    public Guid CustomerId { get; init; }  // Second StreamId - ERROR!

    public string CustomerName { get; init; } = "";
  }

  public record OrderModel {
    public Guid OrderId { get; set; }
    public string CustomerName { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should report WHIZ031 error
    var whiz031 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ031");
    await Assert.That(whiz031).IsNotNull();
    await Assert.That(whiz031!.Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz031.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderCreatedEvent");
    await Assert.That(whiz031.GetMessage(CultureInfo.InvariantCulture)).Contains("multiple");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_ArrayEventTypeWithStreamId_ValidatesElementTypeAsync() {
    // Arrange - Tests that array events validate the element type, not the array itself
    const string source = """

using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }  // StreamId on element type
    public string CustomerName { get; init; } = "";
  }

  public record OrderBatchModel {
    public Guid[] OrderIds { get; set; } = Array.Empty<Guid>();
  }

  // Perspective uses OrderEvent[] (array type)
  public class OrderBatchPerspective : IPerspectiveFor<OrderBatchModel, OrderEvent[]> {
    public OrderBatchModel Apply(OrderBatchModel currentData, OrderEvent[] @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should not report WHIZ030 error (array element type has StreamId)
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    await Assert.That(errors).IsEmpty();

    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_ArrayEventTypeMissingStreamId_ReportsWHIZ030Async() {
    // Arrange - Tests that array events validate element type for missing StreamId
    const string source = """

using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public Guid OrderId { get; init; }  // NO StreamId on element type
    public string CustomerName { get; init; } = "";
  }

  public record OrderBatchModel {
    public Guid[] OrderIds { get; set; } = Array.Empty<Guid>();
  }

  // Perspective uses OrderEvent[] (array type)
  public class OrderBatchPerspective : IPerspectiveFor<OrderBatchModel, OrderEvent[]> {
    public OrderBatchModel Apply(OrderBatchModel currentData, OrderEvent[] @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should report WHIZ030 error for element type
    var whiz030 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ030");
    await Assert.That(whiz030).IsNotNull();
    await Assert.That(whiz030!.Severity).IsEqualTo(DiagnosticSeverity.Error);
    // Should reference simplified event name (without global:: prefix)
    await Assert.That(whiz030.GetMessage(CultureInfo.InvariantCulture)).Contains("OrderEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_InheritedStreamId_FindsAttributeOnBaseClassAsync() {
    // Arrange - Tests that [StreamId] is found on inherited properties from base class
    const string source = """

using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  // Base event class with [StreamId] on inherited property
  public abstract record BaseEvent : IEvent {
    [StreamId]
    public virtual Guid StreamId { get; init; }
  }

  // Derived event that inherits StreamId from base class
  public record OrderCreatedEvent : BaseEvent {
    public string OrderName { get; init; } = "";
  }

  public record OrderModel {
    public Guid StreamId { get; set; }
    public string OrderName { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return new OrderModel { StreamId = @event.StreamId, OrderName = @event.OrderName };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should NOT report WHIZ030 error (StreamId is inherited from base class)
    var whiz030 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ030");
    await Assert.That(whiz030).IsNull();

    // Should generate perspective registration successfully
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("OrderPerspective");
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
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

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_MessageAssociation_IsGeneratedAsync() {
    // Arrange - Tests that MessageAssociation record is generated
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - MessageAssociation record should be generated
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("public sealed record MessageAssociation");
    await Assert.That(generatedSource).Contains("string MessageType");
    await Assert.That(generatedSource).Contains("string AssociationType");
    await Assert.That(generatedSource).Contains("string TargetName");
    await Assert.That(generatedSource).Contains("string ServiceName");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_GetMessageAssociations_ReturnsCorrectAssociationsAsync() {
    // Arrange - Tests that GetMessageAssociations() method is generated and returns correct data
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderUpdatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent, OrderUpdatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) => currentData;
    public OrderModel Apply(OrderModel currentData, OrderUpdatedEvent @event) => currentData;
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - GetMessageAssociations() should exist and return associations
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("public static System.Collections.Generic.IReadOnlyList<MessageAssociation> GetMessageAssociations(string serviceName)");

    // Should return array with 2 associations (OrderCreated and OrderUpdated)
    await Assert.That(generatedSource).Contains("new MessageAssociation");
    await Assert.That(generatedSource).Contains("TestNamespace.OrderCreatedEvent, ");
    await Assert.That(generatedSource).Contains("TestNamespace.OrderUpdatedEvent, ");
    await Assert.That(generatedSource).Contains("\"perspective\"");
    await Assert.That(generatedSource).Contains("\"OrderPerspective\"");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_GetPerspectivesForEvent_HelperMethodGeneratedAsync() {
    // Arrange - Tests that GetPerspectivesForEvent() helper is generated
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) => currentData;
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - GetPerspectivesForEvent() should be generated
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("public static System.Collections.Generic.IEnumerable<string> GetPerspectivesForEvent(string eventType, string serviceName)");
    await Assert.That(generatedSource).Contains("Where(a => a.MessageType == eventType && a.AssociationType == \"perspective\")");
    await Assert.That(generatedSource).Contains("Select(a => a.TargetName)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_GetEventsForPerspective_HelperMethodGeneratedAsync() {
    // Arrange - Tests that GetEventsForPerspective() helper is generated
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) => currentData;
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - GetEventsForPerspective() should be generated
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("public static System.Collections.Generic.IEnumerable<string> GetEventsForPerspective(string perspectiveName, string serviceName)");
    await Assert.That(generatedSource).Contains("Where(a => a.TargetName == perspectiveName && a.AssociationType == \"perspective\")");
    await Assert.That(generatedSource).Contains("Select(a => a.MessageType)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_MultiplePerspectives_AllAssociationsReturnedAsync() {
    // Arrange - Tests that all associations from multiple perspectives are returned
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record PaymentProcessedEvent : IEvent {
    public string PaymentId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public record PaymentModel {
    public string PaymentId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) => currentData;
  }

  public class PaymentPerspective : IPerspectiveFor<PaymentModel, PaymentProcessedEvent> {
    public PaymentModel Apply(PaymentModel currentData, PaymentProcessedEvent @event) => currentData;
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should have 2 associations (one per perspective)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Count MessageAssociation instantiations - should be 2
    var associationCount = _countOccurrences(generatedSource!, "new MessageAssociation(");
    await Assert.That(associationCount).IsEqualTo(2);

    // Verify both perspectives are represented
    await Assert.That(generatedSource).Contains("\"OrderPerspective\"");
    await Assert.That(generatedSource).Contains("\"PaymentPerspective\"");
  }

  /// <summary>
  /// Phase 3 Tests: PerspectiveAssociationInfo with Delegates
  /// </summary>

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_GeneratesGetPerspectiveAssociationsMethodAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return new OrderModel { OrderId = @event.OrderId };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should generate GetPerspectiveAssociations<TModel, TEvent>() method
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("GetPerspectiveAssociations<TModel, TEvent>");
    await Assert.That(generatedSource).Contains("PerspectiveAssociationInfo<TModel, TEvent>");
    await Assert.That(generatedSource).Contains("where TEvent : IEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_GeneratesPerspectiveAssociationInfoRecordAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record ProductCreatedEvent : IEvent {
    public string ProductId { get; init; } = "";
  }

  public record ProductModel {
    public string ProductId { get; set; } = "";
  }

  public class ProductPerspective : IPerspectiveFor<ProductModel, ProductCreatedEvent> {
    public ProductModel Apply(ProductModel currentData, ProductCreatedEvent @event) {
      return new ProductModel { ProductId = @event.ProductId };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should generate PerspectiveAssociationInfo record definition
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("public sealed record PerspectiveAssociationInfo<TModel, TEvent>");
    await Assert.That(generatedSource).Contains("string MessageType");
    await Assert.That(generatedSource).Contains("string TargetName");
    await Assert.That(generatedSource).Contains("string ServiceName");
    await Assert.That(generatedSource).Contains("Func<TModel, TEvent, TModel> ApplyDelegate");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_GeneratesDelegateWithTypeCheckAsync() {
    // Arrange
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record InventoryReservedEvent : IEvent {
    public int Quantity { get; init; }
  }

  public record InventoryModel {
    public int AvailableQuantity { get; set; }
  }

  public class InventoryPerspective : IPerspectiveFor<InventoryModel, InventoryReservedEvent> {
    public InventoryModel Apply(InventoryModel currentData, InventoryReservedEvent @event) {
      return new InventoryModel { AvailableQuantity = currentData.AvailableQuantity - @event.Quantity };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should generate typeof() checks for compile-time type matching
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("typeof(TModel)");
    await Assert.That(generatedSource).Contains("typeof(TEvent)");
    await Assert.That(generatedSource).Contains("InventoryModel");
    await Assert.That(generatedSource).Contains("InventoryReservedEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_MultiplePerspectives_GeneratesSeparateDelegatesAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record PaymentProcessedEvent : IEvent {
    public string PaymentId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public record PaymentModel {
    public string PaymentId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return new OrderModel { OrderId = @event.OrderId };
    }
  }

  public class PaymentPerspective : IPerspectiveFor<PaymentModel, PaymentProcessedEvent> {
    public PaymentModel Apply(PaymentModel currentData, PaymentProcessedEvent @event) {
      return new PaymentModel { PaymentId = @event.PaymentId };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should generate separate type checks for each perspective
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Both perspective types should be checked
    await Assert.That(generatedSource).Contains("OrderModel");
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("PaymentModel");
    await Assert.That(generatedSource).Contains("PaymentProcessedEvent");

    // Both perspectives should have delegate instantiation
    await Assert.That(generatedSource).Contains("OrderPerspective");
    await Assert.That(generatedSource).Contains("PaymentPerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_EmptyResult_ForNonMatchingTypesAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should return empty array when types don't match
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("return Array.Empty<PerspectiveAssociationInfo<TModel, TEvent>>();");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_GeneratesAOTCompatibleDelegatesAsync() {
    // Arrange
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record ShipmentSentEvent : IEvent {
    public string TrackingNumber { get; init; } = "";
  }

  public record ShipmentModel {
    public string TrackingNumber { get; set; } = "";
  }

  public class ShipmentPerspective : IPerspectiveFor<ShipmentModel, ShipmentSentEvent> {
    public ShipmentModel Apply(ShipmentModel currentData, ShipmentSentEvent @event) {
      return new ShipmentModel { TrackingNumber = @event.TrackingNumber };
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should use 'new' keyword (AOT-compatible), not Activator.CreateInstance
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).DoesNotContain("Activator.CreateInstance");
    await Assert.That(generatedSource).DoesNotContain("MethodInfo");
    await Assert.That(generatedSource).DoesNotContain("Invoke(");

    // Should use compile-time instantiation with global:: prefix
    await Assert.That(generatedSource).Contains("new global::TestNamespace.ShipmentPerspective()");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_DoesNotGenerateEFCoreCodeAsync() {
    // Arrange - Base generator should NOT include EF Core-specific code
    // EF Core code should be in Whizbang.Data.EFCore.Postgres.Generators instead
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = "";
  }

  public record OrderModel {
    public string OrderId { get; set; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should NOT contain EF Core specific code
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should NOT have EF Core usings
    await Assert.That(generatedSource).DoesNotContain("using Microsoft.EntityFrameworkCore;");
    await Assert.That(generatedSource).DoesNotContain("using Microsoft.Extensions.Logging;");

    // Should NOT have RegisterPerspectiveAssociationsAsync method
    await Assert.That(generatedSource).DoesNotContain("RegisterPerspectiveAssociationsAsync");
    await Assert.That(generatedSource).DoesNotContain("ExecuteSqlRawAsync");
    await Assert.That(generatedSource).DoesNotContain("DbContext");

    // Should still have non-EF Core functionality
    await Assert.That(generatedSource).Contains("GetMessageAssociations");
    await Assert.That(generatedSource).Contains("AddWhizbangPerspectives");
  }

  // ==================== Multi-Event Support Tests (6-50 events) ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PerspectiveWith10Events_GeneratesRegistrationsAsync() {
    // Arrange - Perspective implementing IPerspectiveFor with 10 event types
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record Event1 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event2 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event3 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event4 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event5 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event6 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event7 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event8 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event9 : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Event10 : IEvent { [StreamId] public Guid Id { get; init; } }

  public record MultiEventModel {
    [StreamId]
    public Guid Id { get; init; }
    public int Counter { get; init; }
  }

  public class MultiEventPerspective : IPerspectiveFor<MultiEventModel, Event1, Event2, Event3, Event4, Event5, Event6, Event7, Event8, Event9, Event10> {
    public MultiEventModel Apply(MultiEventModel current, Event1 @event) => current with { Counter = current.Counter + 1 };
    public MultiEventModel Apply(MultiEventModel current, Event2 @event) => current with { Counter = current.Counter + 2 };
    public MultiEventModel Apply(MultiEventModel current, Event3 @event) => current with { Counter = current.Counter + 3 };
    public MultiEventModel Apply(MultiEventModel current, Event4 @event) => current with { Counter = current.Counter + 4 };
    public MultiEventModel Apply(MultiEventModel current, Event5 @event) => current with { Counter = current.Counter + 5 };
    public MultiEventModel Apply(MultiEventModel current, Event6 @event) => current with { Counter = current.Counter + 6 };
    public MultiEventModel Apply(MultiEventModel current, Event7 @event) => current with { Counter = current.Counter + 7 };
    public MultiEventModel Apply(MultiEventModel current, Event8 @event) => current with { Counter = current.Counter + 8 };
    public MultiEventModel Apply(MultiEventModel current, Event9 @event) => current with { Counter = current.Counter + 9 };
    public MultiEventModel Apply(MultiEventModel current, Event10 @event) => current with { Counter = current.Counter + 10 };
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should generate registrations for perspective with 10 events
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("MultiEventPerspective");
    await Assert.That(generatedSource).Contains("MultiEventModel");
    // Should contain all 10 event types
    await Assert.That(generatedSource).Contains("Event1");
    await Assert.That(generatedSource).Contains("Event10");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PerspectiveWith25Events_GeneratesRegistrationsAsync() {
    // Arrange - Perspective implementing IPerspectiveFor with 25 event types
    var eventDeclarations = string.Join("\n",
        Enumerable.Range(1, 25).Select(i =>
            $"  public record Evt{i} : IEvent {{ [StreamId] public Guid Id {{ get; init; }} }}"));

    var applyMethods = string.Join("\n",
        Enumerable.Range(1, 25).Select(i =>
            $"    public Model Apply(Model c, Evt{i} e) => c with {{ Counter = c.Counter + {i} }};"));

    var eventTypeParams = string.Join(", ", Enumerable.Range(1, 25).Select(i => $"Evt{i}"));

    var source = $@"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {{
{eventDeclarations}

  public record Model {{
    [StreamId]
    public Guid Id {{ get; init; }}
    public int Counter {{ get; init; }}
  }}

  public class BigPerspective : IPerspectiveFor<Model, {eventTypeParams}> {{
{applyMethods}
  }}
}}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - Should generate registrations for perspective with 25 events
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("BigPerspective");
    await Assert.That(generatedSource).Contains("Model");
    // Should contain first and last event types
    await Assert.That(generatedSource).Contains("Evt1");
    await Assert.That(generatedSource).Contains("Evt25");
  }

  // ==================== Nested Perspective CLR Type Name Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_NestedPerspective_UsesClrTypeNameInMessageAssociationAsync() {
    // Arrange - Tests that nested perspective classes use CLR format names (Parent+Child)
    // This is critical for database storage and registry lookup consistency
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record AccountCreatedEvent : IEvent {
    [StreamId]
    public Guid AccountId { get; init; }
  }

  /// <summary>
  /// This is a nested perspective class pattern commonly used in DDD.
  /// The perspective is nested inside the aggregate root class.
  /// </summary>
  public static class ActiveAccount {
    public record Model {
      [StreamId]
      public Guid AccountId { get; init; }
      public string Name { get; init; } = "";
    }

    public class Projection : IPerspectiveFor<Model, AccountCreatedEvent> {
      public Model Apply(Model currentData, AccountCreatedEvent @event) {
        return currentData;
      }
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - MessageAssociation should use CLR format name with '+' for nested types
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // The target name should be "TestNamespace.ActiveAccount+Projection" (CLR format)
    // NOT "Projection" (simple name) or "ActiveAccount.Projection" (display format)
    await Assert.That(generatedSource).Contains("TestNamespace.ActiveAccount+Projection");

    // Should NOT contain just "Projection" as the target name
    // (we allow it in other contexts, but the MessageAssociation target must be CLR format)
    await Assert.That(generatedSource).Contains(@"""TestNamespace.ActiveAccount+Projection""");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_TopLevelPerspective_UsesClrTypeNameInMessageAssociationAsync() {
    // Arrange - Tests that top-level perspective classes also use CLR format names
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace.Perspectives {
  public record OrderCreatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - MessageAssociation should use CLR format name (namespace.class)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // The target name should be fully qualified CLR format
    await Assert.That(generatedSource).Contains(@"""TestNamespace.Perspectives.OrderPerspective""");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_DeeplyNestedPerspective_UsesClrTypeNameAsync() {
    // Arrange - Tests deeply nested perspective classes (multiple levels of nesting)
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record SessionEvent : IEvent {
    [StreamId]
    public Guid SessionId { get; init; }
  }

  /// <summary>
  /// Two levels of nesting: Sessions.Active.Projection
  /// CLR format should be: TestNamespace.Sessions+Active+Projection
  /// </summary>
  public static class Sessions {
    public static class Active {
      public record Model {
        [StreamId]
        public Guid SessionId { get; init; }
      }

      public class Projection : IPerspectiveFor<Model, SessionEvent> {
        public Model Apply(Model currentData, SessionEvent @event) {
          return currentData;
        }
      }
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert - MessageAssociation should use CLR format with multiple '+' for each nesting level
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // The target name should use '+' for each level of nesting
    await Assert.That(generatedSource).Contains(@"""TestNamespace.Sessions+Active+Projection""");
  }
}
