// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;


/// <summary>A line keyed by its id in <see cref="DictionaryMemberModel.Lines"/>: a complex dictionary value.</summary>
public class DictionaryMemberLine {
  public string Name { get; set; } = string.Empty;
  public int Quantity { get; set; }
}

/// <summary>A complex member holding a dictionary of its own: a dictionary one level down.</summary>
public class DictionaryMemberSection {
  public string Title { get; set; } = string.Empty;
  public Dictionary<string, decimal> Totals { get; set; } = [];
}

/// <summary>
/// A perspective model declaring plain dictionaries the way an author writes them: no attribute, no rewrite.
/// </summary>
[SuppressIndexAdvisory("storage fixture; rows are read back to see exactly what the mapping wrote")]
public class DictionaryMemberModel {
  [StreamId]
  public Guid Id { get; set; }

  public string Label { get; set; } = string.Empty;

  /// <summary>Scalar values under string keys.</summary>
  public Dictionary<string, int> Counts { get; set; } = [];

  /// <summary>Complex values under identifier keys, absent on some rows.</summary>
  public Dictionary<Guid, DictionaryMemberLine>? Lines { get; set; }

  /// <summary>A dictionary inside a nested object.</summary>
  public DictionaryMemberSection Section { get; set; } = new();

  /// <summary>A dictionary inside each element of a collection.</summary>
  public List<DictionaryMemberSection> Sections { get; set; } = [];

  /// <summary>A dictionary declared through the read-only interface.</summary>
  public IReadOnlyDictionary<string, string> Notes { get; set; } = new Dictionary<string, string>();
}

public record DictionaryMemberNoted([property: StreamId] Guid Id) : IEvent;

/// <summary>
/// The perspective that makes <see cref="DictionaryMemberModel"/> a perspective model, so the generators treat its
/// dictionaries exactly as they treat a consumer's. Keyed to a context nobody generates, so no shared test context
/// maps it.
/// </summary>
[WhizbangPerspective("dictionary-members")]
public class DictionaryMemberProjection : IPerspectiveFor<DictionaryMemberModel, DictionaryMemberNoted> {
  public DictionaryMemberModel Apply(DictionaryMemberModel currentData, DictionaryMemberNoted eventData) => currentData;
}

/// <summary>
/// A dictionary member on a perspective model stores, reads back and queries with no attribute and no rewrite (#1911).
/// </summary>
/// <remarks>
/// <para>
/// Entity Framework's JSON mapping refuses a dictionary outright: it takes one for a navigation and fails the model
/// build. Each dictionary is stored instead as an array of key/value objects, <c>[{"Key":..,"Value":..}]</c>, in key
/// order, by both writers a document has: Entity Framework's mapping and the serializer the atomic upsert writes with.
/// The mapping here is the shape the generator emits and names no dictionary.
/// </para>
/// <para>
/// A query that looks a key up is translated to SQL: <c>ContainsKey</c> to a containment test, an indexer to a path
/// query over the pairs. EXPLAIN shows the predicate in the plan, so the database decided which rows came back.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
[Category("Integration")]
[Category("Shard2")]
public class DictionaryMemberStorageTests : IAsyncDisposable {
  private const string TABLE = "wh_per_dictionary_member";

  private static readonly DateTime _at = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

  private sealed class DictionaryDbContext(DbContextOptions<DictionaryDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<DictionaryMemberModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        // No dictionary is named here: the generated configuration does not name one either.
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
    }
  }

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = (await PerTestDatabaseFactory.CreateAsync("dict_member")).Name;
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    await _executeAsync($"""
      CREATE TABLE {TABLE} (
        id uuid PRIMARY KEY,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version integer NOT NULL,
        data jsonb NOT NULL,
        metadata jsonb NOT NULL,
        scope jsonb NOT NULL
      );
      """);
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }

    GC.SuppressFinalize(this);
  }

  private DictionaryDbContext _context() => new(new DbContextOptionsBuilder<DictionaryDbContext>()
    .UseNpgsql(_connectionString, npgsql => npgsql.UseWhizbangFunctions())
    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
    .Options);

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

  private static readonly string[] _countsOrder = ["zulu", "alpha", "mike"];
  private static readonly string[] _notesOrder = ["b", "a"];

  private static readonly Guid _lineId = Guid.Parse("0190f0a0-0000-7000-8000-000000000001");

  private static DictionaryMemberModel _model(Guid id, string label) => new() {
    Id = id,
    Label = label,
    // Not in key order, so a round trip that sorted would show.
    Counts = new() { ["zulu"] = 26, ["alpha"] = 1, ["mike"] = 13 },
    Lines = new() { [_lineId] = new DictionaryMemberLine { Name = "widget", Quantity = 3 } },
    Section = new DictionaryMemberSection { Title = "top", Totals = new() { ["net"] = 12.5m, ["gross"] = 15m } },
    Sections = [new DictionaryMemberSection { Title = "first", Totals = new() { ["tax"] = 2.25m } }],
    Notes = new Dictionary<string, string> { ["b"] = "bee", ["a"] = "ay" },
  };

  private static PerspectiveRow<DictionaryMemberModel> _row(DictionaryMemberModel model) => new() {
    Id = model.Id,
    Data = model,
    Metadata = new PerspectiveMetadata(),
    Scope = new PerspectiveScope(),
    CreatedAt = _at,
    UpdatedAt = _at,
    Version = 1,
  };

  private async Task<Guid> _seedThroughEntityFrameworkAsync(string label) {
    var id = TrackedGuid.New();
    await using var context = _context();
    context.Add(_row(_model(id, label)));
    await context.SaveChangesAsync();
    return id;
  }

  private async Task<DictionaryMemberModel> _readAsync(Guid id) {
    await using var context = _context();
    var row = await context.Set<PerspectiveRow<DictionaryMemberModel>>().AsNoTracking().SingleAsync(r => r.Id == id);
    return row.Data;
  }

  private static async Task _assertSameAsSeededAsync(DictionaryMemberModel read) {
    await Assert.That(read.Counts.Keys.ToArray()).IsEquivalentTo(_countsOrder, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the pairs are read back in the order they were written");
    await Assert.That(read.Counts["mike"]).IsEqualTo(13);
    await Assert.That(read.Lines).IsNotNull();
    await Assert.That(read.Lines![_lineId].Name).IsEqualTo("widget");
    await Assert.That(read.Lines[_lineId].Quantity).IsEqualTo(3);
    await Assert.That(read.Section.Totals["net"]).IsEqualTo(12.5m);
    await Assert.That(read.Sections[0].Totals["tax"]).IsEqualTo(2.25m);
    await Assert.That(read.Notes.Keys.ToArray()).IsEquivalentTo(_notesOrder, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  /// <summary>Every dictionary, at every depth, is stored as an array of key/value objects in its own order.</summary>
  [Test]
  public async Task ADictionaryIsStoredAsAnArrayOfKeyValueObjectsAsync() {
    var id = await _seedThroughEntityFrameworkAsync("stored");

    var counts = await _scalarAsync($"SELECT data -> 'Counts' FROM {TABLE} WHERE id = '{id}'");
    var lines = await _scalarAsync($"SELECT data -> 'Lines' FROM {TABLE} WHERE id = '{id}'");
    var nested = await _scalarAsync($"SELECT data #> '{{Section,Totals}}' FROM {TABLE} WHERE id = '{id}'");
    var inElement = await _scalarAsync($"SELECT data #> '{{Sections,0,Totals}}' FROM {TABLE} WHERE id = '{id}'");

    await Assert.That(counts).IsEqualTo("""[{"Key": "zulu", "Value": 26}, {"Key": "alpha", "Value": 1}, {"Key": "mike", "Value": 13}]""");
    await Assert.That(lines).IsEqualTo($$$"""[{"Key": "{{{_lineId}}}", "Value": {"Name": "widget", "Quantity": 3}}]""");
    await Assert.That(nested).IsEqualTo("""[{"Key": "net", "Value": 12.5}, {"Key": "gross", "Value": 15}]""");
    await Assert.That(inElement).IsEqualTo("""[{"Key": "tax", "Value": 2.25}]""");
  }

  /// <summary>What Entity Framework wrote reads back whole, keys in order.</summary>
  [Test]
  public async Task WhatEntityFrameworkWrote_ReadsBackAsync() {
    var id = await _seedThroughEntityFrameworkAsync("round-trip");

    await _assertSameAsSeededAsync(await _readAsync(id));
  }

  /// <summary>
  /// The atomic upsert writes a document with the serializer, not with Entity Framework. The two writers produce the
  /// same document, and Entity Framework reads what the serializer wrote.
  /// </summary>
  [Test]
  public async Task TheSerializerAndEntityFramework_WriteTheSameDocumentAsync() {
    var efId = await _seedThroughEntityFrameworkAsync("ef");
    var stjId = TrackedGuid.New();
    var model = _model(stjId, "ef");
    var document = PerspectiveDocumentSerialization.Serialize(model).Replace("'", "''", StringComparison.Ordinal);
    await _executeAsync($$"""
      INSERT INTO {{TABLE}} (id, created_at, updated_at, version, data, metadata, scope)
      VALUES ('{{stjId}}', now(), now(), 1, '{{document}}'::jsonb, '{}', '{}');
      """);

    var efDocument = await _scalarAsync($"SELECT (data - 'Id')::text FROM {TABLE} WHERE id = '{efId}'");
    var stjDocument = await _scalarAsync($"SELECT (data - 'Id')::text FROM {TABLE} WHERE id = '{stjId}'");
    await Assert.That(stjDocument).IsEqualTo(efDocument)
      .Because("one stored form: a row reads the same whichever writer wrote it");
    await _assertSameAsSeededAsync(await _readAsync(stjId));
  }

  /// <summary>A null dictionary stays null and an empty one stays empty: they are different values.</summary>
  [Test]
  public async Task NullAndEmpty_StayDifferentAsync() {
    var nullId = TrackedGuid.New();
    var emptyId = TrackedGuid.New();
    await using (var context = _context()) {
      var withNull = _model(nullId, "null");
      withNull.Lines = null;
      var withEmpty = _model(emptyId, "empty");
      withEmpty.Lines = [];
      context.AddRange(_row(withNull), _row(withEmpty));
      await context.SaveChangesAsync();
    }

    await Assert.That((await _readAsync(nullId)).Lines).IsNull();
    await Assert.That((await _readAsync(emptyId)).Lines!.Count).IsEqualTo(0);
    await Assert.That(await _scalarAsync($"SELECT data -> 'Lines' FROM {TABLE} WHERE id = '{emptyId}'")).IsEqualTo("[]");
  }

  /// <summary>
  /// A document an earlier release wrote, with each dictionary as a JSON object, still reads while it waits for the
  /// stored-form rewrite.
  /// </summary>
  [Test]
  public async Task AnObjectFormDictionary_StillReadsAsync() {
    var id = TrackedGuid.New();
    await _executeAsync($$$"""
      INSERT INTO {{{TABLE}}} (id, created_at, updated_at, version, data, metadata, scope)
      VALUES ('{{{id}}}', now(), now(), 1, '{
        "Id": "{{{id}}}", "Label": "legacy",
        "Counts": {"alpha": 1, "mike": 13},
        "Lines": {"{{{_lineId}}}": {"Name": "widget", "Quantity": 3}},
        "Section": {"Title": "top", "Totals": {"net": 12.5}},
        "Sections": [{"Title": "first", "Totals": {"tax": 2.25}}],
        "Notes": {"a": "ay"}
      }'::jsonb, '{}', '{}');
      """);

    var read = await _readAsync(id);

    await Assert.That(read.Counts["mike"]).IsEqualTo(13);
    await Assert.That(read.Lines![_lineId].Quantity).IsEqualTo(3);
    await Assert.That(read.Section.Totals["net"]).IsEqualTo(12.5m);
    await Assert.That(read.Sections[0].Totals["tax"]).IsEqualTo(2.25m);
    await Assert.That(read.Notes["a"]).IsEqualTo("ay");
  }

  /// <summary>A stored array naming one key twice is refused rather than read as either value.</summary>
  [Test]
  public async Task ADuplicateKey_IsRefusedAsync() {
    var id = TrackedGuid.New();
    await _executeAsync($$"""
      INSERT INTO {{TABLE}} (id, created_at, updated_at, version, data, metadata, scope)
      VALUES ('{{id}}', now(), now(), 1, '{
        "Id": "{{id}}", "Label": "dup",
        "Counts": [{"Key": "a", "Value": 1}, {"Key": "a", "Value": 2}],
        "Section": {"Title": "t", "Totals": []}, "Sections": [], "Notes": []
      }'::jsonb, '{}', '{}');
      """);

    var failure = await Assert.That(async () => await _readAsync(id)).ThrowsException();
    await Assert.That(failure!.ToString()).Contains("duplicate key");
  }

  /// <summary>A key lookup is answered by the database: ContainsKey and an indexer, top level, nested and per element.</summary>
  [Test]
  public async Task AKeyLookup_IsTranslatedToSqlAsync() {
    var matching = await _seedThroughEntityFrameworkAsync("match");
    await using (var context = _context()) {
      var other = _model(TrackedGuid.New(), "other");
      other.Counts = new() { ["beta"] = 2 };
      other.Lines = null;
      other.Notes = new Dictionary<string, string>();
      other.Section.Totals = [];
      other.Sections = [];
      context.Add(_row(other));
      await context.SaveChangesAsync();
    }

    await using var query = _context();
    var rows = query.Set<PerspectiveRow<DictionaryMemberModel>>();

    await Assert.That(await rows.Where(r => r.Data.Counts.ContainsKey("mike")).Select(r => r.Id).ToListAsync()).IsEquivalentTo(new[] { matching });
    await Assert.That(await rows.Where(r => r.Data.Counts["mike"] == 13).Select(r => r.Id).ToListAsync()).IsEquivalentTo(new[] { matching });
    await Assert.That(await rows.Where(r => r.Data.Counts["mike"] > 20).CountAsync()).IsEqualTo(0);
    await Assert.That(await rows.Where(r => r.Data.Section.Totals.ContainsKey("net")).Select(r => r.Id).ToListAsync()).IsEquivalentTo(new[] { matching });
    await Assert.That(await rows.Where(r => r.Data.Sections.Any(s => s.Totals.ContainsKey("tax"))).Select(r => r.Id).ToListAsync()).IsEquivalentTo(new[] { matching });
    await Assert.That(await rows.Where(r => r.Data.Lines!.ContainsKey(_lineId)).Select(r => r.Id).ToListAsync()).IsEquivalentTo(new[] { matching });
    await Assert.That(await rows.Where(r => r.Data.Notes["a"] == "ay").Select(r => r.Id).ToListAsync()).IsEquivalentTo(new[] { matching });
    await Assert.That(await rows.Where(r => r.Data.Counts.ContainsKey("absent")).CountAsync()).IsEqualTo(0);
    await Assert.That(await rows.Where(r => r.Data.Sections[0].Totals.ContainsKey("tax")).Select(r => r.Id).ToListAsync()).IsEquivalentTo(new[] { matching })
      .Because("a dictionary under an indexed element is asked of its own member, the path naming no fixed key");
  }

  /// <summary>The plan carries the key predicate: the filter ran in the database, not on rows read back.</summary>
  [Test]
  public async Task TheKeyLookupIsInThePlanAsync() {
    await _seedThroughEntityFrameworkAsync("plan");
    await using var query = _context();
    var sql = query.Set<PerspectiveRow<DictionaryMemberModel>>()
      .Where(r => r.Data.Counts.ContainsKey("mike") && r.Data.Counts["alpha"] == 1)
      .ToQueryString();

    var plan = await _scalarAsync($"EXPLAIN (FORMAT JSON) {_inlineParameters(sql)}");

    await Assert.That(plan).Contains("@>");
    await Assert.That(plan).Contains("jsonb_path_query_first");
  }

  /// <summary>
  /// <c>ToQueryString</c> declares each parameter in a leading comment; EXPLAIN takes the statement with the values
  /// written in.
  /// </summary>
  private static string _inlineParameters(string sql) {
    var lines = sql.Split('\n');
    var body = string.Join('\n', lines.Where(l => !l.StartsWith("-- ", StringComparison.Ordinal)));
    foreach (var declaration in lines.Where(l => l.StartsWith("-- @", StringComparison.Ordinal))) {
      var name = declaration[3..declaration.IndexOf('=', StringComparison.Ordinal)].Trim();
      var value = declaration[(declaration.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim();
      var bare = value.Contains(" (DbType", StringComparison.Ordinal) ? value[..value.IndexOf(" (DbType", StringComparison.Ordinal)] : value;
      body = body.Replace(name, bare, StringComparison.Ordinal);
    }
    return body;
  }
}
