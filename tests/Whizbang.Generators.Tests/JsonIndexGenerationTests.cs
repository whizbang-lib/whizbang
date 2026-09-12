using TUnit.Assertions.Extensions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That a declared index on a JSON-only field reaches the schema and the runtime.
/// </summary>
/// <remarks>
/// <para>
/// The attribute is inert on its own. Two things have to be emitted for it to mean anything: the
/// index itself, in the schema the service creates at startup, and a registration telling the query
/// translation that the field carries one, so an equality filter keeps the extraction form instead of
/// being compiled into a containment test that sends the planner to the document index.
/// </para>
/// <para>
/// Both failures are silent. Without the index the field simply scans, as it did before anything was
/// declared. Without the registration the index exists and is never used, which is worse, because the
/// cost has been paid.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public class JsonIndexGenerationTests {
  // A perspective is discovered through IPerspectiveFor, and the schema is emitted for a DbContext
  // that opts in, so both are needed for the generator to produce anything at all.
  private const string MODEL = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record OrderPlaced : IEvent;

    public record OrderModel {
      [StreamId]
      public Guid OrderId { get; init; }

      [Indexed]
      public int Rank { get; init; }

      [Indexed(IndexKinds.Btree | IndexKinds.Trigram)]
      public string Title { get; init; } = string.Empty;

      public string Plain { get; init; } = string.Empty;
    }

    public class OrderPerspective : IPerspectiveFor<OrderModel, OrderPlaced> {
      public OrderModel Apply(OrderModel currentData, OrderPlaced eventData) => currentData;
    }

    [WhizbangDbContext]
    public class OrderDbContext : DbContext {
      public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }
    }
    """;

  /// <summary>The btree over the extraction is created, with the cast the query produces.</summary>
  [Test]
  public async Task ABtreeIndexIsCreatedOverTheExtractionAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(output).Contains("((data ->> 'Rank')::integer)", StringComparison.Ordinal)
      .Because("an int is extracted as integer by the query, so the index has to be built on that "
        + "cast and not on a wider one that would hold the value equally well");
  }

  /// <summary>A text field is indexed without a cast, because the query compares it without one.</summary>
  [Test]
  public async Task ATextFieldIsIndexedWithoutACastAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(output).Contains("(data ->> 'Title')", StringComparison.Ordinal);
    await Assert.That(output).DoesNotContain("(data ->> 'Title')::", StringComparison.Ordinal)
      .Because("the extraction is already text and the comparison adds no cast, so neither may this");
  }

  /// <summary>A trigram declaration creates a trigram index, which is a different method entirely.</summary>
  [Test]
  public async Task ATrigramIndexUsesGinWithTheTrigramOperatorClassAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(output).Contains("gin_trgm_ops", StringComparison.Ordinal)
      .Because("substring matching is answered by a trigram index and by nothing else here");
  }

  /// <summary>An undeclared field gets no index of its own.</summary>
  [Test]
  public async Task AnUndeclaredFieldIsNotIndexedAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(output).DoesNotContain("'Plain'", StringComparison.Ordinal)
      .Because("every index is write amplification, so an undeclared field pays for nothing");
  }

  /// <summary>
  /// The runtime is told, so the containment rewrite stands down and the index is actually used.
  /// </summary>
  [Test]
  public async Task TheRuntimeRegistrationIsEmittedAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(output).Contains("JsonIndexRegistry.Register", StringComparison.Ordinal)
      .Because("without this the index is created and never used, which is worse than not creating it");
    await Assert.That(output).Contains("\"Rank\"", StringComparison.Ordinal);
  }

  /// <summary>
  /// A model asking for every field gets every eligible one, and the date is now among them.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The date is the interesting half, and it changed sides. Stored as a rendering it could not
  /// carry an index at all: the cast from text to a timestamp is stable, and PostgreSQL refuses to
  /// build an index over a stable expression, so a blanket declaration had to skip the field rather
  /// than emit a statement that failed at startup.
  /// </para>
  /// <para>
  /// Stored as a number it casts through bigint, which is immutable, so the same extraction is
  /// indexable and a blanket declaration covers it like anything else. Nothing about the index rules
  /// moved; the stored form did.
  /// </para>
  /// </remarks>
  [Test]
  public async Task IndexAllFieldsCoversTheEligibleFieldsOnlyAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync("""
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record Reported : IEvent;

      [IndexAllFields]
      public record ReportModel {
        [StreamId]
        public Guid ReportId { get; init; }

        public int Count { get; init; }
        public string Label { get; init; } = string.Empty;
        public DateTime OccurredAt { get; init; }
      }

      public class ReportPerspective : IPerspectiveFor<ReportModel, Reported> {
        public ReportModel Apply(ReportModel currentData, Reported eventData) => currentData;
      }

      [WhizbangDbContext]
      public class ReportDbContext : DbContext {
        public ReportDbContext(DbContextOptions<ReportDbContext> options) : base(options) { }
      }
      """);

    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(output).Contains("((data ->> 'Count')::integer)", StringComparison.Ordinal);
    await Assert.That(output).Contains("(data ->> 'Label')", StringComparison.Ordinal);
    await Assert.That(output).Contains("((data ->> 'OccurredAt')::bigint)", StringComparison.Ordinal)
      .Because("a date is stored as microseconds since the epoch now, and a bigint cast is "
        + "immutable, so the extraction PostgreSQL once refused is the one it indexes");
  }

  /// <summary>
  /// A model whose document has to be stored opaquely gets no index over that document, because no
  /// query against it would ever reach one.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A model holding an abstract member cannot be mapped property by property, so it is stored as a
  /// single serialized value. A filter on a field inside that value never compiles to the extraction
  /// an index would be built over, which makes the index pure cost: rebuilt on every write, scanned
  /// by nothing.
  /// </para>
  /// <para>
  /// Skipping it is only safe because WHIZ304 says so out loud at build time. Silence here would
  /// leave the author believing the field is indexed, which is the failure this whole family of
  /// checks exists to prevent.
  /// </para>
  /// </remarks>
  [Test]
  public async Task AnOpaquelyStoredModelGetsNoIndexOverItsDocumentAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync("""
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record Reported : IEvent;

      public abstract class PaymentMethod {
        public string Name { get; init; } = string.Empty;
      }

      public record ReportModel {
        [StreamId]
        public Guid ReportId { get; init; }

        public PaymentMethod? Payment { get; init; }

        [Indexed]
        public int Count { get; init; }
      }

      public class ReportPerspective : IPerspectiveFor<ReportModel, Reported> {
        public ReportModel Apply(ReportModel currentData, Reported eventData) => currentData;
      }

      [WhizbangDbContext]
      public class ReportDbContext : DbContext {
        public ReportDbContext(DbContextOptions<ReportDbContext> options) : base(options) { }
      }
      """);

    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(output).DoesNotContain("'Count'", StringComparison.Ordinal)
      .Because("the document is stored as one serialized value, so an index over an extraction from "
        + "it would be maintained on every write and could never be scanned");
  }

  /// <summary>
  /// An ordinary record still gets its index, which is the case the skip must not swallow.
  /// </summary>
  /// <remarks>
  /// A record was classified as opaquely stored on every occasion until the detection was narrowed
  /// to public properties, because the compiler generates a protected <c>EqualityContract</c> of the
  /// abstract type <c>System.Type</c>. A skip added on top of that behavior would have silently
  /// dropped the index for nearly every model. This is the test that would have caught it.
  /// </remarks>
  [Test]
  public async Task AnOrdinaryRecordStillGetsItsIndexAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(output).Contains("((data ->> 'Rank')::integer)", StringComparison.Ordinal)
      .Because("a record of an int and a string is mapped property by property like any other "
        + "model, so its extraction is exactly what a query produces");
  }

  /// <summary>
  /// A field can opt out of a blanket declaration, which is the only way to say "all but this one".
  /// </summary>
  /// <remarks>
  /// <para>
  /// A per-field declaration overrides the model's, so asking for no kind at all means no index. That
  /// is what makes <c>[IndexAllFields]</c> usable on a real model: without it the blanket is
  /// all-or-nothing, and a model with one field nobody filters has to give up the blanket entirely
  /// or carry write amplification for a column that is never read.
  /// </para>
  /// <para>
  /// This was previously covered only by a test asserting the attribute returns the kind it was
  /// given, which proves the attribute stores a value and nothing about whether opting out works.
  /// </para>
  /// </remarks>
  [Test]
  public async Task AFieldCanOptOutOfABlanketDeclarationAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync("""
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record Reported : IEvent;

      [IndexAllFields]
      public record ReportModel {
        [StreamId]
        public Guid ReportId { get; init; }

        public int Counted { get; init; }

        [Indexed(IndexKinds.None)]
        public int NeverFiltered { get; init; }
      }

      public class ReportPerspective : IPerspectiveFor<ReportModel, Reported> {
        public ReportModel Apply(ReportModel currentData, Reported eventData) => currentData;
      }

      [WhizbangDbContext]
      public class ReportDbContext : DbContext {
        public ReportDbContext(DbContextOptions<ReportDbContext> options) : base(options) { }
      }
      """);

    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(output).Contains("((data ->> 'Counted')::integer)", StringComparison.Ordinal)
      .Because("the blanket still covers everything it was not told to skip");
    await Assert.That(output).DoesNotContain("'NeverFiltered'", StringComparison.Ordinal)
      .Because("a per-field declaration overrides the model's, so asking for no kind is how a field "
        + "declines an index it would otherwise be given");
  }

  // ========================================
  // [Indexed] is universal
  // ========================================

  /// <summary>
  /// A promoted field asks for its index the same way a document field does.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The attribute names the author's intent, which is the same in both places: this field is
  /// filtered, make it fast. Where the index goes is the framework's knowledge, not theirs, and it
  /// follows from whether the field was promoted. <c>[PhysicalField]</c> says promote, and
  /// <c>[Indexed]</c> says index, so the two together say promote and index.
  /// </para>
  /// <para>
  /// Before this, an author had to know their model was stored as a document to pick the right
  /// attribute, and pick a different one for a promoted field. Two names for one intent, where the
  /// difference between them was a storage detail the framework already knows.
  /// </para>
  /// </remarks>
  [Test]
  public async Task APromotedFieldIsIndexedByTheSameAttributeAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync("""
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record Reported : IEvent;

      public record ReportModel {
        [StreamId]
        public Guid ReportId { get; init; }

        [PhysicalField]
        [Indexed]
        public Guid TenantId { get; init; }

        [PhysicalField]
        public string NotFiltered { get; init; } = string.Empty;
      }

      public class ReportPerspective : IPerspectiveFor<ReportModel, Reported> {
        public ReportModel Apply(ReportModel currentData, Reported eventData) => currentData;
      }

      [WhizbangDbContext]
      public class ReportDbContext : DbContext {
        public ReportDbContext(DbContextOptions<ReportDbContext> options) : base(options) { }
      }
      """);

    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(output).Contains("(tenant_id)", StringComparison.Ordinal)
      .Because("the field was promoted to a column and asked to be indexed, so the index belongs on "
        + "the column rather than over an extraction from a document it is no longer in");
    await Assert.That(output).DoesNotContain("data ->> 'TenantId'", StringComparison.Ordinal)
      .Because("a promoted field is not in the document, so an expression index over it would be "
        + "built on a key that is never there");
  }

  /// <summary>A promoted field that asks for nothing gets no index of its own.</summary>
  [Test]
  public async Task APromotedFieldWithoutTheAttributeIsNotIndexedAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync("""
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record Reported : IEvent;

      public record ReportModel {
        [StreamId]
        public Guid ReportId { get; init; }

        [PhysicalField]
        public string NotFiltered { get; init; } = string.Empty;
      }

      public class ReportPerspective : IPerspectiveFor<ReportModel, Reported> {
        public ReportModel Apply(ReportModel currentData, Reported eventData) => currentData;
      }

      [WhizbangDbContext]
      public class ReportDbContext : DbContext {
        public ReportDbContext(DbContextOptions<ReportDbContext> options) : base(options) { }
      }
      """);

    var output = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));

    await Assert.That(output).DoesNotContain("(not_filtered)", StringComparison.Ordinal)
      .Because("every index is write amplification, so a promoted column nobody filters pays for "
        + "nothing");
  }
}
