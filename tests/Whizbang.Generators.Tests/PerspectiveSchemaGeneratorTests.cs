using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for PerspectiveSchemaGenerator - ensures PostgreSQL schema generation for perspectives.
/// </summary>
public class PerspectiveSchemaGeneratorTests {
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspective_GeneratesSchemaAsync() {
    // Arrange
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record OrderModel {
              public Guid Id { get; set; }
              public string CustomerName { get; set; } = string.Empty;
              public decimal TotalAmount { get; set; }
            }

            public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreated> {
              public OrderModel Apply(OrderModel currentData, OrderCreated @event) {
                return currentData;
              }
            }

            public record OrderCreated : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate schema file (Roslyn appends .cs to all AddSource calls)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("CREATE TABLE");
    await Assert.That(generatedSource).Contains("order_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithAbstractPerspective_SkipsSchemaAsync() {
    // Arrange
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record OrderModel {
              public Guid Id { get; set; }
            }

            public abstract class BaseOrderPerspective : IPerspectiveFor<OrderModel, OrderCreated> {
              public abstract OrderModel Apply(OrderModel currentData, OrderCreated @event);
            }

            public record OrderCreated : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should not generate schema for abstract class
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultiplePerspectives_GeneratesAllSchemasAsync() {
    // Arrange
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record OrderModel {
              public Guid Id { get; set; }
              public string CustomerName { get; set; } = string.Empty;
            }

            public record CustomerModel {
              public Guid Id { get; set; }
              public string Name { get; set; } = string.Empty;
              public string Email { get; set; } = string.Empty;
            }

            public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreated> {
              public OrderModel Apply(OrderModel currentData, OrderCreated @event) {
                return currentData;
              }
            }

            public class CustomerPerspective : IPerspectiveFor<CustomerModel, CustomerCreated> {
              public CustomerModel Apply(CustomerModel currentData, CustomerCreated @event) {
                return currentData;
              }
            }

            public record OrderCreated : IEvent;
            public record CustomerCreated : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate schemas for both perspectives
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("order_perspective");
    await Assert.That(generatedSource).Contains("customer_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithLargePerspective_GeneratesSizeWarningAsync() {
    // Arrange - Create perspective with many properties (>35 to exceed 1500 byte threshold)
    var properties = new System.Text.StringBuilder();
    for (int i = 1; i <= 40; i++) {
      properties.AppendLine($"  public string Property{i} {{ get; set; }} = string.Empty;");
    }

    var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record LargeOrderModel {
              public Guid Id { get; set; }
            {{properties}}
            }

            public class LargeOrderPerspective : IPerspectiveFor<LargeOrderModel, OrderCreated> {
              public LargeOrderModel Apply(LargeOrderModel currentData, OrderCreated @event) {
                return currentData;
              }
            }

            public record OrderCreated : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate schema and report size warning diagnostic
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Check that WHIZ008 diagnostic was reported for large perspective
    var sizeWarning = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ008");
    await Assert.That(sizeWarning).IsNotNull();
    await Assert.That(sizeWarning!.Severity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNoPerspectives_GeneratesNoOutputAsync() {
    // Arrange - No IPerspectiveFor implementations
    var source = """
            using System;
            using Whizbang.Core;

            namespace MyApp;

            public class NotAPerspective {
              public Guid Id { get; set; }
            }

            public record SomeEvent : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should not generate schema file
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspective_GeneratesJSONBColumnsAsync() {
    // Arrange
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record OrderModel {
              public Guid Id { get; set; }
            }

            public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreated> {
              public OrderModel Apply(OrderModel currentData, OrderCreated @event) {
                return currentData;
              }
            }

            public record OrderCreated : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate 3-column JSONB pattern
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("model_data");
    await Assert.That(generatedSource).Contains("metadata");
    await Assert.That(generatedSource).Contains("scope");
    await Assert.That(generatedSource).Contains("JSONB");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspective_GeneratesUniversalColumnsAsync() {
    // Arrange
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record OrderModel {
              public Guid Id { get; set; }
            }

            public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreated> {
              public OrderModel Apply(OrderModel currentData, OrderCreated @event) {
                return currentData;
              }
            }

            public record OrderCreated : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate universal columns
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("id");
    await Assert.That(generatedSource).Contains("created_at");
    await Assert.That(generatedSource).Contains("updated_at");
    await Assert.That(generatedSource).Contains("version");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspective_GeneratesCorrectTableNameAsync() {
    // Arrange
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record CustomerOrderModel {
              public Guid Id { get; set; }
            }

            public class CustomerOrderPerspective : IPerspectiveFor<CustomerOrderModel, OrderCreated> {
              public CustomerOrderModel Apply(CustomerOrderModel currentData, OrderCreated @event) {
                return currentData;
              }
            }

            public record OrderCreated : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should convert to snake_case
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("customer_order_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithClassNoBaseList_SkipsAsync() {
    // Arrange - Class with no base list (no interfaces)
    var source = """
            using System;

            namespace MyApp;

            public class SimpleClass {
              public string Name { get; set; } = string.Empty;
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should skip classes without base list
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithStaticProperties_ExcludesFromCountAsync() {
    // Arrange - Tests p => !p.IsStatic branch in property counting
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record MixedModel {
              public Guid Id { get; set; }
              public string InstanceProperty { get; set; } = string.Empty;
            }

            public class MixedPerspective : IPerspectiveFor<MixedModel, TestEvent> {
              public static string StaticProperty { get; set; } = string.Empty;
              public static int StaticCounter { get; set; }
              public MixedModel Apply(MixedModel currentData, TestEvent @event) {
                return currentData;
              }
            }

            public record TestEvent : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should count only instance properties (Id + InstanceProperty = 2, not 4)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("mixed_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithOnlyStaticProperties_GeneratesSchemaAsync() {
    // Arrange - Perspective with only static properties (edge case)
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record StaticOnlyModel {
            }

            public class StaticOnlyPerspective : IPerspectiveFor<StaticOnlyModel, TestEvent> {
              public static string StaticProperty { get; set; } = string.Empty;
              public static int StaticCounter { get; set; }
              public StaticOnlyModel Apply(StaticOnlyModel currentData, TestEvent @event) {
                return currentData;
              }
            }

            public record TestEvent : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate schema even with 0 instance properties
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("static_only_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleIPerspectiveInterfaces_GeneratesSchemaAsync() {
    // Arrange - Class implementing multiple IPerspectiveFor interfaces
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record MultiModel {
              public Guid Id { get; set; }
              public string Data { get; set; } = string.Empty;
            }

            public class MultiPerspective : IPerspectiveFor<MultiModel, EventA>, IPerspectiveFor<MultiModel, EventB> {
              public MultiModel Apply(MultiModel currentData, EventA @event) {
                return currentData;
              }
              public MultiModel Apply(MultiModel currentData, EventB @event) {
                return currentData;
              }
            }

            public record EventA : IEvent;
            public record EventB : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate schema for class with multiple perspective interfaces
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("multi_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveSchemaGenerator_LowercaseClassName_GeneratesTableNameWithoutLeadingUnderscoreAsync() {
    // Arrange - Tests line 150-156: i > 0 condition when i == 0 (lowercase first character)
    var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
              public record TestEvent : IEvent;

              public record orderModel {
                public Guid Id { get; set; }
                public int PropertyCount { get; set; }
              }

              public class orderPerspective : IPerspectiveFor<orderModel, TestEvent> {
                public orderModel Apply(orderModel currentData, TestEvent @event) {
                  return currentData;
                }
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate "order_perspective" (no underscore before first char in table name)
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Check table creation uses correct name (not starting with underscore)
    await Assert.That(generatedSource!).Contains("CREATE TABLE IF NOT EXISTS order_perspective (");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveSchemaGenerator_PerspectiveAtExactThreshold_GeneratesWarningAsync() {
    // Arrange - Tests line 101-108: EstimatedSizeBytes >= SIZE_WARNING_THRESHOLD (boundary condition)
    // SIZE_WARNING_THRESHOLD is 1500 bytes
    // Calculation: 20 (base) + (propertyCount * 40) = 1500 → propertyCount = 37
    var properties = new System.Text.StringBuilder();
    for (int i = 1; i <= 37; i++) {
      properties.AppendLine($"                public string Prop{i} {{ get; set; }} = \"\";");
    }

    var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
              public record TestEvent : IEvent;

              public record ThresholdModel {
                public Guid Id { get; set; }
            {{properties.ToString().TrimEnd()}}
              }

              public class ThresholdPerspective : IPerspectiveFor<ThresholdModel, TestEvent> {
                public ThresholdModel Apply(ThresholdModel currentData, TestEvent @event) {
                  return currentData;
                }
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate size warning at threshold (37 properties + Id = 38, ~1540 bytes)
    var sizeWarnings = result.Diagnostics.Where(d => d.Id == "WHIZ008").ToArray();
    await Assert.That(sizeWarnings).HasCount().GreaterThanOrEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveSchemaGenerator_ClassWithBaseListButNotPerspective_SkipsAsync() {
    // Arrange - Tests line 59: perspectiveInterfaces.Count == 0 branch
    var source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace {
              public record TestEvent : IEvent;

              // Has base list (IDisposable) but doesn't implement IPerspectiveFor
              public class NotAPerspective : IDisposable {
                public Guid Id { get; set; }
                public void Dispose() { }
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should skip class that doesn't implement IPerspectiveFor
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNull();
  }
}
