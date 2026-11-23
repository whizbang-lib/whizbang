// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>A line of a strong-id rewrite fixture: an id inside a collection element.</summary>
public class StrongIdRewriteLine {
  public TestOrderId LineId { get; set; }
  public int Quantity { get; set; }
}

/// <summary>A model holding a strongly-typed id at the top level, nullable, nested and inside a collection.</summary>
[SuppressIndexAdvisory("rewrite fixture; rows are seeded in the old form and read back raw")]
public class StrongIdRewriteModel {
  [StreamId]
  public Guid Id { get; set; }

  public TestOrderId OrderId { get; set; }
  public TestOrderId? ParentId { get; set; }
  public List<StrongIdRewriteLine> Lines { get; set; } = [];
}

public record StrongIdRewriteNoted([property: StreamId] Guid Id) : IEvent;

[WhizbangPerspective("strong-id-rewrite")]
public class StrongIdRewriteProjection : IPerspectiveFor<StrongIdRewriteModel, StrongIdRewriteNoted> {
  public StrongIdRewriteModel Apply(StrongIdRewriteModel currentData, StrongIdRewriteNoted eventData) => currentData;
}

/// <summary>
/// The stored-form rewrite of a strongly-typed id an earlier release stored as <c>{"Value": ...}</c>: converted to the
/// UUID string at every depth through the phase the initializer runs, after which an id predicate reads every row;
/// the journal settles on the first pass that converts nothing (#2518).
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/StrongIdScalarRewrite.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/557_StrongIdsStoredAsUuidStrings.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StrongIdScalarRewriteIntegrationTests {
  private const long LOCK_ID = 192837467;
  private const int TIMEOUT_SECONDS = 60;
  private const string TABLE = "wh_per_strong_id_rewrite";

  private sealed class RewriteDbContext(DbContextOptions<RewriteDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<PerspectiveRow<StrongIdRewriteModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => {
          d.ToJson("data");
          d.ComplexCollection(x => x.Lines);
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
  }

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = (await PerTestDatabaseFactory.CreateAsync("id_rewrite")).Name;
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    var migrations = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public");
    await _executeAsync(migrations.GetMigration("176_StoredFormMigrations")!.Sql);
    await _executeAsync(migrations.GetMigration("557_StrongIdsStoredAsUuidStrings")!.Sql);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private async Task _executeAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return await command.ExecuteScalarAsync() is { } value and not DBNull ? value.ToString() ?? "<null>" : "<null>";
  }

  private async Task _createTableAsync() => await _executeAsync($$"""
    CREATE TABLE {{TABLE}} (
      id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb NOT NULL DEFAULT '{}', scope jsonb NOT NULL DEFAULT '{}',
      created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), version int NOT NULL DEFAULT 1)
    """);

  private async Task<Guid> _seedAsync(string data) {
    var id = (Guid)TrackedGuid.New();
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand($"INSERT INTO {TABLE} (id, data) VALUES ($1, $2::jsonb)", db);
    command.Parameters.AddWithValue(id);
    command.Parameters.AddWithValue(data);
    await command.ExecuteNonQueryAsync();
    return id;
  }

  private static RewriteDbContext _context(string connectionString = "Host=localhost;Database=probe;Username=u;Password=p") =>
    new(new DbContextOptionsBuilder<RewriteDbContext>()
      .UseNpgsql(connectionString, npgsql => npgsql.UseWhizbangFunctions())
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

  private static ImmutableArray<StrongIdScalarTable> _rewrites() {
    using var context = _context();
    return StrongIdScalarRewrite.ForModel(context.Model, PerspectiveDocumentSerialization.Options, "public");
  }

  private async Task<bool> _runAsync() =>
    await CanonicalTemporalRewritePhase.ApplyAsync(
      () => new NpgsqlConnection(_connectionString), LOCK_ID, [.. _rewrites().Select(t => (t.Name, t.Sql))], TIMEOUT_SECONDS);

  private async Task<string> _journalAsync(string column) =>
    await _scalarAsync($"SELECT {column} FROM wh_stored_form_migrations WHERE name = '{_rewrites().Single().Name}'");

  private async Task<JsonElement> _dataAsync(Guid id) =>
    JsonElement.Parse(await _scalarAsync($"SELECT data::text FROM {TABLE} WHERE id = '{id}'"));

  private static readonly Guid _order = Guid.Parse("01a203cc-54ef-7441-8295-5a6cdae706e6");
  private static readonly Guid _parent = Guid.Parse("01a203cc-54ef-7441-82a2-a1d1e91a4852");
  private static readonly Guid _line = Guid.Parse("01a203cc-54ef-7441-82c4-c06ddf3e5876");

  private static string _oldForm(string parent) =>
    $$"""{"OrderId":{"Value":"{{_order}}"},"ParentId":{{parent}},"Lines":[{"LineId":{"Value":"{{_line}}"},"Quantity":2}]}""";

  [Test]
  public async Task ThePaths_AreEveryIdTheModelHoldsAsync() {
    await using var context = _context();
    var rowType = context.Model.FindEntityType(typeof(PerspectiveRow<StrongIdRewriteModel>))!;

    var paths = StrongIdScalarRewrite.PathsOf(rowType, PerspectiveDocumentSerialization.Options);

    await Assert.That(string.Join(" ", paths.Select(p => p.ToString()).Order(StringComparer.Ordinal)))
      .IsEqualTo("data:Lines.[].LineId data:OrderId data:ParentId");
  }

  [Test]
  public async Task AnObjectFormId_IsRewrittenToTheUuidString_AndTheTableSettlesAsync() {
    await _createTableAsync();
    var old = await _seedAsync(_oldForm($$"""{"Value":"{{_parent}}"}"""));
    var nullParent = await _seedAsync(_oldForm("null"));
    var alreadyScalar = await _seedAsync($$"""{"OrderId":"{{_order}}","ParentId":null,"Lines":[]}""");

    await Assert.That(await _runAsync()).IsTrue();

    var data = await _dataAsync(old);
    await Assert.That(data.GetProperty("OrderId").GetString()).IsEqualTo(_order.ToString());
    await Assert.That(data.GetProperty("ParentId").GetString()).IsEqualTo(_parent.ToString());
    await Assert.That(data.GetProperty("Lines")[0].GetProperty("LineId").GetString()).IsEqualTo(_line.ToString());
    await Assert.That(data.GetProperty("Lines")[0].GetProperty("Quantity").GetInt32()).IsEqualTo(2);
    await Assert.That((await _dataAsync(nullParent)).GetProperty("ParentId").ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(await _journalAsync("rows_converted")).IsEqualTo("5")
      .Because("the old row is updated once per path (three), the null-parent row twice, and the row already in the scalar form not at all");
    await Assert.That((await _dataAsync(alreadyScalar)).GetProperty("OrderId").GetString()).IsEqualTo(_order.ToString());
    await Assert.That(await _journalAsync("settled_at")).IsEqualTo("<null>");

    await Assert.That(await _runAsync()).IsTrue();

    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>")
      .Because("the second pass converted nothing, so the table is settled and never scanned again");
  }

  /// <summary>Once rewritten, an id predicate's uuid cast reads every row, where before it failed on the object form.</summary>
  [Test]
  public async Task AfterTheRewrite_AnIdPredicateReadsEveryRowAsync() {
    await _createTableAsync();
    await _seedAsync(_oldForm("null"));
    await using var context = _context(_connectionString);
    var orderId = new TestOrderId(_order);
    var query = context.Set<PerspectiveRow<StrongIdRewriteModel>>().AsNoTracking().Where(r => r.Data.OrderId == orderId);

    await Assert.That(async () => await query.CountAsync()).Throws<PostgresException>();

    await _runAsync();

    await Assert.That(await query.CountAsync()).IsEqualTo(1);
  }

  [Test]
  public async Task ATableThatDoesNotExistYet_IsSettledAtOnceAsync() {
    await Assert.That(await _runAsync()).IsTrue();

    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>");
  }
}
