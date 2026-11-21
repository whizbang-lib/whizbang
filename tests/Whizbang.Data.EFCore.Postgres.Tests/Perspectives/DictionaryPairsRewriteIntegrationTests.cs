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

/// <summary>An enumeration used as a dictionary key: stored by its number, written by an earlier release by its name.</summary>
public enum DictionaryRewriteColor {
  Red = 1,
  Green = 2,
}

/// <summary>A model holding a dictionary under every key kind the rewrite converts, and one it leaves for the reader.</summary>
[SuppressIndexAdvisory("rewrite fixture; rows are seeded in the old form and read back raw")]
public class DictionaryRewriteModel {
  [StreamId]
  public Guid Id { get; set; }

  public Dictionary<string, int> ByName { get; set; } = [];
  public Dictionary<int, string> ByNumber { get; set; } = [];
  public Dictionary<DictionaryRewriteColor, int> ByColor { get; set; } = [];
  public Dictionary<DateTime, int> ByDay { get; set; } = [];
  public List<DictionaryMemberSection> Sections { get; set; } = [];
  public Dictionary<string, Dictionary<string, int>> Nested { get; set; } = [];
}

public record DictionaryRewriteNoted([property: StreamId] Guid Id) : IEvent;

[WhizbangPerspective("dictionary-members")]
public class DictionaryRewriteProjection : IPerspectiveFor<DictionaryRewriteModel, DictionaryRewriteNoted> {
  public DictionaryRewriteModel Apply(DictionaryRewriteModel currentData, DictionaryRewriteNoted eventData) => currentData;
}

/// <summary>
/// The stored-form rewrite of a dictionary an earlier release stored as a JSON object: every key kind it can rebuild is
/// converted to the array of key/value objects, at every depth, through the phase the initializer runs; the journal
/// settles on the first pass that converts nothing; a table that does not exist yet is settled at once (#1911).
/// </summary>
/// <docs>fundamentals/perspectives/dictionary-members#stored-form-rewrite</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/DictionaryPairsRewrite.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/544_DictionaryMembersStoredAsPairs.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class DictionaryPairsRewriteIntegrationTests : IAsyncDisposable {
  private const long LOCK_ID = 192837466;
  private const int TIMEOUT_SECONDS = 60;
  private const string TABLE = "wh_per_dictionary_rewrite";

  private sealed class RewriteDbContext(DbContextOptions<RewriteDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<PerspectiveRow<DictionaryRewriteModel>>(entity => {
        entity.ToTable(TABLE);
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
  }

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = (await PerTestDatabaseFactory.CreateAsync("dict_rewrite")).Name;
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    var migrations = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public");
    await _executeAsync(migrations.GetMigration("176_StoredFormMigrations")!.Sql);
    await _executeAsync(migrations.GetMigration("544_DictionaryMembersStoredAsPairs")!.Sql);
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
    GC.SuppressFinalize(this);
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
    // A SQL NULL comes back as DBNull, whose text is empty: named, so an assertion about a null cannot pass on "".
    return await command.ExecuteScalarAsync() is { } value and not DBNull ? value.ToString() ?? "<null>" : "<null>";
  }

  private async Task _createTableAsync() => await _executeAsync($"""
    CREATE TABLE {TABLE} (
      id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb NOT NULL DEFAULT '{"{}"}', scope jsonb NOT NULL DEFAULT '{"{}"}',
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

  private static RewriteDbContext _context() => new(new DbContextOptionsBuilder<RewriteDbContext>()
    .UseNpgsql("Host=localhost;Database=probe;Username=u;Password=p", npgsql => npgsql.UseWhizbangFunctions())
    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
    .Options);

  private static ImmutableArray<DictionaryPairsTable> _rewrites() {
    using var context = _context();
    return DictionaryPairsRewrite.ForModel(context.Model, PerspectiveDocumentSerialization.Options, "public");
  }

  private async Task<bool> _runAsync() =>
    await CanonicalTemporalRewritePhase.ApplyAsync(
      () => new NpgsqlConnection(_connectionString), LOCK_ID, [.. _rewrites().Select(t => (t.Name, t.Sql))], TIMEOUT_SECONDS);

  private async Task<string> _journalAsync(string column) =>
    await _scalarAsync($"SELECT {column} FROM wh_stored_form_migrations WHERE name = '{_rewrites().Single().Name}'");

  private async Task<JsonElement> _dataAsync(Guid id) =>
    JsonElement.Parse(await _scalarAsync($"SELECT data::text FROM {TABLE} WHERE id = '{id}'"));

  private const string OLD_FORM = """
    {"ByName":{"b":2,"a":1},"ByNumber":{"7":"seven"},"ByColor":{"Green":5},"ByDay":{"2026-03-04T00:00:00Z":1},
     "Sections":[{"Title":"s","Totals":{"tax":2.25}}],"Nested":{"outer":{"inner":7}}}
    """;

  [Test]
  public async Task AnObjectFormDictionary_IsRewrittenToPairs_AndTheTableSettlesAsync() {
    await _createTableAsync();
    var id = await _seedAsync(OLD_FORM);
    var alreadyPairs = await _seedAsync("""{"ByName":[{"Key":"x","Value":1}]}""");

    await Assert.That(await _runAsync()).IsTrue();

    var data = await _dataAsync(id);
    await Assert.That(data.GetProperty("ByName").GetRawText()).IsEqualTo("""[{"Key": "a", "Value": 1}, {"Key": "b", "Value": 2}]""");
    await Assert.That(await _journalAsync("rows_converted")).IsEqualTo("6")
      .Because("the old row is updated once per path it held in the old form (the date-keyed one is left), and a row "
        + "already in pairs is not touched");
    await Assert.That(await _journalAsync("settled_at")).IsEqualTo("<null>")
      .Because("a pass that converted rows runs again, to catch rows an older release wrote during the rollout");
    await Assert.That((await _dataAsync(alreadyPairs)).GetProperty("ByName").GetRawText()).IsEqualTo("""[{"Key": "x", "Value": 1}]""");

    await Assert.That(await _runAsync()).IsTrue();

    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>")
      .Because("the second pass converted nothing, so the table is settled and never scanned again");
  }

  [Test]
  public async Task EachKeyKind_IsStoredAsItsTypeWritesItAsync() {
    await _createTableAsync();
    var id = await _seedAsync(OLD_FORM);

    await _runAsync();

    var data = await _dataAsync(id);
    await Assert.That(data.GetProperty("ByNumber").GetRawText()).IsEqualTo("""[{"Key": 7, "Value": "seven"}]""");
    await Assert.That(data.GetProperty("ByColor").GetRawText()).IsEqualTo("""[{"Key": 2, "Value": 5}]""")
      .Because("an enumeration's name becomes the number the serializer stores it as");
    await Assert.That(data.GetProperty("ByDay").ValueKind).IsEqualTo(JsonValueKind.Object)
      .Because("a date key's stored form is a count the database cannot rebuild from a rendering; the reader reads the object");

    await using var context = new RewriteDbContext(new DbContextOptionsBuilder<RewriteDbContext>()
      .UseNpgsql(_connectionString, npgsql => npgsql.UseWhizbangFunctions())
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
    var read = (await context.Set<PerspectiveRow<DictionaryRewriteModel>>().AsNoTracking().SingleAsync()).Data;
    await Assert.That(read.ByColor[DictionaryRewriteColor.Green]).IsEqualTo(5);
    await Assert.That(read.ByNumber[7]).IsEqualTo("seven");
    await Assert.That(read.ByDay.Count).IsEqualTo(1);
  }

  [Test]
  public async Task ADictionaryInsideACollectionAndInsideADictionary_IsRewrittenAsync() {
    await _createTableAsync();
    var id = await _seedAsync(OLD_FORM);

    await _runAsync();

    var data = await _dataAsync(id);
    await Assert.That(data.GetProperty("Sections")[0].GetProperty("Totals").GetRawText()).IsEqualTo("""[{"Key": "tax", "Value": 2.25}]""");
    await Assert.That(data.GetProperty("Nested").GetRawText()).IsEqualTo("""[{"Key": "outer", "Value": [{"Key": "inner", "Value": 7}]}]""");
  }

  [Test]
  public async Task ANameANumericKeyCannotRead_LeavesTheDictionaryForTheReaderAsync() {
    await _createTableAsync();
    var id = await _seedAsync("""{"ByNumber":{"seven":"x"},"ByColor":{"Purple":1}}""");

    await _runAsync();

    var data = await _dataAsync(id);
    await Assert.That(data.GetProperty("ByNumber").ValueKind).IsEqualTo(JsonValueKind.Object);
    await Assert.That(data.GetProperty("ByColor").ValueKind).IsEqualTo(JsonValueKind.Object);
  }

  [Test]
  public async Task ATableNotCreatedYet_IsSettledAtOnceAsync() {
    await Assert.That(await _runAsync()).IsTrue();

    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>")
      .Because("this release creates the table, so no row in it was ever written in the old form");
  }
}
