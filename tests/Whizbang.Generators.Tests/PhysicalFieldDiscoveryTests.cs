using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for physical field discovery in PerspectiveSchemaGenerator.
/// Validates that [PhysicalField] and [VectorField] attributes are discovered
/// and correctly included in the generated schema.
/// </summary>
public class PhysicalFieldDiscoveryTests {
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPhysicalField_DiscoverFieldAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Extracted)]
            public record ProductModel {
              [StreamKey]
              public Guid ProductId { get; init; }

              [PhysicalField(Indexed = true)]
              public decimal Price { get; init; }

              public string Description { get; init; } = string.Empty;
            }

            public class ProductPerspective : IPerspectiveFor<ProductModel, ProductCreated> {
              public ProductModel Apply(ProductModel? current, ProductCreated @event) {
                return new ProductModel { ProductId = @event.ProductId, Price = @event.Price };
              }
            }

            public record ProductCreated([property: StreamKey] Guid ProductId, decimal Price) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate schema with physical column
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("price");
    await Assert.That(generatedSource).Contains("DECIMAL");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVectorField_DiscoversVectorAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Split)]
            public record ProductSearchModel {
              [StreamKey]
              public Guid ProductId { get; init; }

              [VectorField(1536, DistanceMetric = VectorDistanceMetric.Cosine)]
              public float[]? Embedding { get; init; }

              public string Name { get; init; } = string.Empty;
            }

            public class ProductSearchPerspective : IPerspectiveFor<ProductSearchModel, ProductIndexed> {
              public ProductSearchModel Apply(ProductSearchModel? current, ProductIndexed @event) {
                return new ProductSearchModel { ProductId = @event.ProductId, Embedding = @event.Embedding };
              }
            }

            public record ProductIndexed([property: StreamKey] Guid ProductId, float[]? Embedding) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate schema with vector column
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("embedding");
    await Assert.That(generatedSource).Contains("vector(1536)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultiplePhysicalFields_DiscoversAllAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Extracted)]
            public record OrderModel {
              [StreamKey]
              public Guid OrderId { get; init; }

              [PhysicalField(Indexed = true)]
              public string CustomerName { get; init; } = string.Empty;

              [PhysicalField(Indexed = true)]
              public decimal TotalAmount { get; init; }

              [PhysicalField(Indexed = false)]
              public bool IsActive { get; init; }

              public string Notes { get; init; } = string.Empty;
            }

            public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreated> {
              public OrderModel Apply(OrderModel? current, OrderCreated @event) {
                return new OrderModel { OrderId = @event.OrderId };
              }
            }

            public record OrderCreated([property: StreamKey] Guid OrderId) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should discover all physical fields
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("customer_name");
    await Assert.That(generatedSource).Contains("total_amount");
    await Assert.That(generatedSource).Contains("is_active");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPhysicalFieldMaxLength_GeneratesConstraintAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Extracted)]
            public record ProductModel {
              [StreamKey]
              public Guid ProductId { get; init; }

              [PhysicalField(Indexed = true, MaxLength = 200)]
              public string Sku { get; init; } = string.Empty;
            }

            public class ProductPerspective : IPerspectiveFor<ProductModel, ProductCreated> {
              public ProductModel Apply(ProductModel? current, ProductCreated @event) {
                return new ProductModel { ProductId = @event.ProductId };
              }
            }

            public record ProductCreated([property: StreamKey] Guid ProductId) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should include VARCHAR with max length
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("VARCHAR(200)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVectorHNSWIndex_GeneratesIndexAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Split)]
            public record EmbeddingModel {
              [StreamKey]
              public Guid ItemId { get; init; }

              [VectorField(768, DistanceMetric = VectorDistanceMetric.Cosine, IndexType = VectorIndexType.HNSW)]
              public float[]? ContentEmbedding { get; init; }
            }

            public class EmbeddingPerspective : IPerspectiveFor<EmbeddingModel, ItemEmbedded> {
              public EmbeddingModel Apply(EmbeddingModel? current, ItemEmbedded @event) {
                return new EmbeddingModel { ItemId = @event.ItemId, ContentEmbedding = @event.Embedding };
              }
            }

            public record ItemEmbedded([property: StreamKey] Guid ItemId, float[]? Embedding) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should include HNSW index
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("USING hnsw");
    await Assert.That(generatedSource).Contains("vector_cosine_ops");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithNoPhysicalFields_GeneratesStandardSchemaAsync() {
    // Arrange - No physical fields, just standard JSONB
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public record SimpleModel {
              [StreamKey]
              public Guid Id { get; init; }
              public string Name { get; init; } = string.Empty;
            }

            public class SimplePerspective : IPerspectiveFor<SimpleModel, SimpleEvent> {
              public SimpleModel Apply(SimpleModel? current, SimpleEvent @event) {
                return new SimpleModel { Id = @event.Id };
              }
            }

            public record SimpleEvent([property: StreamKey] Guid Id) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should generate standard JSONB-only schema
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("model_data JSONB NOT NULL");
    // Should NOT contain physical column definitions
    await Assert.That(generatedSource).DoesNotContain("VARCHAR(");
    await Assert.That(generatedSource).DoesNotContain("vector(");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPhysicalFieldCustomColumnName_UsesCustomNameAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Extracted)]
            public record ProductModel {
              [StreamKey]
              public Guid ProductId { get; init; }

              [PhysicalField(ColumnName = "product_price", Indexed = true)]
              public decimal Price { get; init; }
            }

            public class ProductPerspective : IPerspectiveFor<ProductModel, ProductCreated> {
              public ProductModel Apply(ProductModel? current, ProductCreated @event) {
                return new ProductModel { ProductId = @event.ProductId };
              }
            }

            public record ProductCreated([property: StreamKey] Guid ProductId) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should use custom column name
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("product_price");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithVectorFieldIVFFlatIndex_GeneratesIVFFlatIndexAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Split)]
            public record SearchModel {
              [StreamKey]
              public Guid DocId { get; init; }

              [VectorField(512, DistanceMetric = VectorDistanceMetric.L2, IndexType = VectorIndexType.IVFFlat, IndexLists = 50)]
              public float[]? DocEmbedding { get; init; }
            }

            public class SearchPerspective : IPerspectiveFor<SearchModel, DocIndexed> {
              public SearchModel Apply(SearchModel? current, DocIndexed @event) {
                return new SearchModel { DocId = @event.DocId };
              }
            }

            public record DocIndexed([property: StreamKey] Guid DocId) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should include IVFFlat index
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("USING ivfflat");
    await Assert.That(generatedSource).Contains("vector_l2_ops");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithPhysicalFieldUnique_GeneratesUniqueConstraintAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Extracted)]
            public record ProductModel {
              [StreamKey]
              public Guid ProductId { get; init; }

              [PhysicalField(Indexed = true, Unique = true)]
              public string Sku { get; init; } = string.Empty;
            }

            public class ProductPerspective : IPerspectiveFor<ProductModel, ProductCreated> {
              public ProductModel Apply(ProductModel? current, ProductCreated @event) {
                return new ProductModel { ProductId = @event.ProductId };
              }
            }

            public record ProductCreated([property: StreamKey] Guid ProductId) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should include UNIQUE constraint or unique index
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("UNIQUE");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithInnerProductMetric_GeneratesCorrectOpsAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Split)]
            public record SimilarityModel {
              [StreamKey]
              public Guid ItemId { get; init; }

              [VectorField(384, DistanceMetric = VectorDistanceMetric.InnerProduct, IndexType = VectorIndexType.HNSW)]
              public float[]? ItemEmbedding { get; init; }
            }

            public class SimilarityPerspective : IPerspectiveFor<SimilarityModel, ItemProcessed> {
              public SimilarityModel Apply(SimilarityModel? current, ItemProcessed @event) {
                return new SimilarityModel { ItemId = @event.ItemId };
              }
            }

            public record ItemProcessed([property: StreamKey] Guid ItemId) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should use inner product operator class
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource!).Contains("vector_ip_ops");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ReportsDiagnostic_WhenPhysicalFieldsDiscoveredAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Extracted)]
            public record ProductModel {
              [StreamKey]
              public Guid ProductId { get; init; }

              [PhysicalField(Indexed = true)]
              public decimal Price { get; init; }

              [VectorField(1536)]
              public float[]? Embedding { get; init; }
            }

            public class ProductPerspective : IPerspectiveFor<ProductModel, ProductCreated> {
              public ProductModel Apply(ProductModel? current, ProductCreated @event) {
                return new ProductModel { ProductId = @event.ProductId };
              }
            }

            public record ProductCreated([property: StreamKey] Guid ProductId) : IEvent;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    // Assert - Should report WHIZ807 diagnostic for physical fields discovered
    var physicalFieldsDiagnostic = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ807");
    await Assert.That(physicalFieldsDiagnostic).IsNotNull();
    await Assert.That(physicalFieldsDiagnostic!.Severity).IsEqualTo(DiagnosticSeverity.Info);
  }
}
