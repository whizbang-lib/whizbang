// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Runs stream-id filters against PostgreSQL (#2302): the projected point read finds its row, and the added
/// primary-key test never returns a row the original filter would not.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#stream-id-filters</docs>
[Category("Integration")]
[Category("Shard1")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "#1954: the [After(Test)] hook disposes them, the fixture's one teardown; a disposable fixture would be disposed by TUnit a second time.")]
public class StreamIdPrimaryKeyIntegrationTests {
  private const string TABLE = "wh_per_stream_id_pk_test";
  private const string DRAFT_TABLE = "wh_per_stream_id_pk_draft_test";

  private static readonly Guid _matchingId = new("018f2a6e-0000-7000-8000-000000000001");
  private static readonly Guid _otherRowId = new("018f2a6e-0000-7000-8000-000000000002");
  private static readonly Guid _otherDocumentId = new("018f2a6e-0000-7000-8000-000000000003");

  static StreamIdPrimaryKeyIntegrationTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;
  private OrderDbContext? _context;

  /// <summary>A model whose stream id is registered.</summary>
  [SuppressIndexAdvisory("test fixture; its table is created by the test")]
  public class OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }

    public string Title { get; init; } = string.Empty;
  }

  /// <summary>A model whose registered stream id is nullable, so a document can hold none.</summary>
  [SuppressIndexAdvisory("test fixture; its table is created by the test")]
  public class DraftModel {
    [StreamId]
    public Guid? DraftId { get; init; }

    public string Title { get; init; } = string.Empty;
  }

  private sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<OrderModel>>(entity => _mapRow(entity, TABLE));
      modelBuilder.Entity<PerspectiveRow<DraftModel>>(entity => _mapRow(entity, DRAFT_TABLE));
    }

    private static void _mapRow<TModel>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PerspectiveRow<TModel>> entity, string table)
        where TModel : class {
      entity.ToTable(table);
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at");
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
      entity.Property(e => e.Version).HasColumnName("version");
      entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb");
      entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
      entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb");
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _testDatabaseName = (await PerTestDatabaseFactory.CreateAsync("stream_id_pk")).Name;

    var connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    }.ConnectionString;

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.EnableDynamicJson();
    _dataSource = dataSourceBuilder.Build();

    // This class's own model only: the registry is keyed by model type, so this touches nothing another test reads.
    StreamIdRegistry.Register<OrderModel>(nameof(OrderModel.OrderId));
    StreamIdRegistry.Register<DraftModel>(nameof(DraftModel.DraftId));

    _context = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>()
      .UseNpgsql(_dataSource)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    foreach (var table in new[] { TABLE, DRAFT_TABLE }) {
      await _createTableAsync(connection, table);
    }

    // One row that keeps the [StreamId] contract, and one whose document names a different id than its row.
    await _seedAsync(TABLE, _matchingId, new OrderModel { OrderId = _matchingId, Title = "matching" });
    await _seedAsync(TABLE, _otherRowId, new OrderModel { OrderId = _otherDocumentId, Title = "mismatched" });

    // One draft with a stream id, and one whose document holds none.
    await _seedAsync(DRAFT_TABLE, _matchingId, new DraftModel { DraftId = _matchingId, Title = "assigned" });
    await _seedAsync(DRAFT_TABLE, _otherRowId, new DraftModel { DraftId = null, Title = "unassigned" });
  }

  private static async Task _createTableAsync(NpgsqlConnection connection, string table) =>
    await connection.ExecuteAsync($"""
      CREATE TABLE {table} (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ,
        sys_updated_at TIMESTAMPTZ,
        version INTEGER NOT NULL
      );
      """);

  private async Task _seedAsync<TModel>(string table, Guid rowId, TModel model) where TModel : class {
    var metadata = new PerspectiveMetadata { EventType = "OrderCreated", EventId = rowId.ToString(), Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
    await new PostgresUpsertStrategy().UpsertPerspectiveRowWithPhysicalFieldsAsync(
      _context!, table, rowId, model, metadata, new PerspectiveScope(), new Dictionary<string, object?>());
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_context != null) {
      await _context.DisposeAsync();
      _context = null;
    }

    if (_dataSource != null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }

    if (_testDatabaseName != null) {
      await PerTestDatabaseFactory.DropAsync(_testDatabaseName);
      _testDatabaseName = null;
    }
  }

  private IQueryable<OrderModel> _models() =>
    _context!.Set<PerspectiveRow<OrderModel>>().Select(r => r.Data).OrderBy(x => x.OrderId);

  [Test]
  public async Task ProjectedPointRead_FindsTheRowAsync() {
    var id = _matchingId;
    var found = await _models().Where(x => x.OrderId == id).ToListAsync();

    await Assert.That(found.Count).IsEqualTo(1);
    await Assert.That(found[0].Title).IsEqualTo("matching");
  }

  [Test]
  public async Task ProjectedSetRead_FindsEachRowAsync() {
    var ids = new List<Guid> { _matchingId, Guid.Empty };
    var found = await _models().Where(x => ids.Contains(x.OrderId)).ToListAsync();

    await Assert.That(found.Count).IsEqualTo(1);
    await Assert.That(found[0].Title).IsEqualTo("matching");
  }

  /// <summary>
  /// The primary key is never null, so a null comparison, whether written as null or held in a variable or a
  /// set, still finds the document with no stream id, as it did before the key was added.
  /// </summary>
  [Test]
  public async Task NullComparison_StillFindsTheDocumentWithNoStreamIdAsync() {
    Guid? nothing = null;
    var withNull = new List<Guid?> { null };
    var drafts = _context!.Set<PerspectiveRow<DraftModel>>().Select(r => r.Data);

    var byLiteral = await drafts.Where(x => x.DraftId == null).ToListAsync();
    var byVariable = await drafts.Where(x => x.DraftId == nothing).ToListAsync();
    var bySet = await drafts.Where(x => withNull.Contains(x.DraftId)).ToListAsync();
    Guid? assigned = _matchingId;
    var byValue = await drafts.Where(x => x.DraftId == assigned).ToListAsync();

    await Assert.That(byLiteral.Select(d => d.Title)).IsEquivalentTo(["unassigned"]);
    await Assert.That(byVariable.Select(d => d.Title)).IsEquivalentTo(["unassigned"]);
    await Assert.That(bySet.Select(d => d.Title)).IsEquivalentTo(["unassigned"]);
    await Assert.That(byValue.Select(d => d.Title)).IsEquivalentTo(["assigned"]);
  }

  /// <summary>
  /// A row whose document stream id disagrees with its row id is found by neither: the row id fails the
  /// document test, which is kept, and the document id fails the primary-key test. No other row is returned.
  /// </summary>
  [Test]
  public async Task RowBreakingTheContract_IsNotReturnedForEitherIdAsync() {
    var rowId = _otherRowId;
    var documentId = _otherDocumentId;

    var byRowId = await _models().Where(x => x.OrderId == rowId).ToListAsync();
    var byDocumentId = await _models().Where(x => x.OrderId == documentId).ToListAsync();

    await Assert.That(byRowId).IsEmpty();
    await Assert.That(byDocumentId).IsEmpty();
  }

  /// <summary>
  /// A read of the stream id reads the row's id (#2423): a row whose document names a different id is read back with
  /// its row id, the value the runner keys it by.
  /// </summary>
  [Test]
  public async Task ProjectedStreamIdRead_ReadsTheRowIdAsync() {
    var read = await _models().Select(x => new { x.OrderId, x.Title }).ToListAsync();

    await Assert.That(read.Single(r => r.Title == "matching").OrderId).IsEqualTo(_matchingId);
    await Assert.That(read.Single(r => r.Title == "mismatched").OrderId).IsEqualTo(_otherRowId);
  }

  /// <summary>
  /// A query that reads and sorts only the stream id plans no read of the document (#2423): the plan's output and sort
  /// key name the row's id alone.
  /// </summary>
  [Test]
  public async Task ProjectedStreamIdOnly_PlanReadsNoDocumentAsync() {
    var sql = _models().Select(x => x.OrderId).ToQueryString();

    await using var connection = _dataSource!.CreateConnection();
    await connection.OpenAsync();
    await using var explain = new NpgsqlCommand($"EXPLAIN VERBOSE {sql}", connection);
    var lines = new List<string>();
    await using (var reader = await explain.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        lines.Add(reader.GetString(0));
      }
    }
    var plan = string.Join('\n', lines);

    await Assert.That(plan).Contains("Output: ");
    await Assert.That(plan).DoesNotContain("data");
  }
}
