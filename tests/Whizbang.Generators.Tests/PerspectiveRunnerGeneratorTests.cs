using System.Diagnostics.CodeAnalysis;
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
    await Assert.That(result.GeneratedTrees).HasCount().EqualTo(0);
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

  public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
    public Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - No runner should be generated (perspective doesn't implement IPerspectiveModel)
    await Assert.That(result.GeneratedTrees).HasCount().EqualTo(0);
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

  public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent>, IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
      return currentData with { Status = ""Created"" };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate OrderPerspectiveRunner
    await Assert.That(result.GeneratedTrees).HasCount().EqualTo(1);

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

  public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent>, IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
    public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
      return currentData with { Status = ""Created"" };
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should not generate runner (model missing [StreamKey])
    await Assert.That(result.GeneratedTrees).HasCount().EqualTo(0);
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

  public abstract class BasePerspective : IPerspectiveOf<OrderEvent>, IPerspectiveFor<OrderReadModel, OrderEvent> {
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
    await Assert.That(result.GeneratedTrees).HasCount().EqualTo(1);

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

  public class OrderPerspective : IPerspectiveOf<OrderEvent>, IPerspectiveFor<OrderReadModel, OrderEvent> {
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
    await Assert.That(whiz027.GetMessage()).Contains("OrderPerspective");
    await Assert.That(whiz027.GetMessage()).Contains("OrderPerspectiveRunner");
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

  public class OrderPerspective : IPerspectiveOf<OrderEvent>, IPerspectiveFor<OrderReadModel, OrderEvent> {
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

  public class InventoryPerspective : IPerspectiveOf<InventoryEvent>, IPerspectiveFor<InventoryModel, InventoryEvent> {
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

  public class OrderPerspective : IPerspectiveOf<OrderEvent>, IPerspectiveFor<OrderReadModel, OrderEvent> {
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

  public class OrderPerspective : IPerspectiveOf<OrderEvent>, IPerspectiveFor<OrderModel, OrderEvent> {
    public OrderModel Apply(OrderModel currentData, OrderEvent @event) {
      return currentData;
    }
  }

  public class InventoryPerspective : IPerspectiveOf<InventoryEvent>, IPerspectiveFor<InventoryModel, InventoryEvent> {
    public InventoryModel Apply(InventoryModel currentData, InventoryEvent @event) {
      return currentData;
    }
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    // Assert - Should generate two runners
    await Assert.That(result.GeneratedTrees).HasCount().EqualTo(2);

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

  public class OrderPerspective : IPerspectiveOf<OrderEvent>, IPerspectiveFor<OrderModel, OrderEvent> {
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

  public class OrderPerspective : IPerspectiveOf<OrderEvent>, IPerspectiveFor<OrderModel, OrderEvent> {
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
}
