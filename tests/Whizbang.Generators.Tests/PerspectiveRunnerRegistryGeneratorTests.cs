using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the PerspectiveRunnerRegistryGenerator source generator.
/// Ensures correct perspective runner registry generation with proper naming for nested types.
/// </summary>
public class PerspectiveRunnerRegistryGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNestedPerspective_UsesQualifiedNameAsync() {
    // Arrange - Nested class should use "ParentClass.NestedClass" format
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    [StreamKey]
    public string OrderId { get; init; } = """";
  }

  public record OrderModel {
    [StreamKey]
    public string OrderId { get; init; } = """";
  }

  public class DraftJobStatus {
    // Nested perspective class - should be named ""DraftJobStatus.Projection""
    public class Projection : IPerspectiveFor<OrderModel, OrderEvent> {
      public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
        return currentData;
      }
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - Should use CLR format name "TestNamespace.DraftJobStatus+Projection" in switch case
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("\"TestNamespace.DraftJobStatus+Projection\"");
    // Should NOT use just "Projection"
    await Assert.That(registrySource!).DoesNotContain("\"Projection\" =>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNonNestedPerspective_UsesSimpleNameAsync() {
    // Arrange - Top-level class should use simple name
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    [StreamKey]
    public string OrderId { get; init; } = """";
  }

  public record OrderModel {
    [StreamKey]
    public string OrderId { get; init; } = """";
  }

  // Top-level perspective class - should be named ""OrderPerspective""
  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - Should use CLR format name "TestNamespace.OrderPerspective" in switch case
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("\"TestNamespace.OrderPerspective\"");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDuplicateNames_EmitsCollisionErrorAsync() {
    // Arrange - Two nested classes with same name should cause collision error
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record Event1 : IEvent {
    [StreamKey]
    public string Id { get; init; } = """";
  }

  public record Event2 : IEvent {
    [StreamKey]
    public string Id { get; init; } = """";
  }

  public record Model1 {
    [StreamKey]
    public string Id { get; init; } = """";
  }

  public record Model2 {
    [StreamKey]
    public string Id { get; init; } = """";
  }

  // Two top-level classes with same name - should cause collision
  public class DuplicatePerspective : IPerspectiveFor<Model1, Event1> {
    public Model1 Apply(Model1 currentData, Event1 @event) {
      return currentData;
    }
  }
}

namespace OtherNamespace {
  // Same name in different namespace - should cause collision
  public class DuplicatePerspective : IPerspectiveFor<TestNamespace.Model2, TestNamespace.Event2> {
    public TestNamespace.Model2 Apply(TestNamespace.Model2 currentData, TestNamespace.Event2 @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - Should emit WHIZ032 diagnostic error
    var diagnostics = result.Diagnostics;
    var whiz032 = diagnostics.FirstOrDefault(d => d.Id == "WHIZ032");
    await Assert.That(whiz032).IsNotNull();
    await Assert.That(whiz032!.Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz032.GetMessage(CultureInfo.InvariantCulture)).Contains("DuplicatePerspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleNestedPerspectives_SameParentClassName_UsesDistinctNamesAsync() {
    // Arrange - Multiple nested classes with same nested name but different parents
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record Event1 : IEvent {
    [StreamKey]
    public string Id { get; init; } = """";
  }

  public record Event2 : IEvent {
    [StreamKey]
    public string Id { get; init; } = """";
  }

  public record Model1 {
    [StreamKey]
    public string Id { get; init; } = """";
  }

  public record Model2 {
    [StreamKey]
    public string Id { get; init; } = """";
  }

  public class DraftJobStatus {
    public class Projection : IPerspectiveFor<Model1, Event1> {
      public Model1 Apply(Model1 currentData, Event1 @event) {
        return currentData;
      }
    }
  }

  public class ActiveJobStatus {
    public class Projection : IPerspectiveFor<Model2, Event2> {
      public Model2 Apply(Model2 currentData, Event2 @event) {
        return currentData;
      }
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - Should have distinct CLR format names for each nested class
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("\"TestNamespace.DraftJobStatus+Projection\"");
    await Assert.That(registrySource!).Contains("\"TestNamespace.ActiveJobStatus+Projection\"");
    // Should NOT have a collision error since names are different
    var diagnostics = result.Diagnostics;
    var whiz032 = diagnostics.FirstOrDefault(d => d.Id == "WHIZ032");
    await Assert.That(whiz032).IsNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EmptyCompilation_GeneratesNothingAsync() {
    // Arrange
    var source = @"
using System;

namespace TestNamespace {
  public class SomeClass {
    public void SomeMethod() { }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - Should not generate any files when no perspectives exist
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(0);
  }

  // ==================== Multi-Event Support Tests (6-50 events) ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PerspectiveWith10Events_GeneratesRegistryAsync() {
    // Arrange - Perspective implementing IPerspectiveFor with 10 event types
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record Event1 : IEvent { [StreamKey] public Guid Id { get; init; } }
  public record Event2 : IEvent { [StreamKey] public Guid Id { get; init; } }
  public record Event3 : IEvent { [StreamKey] public Guid Id { get; init; } }
  public record Event4 : IEvent { [StreamKey] public Guid Id { get; init; } }
  public record Event5 : IEvent { [StreamKey] public Guid Id { get; init; } }
  public record Event6 : IEvent { [StreamKey] public Guid Id { get; init; } }
  public record Event7 : IEvent { [StreamKey] public Guid Id { get; init; } }
  public record Event8 : IEvent { [StreamKey] public Guid Id { get; init; } }
  public record Event9 : IEvent { [StreamKey] public Guid Id { get; init; } }
  public record Event10 : IEvent { [StreamKey] public Guid Id { get; init; } }

  public record MultiEventModel {
    [StreamKey]
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
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - Should generate registry for perspective with 10 events
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("MultiEventPerspective");
    await Assert.That(registrySource!).Contains("PerspectiveRunnerRegistry");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PerspectiveWith25Events_GeneratesRegistryAsync() {
    // Arrange - Perspective implementing IPerspectiveFor with 25 event types
    var eventDeclarations = string.Join("\n",
        Enumerable.Range(1, 25).Select(i =>
            $"  public record Evt{i} : IEvent {{ [StreamKey] public Guid Id {{ get; init; }} }}"));

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
    [StreamKey]
    public Guid Id {{ get; init; }}
    public int Counter {{ get; init; }}
  }}

  public class BigPerspective : IPerspectiveFor<Model, {eventTypeParams}> {{
{applyMethods}
  }}
}}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - Should generate registry for perspective with 25 events
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("BigPerspective");
    await Assert.That(registrySource!).Contains("PerspectiveRunnerRegistry");
  }
}
