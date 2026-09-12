using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Generators.Shared.Models;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// That an index built the way the generator builds it is the index the planner then uses, per type.
/// </summary>
/// <remarks>
/// <para>
/// This is the assertion the whole declared-index feature rests on, and it cannot be made any other
/// way. An index over a JSON extraction is only used when its expression is the expression the query
/// produces; an index over a different one is simply a different index, and the planner ignores it
/// while everything still returns correct rows. So the failure mode is a silent sequential scan,
/// which is what the feature exists to remove.
/// </para>
/// <para>
/// The expression under test comes from <see cref="JsonIndexSql.Expression"/> rather than being
/// written out here, so a change to what the generator emits is caught by this rather than by a
/// consumer noticing their queries got slower. The query is written in LINQ for the same reason: what
/// matters is the cast Entity Framework chooses, not the one this test would have guessed.
/// </para>
/// <para>
/// A range is used as the probe rather than an equality, because a range is what the document index
/// cannot answer at all and therefore what a declared index is for.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class JsonIndexUsageTests : IAsyncDisposable {
  private const string TABLE = "wh_per_json_index";

  // Enough rows that a sequential scan is genuinely the worse plan for a selective range. Without
  // that the planner may prefer a scan for reasons that have nothing to do with the index.
  private const int ROWS = 40_000;

  public enum Grade { Low = 0, Mid = 1, High = 2 }

  /// <summary>Almost no random identifier sorts above this, which makes a range over one selective.</summary>
  private static readonly Guid _nearMaximumReference = new("ffffffff-ffff-ffff-ffff-fffffffffff0");

  [SuppressIndexAdvisory("this fixture exists to compare an indexed extraction with an unindexed one")]
  public class IndexModel {
    public string Title { get; init; } = string.Empty;
    public Guid Reference { get; init; }
    public bool Flag { get; init; }
    public short Small { get; init; }
    public int Rank { get; init; }
    public long Big { get; init; }
    public decimal Money { get; init; }
    public double Dbl { get; init; }
    public float Flt { get; init; }
    public Grade Level { get; init; }
  }

  private sealed class IndexDbContext(DbContextOptions<IndexDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<IndexModel>>(entity => {
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
  private IndexDbContext? _context;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"json_index_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
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
        CREATE INDEX idx_json_index_data_gin ON {TABLE} USING gin (data);
        """);
    }

    _context = new IndexDbContext(new DbContextOptionsBuilder<IndexDbContext>()
      .UseNpgsql(_connectionString)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

    // One row through the real mapping, so the document's shape and the key names are the shipped
    // ones, then the bulk in SQL reusing it so the table is big enough to plan against.
    _context.Add(new PerspectiveRow<IndexModel> {
      Id = Guid.NewGuid(),
      Data = new IndexModel {
        Title = "aaa",
        Reference = Guid.NewGuid(),
        Flag = true,
        Small = 1,
        Rank = 1,
        Big = 1L,
        Money = 1.5m,
        Dbl = 1.5d,
        Flt = 1.5f,
        Level = Grade.Low,
      },
      Metadata = new PerspectiveMetadata(),
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1,
    });

    await _context.SaveChangesAsync();

    await using (var db = new NpgsqlConnection(_connectionString)) {
      await db.OpenAsync();
      await _execAsync(db, $"""
        INSERT INTO {TABLE} (id, created_at, updated_at, version, data, metadata, scope)
        SELECT gen_random_uuid(), now(), now(), 1,
               jsonb_build_object(
                 'Title', 'title-' || lpad(g::text, 8, '0'),
                 'Reference', gen_random_uuid()::text,
                 'Flag', (g % 2 = 0),
                 'Small', (g % 30000),
                 'Rank', g,
                 'Big', g * 1000,
                 'Money', (g || '.25')::numeric,
                 'Dbl', g + 0.5,
                 'Flt', g + 0.25,
                 'Level', (g % 3)),
               shape.metadata, shape.scope
        FROM generate_series(1, {ROWS}) g
        CROSS JOIN (SELECT metadata, scope FROM {TABLE} LIMIT 1) shape;
        """);
    }
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_context is not null) {
      await _context.DisposeAsync();
      _context = null;
    }

    try {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
      await drop.ExecuteNonQueryAsync();
    } catch (NpgsqlException) {
      // Per-test database on a shared container; a failed drop is not a test failure.
    }

    GC.SuppressFinalize(this);
  }

  private static async Task _execAsync(NpgsqlConnection db, string sql) {
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>The filter for a case, and the key and cast the generator would index it with.</summary>
  private static (Expression<Func<PerspectiveRow<IndexModel>, bool>> Filter, string Key, JsonIndexCast Cast)
    _caseFor(string type) => type switch {
      "string" => (r => r.Data.Title.CompareTo("title-00030000") > 0, "Title", JsonIndexCast.None),
      "short" => (r => r.Data.Small > (short)29990, "Small", JsonIndexCast.Int2),
      "int" => (r => r.Data.Rank > 39990, "Rank", JsonIndexCast.Int4),
      "long" => (r => r.Data.Big > 39990000L, "Big", JsonIndexCast.Int8),
      "decimal" => (r => r.Data.Money > 39990m, "Money", JsonIndexCast.Numeric),
      "double" => (r => r.Data.Dbl > 39990d, "Dbl", JsonIndexCast.Float8),
      "float" => (r => r.Data.Flt > 39990f, "Flt", JsonIndexCast.Float4),
      "enum" => (r => r.Data.Level > Grade.Mid, "Level", JsonIndexCast.Int4),
      // An identifier compares through CompareTo, which is how a cursor page over one is written.
      // The bound is near the top of the range on purpose: the seeded references are random, so a
      // comparison against the empty identifier matches every row and a sequential scan is then
      // correctly the cheaper plan. That would fail this test for a reason that has nothing to do
      // with the index expression being right.
      "guid" => (r => r.Data.Reference.CompareTo(_nearMaximumReference) > 0, "Reference", JsonIndexCast.Uuid),
      _ => throw new InvalidOperationException(type),
    };

  /// <summary>
  /// The index the generator would build for a field is the one the planner uses for a range over it.
  /// </summary>
  /// <remarks>
  /// The cast is asserted against the generated SQL before the plan is, because the two failures are
  /// different and the second is much harder to read. If the index expression and the query's cast
  /// disagree, the plan is a sequential scan and the message would only say "no index used"; naming
  /// the mismatch first says why.
  /// </remarks>
  [Test]
  [Timeout(180000)]
  [Arguments("string")]
  [Arguments("short")]
  [Arguments("int")]
  [Arguments("long")]
  [Arguments("decimal")]
  [Arguments("double")]
  [Arguments("float")]
  [Arguments("enum")]
  [Arguments("guid")]
  public async Task TheGeneratedIndexExpression_IsTheOneAQueryUsesAsync(
    string type, CancellationToken cancellationToken) {
    var (filter, key, cast) = _caseFor(type);
    var expression = JsonIndexSql.Expression("data", key, cast);
    var indexName = $"idx_json_{key.ToLowerInvariant()}";

    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);
    await _execAsync(db, $"CREATE INDEX {indexName} ON {TABLE} ({expression})");
    await _execAsync(db, $"ANALYZE {TABLE}");

    // The cast the query actually chose has to be the cast the index was built with, or the index is
    // over a different expression and can never be matched.
    var sql = _context!.Set<PerspectiveRow<IndexModel>>().Where(filter).ToQueryString();
    var storeType = JsonIndexSql.StoreType(cast);
    if (storeType is null) {
      await Assert.That(sql).Contains($"data ->> '{key}'", StringComparison.Ordinal)
        .Because("a text field is compared without a cast, so the index must not add one");
    } else {
      await Assert.That(sql).Contains($"CAST(w.data ->> '{key}' AS {storeType})", StringComparison.Ordinal)
        .Because($"the index was built over {expression}, so the query has to produce the same cast");
    }

    var plan = new List<string>();
    await using (var explain = new NpgsqlCommand($"EXPLAIN {sql.Replace("@__", "$", StringComparison.Ordinal)}", db)) {
      // The compiled SQL carries no parameters for these cases, every value being a constant.
      await using var reader = await explain.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) {
        plan.Add(reader.GetString(0).Trim());
      }
    }

    await Assert.That(string.Join(" | ", plan)).Contains(indexName, StringComparison.Ordinal)
      .Because($"a range over a {type} field has to be answered from the index the generator builds");
  }
}
