using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage tests for <see cref="Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator"/>
/// targeting branches not exercised by the main test suite: duplicate-hint generator error
/// diagnostics, [WhizbangDbContext] Schema/ConnectionStringName named arguments,
/// namespace-derived schema names (API/Worker suffixes), keyed perspective↔DbContext matching,
/// PhysicalField Unique/ColumnName extraction, VectorField named-argument extraction,
/// array/unknown Postgres column-type mapping, non-lens generic constructor parameters, the
/// no-perspective schema-extension path, and the &gt;2000-dimension vector index skip.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</tests>
[Category("SourceGenerators")]
public class EFCoreServiceRegistrationGeneratorCoverageTests {

  // Minimal perspective boilerplate (class model per coverage-test conventions - records
  // classify polymorphic via compiler-generated EqualityContract in other generators).
  private const string PERSPECTIVE_SNIPPET = """
    public record CoverageEvent : IEvent;

    public class CoverageModel {
      public string Id { get; set; } = "";
    }

    public class CoveragePerspective : IPerspectiveFor<CoverageModel, CoverageEvent> {
      public CoverageModel Apply(CoverageModel currentData, CoverageEvent eventData) => currentData;
    }
    """;

  #region Generator error diagnostics (duplicate hint names)

  /// <summary>
  /// Two [WhizbangDbContext] classes with the SAME class name in different namespaces force
  /// AddSource to be called twice with an identical hint name inside each source-output
  /// callback. The resulting ArgumentException must be swallowed by the generator's own
  /// try/catch and surfaced as EFCORE997 (partial class), EFCORE995 (schema extensions), and
  /// EFCORE994 (turnkey extensions) error diagnostics instead of crashing the compilation.
  /// The registration-metadata callback uses fixed hint names, so EFCORE996 must NOT fire.
  /// </summary>
  [Test]
  public async Task Generator_WithDuplicateDbContextClassNames_ReportsGeneratorErrorDiagnosticsAsync() {
    // Arrange - same class name "DupDbContext" in two namespaces => duplicate hint names
    const string source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace AppOne {
        [WhizbangDbContext]
        public class DupDbContext : DbContext { }
      }

      namespace AppTwo {
        [WhizbangDbContext]
        public class DupDbContext : DbContext { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - first context still generated before the collision
    var partialClass = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("DupDbContext.Generated"));
    await Assert.That(partialClass).IsNotNull();

    // Each per-DbContext callback reports its own error diagnostic
    var efcore997 = result.Diagnostics.FirstOrDefault(d => d.Id == "EFCORE997");
    await Assert.That(efcore997).IsNotNull();
    await Assert.That(efcore997!.Severity).IsEqualTo(DiagnosticSeverity.Error);

    var efcore995 = result.Diagnostics.FirstOrDefault(d => d.Id == "EFCORE995");
    await Assert.That(efcore995).IsNotNull();
    await Assert.That(efcore995!.Severity).IsEqualTo(DiagnosticSeverity.Error);

    var efcore994 = result.Diagnostics.FirstOrDefault(d => d.Id == "EFCORE994");
    await Assert.That(efcore994).IsNotNull();
    await Assert.That(efcore994!.Severity).IsEqualTo(DiagnosticSeverity.Error);

    // Registration metadata uses fixed hint names - no duplicate, no error
    var efcore996 = result.Diagnostics.FirstOrDefault(d => d.Id == "EFCORE996");
    await Assert.That(efcore996).IsNull();
  }

  #endregion

  #region [WhizbangDbContext] Schema / ConnectionStringName named arguments

  /// <summary>
  /// The Schema named argument on [WhizbangDbContext] must win over namespace derivation:
  /// the generated schema SQL targets the explicit schema, not one derived from "TestApp".
  /// </summary>
  [Test]
  public async Task Generator_WithExplicitSchemaProperty_UsesSchemaFromAttributeAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_SNIPPET}}

      [WhizbangDbContext(Schema = "custom_area")]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var schemaExtensions = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("TestDbContext_SchemaExtensions"));
    await Assert.That(schemaExtensions).IsNotNull();

    var sourceText = schemaExtensions!.SourceText.ToString();
    await Assert.That(sourceText).Contains("-- Schema: custom_area")
      .Because("Schema named argument should override namespace-derived schema");
    await Assert.That(sourceText).DoesNotContain("-- Schema: testapp")
      .Because("Namespace derivation must not run when Schema is explicit");
  }

  /// <summary>
  /// The ConnectionStringName named argument on [WhizbangDbContext] must be used as the
  /// default connection string key in BOTH generated registration paths (turnkey extension
  /// and DbContextRegistrationRegistry callback) instead of the "{classname}-db" convention.
  /// </summary>
  [Test]
  public async Task Generator_WithConnectionStringNameProperty_UsesItAsDefaultConnectionKeyAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      {{PERSPECTIVE_SNIPPET}}

      [WhizbangDbContext(ConnectionStringName = "custom-conn")]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - turnkey AddTestDbContext extension uses the explicit key
    var turnkey = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("TestDbContextExtensions"));
    await Assert.That(turnkey).IsNotNull();

    var turnkeyText = turnkey!.SourceText.ToString();
    await Assert.That(turnkeyText).Contains("connectionStringName ?? \"custom-conn\"")
      .Because("ConnectionStringName named argument should replace the derived \"test-db\" default");
    await Assert.That(turnkeyText).DoesNotContain("\"test-db\"")
      .Because("Class-name derivation must not run when ConnectionStringName is explicit");

    // Registration callback path uses the same key
    var registration = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("EFCoreModelRegistration"));
    await Assert.That(registration).IsNotNull();
    await Assert.That(registration!.SourceText.ToString()).Contains("connectionStringNameOverride ?? \"custom-conn\"");
  }

  #endregion

  #region Namespace-derived schema names

  /// <summary>
  /// When the last namespace segment is the generic "API", the schema is derived from the
  /// second-to-last segment: "Shop.Inventory.API" derives schema "inventory".
  /// </summary>
  [Test]
  public async Task Generator_WithApiSuffixNamespace_DerivesSchemaFromSecondToLastSegmentAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace Shop.Inventory.API;

      {{PERSPECTIVE_SNIPPET}}

      [WhizbangDbContext]
      public class InventoryDbContext : DbContext {
        public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var schemaExtensions = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("InventoryDbContext_SchemaExtensions"));
    await Assert.That(schemaExtensions).IsNotNull();

    var sourceText = schemaExtensions!.SourceText.ToString();
    await Assert.That(sourceText).Contains("-- Schema: inventory")
      .Because("\"Shop.Inventory.API\" should derive schema from second-to-last segment \"Inventory\"");
  }

  /// <summary>
  /// A "Worker" suffix on the last namespace segment is stripped during schema derivation:
  /// "ECommerce.InventoryWorker" derives schema "inventory".
  /// </summary>
  [Test]
  public async Task Generator_WithWorkerSuffixNamespace_StripsWorkerSuffixFromSchemaAsync() {
    // Arrange
    var source = $$"""
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace ECommerce.InventoryWorker;

      {{PERSPECTIVE_SNIPPET}}

      [WhizbangDbContext]
      public class InventoryDbContext : DbContext {
        public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var schemaExtensions = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("InventoryDbContext_SchemaExtensions"));
    await Assert.That(schemaExtensions).IsNotNull();

    var sourceText = schemaExtensions!.SourceText.ToString();
    await Assert.That(sourceText).Contains("-- Schema: inventory")
      .Because("\"ECommerce.InventoryWorker\" should strip the Worker suffix and derive schema \"inventory\"");
  }

  #endregion

  #region Keyed perspective <-> DbContext matching

  /// <summary>
  /// A perspective carrying [WhizbangPerspective("catalog")] matches a DbContext keyed
  /// "catalog" (key-intersection path), while a perspective keyed "other" is excluded.
  /// Locks the IPerspectiveFor discovery + keys-array extraction + key matching pipeline.
  /// </summary>
  [Test]
  public async Task Generator_WithKeyedPerspective_IncludesDbSetOnlyForMatchingKeyAsync() {
    // Arrange
    const string source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record CatalogEvent : IEvent;

      public class CatalogModel {
        public string Id { get; set; } = "";
      }

      public class OtherModel {
        public string Id { get; set; } = "";
      }

      [WhizbangPerspective("catalog")]
      public class CatalogPerspective : IPerspectiveFor<CatalogModel, CatalogEvent> {
        public CatalogModel Apply(CatalogModel currentData, CatalogEvent eventData) => currentData;
      }

      [WhizbangPerspective("other")]
      public class OtherPerspective : IPerspectiveFor<OtherModel, CatalogEvent> {
        public OtherModel Apply(OtherModel currentData, CatalogEvent eventData) => currentData;
      }

      [WhizbangDbContext("catalog")]
      public class CatalogDbContext : DbContext {
        public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var partialClass = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("CatalogDbContext.Generated"));
    await Assert.That(partialClass).IsNotNull();

    var sourceText = partialClass!.SourceText.ToString();
    await Assert.That(sourceText).Contains("DbSet<PerspectiveRow<global::TestApp.CatalogModel>>")
      .Because("Perspective keyed \"catalog\" matches DbContext keyed \"catalog\"");
    await Assert.That(sourceText).DoesNotContain("OtherModel")
      .Because("Perspective keyed \"other\" must not match DbContext keyed \"catalog\"");
  }

  /// <summary>
  /// A parameterless [WhizbangPerspective] attribute yields an empty keys array, which
  /// matches the default (unnamed) DbContext key "" only.
  /// </summary>
  [Test]
  public async Task Generator_WithParameterlessPerspectiveAttribute_MatchesDefaultContextAsync() {
    // Arrange
    const string source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record CoverageEvent : IEvent;

      public class CoverageModel {
        public string Id { get; set; } = "";
      }

      [WhizbangPerspective]
      public class CoveragePerspective : IPerspectiveFor<CoverageModel, CoverageEvent> {
        public CoverageModel Apply(CoverageModel currentData, CoverageEvent eventData) => currentData;
      }

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var partialClass = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("TestDbContext.Generated"));
    await Assert.That(partialClass).IsNotNull();

    var sourceText = partialClass!.SourceText.ToString();
    await Assert.That(sourceText).Contains("DbSet<PerspectiveRow<global::TestApp.CoverageModel>>")
      .Because("Parameterless [WhizbangPerspective] should match the default DbContext key \"\"");
  }

  #endregion

  #region PhysicalField / VectorField named-argument extraction

  /// <summary>
  /// The Unique and ColumnName named arguments on [PhysicalField] are extracted: the DDL
  /// column uses the custom name instead of snake_case, and Indexed still creates the index.
  /// </summary>
  [Test]
  public async Task Generator_WithPhysicalFieldUniqueAndCustomColumnName_UsesCustomColumnNameAsync() {
    // Arrange
    const string source = """
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record ReferenceEvent : IEvent;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public class ReferenceModel {
        [StreamId]
        public Guid Id { get; set; }

        [PhysicalField(Indexed = true, Unique = true, ColumnName = "ext_id")]
        public string? ExternalId { get; set; }
      }

      public class ReferencePerspective : IPerspectiveFor<ReferenceModel, ReferenceEvent> {
        public ReferenceModel Apply(ReferenceModel currentData, ReferenceEvent eventData) => currentData;
      }

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var schemaExtensions = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("SchemaExtensions"));
    await Assert.That(schemaExtensions).IsNotNull();

    var sourceText = schemaExtensions!.SourceText.ToString();
    await Assert.That(sourceText).Contains("ext_id TEXT")
      .Because("ColumnName = \"ext_id\" should override snake_case \"external_id\"");
    await Assert.That(sourceText).DoesNotContain("external_id")
      .Because("Default snake_case column name must not be emitted when ColumnName is set");
    // Default config strips the "Model" suffix: ReferenceModel => wh_per_reference
    await Assert.That(sourceText).Contains("idx_reference_ext_id")
      .Because("Indexed = true should create an index over the custom column name");
  }

  /// <summary>
  /// All [VectorField] named arguments (ColumnName, DistanceMetric, IndexType, IndexLists,
  /// Indexed) are extracted: the custom column name carries the dimensions, and a field with
  /// Indexed = false gets a column but no vector index.
  /// </summary>
  [Test]
  public async Task Generator_WithVectorFieldNamedArguments_AppliesCustomColumnAndIndexSettingsAsync() {
    // Arrange
    const string source = """
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record SearchEvent : IEvent;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public class SearchModel {
        [StreamId]
        public Guid Id { get; set; }

        [VectorField(768, Indexed = true, ColumnName = "title_vec", DistanceMetric = VectorDistanceMetric.L2, IndexType = VectorIndexType.HNSW, IndexLists = 200)]
        public float[]? TitleEmbedding { get; set; }

        [VectorField(512, Indexed = false)]
        public float[]? BodyEmbedding { get; set; }
      }

      public class SearchPerspective : IPerspectiveFor<SearchModel, SearchEvent> {
        public SearchModel Apply(SearchModel currentData, SearchEvent eventData) => currentData;
      }

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var schemaExtensions = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("SchemaExtensions"));
    await Assert.That(schemaExtensions).IsNotNull();

    var sourceText = schemaExtensions!.SourceText.ToString();
    await Assert.That(sourceText).Contains("title_vec vector(768)")
      .Because("ColumnName = \"title_vec\" should override snake_case and keep the dimensions");
    // Default config strips the "Model" suffix: SearchModel => wh_per_search
    await Assert.That(sourceText).Contains("idx_search_title_vec_vec")
      .Because("Indexed vector field should get a vector index over the custom column");
    await Assert.That(sourceText).Contains("body_embedding vector(512)")
      .Because("Second vector field should use default snake_case column name");
    await Assert.That(sourceText).DoesNotContain("idx_search_body_embedding_vec")
      .Because("Indexed = false must suppress the vector index");
  }

  /// <summary>
  /// Vector fields above pgvector's 2000-dimension index limit still get a column, but the
  /// index is replaced by an explanatory SQL comment instead of an ivfflat index.
  /// </summary>
  [Test]
  public async Task Generator_WithVectorFieldOver2000Dimensions_SkipsVectorIndexAsync() {
    // Arrange
    const string source = """
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record HugeVectorEvent : IEvent;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public class HugeVectorModel {
        [StreamId]
        public Guid Id { get; set; }

        [VectorField(3072)]
        public float[]? Embeddings { get; set; }
      }

      public class HugeVectorPerspective : IPerspectiveFor<HugeVectorModel, HugeVectorEvent> {
        public HugeVectorModel Apply(HugeVectorModel currentData, HugeVectorEvent eventData) => currentData;
      }

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var schemaExtensions = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("SchemaExtensions"));
    await Assert.That(schemaExtensions).IsNotNull();

    var sourceText = schemaExtensions!.SourceText.ToString();
    await Assert.That(sourceText).Contains("vector(3072)")
      .Because("Column is still created even when dimensions exceed the index limit");
    await Assert.That(sourceText).Contains("Skipping vector index for embeddings (3072 dimensions > 2000 limit)")
      .Because("Generator should emit an explanatory comment instead of an index");
    // Default config strips the "Model" suffix: HugeVectorModel => wh_per_huge_vector
    await Assert.That(sourceText).DoesNotContain("idx_huge_vector_embeddings_vec")
      .Because("No ivfflat index may be created above 2000 dimensions");
  }

  #endregion

  #region Postgres column type mapping (arrays + unknown types)

  /// <summary>
  /// byte[], float[] and double[] physical fields map to BYTEA, REAL[] and DOUBLE PRECISION[]
  /// respectively; an unmapped type (TimeSpan) falls through to TEXT.
  /// </summary>
  [Test]
  public async Task Generator_SchemaExtensions_MapsArrayAndUnknownTypesToPostgresTypesAsync() {
    // Arrange
    const string source = """
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record BinaryEvent : IEvent;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public class BinaryModel {
        [StreamId]
        public Guid Id { get; set; }

        [PhysicalField]
        public byte[]? Payload { get; set; }

        [PhysicalField]
        public float[]? Scores { get; set; }

        [PhysicalField]
        public double[]? Averages { get; set; }

        [PhysicalField]
        public TimeSpan Duration { get; set; }
      }

      public class BinaryPerspective : IPerspectiveFor<BinaryModel, BinaryEvent> {
        public BinaryModel Apply(BinaryModel currentData, BinaryEvent eventData) => currentData;
      }

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert
    var schemaExtensions = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("SchemaExtensions"));
    await Assert.That(schemaExtensions).IsNotNull();

    var sourceText = schemaExtensions!.SourceText.ToString();
    await Assert.That(sourceText).Contains("payload BYTEA")
      .Because("byte[] should map to BYTEA");
    await Assert.That(sourceText).Contains("scores REAL[]")
      .Because("float[] (non-vector) should map to REAL[]");
    await Assert.That(sourceText).Contains("averages DOUBLE PRECISION[]")
      .Because("double[] should map to DOUBLE PRECISION[]");
    await Assert.That(sourceText).Contains("duration TEXT")
      .Because("Unmapped types (TimeSpan) should fall back to TEXT");
  }

  #endregion

  #region Non-lens generic constructor parameters

  /// <summary>
  /// Generic constructor parameters that are not Whizbang.Core.Lenses.ILensQuery must be
  /// rejected by each semantic filter: a generic class (Dictionary), a same-arity interface
  /// with a different name (IDictionary), and an ILensQuery&lt;T1, T2&gt; declared in a
  /// foreign namespace. None may produce a lens registration or a WHIZ401 diagnostic.
  /// </summary>
  [Test]
  public async Task Generator_WithNonLensQueryGenericConstructorParams_DoesNotRegisterLensQueriesAsync() {
    // Arrange
    const string source = """
      using System.Collections.Generic;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;
      using TestApp.Fakes;

      namespace TestApp.Fakes {
        public interface ILensQuery<T1, T2> { }
      }

      namespace TestApp {
        public class ModelA {
          public string Id { get; set; } = "";
        }

        public class ModelB {
          public string Id { get; set; } = "";
        }

        // Generic class parameter - TypeKind is not Interface
        public class ConsumerOne {
          public ConsumerOne(Dictionary<string, int> lookup) { }
        }

        // Interface with 2 type args but wrong name
        public class ConsumerTwo {
          public ConsumerTwo(IDictionary<string, int> lookup) { }
        }

        // ILensQuery by name but declared outside Whizbang.Core.Lenses
        public class ConsumerThree {
          public ConsumerThree(ILensQuery<ModelA, ModelB> query) { }
        }

        [WhizbangDbContext]
        public class TestDbContext : DbContext {
          public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
        }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - registration file exists but contains no lens registrations
    var registration = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("EFCoreModelRegistration"));
    await Assert.That(registration).IsNotNull();

    var sourceText = registration!.SourceText.ToString();
    await Assert.That(sourceText).DoesNotContain("services.AddTransient<Whizbang.Core.Lenses.ILensQuery<")
      .Because("None of the generic parameters is a real Whizbang ILensQuery");

    // No unknown-model warning either - the parameters were rejected before model matching
    var whiz401 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ401");
    await Assert.That(whiz401).IsNull();
    var whiz402 = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ402");
    await Assert.That(whiz402).IsNull();
  }

  #endregion

  #region DbContext without perspectives

  /// <summary>
  /// A [WhizbangDbContext] with zero matching perspectives still gets schema extensions, but
  /// the per-perspective entries collapse to an explanatory comment and the perspective
  /// registry JSON is the empty array literal.
  /// </summary>
  [Test]
  public async Task Generator_WithDbContextButNoPerspectives_EmitsEmptyPerspectiveEntriesAsync() {
    // Arrange - no perspective types at all
    const string source = """
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """;

    // Act
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);

    // Assert - schema extensions still generated for core infrastructure
    var schemaExtensions = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("TestDbContext_SchemaExtensions"));
    await Assert.That(schemaExtensions).IsNotNull();

    var sourceText = schemaExtensions!.SourceText.ToString();
    await Assert.That(sourceText).Contains("No perspectives found for this DbContext")
      .Because("Zero perspectives should produce the placeholder comment instead of table entries");
    await Assert.That(sourceText).Contains("const string PerspectiveRegistryJson = \"[]\";")
      .Because("Perspective registry JSON should be an empty array literal");
    await Assert.That(sourceText).DoesNotContain("CREATE TABLE IF NOT EXISTS \"\"testapp\"\".wh_per_")
      .Because("No perspective tables may be emitted");

    // Partial class still generated with the OnModelCreating override for core entities
    var partialClass = result.GeneratedSources.FirstOrDefault(s => s.HintName.Contains("TestDbContext.Generated"));
    await Assert.That(partialClass).IsNotNull();
    await Assert.That(partialClass!.SourceText.ToString()).Contains("protected override void OnModelCreating(ModelBuilder modelBuilder)");
  }

  #endregion

  #region Perspective data coalescing (dotnet/efcore#38625 workaround)

  // EF Core 10 materializes a JSON-absent complex collection as null rather than empty. That
  // happens on every row written before a collection property was added, so a schema evolution
  // turns yesterday's rows into NullReferenceExceptions on read and PrepareToSave failures on
  // write. The generator walks each perspective model's collection graph at compile time and
  // emits `??=` statements to repair the shape on materialization.
  //
  // The walk is the interesting part: it has to reach collections nested inside complex
  // references and inside other collections' elements, skip the properties where null is a
  // legitimate value, and terminate on a model graph that refers back to itself.

  private static string _coalescerFor(string modelBody, string extraTypes = "") => $$"""
    using System.Collections.Generic;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record CoalesceEvent : IEvent;

    {{extraTypes}}

    public class CoalesceModel {
      public string Id { get; set; } = "";
    {{modelBody}}
    }

    public class CoalescePerspective : IPerspectiveFor<CoalesceModel, CoalesceEvent> {
      public CoalesceModel Apply(CoalesceModel currentData, CoalesceEvent eventData) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _generatedCoalescerAsync(string modelBody, string extraTypes = "") {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(
      _coalescerFor(modelBody, extraTypes));
    return string.Join("\n", result.GeneratedSources.Select(g => g.SourceText.ToString()));
  }

  /// <summary>A settable, non-nullable collection is coalesced to empty.</summary>
  [Test]
  public async Task Coalescer_SettableNonNullableCollection_IsCoalescedAsync() {
    var generated = await _generatedCoalescerAsync("""
      public List<string> Tags { get; set; } = new();
    """);

    await Assert.That(generated).Contains("Tags ??=")
      .Because("an old-shape row materializes this as null, and every read of it then throws");
  }

  /// <summary>A nullable-annotated collection is left alone.</summary>
  [Test]
  public async Task Coalescer_NullableAnnotatedCollection_IsLeftAloneAsync() {
    // The annotation is the author saying null is a real state here — "no tags recorded" is
    // different from "tags recorded, and there were none". Coalescing would erase that.
    var generated = await _generatedCoalescerAsync("""
      public List<string>? Tags { get; set; }
    """);

    await Assert.That(generated).DoesNotContain("Tags ??=")
      .Because("null is a legitimate value on a nullable-annotated collection");
  }

  /// <summary>An init-only collection is not assigned post-construction.</summary>
  [Test]
  public async Task Coalescer_InitOnlyCollection_IsNotAssignedAsync() {
    // `??=` against an init-only setter is CS8852, so emitting it would break the consumer's
    // build in generated code they cannot edit.
    var generated = await _generatedCoalescerAsync("""
      public List<string> Tags { get; init; } = new();
    """);

    await Assert.That(generated).DoesNotContain("Tags ??=")
      .Because("assigning an init-only property post-construction is CS8852 — in generated code");
  }

  /// <summary>A get-only collection is not assigned either.</summary>
  [Test]
  public async Task Coalescer_GetOnlyCollection_IsNotAssignedAsync() {
    var generated = await _generatedCoalescerAsync("""
      public List<string> Tags { get; } = new();
    """);

    await Assert.That(generated).DoesNotContain("Tags ??=");
  }

  /// <summary>The walk descends through a complex reference to reach its collections.</summary>
  [Test]
  public async Task Coalescer_CollectionBehindAComplexReference_IsReachedAsync() {
    // Stopping at the top level would leave every nested collection unrepaired, which is the
    // common shape — a model with an owned address or settings object that holds a list.
    var generated = await _generatedCoalescerAsync("""
      public Settings Config { get; set; } = new();
    """, """
    public class Settings {
      public List<string> Flags { get; set; } = new();
    }
    """);

    await Assert.That(generated).Contains("Flags ??=");
  }

  /// <summary>The complex reference itself is null-guarded rather than constructed.</summary>
  [Test]
  public async Task Coalescer_ComplexReference_IsGuardedNotConstructedAsync() {
    // A null complex reference may be legitimate — only collections are repaired. Constructing
    // one would invent state the row never had.
    var generated = await _generatedCoalescerAsync("""
      public Settings Config { get; set; } = new();
    """, """
    public class Settings {
      public List<string> Flags { get; set; } = new();
    }
    """);

    await Assert.That(generated).Contains("Config is");
    await Assert.That(generated).DoesNotContain("Config ??=")
      .Because("only collections are coalesced; a null reference may be the row's real state");
  }

  /// <summary>The walk descends into a collection's complex element type.</summary>
  [Test]
  public async Task Coalescer_CollectionOfComplexElements_IsWalkedPerElementAsync() {
    var generated = await _generatedCoalescerAsync("""
      public List<Line> Lines { get; set; } = new();
    """, """
    public class Line {
      public List<string> Notes { get; set; } = new();
    }
    """);

    await Assert.That(generated).Contains("Lines ??=");
    await Assert.That(generated).Contains("foreach")
      .Because("each element carries its own collections, so the repair has to run per element");
    await Assert.That(generated).Contains("Notes ??=");
  }

  /// <summary>
  /// A collection that was itself left un-coalesced still has its elements walked, under a null
  /// guard.
  /// </summary>
  [Test]
  public async Task Coalescer_NullableCollectionOfComplexElements_IsWalkedUnderAGuardAsync() {
    // The collection stays possibly-null by the author's choice, so the foreach that repairs
    // its elements must be guarded or the repair itself throws.
    var generated = await _generatedCoalescerAsync("""
      public List<Line>? Lines { get; set; }
    """, """
    public class Line {
      public List<string> Notes { get; set; } = new();
    }
    """);

    await Assert.That(generated).DoesNotContain("Lines ??=");
    await Assert.That(generated).Contains("Notes ??=");
    await Assert.That(generated).Contains("is not null")
      .Because("walking a collection left possibly-null must be guarded, or the repair NREs");
  }

  /// <summary>A self-referencing model does not send the walker into infinite recursion.</summary>
  [Test]
  public async Task Coalescer_SelfReferencingModel_TerminatesAsync() {
    // A tree-shaped model is ordinary. Without the cycle guard the generator would recurse
    // until it died — and a generator crash takes the consumer's whole build with it.
    var generated = await _generatedCoalescerAsync("""
      public Node Root { get; set; } = new();
    """, """
    public class Node {
      public List<string> Labels { get; set; } = new();
      public Node Child { get; set; } = null!;
    }
    """);

    await Assert.That(generated).Contains("Labels ??=")
      .Because("the guard must stop the recursion without abandoning the work already found");
  }

  /// <summary>Mutually recursive models terminate too.</summary>
  [Test]
  public async Task Coalescer_MutuallyRecursiveModels_TerminateAsync() {
    var generated = await _generatedCoalescerAsync("""
      public Parent Top { get; set; } = new();
    """, """
    public class Parent {
      public List<string> ParentTags { get; set; } = new();
      public Child Kid { get; set; } = null!;
    }

    public class Child {
      public List<string> ChildTags { get; set; } = new();
      public Parent Owner { get; set; } = null!;
    }
    """);

    await Assert.That(generated).Contains("ParentTags ??=");
    await Assert.That(generated).Contains("ChildTags ??=");
  }

  /// <summary>A model with no collections emits no coalesce body at all.</summary>
  [Test]
  public async Task Coalescer_ModelWithNoCollections_EmitsNoDataBlockAsync() {
    // The registration is emitted for every model, so a model with nothing to repair must not
    // carry a dead `var data = row.Data;` block into generated output.
    var generated = await _generatedCoalescerAsync("""
      public int Count { get; set; }
    """);

    await Assert.That(generated).DoesNotContain("var data = row.Data;")
      .Because("nothing to coalesce means nothing to emit");
  }

  /// <summary>The scope extensions collection is coalesced for every model regardless.</summary>
  [Test]
  public async Task Coalescer_ScopeExtensions_AreAlwaysCoalescedAsync() {
    // Scope lives on the row rather than the model, so it needs the same repair on every
    // perspective — including the ones whose own model has no collections at all.
    var generated = await _generatedCoalescerAsync("""
      public int Count { get; set; }
    """);

    await Assert.That(generated).Contains("row.Scope.Extensions ??=");
  }

  /// <summary>Arrays are coalesced the same way lists are.</summary>
  [Test]
  public async Task Coalescer_ArrayProperty_IsCoalescedAsync() {
    var generated = await _generatedCoalescerAsync("""
      public string[] Codes { get; set; } = [];
    """);

    await Assert.That(generated).Contains("Codes ??=");
  }

  /// <summary>The generated coalescer compiles — it is emitted into the consumer's build.</summary>
  [Test]
  public async Task Coalescer_GeneratedCodeHasNoCompilationErrorsAsync() {
    // The whole risk of emitting statements from a symbol walk is producing code that does not
    // build, in a file the consumer cannot edit.
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(_coalescerFor("""
      public List<Line> Lines { get; set; } = new();
      public List<string>? Optional { get; set; }
      public Settings Config { get; set; } = new();
    """, """
    public class Line {
      public List<string> Notes { get; set; } = new();
    }

    public class Settings {
      public List<string> Flags { get; set; } = new();
    }
    """));

    await Assert.That(result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
  }

  #endregion
}
