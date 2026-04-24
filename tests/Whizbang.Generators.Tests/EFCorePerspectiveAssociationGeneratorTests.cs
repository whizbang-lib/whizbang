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

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CustomInterfaceExtendingIPerspectiveBase_IncludesAssociationAsync() {
    // Arrange — future interface extending IPerspectiveBase
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record FutureEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record Model {
    [StreamId]
    public Guid Id { get; init; }
  }

  public interface ICustomPerspective<TModel, TEvent> : IPerspectiveBase<TModel, TEvent>
      where TModel : class where TEvent : IEvent { }

  public class FuturePerspective : ICustomPerspective<Model, FutureEvent> { }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    // Assert — FutureEvent must be in generated DB associations
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("FutureEvent")
      .Because("Custom interface extending IPerspectiveBase must have its events in DB associations");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task LockIn_AllThreeInterfaceTypes_InAssociationsAsync() {
    // Arrange — all three interface types in one compilation
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record EventA : IEvent { [StreamId] public Guid Id { get; init; } }
  public record EventB : IEvent { [StreamId] public Guid Id { get; init; } }
  public record EventC : IEvent { [StreamId] public Guid Id { get; init; } }
  public record Model { [StreamId] public Guid Id { get; init; } }

  public class StandardPerspective : IPerspectiveFor<Model, EventA> {
    public Model Apply(Model c, EventA e) => c;
  }

  public class ActionsPerspective : IPerspectiveWithActionsFor<Model, EventB> {
    public ApplyResult<Model> Apply(Model c, EventB e) => ApplyResult<Model>.Purge();
  }

  public interface ICustom<TModel, TEvent> : IPerspectiveBase<TModel, TEvent>
      where TModel : class where TEvent : IEvent { }
  public class CustomPerspective : ICustom<Model, EventC> { }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);

    // Assert — ALL three event types in associations
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("EventA")
      .Because("IPerspectiveFor events must be in DB associations");
    await Assert.That(generatedSource).Contains("EventB")
      .Because("IPerspectiveWithActionsFor events must be in DB associations");
    await Assert.That(generatedSource).Contains("EventC")
      .Because("Custom IPerspectiveBase events must be in DB associations");
  }

  // ════════════════════════════════════════════════════════════════════════
  // AssociationsHash emission — used by schema extension to detect drift in
  // the set of (perspective, event) pairs between builds. Without this, the
  // fast-path hash check at startup misses event-type additions/removals and
  // wh_message_associations goes stale.
  // ════════════════════════════════════════════════════════════════════════

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EmitsAssociationsHashConstantAsync() {
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace {
        public record EventA : IEvent;
        public record EventB : IEvent;
        public record Model;

        public class Persp : IPerspectiveFor<Model, EventA, EventB> {
          public Model Apply(Model c, EventA e) => c;
          public Model Apply(Model c, EventB e) => c;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source);
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "EFCorePerspectiveAssociations.g.cs");

    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("AssociationsHash")
      .Because("Generated class must expose an AssociationsHash constant for drift detection");

    // Hash is 64 hex chars (SHA256) — locate the literal and validate shape
    var match = System.Text.RegularExpressions.Regex.Match(
      generatedSource!, @"AssociationsHash\s*=\s*""([0-9a-f]{64})""");
    await Assert.That(match.Success).IsTrue()
      .Because("AssociationsHash must be a 64-char lowercase hex SHA256 literal");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AssociationsHash_IsDeterministicAcrossDeclarationOrderAsync() {
    // Two sources with the same perspectives but events declared in different orders
    // should produce the same AssociationsHash — generator must sort canonically.
    const string sourceOrderA = """
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace {
        public record EventZ : IEvent;
        public record EventA : IEvent;
        public record Model;

        public class Persp : IPerspectiveFor<Model, EventZ, EventA> {
          public Model Apply(Model c, EventZ e) => c;
          public Model Apply(Model c, EventA e) => c;
        }
      }
      """;

    const string sourceOrderB = """
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace {
        public record EventA : IEvent;
        public record EventZ : IEvent;
        public record Model;

        public class Persp : IPerspectiveFor<Model, EventA, EventZ> {
          public Model Apply(Model c, EventA e) => c;
          public Model Apply(Model c, EventZ e) => c;
        }
      }
      """;

    var resultA = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(sourceOrderA);
    var resultB = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(sourceOrderB);

    var sourceA = GeneratorTestHelper.GetGeneratedSource(resultA, "EFCorePerspectiveAssociations.g.cs");
    var sourceB = GeneratorTestHelper.GetGeneratedSource(resultB, "EFCorePerspectiveAssociations.g.cs");

    var pattern = @"AssociationsHash\s*=\s*""([0-9a-f]{64})""";
    var hashA = System.Text.RegularExpressions.Regex.Match(sourceA!, pattern).Groups[1].Value;
    var hashB = System.Text.RegularExpressions.Regex.Match(sourceB!, pattern).Groups[1].Value;

    await Assert.That(hashA).IsNotEmpty();
    await Assert.That(hashA).IsEqualTo(hashB)
      .Because("Hash must be stable regardless of perspective declaration order");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AssociationsHash_ChangesWhenEventTypeAddedAsync() {
    const string sourceOneEvent = """
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace {
        public record EventA : IEvent;
        public record Model;

        public class Persp : IPerspectiveFor<Model, EventA> {
          public Model Apply(Model c, EventA e) => c;
        }
      }
      """;

    const string sourceTwoEvents = """
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace {
        public record EventA : IEvent;
        public record EventB : IEvent;
        public record Model;

        public class Persp : IPerspectiveFor<Model, EventA, EventB> {
          public Model Apply(Model c, EventA e) => c;
          public Model Apply(Model c, EventB e) => c;
        }
      }
      """;

    var resultOne = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(sourceOneEvent);
    var resultTwo = GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(sourceTwoEvents);

    var sourceOne = GeneratorTestHelper.GetGeneratedSource(resultOne, "EFCorePerspectiveAssociations.g.cs");
    var sourceTwo = GeneratorTestHelper.GetGeneratedSource(resultTwo, "EFCorePerspectiveAssociations.g.cs");

    var pattern = @"AssociationsHash\s*=\s*""([0-9a-f]{64})""";
    var hashOne = System.Text.RegularExpressions.Regex.Match(sourceOne!, pattern).Groups[1].Value;
    var hashTwo = System.Text.RegularExpressions.Regex.Match(sourceTwo!, pattern).Groups[1].Value;

    await Assert.That(hashOne).IsNotEmpty();
    await Assert.That(hashTwo).IsNotEmpty();
    await Assert.That(hashOne).IsNotEqualTo(hashTwo)
      .Because("Adding an event type to a perspective must change AssociationsHash so startup re-registers");
  }
}
