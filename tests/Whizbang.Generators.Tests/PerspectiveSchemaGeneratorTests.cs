using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for PerspectiveSchemaGenerator - ensures PostgreSQL schema generation for perspectives.
/// </summary>
public class PerspectiveSchemaGeneratorTests {

  /// <summary>
  /// A perspective whose model carries one vector property, with the attribute's named arguments
  /// filled in by the caller. The schema generator reads each of those named arguments in its own
  /// switch arm, and the defaults are what every other test in this file exercises.
  /// </summary>
  private static string _vectorPerspective(string vectorFieldAttribute) => $$"""
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record DocumentModel {
              public Guid Id { get; set; }
              {{vectorFieldAttribute}}
              public float[]? Embedding { get; set; }
            }

            public class DocumentPerspective : IPerspectiveFor<DocumentModel, DocumentIndexed> {
              public DocumentModel Apply(DocumentModel currentData, DocumentIndexed @event) => currentData;
            }

            public record DocumentIndexed : IEvent;
            """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_VectorFieldWithIndexingOff_EmitsTheColumnWithoutAnIndexAsync() {
    // Indexing a vector column is the expensive part — building an IVFFlat index over a large
    // table is minutes of work and it is what the opt-out exists for. The column itself must
    // still be created, or the opt-out silently drops the data instead of the index.
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(
      _vectorPerspective("[VectorField(1536, Indexed = false)]"));

    var sql = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");

    await Assert.That(sql).IsNotNull();
    await Assert.That(sql!).Contains("vector(1536)")
      .Because("declining the index is not declining the column");
    await Assert.That(sql!).DoesNotContain("USING ivfflat")
      .Because("Indexed = false is the opt-out for exactly this index");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_VectorFieldWithAnExplicitColumnName_UsesItInsteadOfTheConventionAsync() {
    // Without ColumnName the generator snake-cases the property. An explicit name is how a
    // perspective maps onto a column that already exists, so ignoring it would generate a schema
    // that does not match the table it is meant to describe.
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(
      _vectorPerspective("[VectorField(768, ColumnName = \"doc_vec\")]"));

    var sql = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");

    await Assert.That(sql).IsNotNull();
    await Assert.That(sql!).Contains("doc_vec")
      .Because("an explicit column name is the whole point of the option");
    await Assert.That(sql!).DoesNotContain("embedding")
      .Because("the convention name must not be emitted alongside the explicit one — two columns "
             + "for one property is a schema that will not apply");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_VectorFieldDefaults_IndexTheColumnAsync() {
    // The companion to the opt-out: indexing is on unless asked otherwise, which is what makes
    // the opt-out meaningful rather than a no-op.
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(
      _vectorPerspective("[VectorField(1536)]"));

    var sql = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");

    await Assert.That(sql).IsNotNull();
    await Assert.That(sql!).Contains("ivfflat")
      .Because("vector indexing is opt-out, not opt-in");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspective_GeneratesSchemaAsync() {
    // Arrange
    const string source = """
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
    await Assert.That(generatedSource).Contains("CREATE TABLE");
    await Assert.That(generatedSource).Contains("order_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithAbstractPerspective_SkipsSchemaAsync() {
    // Arrange
    const string source = """
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
    const string source = """
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
    await Assert.That(generatedSource).Contains("order_perspective");
    await Assert.That(generatedSource).Contains("customer_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithLargePerspective_GeneratesSizeWarningAsync() {
    // Arrange - Create perspective with many properties (>35 to exceed 1500 byte threshold)
    var properties = new System.Text.StringBuilder();
    for (int i = 1; i <= 40; i++) {
      properties.AppendLine(CultureInfo.InvariantCulture, $"  public string Property{i} {{ get; set; }} = string.Empty;");
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
    const string source = """
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
    const string source = """
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
    await Assert.That(generatedSource).Contains("model_data");
    await Assert.That(generatedSource).Contains("metadata");
    await Assert.That(generatedSource).Contains("scope");
    await Assert.That(generatedSource).Contains("JSONB");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspective_GeneratesUniversalColumnsAsync() {
    // Arrange
    const string source = """
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
    await Assert.That(generatedSource).Contains("id");
    await Assert.That(generatedSource).Contains("created_at");
    await Assert.That(generatedSource).Contains("updated_at");
    await Assert.That(generatedSource).Contains("version");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspective_GeneratesCorrectTableNameAsync() {
    // Arrange
    const string source = """
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
    await Assert.That(generatedSource).Contains("customer_order_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithClassNoBaseList_SkipsAsync() {
    // Arrange - Class with no base list (no interfaces)
    const string source = """
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
    const string source = """
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
    await Assert.That(generatedSource).Contains("mixed_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithOnlyStaticProperties_GeneratesSchemaAsync() {
    // Arrange - Perspective with only static properties (edge case)
    const string source = """
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
    await Assert.That(generatedSource).Contains("static_only_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleIPerspectiveInterfaces_GeneratesSchemaAsync() {
    // Arrange - Class implementing multiple IPerspectiveFor interfaces
    const string source = """
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
    await Assert.That(generatedSource).Contains("multi_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveSchemaGenerator_LowercaseClassName_GeneratesTableNameWithoutLeadingUnderscoreAsync() {
    // Arrange - Tests line 150-156: i > 0 condition when i == 0 (lowercase first character)
    const string source = """
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

    // Check table creation uses correct name with wh_per_ prefix
    // orderPerspective → wh_per_order_perspective ("Perspective" is NOT in default suffix list)
    // The test verifies lowercase class names don't get leading underscores
    await Assert.That(generatedSource).Contains("CREATE TABLE IF NOT EXISTS wh_per_order_perspective (");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveSchemaGenerator_PerspectiveAtExactThreshold_GeneratesWarningAsync() {
    // Arrange - Tests line 101-108: EstimatedSizeBytes >= SIZE_WARNING_THRESHOLD (boundary condition)
    // SIZE_WARNING_THRESHOLD is 1500 bytes
    // Calculation: 20 (base) + (propertyCount * 40) = 1500 → propertyCount = 37
    var properties = new System.Text.StringBuilder();
    for (int i = 1; i <= 37; i++) {
      properties.AppendLine(CultureInfo.InvariantCulture, $"                public string Prop{i} {{ get; set; }} = \"\";");
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
    await Assert.That(sizeWarnings).Count().IsGreaterThanOrEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveSchemaGenerator_ClassWithBaseListButNotPerspective_SkipsAsync() {
    // Arrange - Tests line 59: perspectiveInterfaces.Count == 0 branch
    const string source = """
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

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NestedPerspective_GeneratesUniqueTableNameAsync() {
    // Arrange - Nested Projection class inside Activity parent
    // Bug: classSymbol.Name returns just "Projection", causing table name collision
    const string source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
              public record TestEvent : IEvent {
                public Guid StreamId { get; init; }
              }

              public static class Activity {
                public class Model {
                  [StreamId]
                  public Guid Id { get; set; }
                  public string Name { get; set; } = "";
                }

                public class Projection : IPerspectiveFor<Model, TestEvent> {
                  public Model Apply(Model currentData, TestEvent @event) {
                    return currentData;
                  }
                }
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate table name with wh_per_ prefix and suffix stripped
    // Activity.Projection → ActivityProjection → wh_per_activity (Projection suffix stripped)
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("CREATE TABLE IF NOT EXISTS wh_per_activity")
      .Because("nested perspective should include parent class and have wh_per_ prefix");
    await Assert.That(generatedSource).DoesNotContain("CREATE TABLE IF NOT EXISTS projection (")
      .Because("table name should not be just 'projection' for nested class");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleNestedProjections_GeneratesDistinctTableNamesAsync() {
    // Arrange - Two nested Projection classes that should NOT collide
    const string source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
              public record TestEvent : IEvent {
                public Guid StreamId { get; init; }
              }

              public static class Activity {
                public class Model {
                  [StreamId]
                  public Guid Id { get; set; }
                }

                public class Projection : IPerspectiveFor<Model, TestEvent> {
                  public Model Apply(Model currentData, TestEvent @event) {
                    return currentData;
                  }
                }
              }

              public static class Session {
                public class Model {
                  [StreamId]
                  public Guid Id { get; set; }
                }

                public class Projection : IPerspectiveFor<Model, TestEvent> {
                  public Model Apply(Model currentData, TestEvent @event) {
                    return currentData;
                  }
                }
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate distinct table names with wh_per_ prefix and suffix stripped
    // Activity.Projection → ActivityProjection → wh_per_activity
    // Session.Projection → SessionProjection → wh_per_session
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("wh_per_activity")
      .Because("Activity.Projection should generate wh_per_activity table");
    await Assert.That(generatedSource).Contains("wh_per_session")
      .Because("Session.Projection should generate wh_per_session table");

    // Count occurrences of CREATE TABLE in the Sql const (before Entries array)
    var entriesIdx = generatedSource.IndexOf("Entries", StringComparison.Ordinal);
    var sqlSection = entriesIdx > 0 ? generatedSource[..entriesIdx] : generatedSource;
    var createTableCount = sqlSection.Split("CREATE TABLE IF NOT EXISTS").Length - 1;
    await Assert.That(createTableCount).IsEqualTo(2)
      .Because("each nested Projection should have its own table");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspective_GeneratesEntriesArrayAsync() {
    // Arrange
    const string source = """
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

            public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreated> {
              public OrderModel Apply(OrderModel currentData, OrderCreated @event) {
                return currentData;
              }
            }

            public record OrderCreated : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate Entries[] array alongside Sql const
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("public static readonly System.Collections.Generic.KeyValuePair<string, string>[] Entries");
    await Assert.That(generatedSource).Contains("\"OrderPerspective\"");
    await Assert.That(generatedSource).Contains("CREATE TABLE");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultiplePerspectives_GeneratesEntriesForEachAsync() {
    // Arrange
    const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record OrderModel {
              public Guid Id { get; set; }
            }

            public record CustomerModel {
              public Guid Id { get; set; }
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

    // Assert - Should generate separate entries for each perspective
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("\"OrderPerspective\"");
    await Assert.That(generatedSource).Contains("\"CustomerPerspective\"");

    // Count entries - should be exactly 2
    var entryCount = generatedSource.Split("new System.Collections.Generic.KeyValuePair<string, string>(\"").Length - 1;
    await Assert.That(entryCount).IsEqualTo(2);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EntriesSqlMatchesConcatenatedSqlAsync() {
    // Arrange
    const string source = """
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

    // Assert - Entries SQL should contain the same CREATE TABLE as the main Sql const
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();

    // Both the Sql const and the Entries[] should reference the same table
    await Assert.That(generatedSource).Contains("order_perspective");

    // The Entries array entry should contain CREATE TABLE for that perspective
    // Extract the portion after "Entries" declaration
    var entriesIdx = generatedSource.IndexOf("Entries", StringComparison.Ordinal);
    var entriesSection = generatedSource[entriesIdx..];
    await Assert.That(entriesSection).Contains("CREATE TABLE IF NOT EXISTS");
    await Assert.That(entriesSection).Contains("order_perspective");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DeeplyNestedPerspective_GeneratesCorrectTableNameAsync() {
    // Arrange - Deeply nested perspective (multiple levels of nesting)
    const string source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestNamespace {
              public record TestEvent : IEvent {
                public Guid StreamId { get; init; }
              }

              public static class Sessions {
                public static class Active {
                  public class Model {
                    [StreamId]
                    public Guid Id { get; set; }
                  }

                  public class Projection : IPerspectiveFor<Model, TestEvent> {
                    public Model Apply(Model currentData, TestEvent @event) {
                      return currentData;
                    }
                  }
                }
              }
            }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate table name with wh_per_ prefix, all nesting levels, and suffix stripped
    // Sessions.Active.Projection → SessionsActiveProjection → wh_per_sessions_active
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("wh_per_sessions_active")
      .Because("deeply nested perspective should include all parent classes with wh_per_ prefix");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_IPerspectiveWithActionsFor_GeneratesSchemaAsync() {
    // Arrange — Perspective using only IPerspectiveWithActionsFor
    const string source = """
using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace MyApp.Perspectives;

public record OrderModel {
  [StreamId]
  public Guid Id { get; set; }
  public string Status { get; set; } = "";
}

public record OrderDeletedEvent : IEvent;

public class OrderPurgePerspective : IPerspectiveWithActionsFor<OrderModel, OrderDeletedEvent> {
  public ApplyResult<OrderModel> Apply(OrderModel current, OrderDeletedEvent @event)
    => ApplyResult<OrderModel>.Purge();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert — WithActionsFor perspective must generate a schema
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("CREATE TABLE")
      .Because("IPerspectiveWithActionsFor perspectives must generate DB schema");
    await Assert.That(generatedSource).Contains("order_purge")
      .Because("Table name should derive from perspective class name");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspective_EmitsBtreeIndexesForScopeFilterKeysAsync() {
    // Locks the alignment between PerspectiveScope JSON keys (t/u/o/c, defined
    // by [JsonPropertyName] on PerspectiveScope) and the btree functional
    // indexes the schema generator emits. If these drift, every scope-filtered
    // perspective lens query falls back to seq_scan — the symptom we caught
    // while investigating "wh_per_active_job_template_section 36 ms/SELECT" in
    // production. The old snippet used '(scope->>'tenant_id')' which never
    // matched any stored row (TenantId serializes as 't'), so the index was
    // dead code and the planner picked seq_scan.
    const string source = """
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

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();

    await Assert.That(generatedSource).Contains("(scope->>'t')")
      .Because("Tenant filter uses JSON key 't' per PerspectiveScope.TenantId — index must match");
    await Assert.That(generatedSource).Contains("(scope->>'u')")
      .Because("User filter uses JSON key 'u' per PerspectiveScope.UserId — index must match");
    await Assert.That(generatedSource).Contains("(scope->>'o')")
      .Because("Organization filter uses JSON key 'o' per PerspectiveScope.OrganizationId — index must match");
    await Assert.That(generatedSource).Contains("(scope->>'c')")
      .Because("Customer filter uses JSON key 'c' per PerspectiveScope.CustomerId — index must match");

    await Assert.That(generatedSource).DoesNotContain("CREATE INDEX IF NOT EXISTS ix_wh_per_order_perspective_tenant ON wh_per_order_perspective((scope->>'tenant_id'))")
      .Because("The historical broken tenant index expression must not be present in newly-generated schemas");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPerspective_EmitsRetrofitDropForBrokenTenantIndexAsync() {
    // Existing perspective tables in deployed services were created with the
    // historical broken `(scope->>'tenant_id')` index. The schema generator
    // runs on every service startup (CREATE TABLE/INDEX IF NOT EXISTS), so we
    // emit a conditional DROP that fires only when the broken indexdef is
    // present — self-healing migration without a separate script.
    const string source = """
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

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();

    await Assert.That(generatedSource).Contains("indexdef LIKE '%scope->>''tenant_id''%'")
      .Because("Retrofit must only drop when the indexdef shows the broken expression — not unconditionally");
    await Assert.That(generatedSource).Contains("DROP INDEX IF EXISTS ix_wh_per_order_perspective_tenant")
      .Because("Retrofit must drop the historical broken index by name");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultiplePerspectives_EmitsScopeIndexesPerTableAsync() {
    // The snippet is invoked once per perspective with __TABLE_NAME__ substituted
    // (see PerspectiveSchemaGenerator's per-perspective loop). Lock the invariant
    // that EVERY perspective gets its own four scope-filter indexes + retrofit DROP,
    // not just the first one.
    const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record OrderModel { public Guid Id { get; set; } }
            public record CustomerModel { public Guid Id { get; set; } }

            public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreated> {
              public OrderModel Apply(OrderModel current, OrderCreated @event) => current;
            }

            public class CustomerPerspective : IPerspectiveFor<CustomerModel, CustomerCreated> {
              public CustomerModel Apply(CustomerModel current, CustomerCreated @event) => current;
            }

            public record OrderCreated : IEvent;
            public record CustomerCreated : IEvent;
            """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();

    // OrderPerspective table: all four scope indexes + retrofit DROP
    await Assert.That(generatedSource).Contains("ix_wh_per_order_perspective_tenant ON wh_per_order_perspective((scope->>'t'))");
    await Assert.That(generatedSource).Contains("ix_wh_per_order_perspective_user ON wh_per_order_perspective((scope->>'u'))");
    await Assert.That(generatedSource).Contains("ix_wh_per_order_perspective_organization ON wh_per_order_perspective((scope->>'o'))");
    await Assert.That(generatedSource).Contains("ix_wh_per_order_perspective_customer ON wh_per_order_perspective((scope->>'c'))");
    await Assert.That(generatedSource).Contains("DROP INDEX IF EXISTS ix_wh_per_order_perspective_tenant");

    // CustomerPerspective table: same four scope indexes + retrofit DROP
    await Assert.That(generatedSource).Contains("ix_wh_per_customer_perspective_tenant ON wh_per_customer_perspective((scope->>'t'))");
    await Assert.That(generatedSource).Contains("ix_wh_per_customer_perspective_user ON wh_per_customer_perspective((scope->>'u'))");
    await Assert.That(generatedSource).Contains("ix_wh_per_customer_perspective_organization ON wh_per_customer_perspective((scope->>'o'))");
    await Assert.That(generatedSource).Contains("ix_wh_per_customer_perspective_customer ON wh_per_customer_perspective((scope->>'c'))");
    await Assert.That(generatedSource).Contains("DROP INDEX IF EXISTS ix_wh_per_customer_perspective_tenant");
  }
}
