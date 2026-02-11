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

    // Assert - Should use qualified name "DraftJobStatus.Projection" in switch case
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("\"DraftJobStatus.Projection\"");
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

    // Assert - Should use simple name "OrderPerspective" in switch case
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("\"OrderPerspective\"");
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

    // Assert - Should have distinct names for each nested class
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("\"DraftJobStatus.Projection\"");
    await Assert.That(registrySource!).Contains("\"ActiveJobStatus.Projection\"");
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
}
