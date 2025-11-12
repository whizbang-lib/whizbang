using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the PerspectiveDiscoveryGenerator source generator.
/// Ensures 100% code coverage and correct perspective discovery and registration.
/// </summary>
public class PerspectiveDiscoveryGeneratorTests {

  [Test]
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
