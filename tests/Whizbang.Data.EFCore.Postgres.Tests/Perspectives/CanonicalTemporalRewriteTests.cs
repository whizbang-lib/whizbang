// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Which paths the stored-form rewrite converts, and the statement it runs for a table.
/// </summary>
/// <remarks>
/// <para>
/// The rewrite used to be generated from a discovery over a model type's own members, which
/// missed a member inherited from a base class, a nested object, an element of a collection and
/// the framework's metadata. The paths are now derived at startup from the two things that
/// actually read a document: Entity Framework's model for a mapped document, and the serializer's
/// metadata for a document stored as one value. What a reader reads is what the rewrite converts,
/// by construction rather than by two discoveries staying in step.
/// </para>
/// <para>
/// No database: building a model needs a provider, not a connection, and the statement is text.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/CanonicalTemporalRewrite.cs</code-under-test>
[Category("Shard4")]
public class CanonicalTemporalRewriteTests {
  /// <summary>A temporal declared on a base class.</summary>
  public abstract class Audited {
    public DateTimeOffset RecordedAt { get; set; }
  }

  /// <summary>A nested object holding two temporal kinds.</summary>
  public sealed class Window {
    public TimeOnly Opens { get; set; }
    public TimeSpan Length { get; set; }
  }

  /// <summary>A complex-collection element holding two more, one optional.</summary>
  public sealed class Occurrence {
    public Guid OccurrenceId { get; set; }
    public DateOnly Day { get; set; }
    public DateTime? MaybeAt { get; set; }
  }

  /// <summary>One of every placement, mapped property by property.</summary>
  public sealed class MappedModel : Audited {
    [StreamId]
    public Guid Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Label { get; set; } = string.Empty;
    public Window Window { get; set; } = new();
    public List<Occurrence> Occurrences { get; set; } = [];

    // A primitive temporal collection: the element is the temporal, with no object to recurse into.
    // The serializer's walk reaches these through its element type; the mapped walk did not, so a
    // document Entity Framework maps kept its renderings while an opaque one was converted.
    public List<DateTimeOffset> AccessedAt { get; set; } = [];
    public List<string> Labels { get; set; } = [];

    // Stored under a different key than it is named. The path has to use the stored key: a rewrite
    // addressed by the member name would convert a path the document does not have, and report success.
    //
    // Renamed on BOTH sides on purpose. The mapping's HasJsonPropertyName is what Entity Framework
    // writes, and [JsonPropertyName] is what the serializer writes; renaming only one of them makes the
    // same model store the member under two different keys depending on how its table happens to be
    // mapped, which the agreement test below catches as a divergence — correctly, because it is one.
    [JsonPropertyName("renamed_at")]
    public DateTime RenamedAt { get; set; }
  }

  /// <summary>A model with nothing temporal, so no rewrite is named for it.</summary>
  public sealed class PlainModel {
    [StreamId]
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
  }

  private static RewriteContext _context() {
    OpaqueDocumentFixture.EnsureRegistered();
    OpaqueNodeFixture.EnsureRegistered();
    return new RewriteContext(new DbContextOptionsBuilder<RewriteContext>()
      .UseNpgsql("Host=localhost;Database=probe;Username=u;Password=p", npgsql => npgsql.UseWhizbangFunctions())
      .Options);
  }

  private static string _render(TemporalPath path) =>
    $"{path.Column}:{string.Join("/", path.Segments)}:{path.Kind}";

  /// <summary>The rendered paths of the document column alone, sorted.</summary>
  private static string _dataPaths(ImmutableArray<TemporalPath> paths) =>
    string.Join("\n", paths.Where(p => p.Column == "data").Select(_render).OrderBy(s => s, StringComparer.Ordinal));

  private static OpaqueMappedContext _opaqueMappedContext() {
    JsonContextRegistry.RegisterContext(MappedModelJsonContext.Default);
    return new OpaqueMappedContext(new DbContextOptionsBuilder<OpaqueMappedContext>()
      .UseNpgsql("Host=localhost;Database=probe;Username=u;Password=p", npgsql => npgsql.UseWhizbangFunctions())
      .Options);
  }

  private static string _rendered(ImmutableArray<TemporalPath> paths) =>
    string.Join("\n", paths.Select(_render).OrderBy(s => s, StringComparer.Ordinal));

  /// <summary>Every placement the model maps is a path, and nothing that is not temporal is.</summary>
  [Test]
  public async Task AMappedDocumentYieldsEveryPlacementTheModelMapsAsync() {
    await using var context = _context();
    var row = context.Model.FindEntityType(typeof(PerspectiveRow<MappedModel>))!;

    var paths = CanonicalTemporalRewrite.PathsOf(row, PerspectiveDocumentSerialization.Options);

    await Assert.That(_rendered(paths)).IsEqualTo(
      "data:AccessedAt/[]:OffsetInstant\n"
      + "data:OccurredAt:Instant\n"
      + "data:Occurrences/[]/Day:Day\n"
      + "data:Occurrences/[]/MaybeAt:Instant\n"
      + "data:RecordedAt:OffsetInstant\n"
      + "data:Window/Length:Duration\n"
      + "data:Window/Opens:TimeOfDay\n"
      + "data:renamed_at:Instant\n"
      + "metadata:Timestamp:Instant")
      .Because("the rewrite converts exactly what the mapping reads: inherited, nested, "
        + "collection-element and primitive-collection-element temporals and the framework's own "
        + "metadata included. Labels is a collection of strings and is not a path, because "
        + "unwrapping element types must not turn into claiming every primitive collection");
  }

  /// <summary>
  /// Every property Entity Framework maps into a document has a stored name.
  /// </summary>
  /// <remarks>
  /// The rewrite addresses a path by its stored key, so the key has to be knowable. Entity Framework
  /// types <c>GetJsonPropertyName()</c> as nullable, and the walk relies on it not being null for a
  /// property inside a document. That reliance is held here rather than defended at the call site: a
  /// fallback to the member name would be unreachable, and if it ever were reached it would address a key
  /// the document does not have — which converts nothing and reports success, the failure mode this whole
  /// area keeps producing.
  /// </remarks>
  [Test]
  public async Task APropertyInADocumentAlwaysHasAStoredNameAsync() {
    await using var context = _context();

    var unnamed = new List<string>();
    foreach (var complex in context.Model.FindEntityType(typeof(PerspectiveRow<MappedModel>))!.GetComplexProperties()) {
      _collectUnnamed(complex, complex.Name, unnamed);
    }

    await Assert.That(unnamed).IsEmpty()
      .Because("the walk reads the stored name without a fallback, so a property without one would "
        + "address the wrong key or throw, and both are worse than this test failing");
  }

  private static void _collectUnnamed(IComplexProperty complex, string path, List<string> found) {
    foreach (var property in complex.ComplexType.GetProperties().Where(p => p.GetJsonPropertyName() is null)) {
      found.Add($"{path}.{property.Name}");
    }
    foreach (var nested in complex.ComplexType.GetComplexProperties()) {
      // A nested object is a key in the document too, and the walk reads its stored name the same way.
      if (nested.GetJsonPropertyName() is null) {
        found.Add($"{path}.{nested.Name}");
      }
      _collectUnnamed(nested, $"{path}.{nested.Name}", found);
    }
  }

  /// <summary>
  /// The two walks name the same paths for the same model, whichever way its document is stored.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the test that was missing. The rewrite has two discoveries — Entity Framework's model for a
  /// mapped document, and the serializer's metadata for one stored as a value — and the type's own remarks
  /// say they agree "by construction rather than by two discoveries staying in step". Nothing checked that.
  /// They had diverged: the serializer's walk reached a primitive temporal collection through its element
  /// type and the mapped walk did not, so the same model was rewritten one way and not the other.
  /// </para>
  /// <para>
  /// Comparing them directly is what makes a future divergence a build failure rather than a silent
  /// difference in stored form. It fails in either direction, so it also catches a placement the
  /// serializer's walk misses and the mapped one finds.
  /// </para>
  /// <para>
  /// Only the document column is compared. How a context maps the framework's own metadata and scope is a
  /// property of the mapping rather than of the model, and the two contexts here spell those differently
  /// on purpose, to be the two shapes a consumer actually uses.
  /// </para>
  /// </remarks>
  [Test]
  public async Task TheTwoWalksNameTheSamePathsForOneModelAsync() {
    await using var mappedContext = _context();
    await using var opaqueContext = _opaqueMappedContext();

    var mapped = _dataPaths(CanonicalTemporalRewrite.PathsOf(
      mappedContext.Model.FindEntityType(typeof(PerspectiveRow<MappedModel>))!,
      PerspectiveDocumentSerialization.Options));
    var opaque = _dataPaths(CanonicalTemporalRewrite.PathsOf(
      opaqueContext.Model.FindEntityType(typeof(PerspectiveRow<MappedModel>))!,
      PerspectiveDocumentSerialization.Options));

    await Assert.That(mapped).IsEqualTo(opaque)
      .Because("one model has one set of temporals, so the rewrite has to name the same paths whether "
        + "Entity Framework maps the document or the serializer writes it whole. A path only one walk "
        + "finds is a stored form that differs by how the table happens to be mapped, which nothing "
        + "downstream expects and nothing reports");
  }

  /// <summary>
  /// A document stored as one value yields its paths from the serializer's metadata, since the
  /// serializer is the only thing that reads it.
  /// </summary>
  [Test]
  public async Task AnOpaqueDocumentYieldsEveryPlacementTheSerializerReadsAsync() {
    await using var context = _context();
    var row = context.Model.FindEntityType(typeof(PerspectiveRow<OpaqueDocument>))!;

    var paths = CanonicalTemporalRewrite.PathsOf(row, PerspectiveDocumentSerialization.Options);

    await Assert.That(_rendered(paths)).IsEqualTo(
      "data:EndedAt:OffsetInstant\n"
      + "data:Scheduled:Day\n"
      + "data:StartedAt:Instant\n"
      + "data:Turns/[]/At:Instant\n"
      + "metadata:Timestamp:Instant")
      .Because("a positional record's constructor parameter inside a collection is a placement the "
        + "serializer reads, so it is one the rewrite converts");
  }

  /// <summary>
  /// Options with no resolver describe no type, so an opaque document read with them has no
  /// placements to name: the walk asks the resolver, never the options, and finds nothing.
  /// </summary>
  [Test]
  public async Task OptionsWithoutAResolverNameNoOpaquePlacementAsync() {
    await using var context = _context();
    var row = context.Model.FindEntityType(typeof(PerspectiveRow<OpaqueDocument>))!;
    var bare = new System.Text.Json.JsonSerializerOptions();

    var paths = CanonicalTemporalRewrite.PathsOf(row, bare);

    await Assert.That(bare.TypeInfoResolver).IsNull().Because("the setup is options with no resolver");
    await Assert.That(_rendered(paths)).DoesNotContain("data:")
      .Because("nothing describes the document's type, so the serializer would not read it either");
  }

  /// <summary>
  /// A member that refers to its own type is walked once; the rewrite names the placements it can
  /// reach without following the cycle.
  /// </summary>
  /// <remarks>
  /// A document may nest itself, a node with a next node. Following the reference forever would
  /// never finish, so the walk visits a type once per path and stops where it would revisit one.
  /// The placements past that point are left to the readers, which take a rendering and count it.
  /// </remarks>
  [Test]
  public async Task ASelfReferencingOpaqueMemberIsWalkedOnceAsync() {
    await using var context = _context();
    var row = context.Model.FindEntityType(typeof(PerspectiveRow<OpaqueNode>))!;

    var paths = CanonicalTemporalRewrite.PathsOf(row, PerspectiveDocumentSerialization.Options);

    await Assert.That(_rendered(paths)).IsEqualTo(
      "data:At:Instant\n"
      + "metadata:Timestamp:Instant")
      .Because("the walk stops where it would revisit the node's own type, and finishes");
  }

  /// <summary>A model with no temporal yields nothing, and no statement is named for its table.</summary>
  [Test]
  public async Task AModelWithNothingTemporalIsNotNamedAsync() {
    await using var context = _context();

    var plain = CanonicalTemporalRewrite.PathsOf(
      context.Model.FindEntityType(typeof(PerspectiveRow<PlainModel>))!, PerspectiveDocumentSerialization.Options);
    var rewrites = CanonicalTemporalRewrite.ForModel(context.Model, PerspectiveDocumentSerialization.Options, "svc");

    await Assert.That(plain.Length).IsEqualTo(1)
      .Because("even a plain model carries the framework's metadata timestamp");
    await Assert.That(string.Join("\n", rewrites.Select(r => r.Name)))
      .IsEqualTo("wh_per_mapped\nwh_per_opaque\nwh_per_opaque_node\nwh_per_plain")
      .Because("every table holds at least the metadata timestamp, and the entries come in table order");
  }

  /// <summary>
  /// The statement for a table is one transaction: guarded, ledger-gated, one update per path,
  /// and the ledger written last in the same block.
  /// </summary>
  /// <remarks>
  /// A DO block is one implicit transaction. Interrupted anywhere inside it, nothing is recorded and
  /// the whole table runs again; the ledger cannot say "converted" about a table that is not.
  /// </remarks>
  [Test]
  public async Task TheStatementIsOneLedgerGatedTransactionAsync() {
    var paths = ImmutableArray.Create(
      new TemporalPath("data", ["Turns", "[]", "At"], StoredTemporalKind.Instant),
      new TemporalPath("data", ["Day"], StoredTemporalKind.Day),
      new TemporalPath("metadata", ["Timestamp"], StoredTemporalKind.Instant));

    var sql = CanonicalTemporalRewrite.StatementFor("svc", "wh_per_thing", paths);

    await Assert.That(sql).Contains("DO $wb$");
    await Assert.That(sql).Contains("IF to_regclass('\"svc\".\"wh_per_thing\"') IS NULL")
      .Because("at this point in startup the table frequently does not exist yet");
    await Assert.That(sql).Contains("FROM \"svc\".wh_perspective_forms WHERE table_name = 'wh_per_thing'");
    await Assert.That(sql).Contains("IF v_settled IS NOT NULL THEN")
      .Because("a table a pass has already found clean is skipped without a scan");
    await Assert.That(sql).Contains(
      "SET \"data\" = \"svc\".wh_canonicalize_temporal(\"data\", ARRAY['Turns','[]','At'], 0::smallint, v_form)");
    await Assert.That(sql).Contains("jsonb_path_exists(\"data\", '$.\"Turns\"[*].\"At\" ? (@.type() == \"string\")')")
      .Because("only a row holding a rendering at the path is touched, which is what keeps a "
        + "converted table from being rewritten on every start");
    await Assert.That(sql).Contains("v_form < 2 AND jsonb_path_exists(\"data\", '$.\"Day\" ? (@.type() == \"number\")')")
      .Because("a number is touched only in the mixed-unit form, and only for a kind whose unit changed");
    await Assert.That(sql).DoesNotContain("v_form < 2 AND jsonb_path_exists(\"data\", '$.\"Turns\"[*].\"At\" ? (@.type() == \"number\")')")
      .Because("an instant was microseconds in every form, so its numbers are never touched");
    await Assert.That(sql).Contains("SET \"metadata\" = \"svc\".wh_canonicalize_temporal(\"metadata\", ARRAY['Timestamp'], 0::smallint, v_form)");
    await Assert.That(sql).Contains("INSERT INTO \"svc\".wh_perspective_forms");
    await Assert.That(sql).Contains("ON CONFLICT (table_name) DO UPDATE");
    await Assert.That(sql.IndexOf("INSERT INTO", StringComparison.Ordinal))
      .IsGreaterThan(sql.LastIndexOf("wh_canonicalize_temporal(", StringComparison.Ordinal))
      .Because("the ledger is written after every update in the same transaction");
  }

  /// <summary>
  /// The statement says what became of the table on every exit, so a startup log shows what a pass
  /// converted, skipped, or could not find.
  /// </summary>
  /// <remarks>
  /// Only the statement knows how many rows it touched and why it stopped early. A pass that was
  /// silent on success once left a fleet unable to tell "converted" from "never ran".
  /// </remarks>
  [Test]
  public async Task TheStatementReportsEveryOutcomeAsANoticeAsync() {
    var paths = ImmutableArray.Create(
      new TemporalPath("metadata", ["Timestamp"], StoredTemporalKind.Instant));

    var sql = CanonicalTemporalRewrite.StatementFor("svc", "wh_per_thing", paths);

    await Assert.That(sql).Contains(
      "RAISE NOTICE USING MESSAGE = format('%s: table absent, nothing to convert', 'wh_per_thing');")
      .Because("a table the model names but the database lacks is worth a line, not silence");
    await Assert.That(sql).Contains(
      "RAISE NOTICE USING MESSAGE = format('%s: settled, skipped', 'wh_per_thing');")
      .Because("a settled table is skipped without a scan, and the log says so");
    await Assert.That(sql).Contains(
      "RAISE NOTICE USING MESSAGE = format('%s: converted, %s row update(s)', 'wh_per_thing', v_touched);")
      .Because("the count is the evidence an operator reads; one update per row and path");
    await Assert.That(sql).Contains(
      "  IF v_touched > 0 THEN\n    PERFORM set_config('whizbang.rewritten_tables', ")
      .Because("a table the statement changed is marked, so the phase analyzes it after the commit (#1004)");
    await Assert.That(sql.IndexOf("row update(s)", StringComparison.Ordinal))
      .IsGreaterThan(sql.IndexOf("ON CONFLICT (table_name) DO UPDATE", StringComparison.Ordinal))
      .Because("the count is reported once the ledger row that records it is written");
  }

  /// <summary>A quote in a name is escaped rather than trusted.</summary>
  [Test]
  public async Task NamesAreQuotedAndEscapedAsync() {
    var paths = ImmutableArray.Create(
      new TemporalPath("data", ["It's"], StoredTemporalKind.Instant));

    var sql = CanonicalTemporalRewrite.StatementFor("svc", "wh_per_thing", paths);

    await Assert.That(sql).Contains("ARRAY['It''s']");
    await Assert.That(sql).Contains("$.\"It''s\"");
  }

  /// <summary>The path renders as the jsonpath the predicate uses.</summary>
  [Test]
  public async Task APathRendersAsJsonPathAsync() {
    var path = new TemporalPath("data", ["Turns", "[]", "At"], StoredTemporalKind.Instant);

    await Assert.That(path.JsonPath).IsEqualTo("$.\"Turns\"[*].\"At\"");
  }
}

/// <summary>An opaque document that nests its own type, so the serializer's walk meets a cycle.</summary>
public sealed record OpaqueNode(Guid Id, DateTime At, OpaqueNode? Next);

/// <summary>Source-generated metadata for <see cref="OpaqueNode"/>.</summary>
[JsonSerializable(typeof(OpaqueNode))]
public sealed partial class OpaqueNodeJsonContext : JsonSerializerContext;

/// <summary>Registers the self-referencing document's metadata with the registry once.</summary>
public static class OpaqueNodeFixture {
  private static readonly Lazy<bool> _registered = new(() => {
    Whizbang.Core.Serialization.JsonContextRegistry.RegisterContext(OpaqueNodeJsonContext.Default);
    return true;
  });

  /// <summary>Ensures the document resolves through the registry's union.</summary>
  public static void EnsureRegistered() => _ = _registered.Value;
}

/// <summary>Two mapped shapes side by side and two opaque, as the generator maps them.</summary>
internal sealed class RewriteContext(DbContextOptions<RewriteContext> options) : DbContext(options) {
  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    modelBuilder.Entity<PerspectiveRow<CanonicalTemporalRewriteTests.MappedModel>>(entity => {
      entity.ToTable("wh_per_mapped");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.ComplexProperty(e => e.Data, d => {
        d.ToJson("data");
        d.Property(x => x.RenamedAt).HasJsonPropertyName("renamed_at");
      });
      entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
      entity.ComplexProperty(e => e.Scope, s => {
        s.ToJson("scope");
        s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
      });
      entity.Property(e => e.CreatedAt).HasColumnName("created_at");
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
      entity.Property(e => e.Version).HasColumnName("version");
    });
    modelBuilder.Entity<PerspectiveRow<CanonicalTemporalRewriteTests.PlainModel>>(entity => {
      entity.ToTable("wh_per_plain");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
      entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
      entity.ComplexProperty(e => e.Scope, s => {
        s.ToJson("scope");
        s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
      });
      entity.Property(e => e.CreatedAt).HasColumnName("created_at");
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
      entity.Property(e => e.Version).HasColumnName("version");
    });
    modelBuilder.Entity<PerspectiveRow<OpaqueDocument>>(entity => {
      entity.ToTable("wh_per_opaque");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb")
        .HasConversion(PerspectiveDocumentSerialization.ConverterFor<OpaqueDocument>());
      entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb")
        .HasConversion(PerspectiveDocumentSerialization.ConverterFor<PerspectiveMetadata>());
      entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb")
        .HasConversion(PerspectiveDocumentSerialization.ConverterFor<PerspectiveScope>());
      entity.Property(e => e.CreatedAt).HasColumnName("created_at");
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
      entity.Property(e => e.Version).HasColumnName("version");
    });
    modelBuilder.Entity<PerspectiveRow<OpaqueNode>>(entity => {
      entity.ToTable("wh_per_opaque_node");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb")
        .HasConversion(PerspectiveDocumentSerialization.ConverterFor<OpaqueNode>());
      entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb")
        .HasConversion(PerspectiveDocumentSerialization.ConverterFor<PerspectiveMetadata>());
      entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb")
        .HasConversion(PerspectiveDocumentSerialization.ConverterFor<PerspectiveScope>());
      entity.Property(e => e.CreatedAt).HasColumnName("created_at");
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
      entity.Property(e => e.Version).HasColumnName("version");
    });
  }
}

/// <summary>
/// Source-generated metadata for the mapped model, so the same type can also be stored opaquely.
/// </summary>
/// <remarks>
/// Top level, because the serializer's generator emits nothing for a context nested in a type that is not
/// itself partial, and the emitted members are abstract on the base.
/// </remarks>
[JsonSerializable(typeof(CanonicalTemporalRewriteTests.MappedModel))]
internal sealed partial class MappedModelJsonContext : JsonSerializerContext;

/// <summary>The same model as <see cref="RewriteContext"/> maps, stored instead as one serialized value.</summary>
internal sealed class OpaqueMappedContext(DbContextOptions<OpaqueMappedContext> options) : DbContext(options) {
  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    ArgumentNullException.ThrowIfNull(modelBuilder);
    modelBuilder.Entity<PerspectiveRow<CanonicalTemporalRewriteTests.MappedModel>>(entity => {
      entity.ToTable("wh_per_mapped_opaque");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb")
        .HasConversion(PerspectiveDocumentSerialization.ConverterFor<CanonicalTemporalRewriteTests.MappedModel>());
      entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb")
        .HasConversion(PerspectiveDocumentSerialization.ConverterFor<PerspectiveMetadata>());
      entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb")
        .HasConversion(PerspectiveDocumentSerialization.ConverterFor<PerspectiveScope>());
      entity.Property(e => e.CreatedAt).HasColumnName("created_at");
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
      entity.Property(e => e.Version).HasColumnName("version");
    });
  }
}
