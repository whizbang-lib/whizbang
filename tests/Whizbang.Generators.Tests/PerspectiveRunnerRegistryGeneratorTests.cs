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
    [StreamId]
    public string OrderId { get; init; } = """";
  }

  public record OrderModel {
    [StreamId]
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
    [StreamId]
    public string OrderId { get; init; } = """";
  }

  public record OrderModel {
    [StreamId]
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
    [StreamId]
    public string Id { get; init; } = """";
  }

  public record Event2 : IEvent {
    [StreamId]
    public string Id { get; init; } = """";
  }

  public record Model1 {
    [StreamId]
    public string Id { get; init; } = """";
  }

  public record Model2 {
    [StreamId]
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
    [StreamId]
    public string Id { get; init; } = """";
  }

  public record Event2 : IEvent {
    [StreamId]
    public string Id { get; init; } = """";
  }

  public record Model1 {
    [StreamId]
    public string Id { get; init; } = """";
  }

  public record Model2 {
    [StreamId]
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
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - Should generate registry for perspective with 25 events
    await Assert.That(result.GeneratedTrees).Count().IsEqualTo(1);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("BigPerspective");
    await Assert.That(registrySource!).Contains("PerspectiveRunnerRegistry");
  }

  // ==================== IEventTypeProvider Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesGetEventTypesMethodAsync() {
    // Arrange - Simple perspective with events
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel current, OrderCreatedEvent @event) => current;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - Should generate GetEventTypes() method
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("public IReadOnlyList<Type> GetEventTypes()");
    await Assert.That(registrySource!).Contains("_allEventTypes");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AllEventTypesContainsTypeofExpressionsAsync() {
    // Arrange - Perspective with multiple events
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record EventA : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record EventB : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record Model {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class TestPerspective : IPerspectiveFor<Model, EventA, EventB> {
    public Model Apply(Model current, EventA @event) => current;
    public Model Apply(Model current, EventB @event) => current;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - Should use typeof() expressions for event types
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("typeof(global::TestNamespace.EventA)");
    await Assert.That(registrySource!).Contains("typeof(global::TestNamespace.EventB)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DeduplicatesEventTypesAsync() {
    // Arrange - Two perspectives sharing an event type
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record SharedEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record UniqueEvent1 : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record UniqueEvent2 : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record Model1 {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record Model2 {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class Perspective1 : IPerspectiveFor<Model1, SharedEvent, UniqueEvent1> {
    public Model1 Apply(Model1 current, SharedEvent @event) => current;
    public Model1 Apply(Model1 current, UniqueEvent1 @event) => current;
  }

  public class Perspective2 : IPerspectiveFor<Model2, SharedEvent, UniqueEvent2> {
    public Model2 Apply(Model2 current, SharedEvent @event) => current;
    public Model2 Apply(Model2 current, UniqueEvent2 @event) => current;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - SharedEvent should appear only once in _allEventTypes
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    // Count occurrences of typeof(global::TestNamespace.SharedEvent) in _allEventTypes
    var allEventTypesSection = registrySource!.Substring(
        registrySource.IndexOf("_allEventTypes", StringComparison.Ordinal),
        registrySource.IndexOf("public IReadOnlyList<Type> GetEventTypes()", StringComparison.Ordinal) -
        registrySource.IndexOf("_allEventTypes", StringComparison.Ordinal)
    );
    var sharedEventCount = _countOccurrences(allEventTypesSection, "typeof(global::TestNamespace.SharedEvent)");
    await Assert.That(sharedEventCount).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistersIEventTypeProviderInDIAsync() {
    // Arrange
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderEvent> {
    public OrderModel Apply(OrderModel current, OrderEvent @event) => current;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - DI registration should include IEventTypeProvider
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("services.AddSingleton<IEventTypeProvider>");
    await Assert.That(registrySource!).Contains("using Whizbang.Core.Messaging;");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventTypesSortedAlphabeticallyAsync() {
    // Arrange - Events should be sorted for deterministic output
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record ZEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record AEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record MEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record Model {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class TestPerspective : IPerspectiveFor<Model, ZEvent, AEvent, MEvent> {
    public Model Apply(Model current, ZEvent @event) => current;
    public Model Apply(Model current, AEvent @event) => current;
    public Model Apply(Model current, MEvent @event) => current;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert - Event types should be sorted: AEvent, MEvent, ZEvent
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    var aIndex = registrySource!.IndexOf("typeof(global::TestNamespace.AEvent)", StringComparison.Ordinal);
    var mIndex = registrySource.IndexOf("typeof(global::TestNamespace.MEvent)", StringComparison.Ordinal);
    var zIndex = registrySource.IndexOf("typeof(global::TestNamespace.ZEvent)", StringComparison.Ordinal);

    await Assert.That(aIndex).IsLessThan(mIndex);
    await Assert.That(mIndex).IsLessThan(zIndex);
  }

  // ==================== EventTypes Format Tests ====================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventTypesInRegistrationInfo_UseRuntimeFormat_NotGlobalPrefixAsync() {
    // Arrange — Verify generated PerspectiveRegistrationInfo.EventTypes
    // uses "FullName, AssemblyName" format (no global:: prefix)
    var source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderCreatedEvent : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
    public OrderModel Apply(OrderModel current, OrderCreatedEvent @event) => current;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    // Assert — EventTypes in PerspectiveRegistrationInfo should use runtime format
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();

    // The PerspectiveRegistrationInfo EventTypes array should contain assembly-qualified format
    // e.g. "TestNamespace.OrderCreatedEvent, TestProject" — NOT "global::TestNamespace.OrderCreatedEvent"
    // The EventTypes are in the last argument of PerspectiveRegistrationInfo constructor: [...]
    // ClassName and ModelType still use global:: format (they're for code gen), but EventTypes must not.

    // Verify EventTypes array uses runtime format (contains assembly name after comma, no global::)
    // The generated code looks like: ["TestNamespace.OrderCreatedEvent, TestProject"]
    await Assert.That(registrySource!).Contains("[\"TestNamespace.OrderCreatedEvent,");
    // Verify EventTypes do NOT use global:: prefix
    await Assert.That(registrySource).DoesNotContain("[\"global::");

    // _allEventTypes should still use typeof(global::...) for code generation
    await Assert.That(registrySource).Contains("typeof(global::TestNamespace.OrderCreatedEvent)");
  }

  private static int _countOccurrences(string text, string pattern) {
    int count = 0;
    int index = 0;
    while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1) {
      count++;
      index += pattern.Length;
    }
    return count;
  }
}
