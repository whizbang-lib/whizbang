using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage tests for <see cref="EFCorePerspectiveConfigurationGenerator"/> targeting
/// branches not exercised by the diagnostics test suite: library-assembly skip,
/// MSBuild identifier-length overrides, DbContext schema fallbacks, storage-mode and
/// physical/vector/discriminator field extraction, column-type mapping, and the
/// recursive polymorphic-model detection paths.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCorePerspectiveConfigurationGenerator.cs</tests>
[Category("SourceGenerators")]
public class EFCorePerspectiveConfigurationGeneratorCoverageTests {
  private const string GENERATED_FILE = "WhizbangModelBuilderExtensions.g.cs";

  /// <summary>Standard-mode snippet marker (ComplexProperty().ToJson() path).</summary>
  private const string STANDARD_CONFIG_MARKER = "entity.ComplexProperty(e => e.Data, d => d.ToJson(\"data\"));";

  /// <summary>Polymorphic-mode snippet marker (Property().HasColumnType("jsonb") path).</summary>
  private const string POLYMORPHIC_CONFIG_MARKER = "POLYMORPHIC MODEL";

  #region Library assembly skip

  /// <summary>
  /// Test that the generator produces NOTHING when run inside the library assembly itself
  /// (Whizbang.Data.EFCore.Postgres) - only consuming projects get the extension class.
  /// The same source in a consumer-named assembly generates the file, proving the assembly
  /// name is the discriminating factor.
  /// </summary>
  [Test]
  public async Task Generator_InLibraryAssemblyItself_SkipsGenerationAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public record ProductDto(string Name);

        public class ProductPerspective(IPerspectiveStore<ProductDto> store)
          : IPerspectiveFor<ProductDto, ProductCreated> {
          public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
        }

        public record ProductCreated : IEvent;
        """;

    // Act
    var libraryResult = _runGeneratorWithAssemblyName(source, "Whizbang.Data.EFCore.Postgres");
    var consumerResult = _runGeneratorWithAssemblyName(source, "ConsumerApp");

    // Assert - library assembly: no sources, no diagnostics (early return before any reporting)
    await Assert.That(libraryResult.GeneratedTrees).IsEmpty();
    await Assert.That(libraryResult.Diagnostics).IsEmpty();

    // Consumer assembly: the extension file IS generated from identical source
    var consumerFileNames = consumerResult.GeneratedTrees
        .Select(t => Path.GetFileName(t.FilePath))
        .ToList();
    await Assert.That(consumerFileNames).Contains(GENERATED_FILE);
  }

  #endregion

  #region Max identifier length override (MSBuild property)

  /// <summary>
  /// Test that WhizbangMaxIdentifierLength lowers the limit: a table name valid under
  /// PostgreSQL's default 63 bytes fails a 20-byte override, and the diagnostic names
  /// the overridden provider limits.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithSmallMaxIdentifierOverride_ReportsWHIZ820AgainstOverrideAsync() {
    // Arrange - "ProductDetails" => wh_per_product_details (22 bytes) > 20-byte override
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public record ProductDetails(string Name);

        public class ProductDetailsPerspective(IPerspectiveStore<ProductDetails> store)
          : IPerspectiveFor<ProductDetails, ProductCreated> {
          public ProductDetails Apply(ProductDetails currentData, ProductCreated @event) => currentData;
        }

        public record ProductCreated : IEvent;
        """;
    var globalOptions = new Dictionary<string, string> {
      ["build_property.WhizbangMaxIdentifierLength"] = "20"
    };

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source, globalOptions);

    // Assert - WHIZ820 emitted against the overridden limit
    var whiz820 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ820");
    await Assert.That(whiz820).IsNotNull();
    await Assert.That(whiz820!.GetMessage(CultureInfo.InvariantCulture)).Contains("override: 20");
    await Assert.That(whiz820.GetMessage(CultureInfo.InvariantCulture)).Contains("20 bytes");

    // Failing perspective is excluded from the generated configuration
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).DoesNotContain("wh_per_product_details");
  }

  /// <summary>
  /// Test that WhizbangMaxIdentifierLength raises the limit: a table name over
  /// PostgreSQL's default 63 bytes passes a 128-byte override and is generated.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithLargeMaxIdentifierOverride_AllowsLongTableNameAsync() {
    // Arrange - same model name that fails the default 63-byte limit in the diagnostics suite
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public record VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres(string Name);

        public class TestPerspective(IPerspectiveStore<VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres> store)
          : IPerspectiveFor<VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres, ProductCreated> {
          public VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres Apply(VeryLongModelNameThatWillDefinitelyExceedTheSixtyThreeByteLimitForPostgres currentData, ProductCreated @event) => currentData;
        }

        public record ProductCreated : IEvent;
        """;
    var globalOptions = new Dictionary<string, string> {
      ["build_property.WhizbangMaxIdentifierLength"] = "128"
    };

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source, globalOptions);

    // Assert - no WHIZ820 and the long table name is generated
    var whiz820 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ820");
    await Assert.That(whiz820).IsNull();

    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("wh_per_very_long_model_name");
  }

  #endregion

  #region DbContext schema discovery fallbacks

  /// <summary>
  /// Test that attributed classes with non-DbContext base chains and DbContext subclasses
  /// WITHOUT [WhizbangDbContext] are both ignored for schema discovery - the generated
  /// code falls back to the "public" schema.
  /// </summary>
  [Test]
  public async Task Generator_NonWhizbangDbContextClasses_FallBackToPublicSchemaAsync() {
    // Arrange
    const string source = """
        using System;
        using Microsoft.EntityFrameworkCore;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class BaseThing {
        }

        [Obsolete("attributed with a base chain, but not a DbContext")]
        public class DerivedThing : BaseThing {
        }

        [Obsolete("a DbContext without [WhizbangDbContext] must not be discovered")]
        public class PlainContext : DbContext {
        }

        public record ProductDto(string Name);

        public class ProductPerspective(IPerspectiveStore<ProductDto> store)
          : IPerspectiveFor<ProductDto, ProductCreated> {
          public ProductDto Apply(ProductDto currentData, ProductCreated @event) => currentData;
        }

        public record ProductCreated : IEvent;
        """;

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorWithEFCoreReferencesAsync(source);

    // Assert - no schema discovered => default "public"
    var generated = result.GeneratedSources
        .First(s => s.HintName == GENERATED_FILE)
        .SourceText.ToString();
    await Assert.That(generated).Contains("modelBuilder.HasDefaultSchema(\"public\")");
  }

  /// <summary>
  /// Test that a namespace ending in a generic segment ("API") derives the schema from
  /// the second-to-last segment with the "Service" suffix stripped:
  /// "TestApp.OrderService.API" => "order".
  /// </summary>
  [Test]
  public async Task Generator_DbContextInApiNamespace_DerivesSchemaFromParentSegmentAsync() {
    // Arrange
    const string source = """
        using Microsoft.EntityFrameworkCore;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;
        using Whizbang.Data.EFCore.Custom;

        namespace TestApp.OrderService.API;

        public record OrderItem(string Sku);

        public class OrderPerspective(IPerspectiveStore<OrderItem> store)
          : IPerspectiveFor<OrderItem, ItemCreated> {
          public OrderItem Apply(OrderItem currentData, ItemCreated @event) => currentData;
        }

        public record ItemCreated : IEvent;

        [WhizbangDbContext]
        public class OrderDbContext : DbContext {
          public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }
        }
        """;

    // Act
    var result = await GeneratorTestHelpers.RunEFCoreGeneratorWithEFCoreReferencesAsync(source);

    // Assert - "TestApp.OrderService.API" => segment "OrderService" => "order"
    var generated = result.GeneratedSources
        .First(s => s.HintName == GENERATED_FILE)
        .SourceText.ToString();
    await Assert.That(generated).Contains("modelBuilder.HasDefaultSchema(\"order\")");
  }

  #endregion

  #region Storage mode and model-shape edge cases

  /// <summary>
  /// Test that a generic (open) perspective whose TModel is a type parameter is handled
  /// safely: no physical fields, no polymorphic detection, no split mode - and generation
  /// still succeeds for the discovered perspective.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GenericPerspective_HandlesTypeParameterModelAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class GenericPerspective<TModel>(IPerspectiveStore<TModel> store)
          : IPerspectiveFor<TModel, ItemChanged> where TModel : class {
          public TModel Apply(TModel currentData, ItemChanged @event) => currentData;
        }

        public record ItemChanged : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert - discovered as one perspective; type-parameter model takes the null-safe paths
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("1 perspective(s)");
    await Assert.That(generated).DoesNotContain(POLYMORPHIC_CONFIG_MARKER);
  }

  /// <summary>
  /// Test that a model with [PerspectiveStorage(FieldStorageMode.Split)] is extracted
  /// (Split detection path) and still generates the standard configuration with its
  /// physical field columns. Split mode currently only feeds PerspectiveInfo metadata;
  /// snippet selection is driven by polymorphism.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_SplitStorageModel_GeneratesStandardConfigWithPhysicalFieldsAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        [PerspectiveStorage(FieldStorageMode.Split)]
        public class SensorReading {
          [PhysicalField(Indexed = true)]
          public string DeviceId { get; init; } = "";

          public string Payload { get; init; } = "";
        }

        public class SensorPerspective(IPerspectiveStore<SensorReading> store)
          : IPerspectiveFor<SensorReading, ReadingRecorded> {
          public SensorReading Apply(SensorReading currentData, ReadingRecorded @event) => currentData;
        }

        public record ReadingRecorded : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(STANDARD_CONFIG_MARKER);
    await Assert.That(generated).Contains("entity.Property<string>(\"device_id\")");
    await Assert.That(generated).Contains(".HasDatabaseName(\"ix_wh_per_sensor_reading_device_id\")");
  }

  /// <summary>
  /// Test that [PerspectiveStorage] with a non-Split mode (Extracted) takes the
  /// mode-mismatch branch and generates the standard configuration.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ExtractedStorageModel_GeneratesStandardConfigAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        [PerspectiveStorage(FieldStorageMode.Extracted)]
        public class SensorReading {
          [PhysicalField]
          public string DeviceId { get; init; } = "";
        }

        public class SensorPerspective(IPerspectiveStore<SensorReading> store)
          : IPerspectiveFor<SensorReading, ReadingRecorded> {
          public SensorReading Apply(SensorReading currentData, ReadingRecorded @event) => currentData;
        }

        public record ReadingRecorded : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(STANDARD_CONFIG_MARKER);
    await Assert.That(generated).Contains("entity.Property<string>(\"device_id\")");
  }

  #endregion

  #region Physical field extraction

  /// <summary>
  /// Test that [PhysicalField(MaxLength, ColumnName)] produces a varchar(N) column
  /// under the custom column name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PhysicalFieldWithMaxLengthAndColumnName_GeneratesVarcharColumnAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class Catalog {
          [PhysicalField(MaxLength = 100, ColumnName = "custom_sku")]
          public string Sku { get; init; } = "";
        }

        public class CatalogPerspective(IPerspectiveStore<Catalog> store)
          : IPerspectiveFor<Catalog, CatalogChanged> {
          public Catalog Apply(Catalog currentData, CatalogChanged @event) => currentData;
        }

        public record CatalogChanged : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("entity.Property<string>(\"custom_sku\")");
    await Assert.That(generated).Contains(".HasColumnName(\"custom_sku\")");
    await Assert.That(generated).Contains(".HasColumnType(\"varchar(100)\")");
  }

  /// <summary>
  /// Test that MaxLength = 0 means "not set": the string column falls back to
  /// unlimited text with the default snake_case column name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PhysicalFieldWithZeroMaxLength_GeneratesTextColumnAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class Catalog {
          [PhysicalField(MaxLength = 0)]
          public string LongNotes { get; init; } = "";
        }

        public class CatalogPerspective(IPerspectiveStore<Catalog> store)
          : IPerspectiveFor<Catalog, CatalogChanged> {
          public Catalog Apply(Catalog currentData, CatalogChanged @event) => currentData;
        }

        public record CatalogChanged : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("entity.Property<string>(\"long_notes\")");
    await Assert.That(generated).Contains(".HasColumnType(\"text\")");
    await Assert.That(generated).DoesNotContain("varchar(");
  }

  /// <summary>
  /// Test that an indexed unique physical field generates a unique index.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_UniqueIndexedPhysicalField_GeneratesUniqueIndexAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class Catalog {
          [PhysicalField(Indexed = true, Unique = true)]
          public string Sku { get; init; } = "";
        }

        public class CatalogPerspective(IPerspectiveStore<Catalog> store)
          : IPerspectiveFor<Catalog, CatalogChanged> {
          public Catalog Apply(Catalog currentData, CatalogChanged @event) => currentData;
        }

        public record CatalogChanged : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("entity.HasIndex(\"sku\")");
    await Assert.That(generated).Contains(".HasDatabaseName(\"ix_wh_per_catalog_sku\")");
    await Assert.That(generated).Contains(".IsUnique();");
  }

  /// <summary>
  /// Test the full C#-to-PostgreSQL column type mapping for physical fields:
  /// numeric types, bool, Guid (incl. nullable), date/time types, and the text fallback.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PhysicalFieldTypes_MapToPostgresColumnTypesAsync() {
    // Arrange
    const string source = """
        using System;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class Telemetry {
          [PhysicalField]
          public int Quantity { get; init; }

          [PhysicalField]
          public long BigCount { get; init; }

          [PhysicalField]
          public short SmallCount { get; init; }

          [PhysicalField]
          public decimal Price { get; init; }

          [PhysicalField]
          public double Ratio { get; init; }

          [PhysicalField]
          public float Score { get; init; }

          [PhysicalField]
          public bool Active { get; init; }

          [PhysicalField]
          public Guid ExternalId { get; init; }

          [PhysicalField]
          public Guid? OptionalRef { get; init; }

          [PhysicalField]
          public DateTime LocalStamp { get; init; }

          [PhysicalField]
          public DateTimeOffset RecordedAt { get; init; }

          [PhysicalField]
          public DateOnly Day { get; init; }

          [PhysicalField]
          public TimeOnly TimeOfDay { get; init; }

          [PhysicalField]
          public char Code { get; init; }
        }

        public class TelemetryPerspective(IPerspectiveStore<Telemetry> store)
          : IPerspectiveFor<Telemetry, TelemetryRecorded> {
          public Telemetry Apply(Telemetry currentData, TelemetryRecorded @event) => currentData;
        }

        public record TelemetryRecorded : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert - one shadow property per field, with the mapped PostgreSQL column type
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();

    await Assert.That(generated).Contains("entity.Property<int>(\"quantity\")");
    await Assert.That(generated).Contains(".HasColumnType(\"integer\")");
    await Assert.That(generated).Contains("entity.Property<long>(\"big_count\")");
    await Assert.That(generated).Contains(".HasColumnType(\"bigint\")");
    await Assert.That(generated).Contains("entity.Property<short>(\"small_count\")");
    await Assert.That(generated).Contains(".HasColumnType(\"smallint\")");
    await Assert.That(generated).Contains("entity.Property<decimal>(\"price\")");
    await Assert.That(generated).Contains(".HasColumnType(\"decimal\")");
    await Assert.That(generated).Contains("entity.Property<double>(\"ratio\")");
    await Assert.That(generated).Contains(".HasColumnType(\"double precision\")");
    await Assert.That(generated).Contains("entity.Property<float>(\"score\")");
    await Assert.That(generated).Contains(".HasColumnType(\"real\")");
    await Assert.That(generated).Contains("entity.Property<bool>(\"active\")");
    await Assert.That(generated).Contains(".HasColumnType(\"boolean\")");
    await Assert.That(generated).Contains("entity.Property<System.Guid>(\"external_id\")");
    await Assert.That(generated).Contains(".HasColumnType(\"uuid\")");

    // Nullability preserved on the shadow property; column type still uuid
    await Assert.That(generated).Contains("entity.Property<System.Guid?>(\"optional_ref\")");

    await Assert.That(generated).Contains("entity.Property<System.DateTime>(\"local_stamp\")");
    await Assert.That(generated).Contains(".HasColumnType(\"timestamp\")");
    await Assert.That(generated).Contains("entity.Property<System.DateTimeOffset>(\"recorded_at\")");
    await Assert.That(generated).Contains(".HasColumnType(\"timestamptz\")");
    await Assert.That(generated).Contains("entity.Property<System.DateOnly>(\"day\")");
    await Assert.That(generated).Contains(".HasColumnType(\"date\")");
    await Assert.That(generated).Contains("entity.Property<System.TimeOnly>(\"time_of_day\")");
    await Assert.That(generated).Contains(".HasColumnType(\"time\")");

    // char has no dedicated mapping - falls back to text
    await Assert.That(generated).Contains("entity.Property<char>(\"code\")");
    await Assert.That(generated).Contains(".HasColumnType(\"text\")");
  }

  #endregion

  #region Vector field extraction

  /// <summary>
  /// Test [VectorField] with every named argument: HNSW index type, L2 metric,
  /// custom column name, and IndexLists - including the pgvector extension requirement.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_VectorFieldWithHnswAndL2_GeneratesHnswIndexWithL2OperatorsAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class Document {
          [VectorField(768, DistanceMetric = VectorDistanceMetric.L2, IndexType = VectorIndexType.HNSW, ColumnName = "embedding_vec", IndexLists = 200)]
          public float[]? Embedding { get; init; }
        }

        public class DocumentPerspective(IPerspectiveStore<Document> store)
          : IPerspectiveFor<Document, DocumentIndexed> {
          public Document Apply(Document currentData, DocumentIndexed @event) => currentData;
        }

        public record DocumentIndexed : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();

    await Assert.That(generated).Contains("entity.Property<Pgvector.Vector?>(\"embedding_vec\")");
    await Assert.That(generated).Contains(".HasColumnType(\"vector(768)\")");
    await Assert.That(generated).Contains(".HasDatabaseName(\"ix_wh_per_document_embedding_vec_vec\")");
    await Assert.That(generated).Contains(".HasMethod(\"hnsw\")");
    await Assert.That(generated).Contains(".HasOperators(\"vector_l2_ops\")");
    await Assert.That(generated).Contains("modelBuilder.HasPostgresExtension(\"vector\")");
  }

  /// <summary>
  /// Test [VectorField] with InnerProduct metric and default index type (IVFFlat).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_VectorFieldWithInnerProduct_GeneratesIvfflatWithInnerProductOperatorsAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class Article {
          [VectorField(384, DistanceMetric = VectorDistanceMetric.InnerProduct)]
          public float[]? Embedding { get; init; }
        }

        public class ArticlePerspective(IPerspectiveStore<Article> store)
          : IPerspectiveFor<Article, ArticleIndexed> {
          public Article Apply(Article currentData, ArticleIndexed @event) => currentData;
        }

        public record ArticleIndexed : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();

    await Assert.That(generated).Contains("entity.Property<Pgvector.Vector?>(\"embedding\")");
    await Assert.That(generated).Contains(".HasColumnType(\"vector(384)\")");
    await Assert.That(generated).Contains(".HasMethod(\"ivfflat\")");
    await Assert.That(generated).Contains(".HasOperators(\"vector_ip_ops\")");
  }

  /// <summary>
  /// Test that [VectorField(Indexed = false)] generates the vector column but NO index,
  /// while still requiring the pgvector extension.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_VectorFieldNotIndexed_OmitsVectorIndexAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class Snapshot {
          [VectorField(128, Indexed = false)]
          public float[]? Embedding { get; init; }
        }

        public class SnapshotPerspective(IPerspectiveStore<Snapshot> store)
          : IPerspectiveFor<Snapshot, SnapshotTaken> {
          public Snapshot Apply(Snapshot currentData, SnapshotTaken @event) => currentData;
        }

        public record SnapshotTaken : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();

    await Assert.That(generated).Contains(".HasColumnType(\"vector(128)\")");
    await Assert.That(generated).DoesNotContain(".HasMethod(");
    await Assert.That(generated).DoesNotContain(".HasOperators(");
    await Assert.That(generated).Contains("modelBuilder.HasPostgresExtension(\"vector\")");
  }

  /// <summary>
  /// Test that an out-of-range distance metric value falls back to cosine operators.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_VectorFieldWithUnknownDistanceMetric_FallsBackToCosineOperatorsAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class Sketch {
          [VectorField(64, DistanceMetric = (VectorDistanceMetric)99)]
          public float[]? Embedding { get; init; }
        }

        public class SketchPerspective(IPerspectiveStore<Sketch> store)
          : IPerspectiveFor<Sketch, SketchSaved> {
          public Sketch Apply(Sketch currentData, SketchSaved @event) => currentData;
        }

        public record SketchSaved : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(".HasColumnType(\"vector(64)\")");
    await Assert.That(generated).Contains(".HasOperators(\"vector_cosine_ops\")");
  }

  #endregion

  #region Polymorphic discriminator extraction

  /// <summary>
  /// Test that [PolymorphicDiscriminator] produces an indexed, non-unique text column
  /// (default snake_case name) and that the polymorphic entity configuration is selected
  /// for the model containing the abstract property.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PolymorphicDiscriminator_GeneratesIndexedTextColumnAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public abstract class PaymentMethod {
          public string Name { get; init; } = "";
        }

        public record OrderDto {
          public string Number { get; init; } = "";
          public PaymentMethod? Payment { get; init; }

          [PolymorphicDiscriminator]
          public string PaymentKind { get; init; } = "";
        }

        public class OrderPerspective(IPerspectiveStore<OrderDto> store)
          : IPerspectiveFor<OrderDto, OrderPlaced> {
          public OrderDto Apply(OrderDto currentData, OrderPlaced @event) => currentData;
        }

        public record OrderPlaced : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert - polymorphic snippet + discriminator shadow column with index
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();

    await Assert.That(generated).Contains(POLYMORPHIC_CONFIG_MARKER);
    await Assert.That(generated).Contains("entity.Property<System.String>(\"payment_kind\")");
    await Assert.That(generated).Contains(".HasColumnType(\"text\")");
    await Assert.That(generated).Contains(".HasDatabaseName(\"ix_wh_per_order_payment_kind\")");

    // Discriminators are never unique
    await Assert.That(generated).DoesNotContain(".IsUnique();");
  }

  /// <summary>
  /// Test that [PolymorphicDiscriminator(ColumnName = ...)] overrides the column name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PolymorphicDiscriminatorWithColumnName_UsesCustomColumnNameAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public abstract class PaymentMethod {
          public string Name { get; init; } = "";
        }

        public record InvoiceDto {
          public PaymentMethod? Payment { get; init; }

          [PolymorphicDiscriminator(ColumnName = "ptype")]
          public string PaymentKind { get; init; } = "";
        }

        public class InvoicePerspective(IPerspectiveStore<InvoiceDto> store)
          : IPerspectiveFor<InvoiceDto, InvoiceIssued> {
          public InvoiceDto Apply(InvoiceDto currentData, InvoiceIssued @event) => currentData;
        }

        public record InvoiceIssued : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("entity.Property<System.String>(\"ptype\")");
    await Assert.That(generated).Contains(".HasDatabaseName(\"ix_wh_per_invoice_ptype\")");
    await Assert.That(generated).DoesNotContain("payment_kind");
  }

  #endregion

  #region Polymorphic model detection

  /// <summary>
  /// Test that an abstract property nested inside a concrete wrapper type is detected
  /// recursively and switches the model to the polymorphic configuration.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ModelWithNestedAbstractProperty_UsesPolymorphicConfigAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public abstract class PaymentMethod {
          public string Name { get; init; } = "";
        }

        public class OrderDetails {
          public PaymentMethod? Payment { get; init; }
        }

        public record OrderDto {
          public OrderDetails? Details { get; init; }
        }

        public class OrderPerspective(IPerspectiveStore<OrderDto> store)
          : IPerspectiveFor<OrderDto, OrderPlaced> {
          public OrderDto Apply(OrderDto currentData, OrderPlaced @event) => currentData;
        }

        public record OrderPlaced : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(POLYMORPHIC_CONFIG_MARKER);
    await Assert.That(generated).Contains("entity.Property(e => e.Data).HasColumnName(\"data\").HasColumnType(\"jsonb\");");
  }

  /// <summary>
  /// Test that a List of a concrete wrapper containing an abstract property is detected
  /// through the collection element type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ModelWithListOfWrapperType_UsesPolymorphicConfigAsync() {
    // Arrange
    const string source = """
        using System.Collections.Generic;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public abstract class PaymentMethod {
          public string Name { get; init; } = "";
        }

        public class OrderDetails {
          public PaymentMethod? Payment { get; init; }
        }

        public record OrderDto {
          public List<OrderDetails> History { get; init; } = new();
        }

        public class OrderPerspective(IPerspectiveStore<OrderDto> store)
          : IPerspectiveFor<OrderDto, OrderPlaced> {
          public OrderDto Apply(OrderDto currentData, OrderPlaced @event) => currentData;
        }

        public record OrderPlaced : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(POLYMORPHIC_CONFIG_MARKER);
  }

  /// <summary>
  /// Test that IReadOnlyList of an abstract element type is detected (collection
  /// interface variant of the element-type extraction).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ModelWithReadOnlyListOfAbstract_UsesPolymorphicConfigAsync() {
    // Arrange
    const string source = """
        using System.Collections.Generic;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public abstract class PaymentMethod {
          public string Name { get; init; } = "";
        }

        public record OrderDto {
          public IReadOnlyList<PaymentMethod> Methods { get; init; } = new List<PaymentMethod>();
        }

        public class OrderPerspective(IPerspectiveStore<OrderDto> store)
          : IPerspectiveFor<OrderDto, OrderPlaced> {
          public OrderDto Apply(OrderDto currentData, OrderPlaced @event) => currentData;
        }

        public record OrderPlaced : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(POLYMORPHIC_CONFIG_MARKER);
  }

  /// <summary>
  /// Test that Dictionary is NOT a recognized collection (element extraction returns null),
  /// but its abstract type ARGUMENT is still detected via the generic-argument scan.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ModelWithDictionaryOfAbstract_UsesPolymorphicConfigAsync() {
    // Arrange
    const string source = """
        using System.Collections.Generic;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public abstract class PaymentMethod {
          public string Name { get; init; } = "";
        }

        public record OrderDto {
          public Dictionary<string, PaymentMethod> ByName { get; init; } = new();
        }

        public class OrderPerspective(IPerspectiveStore<OrderDto> store)
          : IPerspectiveFor<OrderDto, OrderPlaced> {
          public OrderDto Apply(OrderDto currentData, OrderPlaced @event) => currentData;
        }

        public record OrderPlaced : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(POLYMORPHIC_CONFIG_MARKER);
  }

  /// <summary>
  /// Test that a generic type argument which is itself a concrete wrapper containing an
  /// abstract property is detected recursively through the argument scan.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ModelWithDictionaryOfWrapperType_UsesPolymorphicConfigAsync() {
    // Arrange
    const string source = """
        using System.Collections.Generic;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public abstract class PaymentMethod {
          public string Name { get; init; } = "";
        }

        public class OrderDetails {
          public PaymentMethod? Payment { get; init; }
        }

        public record OrderDto {
          public Dictionary<string, OrderDetails> ByRegion { get; init; } = new();
        }

        public class OrderPerspective(IPerspectiveStore<OrderDto> store)
          : IPerspectiveFor<OrderDto, OrderPlaced> {
          public OrderDto Apply(OrderDto currentData, OrderPlaced @event) => currentData;
        }

        public record OrderPlaced : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(POLYMORPHIC_CONFIG_MARKER);
  }

  /// <summary>
  /// Test that a self-referencing model terminates via cycle detection and stays on
  /// the standard (non-polymorphic) configuration.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_SelfReferencingModel_UsesStandardConfigAsync() {
    // Arrange
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public class Category {
          public string Name { get; init; } = "";
          public Category? Parent { get; init; }
        }

        public class CategoryPerspective(IPerspectiveStore<Category> store)
          : IPerspectiveFor<Category, CategoryCreated> {
          public Category Apply(Category currentData, CategoryCreated @event) => currentData;
        }

        public record CategoryCreated : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(STANDARD_CONFIG_MARKER);
    await Assert.That(generated).DoesNotContain(POLYMORPHIC_CONFIG_MARKER);
  }

  /// <summary>
  /// Test that non-collection System types (Exception), System primitives (Uri), and
  /// concrete types carrying non-polymorphic attributes are all treated as
  /// non-polymorphic - the standard configuration is used.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ModelWithSystemAndAttributedConcreteTypes_UsesStandardConfigAsync() {
    // Arrange
    const string source = """
        using System;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        [Obsolete("attributed but concrete - not polymorphic")]
        public class LegacyInfo {
          public string Detail { get; init; } = "";
        }

        public class AuditEntry {
          public Exception? LastError { get; init; }
          public Uri? Link { get; init; }
          public LegacyInfo? Legacy { get; init; }
        }

        public class AuditPerspective(IPerspectiveStore<AuditEntry> store)
          : IPerspectiveFor<AuditEntry, AuditRecorded> {
          public AuditEntry Apply(AuditEntry currentData, AuditRecorded @event) => currentData;
        }

        public record AuditRecorded : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(STANDARD_CONFIG_MARKER);
    await Assert.That(generated).DoesNotContain(POLYMORPHIC_CONFIG_MARKER);
  }

  /// <summary>
  /// Test that a concrete class marked [JsonPolymorphic] switches the model to the
  /// polymorphic configuration (attribute-based detection, not abstractness).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ModelWithJsonPolymorphicProperty_UsesPolymorphicConfigAsync() {
    // Arrange
    const string source = """
        using System.Text.Json.Serialization;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        [JsonPolymorphic]
        [JsonDerivedType(typeof(Circle), "circle")]
        public class Shape {
          public string Color { get; init; } = "";
        }

        public class Circle : Shape {
          public double Radius { get; init; }
        }

        public record CanvasDto {
          public Shape? MainShape { get; init; }
        }

        public class CanvasPerspective(IPerspectiveStore<CanvasDto> store)
          : IPerspectiveFor<CanvasDto, CanvasSaved> {
          public CanvasDto Apply(CanvasDto currentData, CanvasSaved @event) => currentData;
        }

        public record CanvasSaved : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(POLYMORPHIC_CONFIG_MARKER);
  }

  /// <summary>
  /// Test that a [JsonIgnore] abstract property does NOT switch the model to the
  /// polymorphic configuration (ignored properties are excluded from detection).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ModelWithJsonIgnoredAbstractProperty_UsesStandardConfigAsync() {
    // Arrange
    const string source = """
        using System.Text.Json.Serialization;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;

        namespace TestApp;

        public abstract class PaymentMethod {
          public string Name { get; init; } = "";
        }

        public class OrderDto {
          public string Number { get; init; } = "";

          [JsonIgnore]
          public PaymentMethod? CachedPayment { get; init; }
        }

        public class OrderPerspective(IPerspectiveStore<OrderDto> store)
          : IPerspectiveFor<OrderDto, OrderPlaced> {
          public OrderDto Apply(OrderDto currentData, OrderPlaced @event) => currentData;
        }

        public record OrderPlaced : IEvent;
        """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);

    // Assert
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE);
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(STANDARD_CONFIG_MARKER);
    await Assert.That(generated).DoesNotContain(POLYMORPHIC_CONFIG_MARKER);
  }

  #endregion

  /// <summary>
  /// Runs the generator against source compiled into an assembly with the given name.
  /// Used to verify the library-assembly self-exclusion gate.
  /// </summary>
  private static GeneratorDriverRunResult _runGeneratorWithAssemblyName(string source, string assemblyName) {
    var syntaxTree = CSharpSyntaxTree.ParseText(source);

    var references = new List<MetadataReference> {
      MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
      MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
      MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("netstandard").Location),
      MetadataReference.CreateFromFile(typeof(Whizbang.Core.IEvent).Assembly.Location)
    };

    var compilation = CSharpCompilation.Create(
        assemblyName: assemblyName,
        syntaxTrees: [syntaxTree],
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    var driver = CSharpGeneratorDriver.Create(new EFCorePerspectiveConfigurationGenerator());
    driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);
    return driver.GetRunResult();
  }
}
