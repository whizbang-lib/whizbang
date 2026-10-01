#pragma warning disable CA1707

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Collective;
using Whizbang.Data.Postgres.Collective;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Ordering comparisons in a collective <c>Where</c> through the EF Core applier, against a real Postgres: the twin of
/// the Dapper scenarios. Each scenario applies the collective live and replays the same spec in memory against the
/// models the rows hold, and the two must select the same rows: a number that orders differently as text, a JSON
/// null and a missing key (with and without <c>!</c> and <c>??</c>), a temporal stored as its microsecond count, and a
/// physical column.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>src/Whizbang.Data.Postgres/Collective/CollectivePredicateSqlCompiler.cs</tests>
/// <tests>src/Whizbang.Data.EFCore.Postgres/Collective/EFCoreCollectiveAdapter.cs</tests>
[Category("Integration")]
[Category("CollectiveEvents")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class CollectiveOrderingIntegrationTests : IAsyncDisposable {
  private const string TABLE = "wh_per_ranked";
  private static readonly DateTimeOffset _t0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly Guid _a = Guid.Parse("00000000-0000-7000-8000-00000000000a");
  private static readonly Guid _b = Guid.Parse("00000000-0000-7000-8000-00000000000b");
  private static readonly Guid _c = Guid.Parse("00000000-0000-7000-8000-00000000000c");

  static CollectiveOrderingIntegrationTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
    PerspectivePhysicalFieldRegistry.Register(typeof(RankedModel), nameof(RankedModel.Rank), "rank", FieldStorageMode.Split);
  }

  private string? _databaseName;
  private string _connectionString = null!;
  private NpgsqlDataSource? _dataSource;

  [Test]
  public async Task Ordering_OnANumberThatOrdersDifferentlyAsText_ComparesNumericallyAsync() =>
    await _assertLiveMatchesReplayAsync(r => r.Data.Ordinal < 9, _a);

  [Test]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1940:Boolean checks should not be inverted", Justification = "The negated ordering is the case under test.")]
  public async Task Ordering_NegatedOverANullableMember_SelectsTheNullAndTheMissingKeyAsync() =>
#pragma warning disable RCS1068 // the negation of an ordering is the shape under test
    await _assertLiveMatchesReplayAsync(r => !(r.Data.MaybeOrdinal < 5), _b, _c);
#pragma warning restore RCS1068

  [Test]
  public async Task Ordering_Coalesced_CountsTheMissingKeyAsTheDefaultAsync() =>
    await _assertLiveMatchesReplayAsync(r => (r.Data.MaybeOrdinal ?? 0) < 5, _a, _b, _c);

  [Test]
  public async Task Ordering_OnAStoredInstant_ComparesTheMicrosecondCountAsync() =>
    await _assertLiveMatchesReplayAsync(r => r.Data.ActivatedAt > _t0, _b, _c);

  [Test]
  public async Task Ordering_OnAPhysicalColumn_ComparesTheColumnAsync() =>
    await _assertLiveMatchesReplayAsync(r => r.Data.Rank > 2, _b, _c);

  // ── Scenario runner ─────────────────────────────────────────────────────────────────────────

  private async Task _assertLiveMatchesReplayAsync(Expression<Func<PerspectiveRow<RankedModel>, bool>> where, params Guid[] expected) {
    var models = await _seedAsync();
    var spec = new RankedSpec(s => s.SetProperty(m => m.Title, "hit"), where);
    var entry = new CollectiveApplyEntry(
      typeof(RankedModel), typeof(RankedCollectiveEvent), typeof(SpecHandler), "Apply",
      CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq, (_, _, _) => spec);

    await using (var ctx = _newContext()) {
      await CollectiveEventApplier<RankedModel>.ApplyAsync(
        entry, new SpecHandler(), new RankedCollectiveEvent { Scope = new TenantCollectiveScope("t-A") },
        new TenantCollectiveScopeResolver(), ctx, Guid.NewGuid(), CollectiveApplyOptions.Default);
    }

    await using var conn = await _openAsync();
    var live = (await conn.QueryAsync<Guid>($"SELECT id FROM {TABLE} WHERE data->>'Title' = 'hit' ORDER BY id")).ToList();
    var replayed = models
      .Where(m => CollectiveInMemoryEvaluator<RankedModel>.Matches(spec, m.Key, m.Value))
      .Select(m => m.Key)
      .Order()
      .ToList();

    await Assert.That(live).IsEquivalentTo(expected);
    await Assert.That(replayed).IsEquivalentTo(live)
      .Because("The in-memory replay must select exactly the rows the live apply selected.");
  }

  private async Task<Dictionary<Guid, RankedModel>> _seedAsync() {
    var t1 = _t0.AddDays(1);
    var t2 = _t0.AddDays(2);
    await using var conn = await _openAsync();
    await _insertAsync(conn, _a, 1, $"{{\"Ordinal\": 3, \"MaybeOrdinal\": 3, \"ActivatedAt\": {_us(_t0)}, \"Title\": \"a\"}}");
    await _insertAsync(conn, _b, 5, $"{{\"Ordinal\": 10, \"MaybeOrdinal\": null, \"ActivatedAt\": {_us(t1)}, \"Title\": \"b\"}}");
    await _insertAsync(conn, _c, 3, $"{{\"Ordinal\": 9, \"ActivatedAt\": {_us(t2)}, \"Title\": \"c\"}}");
    return new Dictionary<Guid, RankedModel> {
      [_a] = new() { Ordinal = 3, MaybeOrdinal = 3, ActivatedAt = _t0, Rank = 1, Title = "a" },
      [_b] = new() { Ordinal = 10, ActivatedAt = t1, Rank = 5, Title = "b" },
      [_c] = new() { Ordinal = 9, ActivatedAt = t2, Rank = 3, Title = "c" },
    };
  }

  private static string _us(DateTimeOffset value) =>
    CanonicalTemporalFormat.ToEpochMicroseconds(value).ToString(System.Globalization.CultureInfo.InvariantCulture);

  private static Task _insertAsync(NpgsqlConnection conn, Guid id, int rank, string data) =>
    conn.ExecuteAsync($$"""
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, version, rank)
      VALUES (@id, @data::jsonb, '{}'::jsonb, '{"t": "t-A"}'::jsonb, now(), now(), 1, @rank)
      """, new { id, data, rank });

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    return conn;
  }

  // ── Setup / teardown ──────────────────────────────────────────────────────────────────────────

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"test_collective_ordering_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await admin.ExecuteAsync($"CREATE DATABASE {_databaseName}");
    }
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true,
    }.ConnectionString;

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
    // Reflection-based options: the fixture model is a nested test type no generated JSON context covers.
    dataSourceBuilder.ConfigureJsonOptions(new System.Text.Json.JsonSerializerOptions());
    dataSourceBuilder.EnableDynamicJson();
    _dataSource = dataSourceBuilder.Build();

    await using var conn = await _openAsync();
    await conn.ExecuteAsync($"""
      CREATE TABLE {TABLE} (
        id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, version INTEGER NOT NULL,
        rank INTEGER NOT NULL DEFAULT 0);
      """);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_dataSource is not null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }
    if (_databaseName is not null) {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
      _databaseName = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private RankedDbContext _newContext() {
    var options = new DbContextOptionsBuilder<RankedDbContext>()
      .UseNpgsql(_dataSource!)
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;
    return new RankedDbContext(options);
  }

  private sealed class RankedDbContext(DbContextOptions<RankedDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<RankedModel>>(e => {
        e.ToTable(TABLE);
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id");
        e.Property(x => x.Data).HasColumnName("data").HasColumnType("jsonb");
        e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        e.Property(x => x.Scope).HasColumnName("scope").HasColumnType("jsonb");
        e.Property(x => x.CreatedAt).HasColumnName("created_at");
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        e.Property(x => x.Version).HasColumnName("version");
        e.Property<int>("rank").HasColumnName("rank");
      });
    }
  }

  // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

  [SuppressIndexAdvisory("test fixture; the table holds three rows")]
  [PerspectiveStorage(FieldStorageMode.Split)]
  internal sealed class RankedModel {
    public long Ordinal { get; set; }
    public long? MaybeOrdinal { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    [PhysicalField] public int Rank { get; set; }
    public string Title { get; set; } = "";
  }

  private sealed record RankedSpec(
      Expression<Action<ICollectiveSetters<RankedModel>>> Setters,
      Expression<Func<PerspectiveRow<RankedModel>, bool>>? Where = null) : ICollectiveSpec<RankedModel>;

  internal sealed record RankedCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  private sealed class SpecHandler;
}
