using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Pins the SQL a lens query compiles to, shape by shape.
/// </summary>
/// <remarks>
/// <para>
/// These read the compiled command text through <c>ToQueryString</c> and never open a connection,
/// so they are fast and need no database. What makes them worth having is the mapping: the entity
/// is configured the way the generator configures a real perspective, with
/// <c>ComplexProperty().ToJson()</c> on data, metadata and scope. The older query tests map the
/// data column with <c>Property().HasColumnType("jsonb")</c>, which is the Npgsql POCO mapping and
/// not what ships, so the SQL they observe is not the SQL that runs.
/// </para>
/// <para>
/// The reason to pin these is that the difference between a physical column and a JSON path is
/// invisible in the LINQ. Both are written <c>row.Data.Something</c>. Only the compiled SQL says
/// which one the database is asked for, and therefore whether an index can be used at all.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <docs>operations/diagnostics/whiz302</docs>
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PerspectiveSqlShapeTests {
  // No connection is opened; the provider only needs a syntactically valid string to build a model.
  private const string UNUSED_CONNECTION = "Host=localhost;Database=shape;Username=u;Password=p";

  /// <summary>
  /// A model carrying one property of every shape that matters: promoted and indexed, promoted and
  /// unique, and a spread of types left in the JSON document.
  /// </summary>
  [SuppressIndexAdvisory("this model exists to compile queries, never to run them")]
  public class CatalogModel {
    [StreamId]
    public Guid CatalogId { get; init; }

    // Promoted to real columns.
    [PhysicalField(Indexed = true)]
    public Guid OwnerId { get; init; }

    [PhysicalField(Indexed = true)]
    public decimal Price { get; init; }

    [PhysicalField(Unique = true)]
    public string Sku { get; init; } = string.Empty;

    // Left in the JSON document.
    public string Title { get; init; } = string.Empty;
    public Guid TenantId { get; init; }
    public bool Archived { get; init; }
    public int Rank { get; init; }
    public string? Notes { get; init; }
  }

  /// <summary>
  /// Mirrors Templates/Snippets/EFCoreSnippets.cs, which is what the generator emits.
  /// </summary>
  /// <param name="modelBuilder">The model being built.</param>
  /// <param name="shareDataColumn">
  /// When true, also map a scalar shadow property to the column the complex property owns, to show
  /// what EF does about it.
  /// </param>
  private static void _mapPerspectiveRow(ModelBuilder modelBuilder, bool shareDataColumn) {
    modelBuilder.Entity<PerspectiveRow<CatalogModel>>(entity => {
      entity.ToTable("wh_per_catalog");
      entity.HasKey(e => e.Id);

      entity.Property(e => e.Id).HasColumnName("id");

      entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
      entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
      entity.ComplexProperty(e => e.Scope, s => {
        s.ToJson("scope");
        s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
      });

      entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
      entity.Property(e => e.Version).HasColumnName("version").IsRequired();
      entity.Property<DateTime?>("expires_at").HasColumnName("expires_at");

      // Physical fields arrive as shadow columns alongside the JSON document.
      entity.Property<Guid>("owner_id").HasColumnName("owner_id");
      entity.Property<decimal>("price").HasColumnName("price");
      entity.Property<string?>("sku").HasColumnName("sku").HasMaxLength(100);

      if (shareDataColumn) {
        entity.Property<string>("DataRaw").HasColumnName("data").HasColumnType("jsonb");
      }
    });
  }

  private sealed class ShapeDbContext(DbContextOptions<ShapeDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      _mapPerspectiveRow(modelBuilder, shareDataColumn: false);
  }

  private static readonly Guid _probeId = new("305a83c8-b1b0-47ca-86ec-c2ce0e4502c3");

  // Built once; see the note in JsonbContainmentSqlMatrixTests about internal service providers.
  private static readonly DbContextOptions<ShapeDbContext> _shapeOptions =
    new DbContextOptionsBuilder<ShapeDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static readonly DbContextOptions<OperatorDbContext> _operatorOptions =
    new DbContextOptionsBuilder<OperatorDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static readonly DbContextOptions<SharedColumnDbContext> _sharedColumnOptions =
    new DbContextOptionsBuilder<SharedColumnDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static ShapeDbContext _newContext() {
    PhysicalFieldRegistry.Register<CatalogModel>("OwnerId", "owner_id");
    PhysicalFieldRegistry.Register<CatalogModel>("Price", "price");
    PhysicalFieldRegistry.Register<CatalogModel>("Sku", "sku");

    return new ShapeDbContext(_shapeOptions);
  }

  private static string _sqlForTenant() {
    var id = _probeId;
    return _sqlFor(rows => rows.Where(r => r.Data.TenantId == id));
  }

  private static string _sqlFor(Func<IQueryable<PerspectiveRow<CatalogModel>>, IQueryable<PerspectiveRow<CatalogModel>>> shape) {
    using var db = _newContext();
    return shape(db.Set<PerspectiveRow<CatalogModel>>()).ToQueryString();
  }

  // ========================================
  // Promoted fields read a column
  // ========================================

  /// <summary>A promoted identifier compares against its own column, which an index can serve.</summary>
  [Test]
  public async Task PhysicalGuidEquality_ReadsTheColumnAsync() {
    var id = _probeId;
    var sql = _sqlFor(rows => rows.Where(r => r.Data.OwnerId == id));

    await Assert.That(sql).Contains("WHERE w.owner_id = @");
    await Assert.That(sql).DoesNotContain("data ->>");
  }

  /// <summary>A promoted decimal compares against its column, with no cast.</summary>
  [Test]
  public async Task PhysicalDecimalRange_ReadsTheColumnAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Price >= 50m));

    await Assert.That(sql).Contains("WHERE w.price >= ");
    await Assert.That(sql).DoesNotContain("data ->>");
  }

  /// <summary>A promoted string compares against its column.</summary>
  [Test]
  public async Task PhysicalStringEquality_ReadsTheColumnAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Sku == "abc"));

    await Assert.That(sql).Contains("WHERE w.sku = ");
    await Assert.That(sql).DoesNotContain("data ->>");
  }

  /// <summary>Ordering on a promoted field orders by the column, so an index can supply the order.</summary>
  [Test]
  public async Task PhysicalOrdering_ReadsTheColumnAsync() {
    var sql = _sqlFor(rows => rows.OrderBy(r => r.Data.Price));

    await Assert.That(sql).Contains("ORDER BY w.price");
    await Assert.That(sql).DoesNotContain("data ->>");
  }

  // ========================================
  // Unpromoted fields extract from the document
  // ========================================

  /// <summary>
  /// The shape WHIZ302 exists for. A string comparison becomes a text extraction, which no GIN
  /// index can serve and only an expression index on the same extraction could.
  /// </summary>
  [Test]
  public async Task JsonStringEquality_ExtractsFromTheDocumentAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Title == "abc"));

    await Assert.That(sql).Contains("WHERE (w.data ->> 'Title') = ");
  }

  /// <summary>
  /// A typed property extracts as text and then casts, so an expression index would have to carry
  /// the cast too. This is the shape a tenant or entity lookup takes.
  /// </summary>
  [Test]
  public async Task JsonGuidEquality_ExtractsThenCastsAsync() {
    var id = _probeId;
    var sql = _sqlFor(rows => rows.Where(r => r.Data.TenantId == id));

    await Assert.That(sql).Contains("CAST(w.data ->> 'TenantId' AS uuid)");
  }

  /// <summary>A boolean extracts and casts the same way.</summary>
  [Test]
  public async Task JsonBooleanPredicate_ExtractsThenCastsAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Archived));

    await Assert.That(sql).Contains("CAST(w.data ->> 'Archived' AS boolean)");
  }

  /// <summary>A numeric range extracts and casts, so ordering and bounds run per row.</summary>
  [Test]
  public async Task JsonNumericRange_ExtractsThenCastsAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Rank > 3));

    await Assert.That(sql).Contains("CAST(w.data ->> 'Rank' AS integer)");
  }

  /// <summary>
  /// A null test reads as a missing-or-null extraction. Worth pinning because JSON null and an
  /// absent key both extract to SQL NULL, which is not the same distinction the model draws.
  /// </summary>
  [Test]
  public async Task JsonNullCheck_ExtractsFromTheDocumentAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Notes == null));

    await Assert.That(sql).Contains("(w.data ->> 'Notes') IS NULL");
  }

  /// <summary>A substring search extracts and then matches, which no index of any kind serves here.</summary>
  [Test]
  public async Task JsonSubstringSearch_ExtractsThenMatchesAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.Data.Title.Contains("ab")));

    await Assert.That(sql).Contains("w.data ->> 'Title' LIKE ");
  }

  /// <summary>Ordering on an unpromoted field sorts on the extraction, so every row is read and sorted.</summary>
  [Test]
  public async Task JsonOrdering_ExtractsFromTheDocumentAsync() {
    var sql = _sqlFor(rows => rows.OrderBy(r => r.Data.Title));

    await Assert.That(sql).Contains("ORDER BY w.data ->> 'Title'");
  }

  /// <summary>Two unpromoted predicates produce two independent extractions, both evaluated per row.</summary>
  [Test]
  public async Task JsonCompoundPredicate_ExtractsEachFieldAsync() {
    var id = _probeId;
    var sql = _sqlFor(rows => rows.Where(r => r.Data.TenantId == id && r.Data.Title == "abc"));

    await Assert.That(sql).Contains("CAST(w.data ->> 'TenantId' AS uuid)");
    await Assert.That(sql).Contains("(w.data ->> 'Title') = ");
  }

  // ========================================
  // The row's own columns
  // ========================================

  /// <summary>The row key is a real column and stays one.</summary>
  [Test]
  public async Task RowKeyEquality_ReadsTheKeyColumnAsync() {
    var id = _probeId;
    var sql = _sqlFor(rows => rows.Where(r => r.Id == id));

    await Assert.That(sql).Contains("WHERE w.id = @");
    await Assert.That(sql).DoesNotContain("data ->>");
  }

  /// <summary>So does the system timestamp.</summary>
  [Test]
  public async Task RowTimestampRange_ReadsTheColumnAsync() {
    var sql = _sqlFor(rows => rows.Where(r => r.UpdatedAt > DateTime.UnixEpoch));

    await Assert.That(sql).Contains("WHERE w.updated_at > ");
    await Assert.That(sql).DoesNotContain("data ->>");
  }

  // ========================================
  // Why the GIN indexes on the JSON columns are unreachable
  // ========================================

  /// <summary>
  /// No query shape the lens can express emits a containment operator, which is the only thing the
  /// GIN indexes on data, metadata and scope can answer.
  /// </summary>
  [Test]
  public async Task NoQueryShape_EmitsContainmentAsync() {
    var shapes = new Func<IQueryable<PerspectiveRow<CatalogModel>>, IQueryable<PerspectiveRow<CatalogModel>>>[] {
      rows => rows.Where(r => r.Data.Title == "abc"),
      rows => rows.Where(r => r.Data.TenantId == _probeId),
      rows => rows.Where(r => r.Data.Archived),
      rows => rows.Where(r => r.Data.Rank > 3),
      rows => rows.Where(r => r.Data.Notes == null),
      rows => rows.OrderBy(r => r.Data.Title),
    };

    foreach (var shape in shapes) {
      await Assert.That(_sqlFor(shape)).DoesNotContain("@>");
    }
  }

  /// <summary>
  /// Npgsql's own containment helpers do not translate here, because they need an operand mapped as
  /// a scalar jsonb value and the data column is a structural type.
  /// </summary>
  [Test]
  public async Task NpgsqlContainmentHelpers_DoNotTranslateOverTheComplexPropertyAsync() {
    using var db = _newContext();
    var rows = db.Set<PerspectiveRow<CatalogModel>>();

    await Assert.That(() => rows.Where(r => EF.Functions.JsonContains(r.Data, "{\"Title\":\"abc\"}")).ToQueryString())
      .Throws<InvalidOperationException>();

    await Assert.That(() => rows.Where(r => EF.Functions.JsonExists(r.Data, "Title")).ToQueryString())
      .Throws<InvalidOperationException>();

    await Assert.That(() => rows
        .Where(r => EF.Functions.JsonContains(EF.Property<string>(r, "data"), "{\"Title\":\"abc\"}"))
        .ToQueryString())
      .Throws<InvalidOperationException>();
  }

  /// <summary>
  /// A custom translation can emit any operator we like, including the containment operator the GIN
  /// indexes answer. What it cannot do is put the bare column on the left of it.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the test that decides whether the GIN indexes are salvageable, so it is worth being
  /// precise about what it shows. EF Core's <c>HasDbFunction(...).HasTranslation(...)</c> is a
  /// supported extension point, and it happily produces <c>@&gt;</c> over a JSON member. Writing our
  /// own SQL is therefore not the obstacle.
  /// </para>
  /// <para>
  /// The obstacle is the operand. PostgreSQL matches a GIN index only when the left side of the
  /// operator is the indexed expression itself, the bare <c>data</c> column. A containment test over
  /// an extraction, which is all we can build, plans as a sequential scan. Combined with
  /// <see cref="TheDataColumn_IsNotAddressableAsAScalarAsync"/>, which shows the bare column cannot
  /// be reached, that closes the question.
  /// </para>
  /// </remarks>
  [Test]
  public async Task ACustomTranslation_CanEmitAnyOperator_ButOnlyOverAnExtractionAsync() {
    using var db = new OperatorDbContext(_operatorOptions);

    var sql = db.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => JsonbContains(r.Data.Title, "\"abc\""))
      .ToQueryString();

    // The operator is emitted, so custom SQL generation works.
    await Assert.That(sql).Contains("@>", StringComparison.Ordinal);

    // But its left operand is the extraction, not the column, so no GIN index can match it.
    await Assert.That(sql).Contains("(w.data ->> 'Title') @>", StringComparison.Ordinal);
    await Assert.That(sql).DoesNotContain("WHERE w.data @>", StringComparison.Ordinal);
  }

  /// <summary>
  /// The bare data column cannot be addressed as a scalar by any of the three routes available, so
  /// there is nothing to put on the left of a containment test.
  /// </summary>
  [Test]
  public async Task TheDataColumn_IsNotAddressableAsAScalarAsync() {
    // A second model element cannot share the column the complex property already owns.
    await Assert.That(() => {
      using var db = new SharedColumnDbContext(_sharedColumnOptions);
      return db.Set<PerspectiveRow<CatalogModel>>().Where(r => r.Data.Title == "abc").ToQueryString();
    }).Throws<ArgumentException>();

    using var plain = _newContext();
    var rows = plain.Set<PerspectiveRow<CatalogModel>>();

    // Addressing it by property name resolves to the complex property, which is not a scalar.
    await Assert.That(() => rows.Where(r => JsonbContains(EF.Property<string>(r, "Data"), "{}")).ToQueryString())
      .ThrowsException();

    // Addressing it by column name finds no property at all.
    await Assert.That(() => rows.Where(r => JsonbContains(EF.Property<string>(r, "data"), "{}")).ToQueryString())
      .ThrowsException();
  }

  /// <summary>Marker method the custom containment translation is attached to.</summary>
  public static bool JsonbContains(string column, string contained) => throw new NotSupportedException();

  private sealed class OperatorDbContext(DbContextOptions<OperatorDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      _mapPerspectiveRow(modelBuilder, shareDataColumn: false);
      modelBuilder
        .HasDbFunction(typeof(PerspectiveSqlShapeTests).GetMethod(nameof(JsonbContains))!)
        .HasTranslation(_emitContainment);
    }
  }

  private sealed class SharedColumnDbContext(DbContextOptions<SharedColumnDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      _mapPerspectiveRow(modelBuilder, shareDataColumn: true);
  }

  /// <summary>
  /// Builds Npgsql's arbitrary-operator expression by reflection, so the test carries no
  /// compile-time dependency on a type that lives in an Internal namespace.
  /// </summary>
  private static Microsoft.EntityFrameworkCore.Query.SqlExpressions.SqlExpression _emitContainment(
      IReadOnlyList<Microsoft.EntityFrameworkCore.Query.SqlExpressions.SqlExpression> args) {
    var type = System.Reflection.Assembly.Load("Npgsql.EntityFrameworkCore.PostgreSQL")
      .GetType("Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal.PgUnknownBinaryExpression")!;
    var ctor = type.GetConstructors()[0];
    var parameters = ctor.GetParameters();
    var values = new object?[parameters.Length];

    for (var i = 0; i < parameters.Length; i++) {
      values[i] = parameters[i].Name switch {
        "left" => args[0],
        "right" => args[1],
        "binaryOperator" => "@>",
        "type" => typeof(bool),
        // A null type mapping makes the expression untranslatable, which reads as "could not be
        // translated" and is easy to mistake for the operator being unsupported.
        "typeMapping" => Microsoft.EntityFrameworkCore.Storage.BoolTypeMapping.Default,
        _ => null,
      };
    }

    return (Microsoft.EntityFrameworkCore.Query.SqlExpressions.SqlExpression)ctor.Invoke(values);
  }

  /// <summary>
  /// The extraction an unpromoted filter compiles to is the expression an index would have to
  /// match, character for character. Recorded here so the shape a candidate index must carry is
  /// stated in one place rather than inferred.
  /// </summary>
  [Test]
  [Arguments("Title", "w.data ->> 'Title'")]
  [Arguments("TenantId", "CAST(w.data ->> 'TenantId' AS uuid)")]
  [Arguments("Rank", "CAST(w.data ->> 'Rank' AS integer)")]
  public async Task ExtractionShape_IsWhatAnExpressionIndexMustMatchAsync(string field, string expected) {
    var sql = field switch {
      "Title" => _sqlFor(rows => rows.Where(r => r.Data.Title == "abc")),
      "TenantId" => _sqlForTenant(),
      _ => _sqlFor(rows => rows.Where(r => r.Data.Rank > 3)),
    };

    await Assert.That(sql).Contains(expected, StringComparison.Ordinal);
  }
}
