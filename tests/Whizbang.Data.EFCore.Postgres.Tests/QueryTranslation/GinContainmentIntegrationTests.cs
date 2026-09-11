using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
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
  }

  /// <summary>Marker whose translation rewrites a member comparison into a containment test.</summary>
  public static bool GinEquals(string member, string value) => throw new NotSupportedException();

  /// <summary>Marker for a Guid member.</summary>
  public static bool GinEquals(Guid member, Guid value) => throw new NotSupportedException();

  private static readonly bool[] _noPropagate = [false, false];

  /// <summary>
  /// Rewrites <c>GinEquals(row.Data.Field, value)</c> into <c>data @&gt; jsonb_build_object('Field', value)</c>.
  /// The column comes out of the translated argument, which is a <see cref="JsonScalarExpression"/>
  /// carrying both the column and the JSON path.
  /// </summary>
  private static SqlExpression _toContainment(IReadOnlyList<SqlExpression> args) {
    if (args[0] is not JsonScalarExpression json) {
      return args[0];
    }

    var column = json.Json;
    SqlExpression payload = args[1];

    for (var i = json.Path.Count - 1; i >= 0; i--) {
      var name = json.Path[i].PropertyName;
      if (name is null) {
        return args[0];
      }

      payload = new SqlFunctionExpression(
        "jsonb_build_object",
        [new SqlConstantExpression(name, typeof(string), StringTypeMapping.Default), payload],
        nullable: true,
        argumentsPropagateNullability: _noPropagate,
        typeof(string),
        column.TypeMapping!);
    }

    var pgBinary = System.Reflection.Assembly.Load("Npgsql.EntityFrameworkCore.PostgreSQL")
      .GetType("Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal.PgUnknownBinaryExpression")!;
    var ctor = pgBinary.GetConstructors()[0];
    var ps = ctor.GetParameters();
    var values = new object?[ps.Length];
    for (var i = 0; i < ps.Length; i++) {
      values[i] = ps[i].Name switch {
        "left" => column,
        "right" => payload,
        "binaryOperator" => "@>",
        "type" => typeof(bool),
        "typeMapping" => BoolTypeMapping.Default,
        _ => null,
      };
    }

    return (SqlExpression)ctor.Invoke(values);
  }

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

      foreach (var method in typeof(GinContainmentIntegrationTests)
                 .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                 .Where(m => m.Name == nameof(GinEquals))) {
        modelBuilder.HasDbFunction(method).HasTranslation(_toContainment);
      }
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
    }

    _context = new GinDbContext(new DbContextOptionsBuilder<GinDbContext>()
      .UseNpgsql(_connectionString)
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
      .Where(r => GinEquals(r.Data.Title, needle))
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
      .Where(r => GinEquals(r.Data.TenantId, tenant))
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
