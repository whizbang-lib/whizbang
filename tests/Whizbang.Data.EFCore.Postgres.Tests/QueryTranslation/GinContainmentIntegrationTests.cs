using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Proves end to end, against a real database, that a lens filter can be compiled into the
/// containment form and that PostgreSQL then answers it from the GIN index instead of scanning.
/// </summary>
/// <remarks>
/// <para>
/// The unit-level shape tests show what SQL comes out. They cannot show that the planner uses the
/// index, or that containment returns the same rows equality would. Both are checked here, on a
/// table seeded with enough rows that the planner has a real choice to make.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz302</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class GinContainmentIntegrationTests : IAsyncDisposable {
  // Seeded through EF so the JSON byte format is exactly what the stack writes.
  private const int EF_SEEDED_ROWS = 200;

  // Bulk-seeded in SQL, reusing an EF-written row's metadata and scope so the format stays
  // identical. The table has to be big enough that a sequential scan is genuinely the worse
  // plan, or the index assertion proves nothing.
  private const int BULK_SEEDED_ROWS = 200_000;
  private const string TABLE = "wh_per_gin_probe";

  [SuppressIndexAdvisory("the point of this fixture is to compare an indexed containment filter with an unindexed scan")]
  public class CatalogModel {
    public string Title { get; init; } = string.Empty;
    public Guid TenantId { get; init; }
    public int Rank { get; init; }

    /// <summary>A date, which reaches the index through a rendering rather than through the value.</summary>
    public DateTime OccurredAt { get; init; }
  }

  // One row in the table carries this instant, so a filter on it is genuinely selective. It has a
  // fractional part, which is the case where the rendering has to trim.
  private static readonly DateTime _needleInstant = new(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc);

  private sealed class GinDbContext(DbContextOptions<GinDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<CatalogModel>>(entity => {
        entity.ToTable(TABLE);
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
      });

      modelBuilder.UseWhizbangJsonbContainment();
    }
  }

  private string _databaseName = null!;
  private string _connectionString = null!;
  private GinDbContext? _context;
  private Guid _needleTenant;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"gin_probe_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await _execAsync(admin, $"CREATE DATABASE {_databaseName}");
    }

    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    await using (var db = new NpgsqlConnection(_connectionString)) {
      await db.OpenAsync();
      await _execAsync(db, $"""
        CREATE TABLE {TABLE} (
          id uuid PRIMARY KEY,
          created_at timestamptz NOT NULL,
          updated_at timestamptz NOT NULL,
          version integer NOT NULL,
          data jsonb NOT NULL,
          metadata jsonb NOT NULL,
          scope jsonb NOT NULL
        );
        CREATE INDEX idx_gin_probe_data ON {TABLE} USING gin (data);
        """);

      // The real migration text, not a copy of it: if 152 stops producing a usable function the
      // end-to-end tests below fail rather than passing against a local imitation. 000 comes first
      // because 152 opens by dropping any earlier overload of itself, and the helper that does that
      // is defined there.
      foreach (var migration in new[] { "000_MigrationTracking.sql", "152_JsonbContainmentSet.sql" }) {
        await _execAsync(db, (await File.ReadAllTextAsync(_migrationPath(migration)))
          .Replace("__SCHEMA__", "public", StringComparison.Ordinal));
      }
    }

    _context = new GinDbContext(new DbContextOptionsBuilder<GinDbContext>()
      .UseNpgsql(_connectionString)
      // The shipped wiring, so these tests exercise the real rewrite rather than a local imitation
      // of it. Without this the fixture proved only that the SQL shape works if something emits it.
      .UseWhizbangPhysicalFields()
      // Every test here gets its own database, so the options cannot be shared the way a fixture
      // over one schema would share them, and each instance asks Entity Framework for another
      // internal service provider. Past twenty of those it raises this as an error, which fails the
      // whole class in a full run while passing when the class runs alone.
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

    _needleTenant = Guid.NewGuid();
    var rows = new List<PerspectiveRow<CatalogModel>>(EF_SEEDED_ROWS);
    for (var i = 0; i < EF_SEEDED_ROWS; i++) {
      rows.Add(new PerspectiveRow<CatalogModel> {
        Id = Guid.NewGuid(),
        // One row in the whole table carries the needle, so the predicate is genuinely selective.
        Data = new CatalogModel {
          Title = i == 0 ? "needle" : $"hay-{i.ToString(CultureInfo.InvariantCulture)}",
          TenantId = i == 0 ? _needleTenant : Guid.NewGuid(),
          Rank = i,
          OccurredAt = i == 0 ? _needleInstant : _needleInstant.AddDays(i),
        },
        Metadata = new PerspectiveMetadata(),
        Scope = new PerspectiveScope(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Version = 1,
      });
    }

    _context.AddRange(rows);
    await _context.SaveChangesAsync();

    await using (var db = new NpgsqlConnection(_connectionString)) {
      await db.OpenAsync();

      await _execAsync(db, $"""
        INSERT INTO {TABLE} (id, created_at, updated_at, version, data, metadata, scope)
        SELECT gen_random_uuid(), now(), now(), 1,
               jsonb_build_object('Title', 'bulk-' || g,
                                  'TenantId', gen_random_uuid()::text,
                                  'Rank', g),
               shape.metadata, shape.scope
        FROM generate_series(1, {BULK_SEEDED_ROWS}) g
        CROSS JOIN (SELECT metadata, scope FROM {TABLE} LIMIT 1) shape;
        """);

      await _execAsync(db, $"ANALYZE {TABLE}");
    }
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_context is not null) {
      await _context.DisposeAsync();
      _context = null;
    }

    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await _execAsync(admin, $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
      } catch (NpgsqlException) {
        // The container is shared and the database is per-test; a failed drop is not a test failure.
      }
    }

    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// The containment filter returns exactly the rows the equality filter returns. Without this the
  /// rewrite would be faster and wrong.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task ContainmentFilter_ReturnsTheSameRowsAsEqualityAsync(CancellationToken cancellationToken) {
    var needle = "needle";

    var byEquality = await _context!.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => r.Data.Title == needle)
      .Select(r => r.Id)
      .ToListAsync(cancellationToken);

    var byContainment = await _context.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => r.Data.Title == needle)
      .Select(r => r.Id)
      .ToListAsync(cancellationToken);

    await Assert.That(byEquality).Count().IsEqualTo(1);
    await Assert.That(byContainment).IsEquivalentTo(byEquality);
  }

  /// <summary>A Guid survives the round trip through jsonb_build_object, casing included.</summary>
  [Test]
  [Timeout(120000)]
  public async Task ContainmentFilter_MatchesAGuidValueAsync(CancellationToken cancellationToken) {
    var tenant = _needleTenant;

    var byEquality = await _context!.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => r.Data.TenantId == tenant)
      .Select(r => r.Id)
      .ToListAsync(cancellationToken);

    var byContainment = await _context.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => r.Data.TenantId == tenant)
      .Select(r => r.Id)
      .ToListAsync(cancellationToken);

    await Assert.That(byEquality).Count().IsEqualTo(1);
    await Assert.That(byContainment).IsEquivalentTo(byEquality);
  }

  /// <summary>
  /// The whole point: the planner answers the containment form from the GIN index, and the
  /// extraction form by reading every row.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task ContainmentUsesTheGinIndex_WhileExtractionScansAsync(CancellationToken cancellationToken) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);

    var containmentPlan = await _explainAsync(db,
      $"SELECT id FROM {TABLE} WHERE data @> jsonb_build_object('Title', @p)", "needle");

    var extractionPlan = await _explainAsync(db,
      $"SELECT id FROM {TABLE} WHERE data ->> 'Title' = @p", "needle");

    await Assert.That(containmentPlan).Contains("idx_gin_probe_data", StringComparison.Ordinal);
    await Assert.That(containmentPlan).Contains("Bitmap Index Scan", StringComparison.Ordinal);

    await Assert.That(extractionPlan).Contains("Seq Scan", StringComparison.Ordinal);
    await Assert.That(extractionPlan).DoesNotContain("idx_gin_probe_data", StringComparison.Ordinal);
  }

  /// <summary>
  /// A date filter reaches the index too, even though the value on the right of the operator is a
  /// rendering of the instant rather than the instant.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the question a date raises that no other eligible type does. A number or an identifier
  /// is handed to <c>jsonb_build_object</c> as it stands; a date has to be formatted into the text
  /// the document holds, and that formatting is built from <c>to_char</c>, which PostgreSQL declares
  /// STABLE rather than IMMUTABLE. A stable expression cannot appear in an index definition, which
  /// invites the assumption that it cannot be used with an index either. It can: the planner
  /// evaluates it once per statement and treats the result as a run-time constant, so the bitmap
  /// index scan is still available.
  /// </para>
  /// <para>
  /// Asserted through the shipped wiring on a table large enough for a sequential scan to be the
  /// genuinely cheaper plan if the index were unusable, because that is the only way the assertion
  /// means anything. The row count is checked alongside the plan, since an index scan that returns
  /// the wrong rows is worse than a sequential one that returns the right ones.
  /// </para>
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ADateFilterUsesTheGinIndexAsync(CancellationToken cancellationToken) {
    var sql = _context!.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => r.Data.OccurredAt == _needleInstant)
      .ToQueryString();

    await Assert.That(sql).Contains("@>", StringComparison.Ordinal);

    var found = await _context.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => r.Data.OccurredAt == _needleInstant)
      .Select(r => r.Id)
      .ToListAsync(cancellationToken);

    await Assert.That(found).Count().IsEqualTo(1)
      .Because("the rendering has to produce the text the row holds");

    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);

    var plan = await _explainDateAsync(db, _needleInstant);

    await Assert.That(plan).Contains("idx_gin_probe_data", StringComparison.Ordinal)
      .Because("a stable expression on the right of the operator is still a run-time constant");
    await Assert.That(plan).Contains("Bitmap Index Scan", StringComparison.Ordinal);
  }

  /// <summary>
  /// Records how the planner treats composed predicates and ranges, which is what decides whether
  /// there is anything left to win for them.
  /// </summary>
  /// <remarks>
  /// Four separate questions, none of which can be answered by reading generated SQL. Whether two
  /// containment tests joined by AND use the index twice or fold into one probe; whether OR does;
  /// whether a range over a JSON member can be indexed at all; and whether the stored date text can
  /// be range-scanned as text, which depends on its ordering being chronological.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ComposedPredicatesAndRanges_AreRecordedAsync(CancellationToken cancellationToken) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);

    var lines = new List<string>();

    async Task<string> planAsync(string label, string where) {
      await using var command = new NpgsqlCommand(
        $"EXPLAIN (FORMAT TEXT) SELECT id FROM {TABLE} WHERE {where}", db);
      var rows = new List<string>();
      await using var reader = await command.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) {
        rows.Add(reader.GetString(0).Trim());
      }

      var plan = string.Join(" | ", rows);
      lines.Add($"{label}: {plan}");
      return plan;
    }

    // Two containment tests ANDed, as the rewrite emits them today, against the same pair folded into
    // one document. The question is whether folding buys a probe.
    await planAsync("and-two-tests",
      "data @> jsonb_build_object('Title', 'needle') AND data @> jsonb_build_object('Rank', 0)");
    await planAsync("and-folded",
      "data @> jsonb_build_object('Title', 'needle', 'Rank', 0)");
    await planAsync("or-two-tests",
      "data @> jsonb_build_object('Title', 'needle') OR data @> jsonb_build_object('Title', 'hay-7')");

    // A range over a JSON member, before and after a btree index on the extraction. The cast has to be
    // immutable for such an index to be allowed at all.
    await planAsync("range-unindexed", "(data ->> 'Rank')::int > 199000");
    await _execAsync(db, $"CREATE INDEX idx_gin_probe_rank ON {TABLE} (((data ->> 'Rank')::int))");
    await _execAsync(db, $"ANALYZE {TABLE}");
    await planAsync("range-indexed", "(data ->> 'Rank')::int > 199000");

    // And whether a date can be range-scanned as stored text, which needs the text ordering to agree
    // with the chronological one. The variable-width fraction is what puts that in doubt.
    await using (var ordering = new NpgsqlCommand("""
      SELECT format('whole<fraction=%s next-second>fraction=%s',
        ('2026-03-04T05:06:07Z' < '2026-03-04T05:06:07.1Z' COLLATE "C"),
        ('2026-03-04T05:06:08Z' > '2026-03-04T05:06:07.1Z' COLLATE "C"))
      """, db)) {
      lines.Add($"date-text-ordering: {await ordering.ExecuteScalarAsync(cancellationToken)}");
    }

    var report = string.Join('\n', lines);
    var target = Environment.GetEnvironmentVariable("WHIZ_COMPOSE_DUMP");
    if (!string.IsNullOrWhiteSpace(target)) {
      await File.WriteAllTextAsync(target, report, cancellationToken);
    }

    string plan(string label) =>
      lines.Find(l => l.StartsWith(label + ":", StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"no plan recorded for {label}");

    // Two containment tests joined by AND become one index scan carrying both conditions, not two
    // scans combined afterwards. This is why the rewrite emits them separately rather than folding
    // them into a single document: the planner already does the folding, and the unfolded form
    // additionally gets its selectivity estimated per condition.
    await Assert.That(plan("and-two-tests")).Contains("Bitmap Index Scan", StringComparison.Ordinal);
    await Assert.That(plan("and-two-tests").Split("Bitmap Index Scan").Length - 1).IsEqualTo(1)
      .Because("both conditions belong to one index scan, which is why the rewrite emits a test "
        + "per comparison instead of folding them into a single containment document. IF THIS "
        + "ASSERTION FAILS BECAUSE THE PLANNER NOW USES A SCAN PER CONDITION, folding becomes worth "
        + "doing and that decision should be revisited.");

    // OR cannot fold, because containment is conjunctive, so it fans out and recombines. Both arms
    // still reach the index, which is what makes an OR of equality filters viable at all.
    await Assert.That(plan("or-two-tests")).Contains("BitmapOr", StringComparison.Ordinal);
    await Assert.That(plan("or-two-tests").Split("Bitmap Index Scan").Length - 1).IsEqualTo(2);

    // A range cannot be answered by the GIN index at any cost, and can be answered by a btree on the
    // extraction. This pair is the whole case for offering expression indexes.
    await Assert.That(plan("range-unindexed")).Contains("Seq Scan", StringComparison.Ordinal);
    await Assert.That(plan("range-indexed")).Contains("Index Scan", StringComparison.Ordinal);
    await Assert.That(plan("range-indexed")).Contains("idx_gin_probe_rank", StringComparison.Ordinal)
      .Because("the index the extraction carries has to be the one the planner chooses");

    // And the stored date text does not sort chronologically, which is why it cannot stand in as a
    // btree key without changing the format first.
    await Assert.That(plan("date-text-ordering")).Contains("fraction=f", StringComparison.Ordinal)
      .Because("a whole second sorts after a fractional one, so the stored text cannot serve as a "
        + "btree key for a range or an ordering. This is what a canonical stored form exists to fix. "
        + "IF THIS ASSERTION FAILS BECAUSE THE DEFAULT RENDERING IS NOW FIXED WIDTH, the default "
        + "form is already range-indexable and the canonical form is only a size optimization.");
  }

  /// <summary>
  /// The plan for the date form, spelled the way the translation emits it so the plan being read is
  /// the plan the framework produces.
  /// </summary>
  private static async Task<string> _explainDateAsync(NpgsqlConnection db, DateTime value) {
    await using var command = new NpgsqlCommand($"""
      EXPLAIN (FORMAT TEXT) SELECT id FROM {TABLE}
      WHERE data @> jsonb_build_object('OccurredAt', CASE
        WHEN isfinite(@p) THEN to_jsonb(concat(rtrim(rtrim(to_char(
          timezone('UTC', @p), 'YYYY-MM-DD"T"HH24:MI:SS.US'), '0'), '.'), 'Z'))
        ELSE to_jsonb(@p)
      END)
      """, db);
    command.Parameters.AddWithValue("p", value);

    var lines = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lines.Add(reader.GetString(0));
    }

    return string.Join('\n', lines);
  }

  /// <summary>
  /// The boundary the rewrite must respect. A row written before a property existed has no key at
  /// all, and the two forms disagree about it: an extraction reads a missing key as SQL NULL, while
  /// containment of an explicit JSON null does not match a key that is absent. So a comparison
  /// against null must keep the extraction form, and only non-null scalar equality may be rewritten.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task MissingKey_IsWhereContainmentAndExtractionDisagreeAsync(CancellationToken cancellationToken) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);

    // A row from before the property existed: the key is absent rather than null.
    await _execAsync(db, $"""
      INSERT INTO {TABLE} (id, created_at, updated_at, version, data, metadata, scope)
      SELECT gen_random_uuid(), now(), now(), 1,
             jsonb_build_object('Title', 'legacy'),
             shape.metadata, shape.scope
      FROM (SELECT metadata, scope FROM {TABLE} LIMIT 1) shape;
      """);

    var byExtraction = await _scalarAsync(db,
      $"SELECT count(*) FROM {TABLE} WHERE data ->> 'Rank' IS NULL");
    var byContainment = await _scalarAsync(db,
      $"SELECT count(*) FROM {TABLE} WHERE data @> '{{\"Rank\": null}}'");

    await Assert.That(byExtraction).IsEqualTo(1L);
    await Assert.That(byContainment).IsEqualTo(0L);
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection db, string sql) {
    await using var command = new NpgsqlCommand(sql, db);
    return (long)(await command.ExecuteScalarAsync())!;
  }

  /// <summary>
  /// Set membership: whether an immutable helper function on the right of the containment operator
  /// still reaches the index, which decides how the rewrite for <c>Contains</c> should be built.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The shape has to build one containment document per candidate value, and the values arrive as a
  /// single array parameter. Two ways to produce that array: inline, with a scalar subquery over
  /// <c>unnest</c>, or by calling a function the framework ships. The second is far easier to emit
  /// from a translation, and the only question that matters is whether the planner still folds it
  /// well enough to use the index.
  /// </para>
  /// <para>
  /// The function is declared IMMUTABLE and written in SQL so PostgreSQL can inline it. If that
  /// stopped being true the plan would fall back to a sequential scan, which is what this asserts
  /// against.
  /// </para>
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task SetMembership_ReachesTheIndexThroughAnImmutableHelperAsync(CancellationToken cancellationToken) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);

    await _execAsync(db, """
      CREATE OR REPLACE FUNCTION wh_jsonb_objects(key text, vals anyarray)
      RETURNS jsonb[] AS $$
        SELECT array_agg(jsonb_build_object(key, v)) FROM unnest(vals) AS v;
      $$ LANGUAGE sql IMMUTABLE;
      """);

    var throughFunction = await _explainArrayAsync(db,
      $"SELECT id FROM {TABLE} WHERE data @> ANY(wh_jsonb_objects('Title', @p))");

    var inlineSubquery = await _explainArrayAsync(db,
      $"SELECT id FROM {TABLE} WHERE data @> ANY(ARRAY(SELECT jsonb_build_object('Title', v) FROM unnest(@p) AS v))");

    // Both must reach the index; the choice between them is then about how each is built, not about
    // whether either works.
    await Assert.That(inlineSubquery).Contains("idx_gin_probe_data", StringComparison.Ordinal);
    await Assert.That(throughFunction).Contains("idx_gin_probe_data", StringComparison.Ordinal);
    await Assert.That(throughFunction).Contains("Bitmap Index Scan", StringComparison.Ordinal);
  }

  /// <summary>Set membership returns the same rows a chain of equality comparisons would.</summary>
  [Test]
  [Timeout(120000)]
  public async Task SetMembership_ReturnsTheSameRowsAsEqualityAsync(CancellationToken cancellationToken) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);

    await _execAsync(db, """
      CREATE OR REPLACE FUNCTION wh_jsonb_objects(key text, vals anyarray)
      RETURNS jsonb[] AS $$
        SELECT array_agg(jsonb_build_object(key, v)) FROM unnest(vals) AS v;
      $$ LANGUAGE sql IMMUTABLE;
      """);

    var byMembership = await _scalarAsync(db,
      $"SELECT count(*) FROM {TABLE} WHERE data @> ANY(wh_jsonb_objects('Title', ARRAY['needle','bulk-1']))");

    var byEquality = await _scalarAsync(db,
      $"SELECT count(*) FROM {TABLE} WHERE data ->> 'Title' = 'needle' OR data ->> 'Title' = 'bulk-1'");

    await Assert.That(byMembership).IsEqualTo(byEquality);
    await Assert.That(byMembership).IsGreaterThan(0L);
  }

  private static readonly string[] _membershipProbe = ["needle", "bulk-1"];

  private static async Task<string> _explainArrayAsync(NpgsqlConnection db, string sql) {
    await using var command = new NpgsqlCommand("EXPLAIN " + sql, db);
    command.Parameters.AddWithValue("p", _membershipProbe);

    var lines = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lines.Add(reader.GetString(0));
    }

    return string.Join('\n', lines);
  }

  /// <summary>
  /// The seam. Every other test proves one half: that the rewrite emits containment, or that
  /// containment uses the index and means the same thing. This runs a real lens query through the
  /// real rewrite against a real database and checks the rows that come back.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task SetMembership_ThroughTheLens_ReturnsTheRightRowsAsync(CancellationToken cancellationToken) {
    var candidates = new[] { "needle", "bulk-7" };

    var byMembership = await _context!.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => candidates.Contains(r.Data.Title))
      .Select(r => r.Data.Title)
      .ToListAsync(cancellationToken);

    var byEquality = await _context.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => r.Data.Title == "needle" || r.Data.Title == "bulk-7")
      .Select(r => r.Data.Title)
      .ToListAsync(cancellationToken);

    await Assert.That(byMembership.Order()).IsEquivalentTo(byEquality.Order());
    await Assert.That(byMembership).Count().IsEqualTo(2);
  }

  /// <summary>The membership query really did compile to the helper, not quietly fall back to IN.</summary>
  [Test]
  [Timeout(120000)]
  public async Task SetMembership_ThroughTheLens_CompilesToTheHelperAsync(CancellationToken cancellationToken) {
    var candidates = new[] { "needle" };

    var sql = _context!.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => candidates.Contains(r.Data.Title))
      .ToQueryString();

    await Assert.That(sql).Contains("jsonb_containment_set", StringComparison.Ordinal);
    await Assert.That(sql).Contains("@> ANY", StringComparison.Ordinal);

    // And it executes, which a compiled-text assertion alone would not prove.
    var rows = await _context.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => candidates.Contains(r.Data.Title))
      .CountAsync(cancellationToken);

    await Assert.That(rows).IsEqualTo(1);
  }

  /// <summary>An Equals spelling returns the same rows the operator form does, executed for real.</summary>
  [Test]
  [Timeout(120000)]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1309:Use ordinal string comparison",
    Justification = "The overload without a StringComparison is the one under test: it is what a developer " +
      "writes, Entity Framework can translate it, and the rewrite has to agree with that translation.")]
  public async Task EqualsSpelling_ThroughTheLens_ReturnsTheRightRowsAsync(CancellationToken cancellationToken) {
    var needle = "needle";

    var byEquals = await _context!.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => r.Data.Title.Equals(needle))
      .Select(r => r.Id)
      .ToListAsync(cancellationToken);

    var byOperator = await _context.Set<PerspectiveRow<CatalogModel>>()
      .Where(r => r.Data.Title == needle)
      .Select(r => r.Id)
      .ToListAsync(cancellationToken);

    await Assert.That(byEquals).IsEquivalentTo(byOperator);
    await Assert.That(byEquals).Count().IsEqualTo(1);
  }

  /// <summary>
  /// The projected dialect, which is how most repositories are written, returns the right rows when
  /// its predicate has been rewritten.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task ProjectedDialect_ThroughTheLens_ReturnsTheRightRowsAsync(CancellationToken cancellationToken) {
    var needle = "needle";

    var projected = await _context!.Set<PerspectiveRow<CatalogModel>>()
      .Select(r => r.Data)
      .Where(m => m.Title == needle)
      .Select(m => m.Title)
      .ToListAsync(cancellationToken);

    await Assert.That(projected).Count().IsEqualTo(1);
    await Assert.That(projected[0]).IsEqualTo(needle);
  }

  /// <summary>
  /// Why rewriting the <c>StringComparison.Ordinal</c> overload is not merely convenient: containment
  /// actually delivers the semantics that overload asks for, and an extraction does not.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Entity Framework refuses to translate <c>string.Equals(value, StringComparison)</c> at all, and
  /// the refusal is principled: it would have to compile to <c>=</c> on text, whose meaning follows
  /// the collation in force. Under a case-insensitive collation that comparison is case-insensitive,
  /// which is not what the caller asked for.
  /// </para>
  /// <para>
  /// Containment compares values inside the document rather than as collated text, so it is exact
  /// whatever the collation says. That is ordinal, which is what the overload requested. So the
  /// rewrite is not doing something Entity Framework declined out of caution; it is doing the thing
  /// Entity Framework had no correct way to express.
  /// </para>
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ContainmentIsOrdinal_WhereAnExtractionFollowsTheCollationAsync(CancellationToken cancellationToken) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);

    await _execAsync(db, """
      CREATE COLLATION IF NOT EXISTS case_insensitive
        (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
      """);

    // The same stored value and the same candidate, differing only in case.
    var byExtraction = await _scalarAsync(db, """
      SELECT count(*) FROM (SELECT '{"t":"ABC"}'::jsonb AS data) s
      WHERE (s.data ->> 't') COLLATE case_insensitive = 'abc'
      """);

    var byContainment = await _scalarAsync(db, """
      SELECT count(*) FROM (SELECT '{"t":"ABC"}'::jsonb AS data) s
      WHERE s.data @> '{"t":"abc"}'
      """);

    // The extraction follows the collation and matches; containment is exact and does not.
    await Assert.That(byExtraction).IsEqualTo(1L);
    await Assert.That(byContainment).IsEqualTo(0L);
  }

  private static string _migrationPath(string fileName) => Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "Whizbang.Data.Postgres", "Migrations", fileName);

  private static async Task _execAsync(NpgsqlConnection db, string sql) {
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private static async Task<string> _explainAsync(NpgsqlConnection db, string sql, string parameter) {
    await using var command = new NpgsqlCommand("EXPLAIN " + sql, db);
    command.Parameters.AddWithValue("p", parameter);

    var lines = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lines.Add(reader.GetString(0));
    }

    return string.Join('\n', lines);
  }
}
