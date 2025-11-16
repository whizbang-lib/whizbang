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
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
    public Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
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
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record PaymentProcessedEvent : IEvent {
    public string PaymentId { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent>, IPerspectiveOf<PaymentProcessedEvent> {
    public Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task Update(PaymentProcessedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
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
    await Assert.That(generatedSource!).Contains("1 discovered perspective(s)");
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
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public record InventoryReservedEvent : IEvent {
    public string ReservationId { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
    public Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }
  }

  public class InventoryPerspective : IPerspectiveOf<InventoryReservedEvent> {
    public Task Update(InventoryReservedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
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
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public abstract class BasePerspective : IPerspectiveOf<OrderCreatedEvent> {
    public abstract Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default);
  }

  public class ConcretePerspective : BasePerspective {
    public override Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
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
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
    public Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
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
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent { }

  public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
    public Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("namespace Whizbang.Core.Generated");
    await Assert.That(generatedSource!).Contains("using Microsoft.Extensions.DependencyInjection");
    await Assert.That(generatedSource!).Contains("using Whizbang.Core");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_UsesFullyQualifiedTypeNamesAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent { }

  public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
    public Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
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
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent { }

  public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
    public Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    // Assert
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRegistrations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("return services;");
    await Assert.That(generatedSource!).Contains("IServiceCollection AddWhizbangPerspectives(this IServiceCollection services)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveDiscoveryGenerator_ClassWithoutIPerspectiveOf_SkipsAsync() {
    // Arrange - Tests ExtractPerspectiveInfo when class doesn't implement IPerspectiveOf
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
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  // Array event type to test GetSimpleName array handling
  public record OrderBatchEvent : IEvent {
    public string[] OrderIds { get; init; } = Array.Empty<string>();
  }

  public class OrderBatchPerspective : IPerspectiveOf<OrderBatchEvent> {
    public Task Update(OrderBatchEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
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
using System.Threading;
using System.Threading.Tasks;

public record GlobalEvent : IEvent {
  public string Id { get; init; } = """";
}

public class GlobalPerspective : IPerspectiveOf<GlobalEvent> {
  public Task Update(GlobalEvent @event, CancellationToken cancellationToken = default) {
    return Task.CompletedTask;
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
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public class Perspectives {
    public class OrderPerspective : IPerspectiveOf<OrderEvent> {
      public Task Update(OrderEvent @event, CancellationToken cancellationToken = default) {
        return Task.CompletedTask;
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
using System.Threading;
using System.Threading.Tasks;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public interface IOrderPerspective : IPerspectiveOf<OrderEvent> {
    // Interface can't be instantiated
  }

  public class ConcreteOrderPerspective : IOrderPerspective {
    public Task Update(OrderEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
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

namespace TestNamespace {
  public record OrderEvent : IEvent {
    public string OrderId { get; init; } = """";
  }

  public class OrderBatchPerspective : IPerspectiveOf<OrderEvent[]> {
    public Guid Id { get; set; }

    public Task Update(OrderEvent[] @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
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
