using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="PerspectiveSchemaGenerator"/> targeting: the
/// marker-interface-only discovery guard, the fallback (non-<c>INamedTypeSymbol</c>) model-property
/// enumeration path, a class-level attribute list where <c>[PerspectiveStorage]</c> is not the first
/// attribute, two <c>[VectorField]</c> edge cases not covered by
/// <c>PerspectiveSchemaGeneratorTests.cs</c>'s existing vector-field suite, and two
/// <c>PerspectiveSchemaInfo</c> record properties that no generated output ever reads back.
/// </summary>
[Category("SourceGenerators")]
public class PerspectiveSchemaGeneratorCoverageTests {

  /// <summary>
  /// A perspective whose model carries one vector property, with the attribute's named arguments
  /// filled in by the caller.
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

  // ==================== Marker-interface-only discovery guard ====================

  /// <summary>
  /// <c>IPerspectiveBase&lt;TModel&gt;</c> is documented "do not implement directly" — it is the
  /// unified marker every real perspective interface extends, not a schema-worthy perspective on its
  /// own. A class implementing only the 1-arg marker (no event type at all) must be skipped, or the
  /// schema generator would try to build a table for a "perspective" that never applies any event.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ClassImplementingOnlyPerspectiveMarkerInterface_SkipsSchemaAsync() {
    const string source = """
      using Whizbang.Core.Perspectives;

      namespace MyApp.Perspectives;

      public class MarkerModel { }

      public class MarkerOnlyPerspective : IPerspectiveBase<MarkerModel> { }
      """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    var sql = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");

    await Assert.That(sql).IsNull()
      .Because("a class implementing only the marker-only IPerspectiveBase<TModel> handles no event and must not produce a schema");
  }

  // ==================== Fallback (non-INamedTypeSymbol) model-property enumeration ====================

  /// <summary>
  /// An open-generic perspective's model type argument is a type PARAMETER, not a named type — the
  /// generator falls back to enumerating members directly off the type-parameter symbol instead of the
  /// named-type property-walk helper. If that fallback were removed in favor of an unconditional cast,
  /// this perspective would throw inside the shared <c>RegisterSourceOutput</c> callback and take down
  /// schema generation for every OTHER perspective in the same compilation, not just this one.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GenericPerspectiveModelType_DoesNotCrashAndSiblingPerspectiveStillGeneratesAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace MyApp.Perspectives;

      public class GenericPerspective<TModel> : IPerspectiveFor<TModel, GenericIndexed> where TModel : class {
        public TModel Apply(TModel currentData, GenericIndexed @event) => currentData;
      }

      public record GenericIndexed : IEvent;

      public record OrderModel {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
      }

      public class OrderPerspective : IPerspectiveFor<OrderModel, GenericIndexed> {
        public OrderModel Apply(OrderModel currentData, GenericIndexed @event) => currentData;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error)
      .Because("an open-generic model's fallback property enumeration must degrade gracefully, not crash the generator");

    var discovered = result.Diagnostics.Single(d => d.Id == "WHIZ007");
    await Assert.That(discovered.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("OrderPerspective")
      .Because("a sibling perspective in the same compilation must still be discovered and scheduled even though an unrelated perspective's model type is an open generic parameter");
    await Assert.That(discovered.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("GenericPerspective")
      .Because("the open-generic perspective itself is still a valid perspective (it names a real event) and must still be processed via the fallback member-enumeration path");
  }

  // ==================== [PerspectiveStorage] not the first class-level attribute ====================

  /// <summary>
  /// The storage-mode scan walks every attribute on the model class looking for
  /// [PerspectiveStorage] — it must not stop or misfire on an unrelated attribute that happens to
  /// come first. If it did, every model that layers on some other class-level attribute (like
  /// [Obsolete]) ahead of [PerspectiveStorage] would silently fall back to JsonOnly, dropping the
  /// physical columns/indexes a consumer explicitly asked for — with no diagnostic pointing at why.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ModelWithUnrelatedAttributeBeforePerspectiveStorage_StillAppliesStorageModeAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace MyApp.Perspectives;

      [Obsolete("legacy")]
      [PerspectiveStorage(FieldStorageMode.Split)]
      public record OrderStorageModel {
        public Guid Id { get; set; }
        [PhysicalField(Indexed = true)]
        public string Sku { get; set; } = string.Empty;
      }

      public class OrderStoragePerspective : IPerspectiveFor<OrderStorageModel, OrderStorageIndexed> {
        public OrderStorageModel Apply(OrderStorageModel currentData, OrderStorageIndexed @event) => currentData;
      }

      public record OrderStorageIndexed : IEvent;
      """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    var physicalFieldsDiagnostic = result.Diagnostics.Single(d => d.Id == "WHIZ807");
    await Assert.That(physicalFieldsDiagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Split mode")
      .Because("[PerspectiveStorage(FieldStorageMode.Split)] must still be found and applied even though [Obsolete] precedes it in the model's attribute list");
  }

  // ==================== VectorField edge cases ====================

  /// <summary>
  /// Indexed defaults to true and is left untouched here — only IndexType is set to None. If the
  /// generator only ever checked Indexed (never VectorIndexType itself) before building the index
  /// statement, this exact combination would slip through to the index-SQL builder and could emit a
  /// CREATE INDEX with no method name, breaking the whole schema file for every perspective in the
  /// assembly.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_VectorFieldWithExplicitNoneIndexTypeButIndexedTrue_EmitsNoIndexStatementAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(
      _vectorPerspective("[VectorField(1536, IndexType = VectorIndexType.None)]"));

    var sql = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");

    await Assert.That(sql).IsNotNull();
    await Assert.That(sql!).Contains("vector(1536)")
      .Because("an explicit no-index request is not a request to drop the column");
    await Assert.That(sql!).DoesNotContain("_vec")
      .Because("VectorIndexType.None must produce no index statement even when Indexed itself is left at its true default");
  }

  /// <summary>
  /// C# lets any int be cast to an enum, so (VectorDistanceMetric)99 compiles and reaches the
  /// generator as a distance metric it has no case for. Unlike an unrecognized index TYPE (which
  /// safely emits no index at all), the ops-class switch always needs SOME operator class to build a
  /// well-formed "USING ivfflat (col vector_x_ops)" clause — falling back to cosine keeps the index
  /// statement valid SQL instead of one with a missing/blank operator class that fails the whole
  /// schema file to apply.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_VectorFieldWithDistanceMetricOutsideTheEnum_FallsBackToCosineOpsAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(
      _vectorPerspective("[VectorField(1536, DistanceMetric = (VectorDistanceMetric)99)]"));

    var sql = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");

    await Assert.That(sql).IsNotNull();
    await Assert.That(sql!).Contains("USING ivfflat")
      .Because("indexing is still on by default even though the distance metric could not be named");
    await Assert.That(sql!).Contains("vector_cosine_ops")
      .Because("an unrecognized distance metric must fall back to cosine ops rather than emit no operator class at all");
  }

  // ==================== PerspectiveSchemaInfo fields no generated output reads back ====================

  /// <summary>
  /// Neither FullyQualifiedClassName nor PropertyCount is read anywhere in today's SQL emission — only
  /// ClassName, ModelClassName, TableName, EstimatedSizeBytes, StorageMode, and PhysicalFields feed the
  /// generated output — so a getter that silently returned stale or default data would go unnoticed by
  /// every black-box generator test. A future feature keying off the model's fully-qualified name (e.g.
  /// cross-referencing a generated table back to its source type) or its raw property count (e.g. a
  /// size-estimation diagnostic) would silently read corrupted data with nothing to catch it.
  /// </summary>
  [Test]
  public async Task PerspectiveSchemaInfo_FullyQualifiedClassNameAndPropertyCount_RoundTripThroughTheRecordAsync() {
    var schemaInfoType = typeof(PerspectiveSchemaGenerator).Assembly.GetType("Whizbang.Generators.PerspectiveSchemaInfo")
      ?? throw new InvalidOperationException("Whizbang.Generators.PerspectiveSchemaInfo not found — check the type's namespace/name.");

    var instance = Activator.CreateInstance(
      schemaInfoType,
      "OrderSummary",
      "global::MyApp.Perspectives.OrderSummary",
      "OrderSummaryModel",
      "order_summary",
      3,
      140,
      GeneratorFieldStorageMode.JsonOnly,
      _emptyPhysicalFields(schemaInfoType))
      ?? throw new InvalidOperationException("Failed to construct PerspectiveSchemaInfo via reflection.");

    var fullyQualifiedClassName = (string)schemaInfoType.GetProperty("FullyQualifiedClassName")!.GetValue(instance)!;
    var propertyCount = (int)schemaInfoType.GetProperty("PropertyCount")!.GetValue(instance)!;

    await Assert.That(fullyQualifiedClassName).IsEqualTo("global::MyApp.Perspectives.OrderSummary");
    await Assert.That(propertyCount).IsEqualTo(3);
  }

  /// <summary>
  /// The empty physical-field collection for <c>PerspectiveSchemaInfo</c>'s last constructor
  /// parameter, built from the constructor's own parameter type rather than by naming
  /// <c>PhysicalFieldInfo</c> here.
  /// </summary>
  /// <remarks>
  /// <c>Whizbang.Generators.Shared</c> is referenced with <c>&lt;Aliases&gt;shared&lt;/Aliases&gt;</c> to keep
  /// its types from colliding with the ILRepack-merged copies inside <c>Whizbang.Generators</c>. A
  /// normal build resolves a bare <c>PhysicalFieldInfo</c> against the merged assembly, but
  /// <c>dotnet format</c> loads the pre-merge one through MSBuildWorkspace and fails with CS0246 —
  /// which is a CI formatting failure with no local build error to explain it. Since the whole call
  /// is reflective anyway, deriving the element type sidesteps the alias question entirely.
  /// </remarks>
  private static Array _emptyPhysicalFields(Type schemaInfoType) {
    var ctor = schemaInfoType.GetConstructors()
      .OrderByDescending(static c => c.GetParameters().Length)
      .First();
    var last = ctor.GetParameters()[^1].ParameterType;
    var element = last.IsArray ? last.GetElementType()! : last.GetGenericArguments()[0];
    return Array.CreateInstance(element, 0);
  }


  /// <summary>
  /// An ARRAY model type is an <c>IArrayTypeSymbol</c>, not an <c>INamedTypeSymbol</c>, so the
  /// named-type property walk cannot be used and the generator falls back to enumerating the
  /// symbol's own instance properties — of which an array has none.
  /// </summary>
  /// <remarks>
  /// The number that falls out of this is the estimated row size the CREATE TABLE comment carries,
  /// and it is what a regression here would corrupt: enumerating the ELEMENT type's properties
  /// instead would size the row as though one array cell were the whole row, and an unconditional
  /// cast to <c>INamedTypeSymbol</c> would throw inside the shared <c>RegisterSourceOutput</c>
  /// callback and take every other perspective's schema down with it. The sibling perspective on
  /// the same element type pins what the named-type walk produces, so the two numbers cannot both
  /// be satisfied by one code path.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ArrayModelType_SizesTheRowFromTheArrayNotItsElementTypeAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace MyApp.Perspectives;

      public record OrderRow {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; }
      }

      public record OrderIndexed : IEvent;

      public class ArrayModelPerspective : IPerspectiveFor<OrderRow[], OrderIndexed> {
        public OrderRow[] Apply(OrderRow[] currentData, OrderIndexed @event) => currentData;
      }

      public class RowModelPerspective : IPerspectiveFor<OrderRow, OrderIndexed> {
        public OrderRow Apply(OrderRow currentData, OrderIndexed @event) => currentData;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);

    await Assert.That(result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse()
      .Because("an array model must degrade to zero discovered properties, not crash schema generation");

    var sql = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    await Assert.That(sql).IsNotNull();

    // 20 bytes of JSON object overhead plus 40 per discovered property.
    await Assert.That(sql).Contains("-- Estimated size: ~20 bytes")
      .Because("an array symbol exposes no instance properties, so the array-modelled perspective is sized at the base overhead alone");
    await Assert.That(sql).Contains("-- Estimated size: ~140 bytes")
      .Because("the sibling perspective on the same element type must still be sized from its three properties, proving the zero above is the array path and not a broken walk");
  }
}
