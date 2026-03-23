using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the EFCorePerspectiveAssociationGenerator source generator.
/// Ensures EF Core-specific perspective association registration code is generated correctly.
/// </summary>
public class EFCorePerspectiveAssociationGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspective_GeneratesEFCoreRegistrationMethodAsync() {
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
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    // Assert - Should generate EF Core specific registration method
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Should have EF Core usings
    await Assert.That(generatedSource).Contains("using Microsoft.EntityFrameworkCore;");
    await Assert.That(generatedSource).Contains("using Microsoft.Extensions.Logging;");

    // Should have RegisterPerspectiveAssociationsAsync method
    await Assert.That(generatedSource).Contains("RegisterPerspectiveAssociationsAsync");
    await Assert.That(generatedSource).Contains("DbContext");
    await Assert.That(generatedSource).Contains("ExecuteSqlRawAsync");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EmptyCompilation_GeneratesNothingAsync() {
    // Arrange
    const string source = @"
using System;

namespace TestNamespace {
  public class SomeClass {
    public void SomeMethod() { }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    // Assert - Should not generate any files when no perspectives exist
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultiplePerspectives_GeneratesAllAssociationsAsync() {
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
      return currentData;
    }
  }

  public class PaymentPerspective : IPerspectiveFor<PaymentModel, PaymentProcessedEvent> {
    public PaymentModel Apply(PaymentModel currentData, PaymentProcessedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    // Assert - Should generate associations for both perspectives
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("OrderPerspective");
    await Assert.That(generatedSource).Contains("PaymentPerspective");
    await Assert.That(generatedSource).Contains("OrderCreatedEvent");
    await Assert.That(generatedSource).Contains("PaymentProcessedEvent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesJsonFormatForDatabaseAsync() {
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
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    // Assert - Should generate JSON format for database registration
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("MessageType");
    await Assert.That(generatedSource).Contains("AssociationType");
    await Assert.That(generatedSource).Contains("TargetName");
    await Assert.That(generatedSource).Contains("ServiceName");
    await Assert.That(generatedSource).Contains("perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AbstractClass_IsIgnoredAsync() {
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
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    // Assert - Should only register the concrete class, not the abstract base
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("ConcretePerspective");
    await Assert.That(generatedSource).DoesNotContain("BasePerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DuplicatePerspectiveEventPairs_DeduplicatesAsync() {
    // Arrange - A perspective implementing multiple interfaces that share the same event type
    // This can cause duplicate (PerspectiveClassName, MessageTypeName) pairs which would cause
    // "ON CONFLICT DO UPDATE command cannot affect row a second time" PostgreSQL errors
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

  // A perspective implementing two IPerspectiveFor interfaces
  public class OrderPerspective :
    IPerspectiveFor<OrderModel, OrderCreatedEvent>,
    IPerspectiveFor<OrderModel, OrderCreatedEvent, OrderUpdatedEvent> {

    public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) {
      return currentData;
    }

    public OrderModel Apply(OrderModel currentData, OrderUpdatedEvent @event) {
      return currentData;
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    // Assert - Should generate associations but deduplicate duplicate pairs
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Count occurrences of OrderCreatedEvent - should only appear once even though
    // it's present in both IPerspectiveFor<OrderModel, OrderCreatedEvent> and
    // IPerspectiveFor<OrderModel, OrderCreatedEvent, OrderUpdatedEvent>
    var orderCreatedOccurrences = _countOccurrences(generatedSource!, "OrderCreatedEvent");
    await Assert.That(orderCreatedOccurrences).IsEqualTo(1)
      .Because("duplicate (PerspectiveClassName, MessageTypeName) pairs should be deduplicated");

    // OrderUpdatedEvent should appear exactly once
    var orderUpdatedOccurrences = _countOccurrences(generatedSource!, "OrderUpdatedEvent");
    await Assert.That(orderUpdatedOccurrences).IsEqualTo(1);
  }

  private static int _countOccurrences(string text, string pattern) {
    var count = 0;
    var index = 0;
    while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1) {
      count++;
      index += pattern.Length;
    }
    return count;
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NestedPerspective_UsesClrTypeNameWithPlusSeparatorAsync() {
    // Arrange - A nested perspective class inside an Activity parent class
    // This is a common pattern: Activity { Model, Projection }
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record CreatedEvent : IEvent {
    public Guid StreamId { get; init; }
  }

  public static class Activity {
    public class Model {
      [StreamId]
      public Guid Id { get; set; }
      public string Name { get; set; } = "";
    }

    // Nested perspective class - should be registered as "TestNamespace.Activity+Projection"
    public class Projection : IPerspectiveFor<Model, CreatedEvent> {
      public Model Apply(Model currentData, CreatedEvent @event) {
        return currentData;
      }
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    // Assert - Should use CLR format with '+' for nested types
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // The perspective name should use CLR format: "Namespace.Parent+Child"
    // NOT just "Projection" or "Activity.Projection"
    await Assert.That(generatedSource).Contains("TestNamespace.Activity+Projection")
      .Because("nested perspective should use CLR format with '+' separator");

    // Should NOT contain just "Projection" without the parent
    // (checking that the TargetName includes the parent)
    await Assert.That(generatedSource).DoesNotContain("\"TargetName\\\": \\\"Projection\\\"")
      .Because("nested perspective should include parent class in name");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DeeplyNestedPerspective_UsesClrTypeNameAsync() {
    // Arrange - A deeply nested perspective class (multiple levels)
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record SessionEvent : IEvent {
    public Guid StreamId { get; init; }
  }

  public static class Sessions {
    public static class Active {
      public class Model {
        [StreamId]
        public Guid Id { get; set; }
      }

      // Deeply nested: Sessions > Active > Projection
      public class Projection : IPerspectiveFor<Model, SessionEvent> {
        public Model Apply(Model currentData, SessionEvent @event) {
          return currentData;
        }
      }
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    // Assert - Should use CLR format with '+' for all nesting levels
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNotNull();

    // The perspective name should use CLR format: "Namespace.Parent+Child+GrandChild"
    await Assert.That(generatedSource).Contains("TestNamespace.Sessions+Active+Projection")
      .Because("deeply nested perspective should use CLR format with '+' for each nesting level");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_IPerspectiveWithActionsFor_IncludesAssociationAsync() {
    // Arrange — Perspective using IPerspectiveWithActionsFor for Purge
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record DeletedEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class OrderPurgePerspective : IPerspectiveWithActionsFor<OrderModel, DeletedEvent> {
    public ApplyResult<OrderModel> Apply(OrderModel current, DeletedEvent @event)
        => ApplyResult<OrderModel>.Purge();
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    // Assert — DeletedEvent must be in generated JSON associations
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("DeletedEvent")
      .Because("IPerspectiveWithActionsFor events must be in DB associations for process_work_batch to create perspective events");
    await Assert.That(generatedSource).Contains("OrderPurgePerspective")
      .Because("The perspective class must be the target in the association");
  }
}
