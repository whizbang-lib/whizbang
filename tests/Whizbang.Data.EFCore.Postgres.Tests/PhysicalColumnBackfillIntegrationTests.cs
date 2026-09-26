using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Generators.Shared.Models;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A physical column added to a table that already holds rows is filled with exactly the value the writer
/// would have put there.
/// </summary>
/// <remarks>
/// <para>
/// Promoting a field to a column changes where queries read it: the translator redirects the property to
/// the column. Rows written before the column existed have the value only in the document, so without a
/// backfill every filter and sort on the field reads NULL for them, silently.
/// </para>
/// <para>
/// The proof is a round trip through the real writer: each row is written by the atomic upsert with its
/// physical values, the columns are emptied, and the backfill must restore every one exactly. That holds
/// the backfill to the stored form of each type (a date or time is a microsecond count in the document,
/// not a rendering) rather than to what the backfill's author believed it to be.
/// </para>
/// </remarks>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public class PhysicalColumnBackfillIntegrationTests : IAsyncDisposable {
  static PhysicalColumnBackfillIntegrationTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private const string TABLE = "wh_per_physical_backfill";
  private string? _testDatabaseName;
  private string _connectionString = null!;

  // (property, column, CLR type as the generator renders it, column type)
  private static readonly (string Property, string Column, string Type, string ColumnType)[] _fields = [
    ("Text", "text_value", "global::System.String", "TEXT"),
    ("Ref", "ref", "global::System.Guid", "UUID"),
    ("Count", "count", "global::System.Int32", "INTEGER"),
    ("Big", "big", "global::System.Int64", "BIGINT"),
    ("Small", "small", "global::System.Int16", "SMALLINT"),
    ("Flag", "flag", "global::System.Boolean", "BOOLEAN"),
    ("Amount", "amount", "global::System.Decimal", "NUMERIC"),
    ("Ratio", "ratio", "global::System.Double", "DOUBLE PRECISION"),
    ("Weight", "weight", "global::System.Single", "REAL"),
    ("At", "at", "global::System.DateTime", "TIMESTAMPTZ"),
    ("AtOffset", "at_offset", "global::System.DateTimeOffset", "TIMESTAMPTZ"),
    ("Day", "day", "global::System.DateOnly", "DATE"),
    ("Clock", "clock", "global::System.TimeOnly", "TIME"),
    ("Maybe", "maybe", "global::System.Int32?", "INTEGER"),
  ];

  private static PhysicalFieldInfo _info((string Property, string Column, string Type, string ColumnType) f) =>
    new(f.Property, f.Column, f.Type, IsIndexed: false, IsUnique: false, MaxLength: null, IsVector: false,
      VectorDimensions: null, VectorDistanceMetric: null, VectorIndexType: null, VectorIndexLists: null);

  private sealed class TestDbContext(DbContextOptions<TestDbContext> opts) : DbContext(opts) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<PhysicalBackfillModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property<DateTime?>("sys_created_at").HasColumnName("sys_created_at");
        entity.Property<DateTime?>("sys_updated_at").HasColumnName("sys_updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
        entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb");
        entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb");
        entity.Property<string?>("text_value").HasColumnName("text_value");
        entity.Property<Guid?>("ref").HasColumnName("ref");
        entity.Property<int?>("count").HasColumnName("count");
        entity.Property<long?>("big").HasColumnName("big");
        entity.Property<short?>("small").HasColumnName("small");
        entity.Property<bool?>("flag").HasColumnName("flag");
        entity.Property<decimal?>("amount").HasColumnName("amount");
        entity.Property<double?>("ratio").HasColumnName("ratio");
        entity.Property<float?>("weight").HasColumnName("weight");
        entity.Property<DateTime?>("at").HasColumnName("at").HasColumnType("timestamptz");
        entity.Property<DateTimeOffset?>("at_offset").HasColumnName("at_offset").HasColumnType("timestamptz");
        entity.Property<DateOnly?>("day").HasColumnName("day");
        entity.Property<TimeOnly?>("clock").HasColumnName("clock");
        entity.Property<int?>("maybe").HasColumnName("maybe");
      });
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _testDatabaseName = $"test_{Guid.NewGuid():N}";
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true,
    }.ConnectionString;
    await using var ctx = _context();
    await ctx.Database.EnsureCreatedAsync();
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>
      Generated.PerspectivePersistenceJsonContext.CreateOptions(
        Generated.MessageJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
    if (!string.IsNullOrEmpty(_testDatabaseName)) {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName} WITH (FORCE)");
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private TestDbContext _context() => new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(_connectionString).Options);

  private async Task _writeAsync(PhysicalBackfillModel m) {
    await using var ctx = _context();
    // The runner hands the writer each physical column's raw CLR value, keyed by column name.
    var physical = new Dictionary<string, object?> {
      ["text_value"] = m.Text,
      ["ref"] = m.Ref,
      ["count"] = m.Count,
      ["big"] = m.Big,
      ["small"] = m.Small,
      ["flag"] = m.Flag,
      ["amount"] = m.Amount,
      ["ratio"] = m.Ratio,
      ["weight"] = m.Weight,
      ["at"] = m.At,
      ["at_offset"] = m.AtOffset,
      ["day"] = m.Day,
      ["clock"] = m.Clock,
      ["maybe"] = m.Maybe,
    };
    await new PostgresUpsertStrategy().UpsertPerspectiveRowWithPhysicalFieldsAsync(
      ctx, TABLE, m.Id, m, new PerspectiveMetadata { EventType = "Created", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
      new PerspectiveScope(), physical);
  }

  private static async Task<Dictionary<Guid, string>> _columnsAsync(NpgsqlConnection conn) {
    var select = string.Join(" || '|' || ", _fields.Select(f => $"coalesce({f.Column}::text, '∅')"));
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT id, {select} FROM {TABLE}";
    await using var reader = await cmd.ExecuteReaderAsync();
    var result = new Dictionary<Guid, string>();
    while (await reader.ReadAsync()) {
      result[reader.GetGuid(0)] = reader.GetString(1);
    }
    return result;
  }

  private static PhysicalBackfillModel _sample(int seed, int? maybe) => new() {
    Id = Guid.CreateVersion7(),
    Text = $"text {seed} with 'quote'",
    Ref = Guid.CreateVersion7(),
    Count = -123 * seed,
    Big = 9_000_000_000_000L + seed,
    Small = (short)(-7 * seed),
    Flag = seed % 2 == 0,
    Amount = 1234.5678m * seed,
    Ratio = 0.1 + seed,
    Weight = 2.5f * seed,
    // Microsecond precision: what a timestamp holds, and what the stored form keeps.
    At = new DateTime(2026, 9, 25, 13, 45, 7, DateTimeKind.Utc).AddTicks(1234560 + (seed * 10)),
    AtOffset = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(9876540 + (seed * 10)),
    Day = new DateOnly(1969, 12, 31).AddDays(seed),
    Clock = new TimeOnly(23, 59, 58).Add(TimeSpan.FromTicks(123450)),
    Maybe = maybe,
  };

  [Test]
  public async Task Backfill_RestoresExactlyWhatTheWriterStored_ForEveryTypeAsync() {
    var rows = new[] { _sample(1, 42), _sample(2, null), _sample(3, 0) };
    foreach (var row in rows) {
      await _writeAsync(row);
    }
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    var written = await _columnsAsync(conn);
    await conn.ExecuteAsync($"UPDATE {TABLE} SET {string.Join(", ", _fields.Select(f => $"{f.Column} = NULL"))}");

    foreach (var field in _fields) {
      var sql = PhysicalColumnSql.Backfill(TABLE, _info(field));
      await Assert.That(sql).IsNotNull().Because($"{field.Type} is stored in a form the backfill can reproduce");
      await conn.ExecuteAsync(sql!);
    }

    var restored = await _columnsAsync(conn);
    foreach (var id in rows.Select(r => r.Id)) {
      await Assert.That(restored[id]).IsEqualTo(written[id])
        .Because("the backfilled columns must equal, value for value, what the writer put there");
    }
  }

  [Test]
  public async Task Backfill_LeavesAColumnThatAlreadyHasAValueAloneAsync() {
    var row = _sample(4, 7);
    await _writeAsync(row);
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync($"UPDATE {TABLE} SET count = 999");

    await conn.ExecuteAsync(PhysicalColumnSql.Backfill(TABLE, _info(_fields.Single(f => f.Property == "Count")))!);

    await Assert.That(await conn.ExecuteScalarAsync<int>($"SELECT count FROM {TABLE}")).IsEqualTo(999)
      .Because("the backfill fills gaps; a column the writer has since written is not overwritten from the document");
  }

  [Test]
  public async Task AddColumn_OnATableThatPredatesIt_AddsTheColumn_AndIsIdempotentAsync() {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync($"ALTER TABLE {TABLE} DROP COLUMN count");

    await conn.ExecuteAsync(PhysicalColumnSql.AddColumn(TABLE, "count", "INTEGER"));
    await conn.ExecuteAsync(PhysicalColumnSql.AddColumn(TABLE, "count", "INTEGER"));

    await Assert.That(await conn.ExecuteScalarAsync<string>(
      $"SELECT data_type FROM information_schema.columns WHERE table_name = '{TABLE}' AND column_name = 'count'")).IsEqualTo("integer");
  }
}
