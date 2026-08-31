using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The CLR-to-PostgreSQL column type mapping behind <c>[PhysicalField]</c>.
/// </summary>
/// <remarks>
/// A physical field is a property lifted out of the perspective's JSON body into a real column so
/// it can be indexed and filtered in SQL. The generator picks that column's type at compile time,
/// and the choice is written into a migration — so a wrong mapping is not a runtime error that
/// surfaces on the first query, it is a column of the wrong type that has to be migrated back out
/// once rows exist in it.
///
/// <para>
/// Two properties matter beyond the individual rows. Precision must not be silently lost — a long
/// in an INTEGER column truncates, and a DateTimeOffset in a TIMESTAMP drops the offset. And the
/// fallback must stay TEXT: an unrecognized type has to land somewhere that can hold its
/// serialized form rather than failing the migration.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/PerspectiveSchemaGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class PhysicalFieldTypeMappingTests {

  /// <summary>Runs the schema generator over a model carrying one physical field of the given type.</summary>
  private static string _schemaFor(string clrType, string attributeArgs = "") {
    var source = $$"""
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Extracted)]
            public record ProbeModel {
              [StreamId]
              public Guid ProbeId { get; init; }

              [PhysicalField({{attributeArgs}})]
              public {{clrType}} Value { get; init; }
            }

            public class ProbePerspective : IPerspectiveFor<ProbeModel, ProbeCreated> {
              public ProbeModel Apply(ProbeModel? current, ProbeCreated @event) {
                return new ProbeModel { ProbeId = @event.ProbeId };
              }
            }

            public record ProbeCreated([property: StreamId] Guid ProbeId) : IEvent;
            """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    return GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? string.Empty;
  }

  // ============================================================
  // The mapping table
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("int", "INTEGER")]
  [Arguments("long", "BIGINT")]
  [Arguments("short", "SMALLINT")]
  [Arguments("decimal", "DECIMAL")]
  [Arguments("double", "DOUBLE PRECISION")]
  [Arguments("float", "REAL")]
  [Arguments("bool", "BOOLEAN")]
  [Arguments("Guid", "UUID")]
  [Arguments("DateTime", "TIMESTAMP")]
  [Arguments("DateOnly", "DATE")]
  [Arguments("TimeOnly", "TIME")]
  public async Task PhysicalField_MapsTheClrTypeToItsColumnTypeAsync(string clrType, string expected) {
    var schema = _schemaFor(clrType);

    await Assert.That(schema).Contains(expected);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PhysicalField_DateTimeOffset_KeepsTheOffsetAsync() {
    // TIMESTAMPTZ rather than TIMESTAMP: mapping to the naive type would drop the offset on
    // write, and the loss is invisible until someone compares two rows written from different
    // regions.
    var schema = _schemaFor("DateTimeOffset");

    await Assert.That(schema).Contains("TIMESTAMPTZ");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PhysicalField_LongDoesNotMapToIntegerAsync() {
    // The distinction that actually costs something: a long in an INTEGER column truncates
    // silently at write time, and by the time anyone notices the rows are already wrong.
    var schema = _schemaFor("long");

    await Assert.That(schema).Contains("BIGINT");
    await Assert.That(schema).DoesNotContain("value INTEGER");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PhysicalField_String_DefaultsToUnboundedTextAsync() {
    // No declared length means no imposed limit — a VARCHAR default would reject values the
    // model happily holds.
    var schema = _schemaFor("string");

    await Assert.That(schema).Contains("TEXT");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PhysicalField_StringWithMaxLength_BecomesVarcharAsync() {
    var schema = _schemaFor("string", "MaxLength = 64");

    await Assert.That(schema).Contains("VARCHAR(64)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PhysicalField_UnknownType_FallsBackToTextAsync() {
    // The fallback has to hold: an unmapped type must land in a column that can hold its
    // serialized form rather than failing the whole migration over one property.
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            public enum Grade { Low, High }

            [PerspectiveStorage(FieldStorageMode.Extracted)]
            public record ProbeModel {
              [StreamId]
              public Guid ProbeId { get; init; }

              [PhysicalField]
              public Grade Value { get; init; }
            }

            public class ProbePerspective : IPerspectiveFor<ProbeModel, ProbeCreated> {
              public ProbeModel Apply(ProbeModel? current, ProbeCreated @event)
                => new ProbeModel { ProbeId = @event.ProbeId };
            }

            public record ProbeCreated([property: StreamId] Guid ProbeId) : IEvent;
            """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    var schema = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? string.Empty;

    await Assert.That(schema).Contains("TEXT");
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PhysicalField_NullableType_MapsToTheSameColumnTypeAsync() {
    // The nullable marker is stripped before the lookup, so `int?` and `int` share a column
    // type — nullability is a constraint on the column, not a different type.
    var nullable = _schemaFor("int?");
    var plain = _schemaFor("int");

    await Assert.That(nullable).Contains("INTEGER");
    await Assert.That(plain).Contains("INTEGER");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PhysicalField_FloatArrayWithoutVectorField_FallsBackToRealArrayAsync() {
    // A float[] that nobody marked as a vector is still an array of reals — mapping it to TEXT
    // would make it unqueryable, and mapping it to vector() would invent a dimension.
    var schema = _schemaFor("float[]");

    await Assert.That(schema).Contains("REAL[]");
  }

  // ============================================================
  // Vectors
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task VectorField_ColumnCarriesItsDeclaredDimensionAsync() {
    // pgvector fixes the dimension in the column type, so it has to come from the attribute —
    // a mismatch is rejected on every insert.
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Split)]
            public record DocModel {
              [StreamId]
              public Guid DocId { get; init; }

              [VectorField(768)]
              public float[]? Embedding { get; init; }
            }

            public class DocPerspective : IPerspectiveFor<DocModel, DocCreated> {
              public DocModel Apply(DocModel? current, DocCreated @event)
                => new DocModel { DocId = @event.DocId };
            }

            public record DocCreated([property: StreamId] Guid DocId) : IEvent;
            """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    var schema = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? string.Empty;

    await Assert.That(schema).Contains("vector(768)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task VectorField_IndexesByDefaultAsync() {
    // Indexing is opt-out rather than opt-in, and IVFFlat is the default method. That is the
    // right way round: a vector column exists to be searched, and an unindexed one degrades to
    // a sequential scan over every row — which looks like a working query until the table grows.
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Split)]
            public record DocModel {
              [StreamId]
              public Guid DocId { get; init; }

              [VectorField(128)]
              public float[]? Embedding { get; init; }
            }

            public class DocPerspective : IPerspectiveFor<DocModel, DocCreated> {
              public DocModel Apply(DocModel? current, DocCreated @event)
                => new DocModel { DocId = @event.DocId };
            }

            public record DocCreated([property: StreamId] Guid DocId) : IEvent;
            """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    var schema = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? string.Empty;

    await Assert.That(schema).Contains("vector(128)");
    await Assert.That(schema).Contains("ivfflat")
      .Because("a vector column exists to be searched, so the index comes by default");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task VectorField_DefaultDistanceMetric_IsCosineAsync() {
    // Cosine is the metric normalized embeddings are trained for, so it is the right default —
    // and an index built for the wrong metric returns wrong neighbors rather than failing.
    var source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace MyApp.Perspectives;

            [PerspectiveStorage(FieldStorageMode.Split)]
            public record DocModel {
              [StreamId]
              public Guid DocId { get; init; }

              [VectorField(384, IndexType = VectorIndexType.HNSW)]
              public float[]? Embedding { get; init; }
            }

            public class DocPerspective : IPerspectiveFor<DocModel, DocCreated> {
              public DocModel Apply(DocModel? current, DocCreated @event)
                => new DocModel { DocId = @event.DocId };
            }

            public record DocCreated([property: StreamId] Guid DocId) : IEvent;
            """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    var schema = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? string.Empty;

    await Assert.That(schema).Contains("vector_cosine_ops");
  }
}
