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
public class StreamIdPrimaryKeyIntegrationTests : IAsyncDisposable {
  private const string TABLE = "wh_per_stream_id_pk_test";

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

  private sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<OrderModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
        entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb");
        entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb");
      });
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

    _context = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>()
      .UseNpgsql(_dataSource)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync($"""
      CREATE TABLE {TABLE} (
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

    // One row that keeps the [StreamId] contract, and one whose document names a different id than its row.
    await _seedAsync(_matchingId, new OrderModel { OrderId = _matchingId, Title = "matching" });
    await _seedAsync(_otherRowId, new OrderModel { OrderId = _otherDocumentId, Title = "mismatched" });
  }

  private async Task _seedAsync(Guid rowId, OrderModel model) {
    var metadata = new PerspectiveMetadata { EventType = "OrderCreated", EventId = rowId.ToString(), Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
    await new PostgresUpsertStrategy().UpsertPerspectiveRowWithPhysicalFieldsAsync(
      _context!, TABLE, rowId, model, metadata, new PerspectiveScope(), new Dictionary<string, object?>());
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

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
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
}
