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

/// <summary>The carrier of a shipment: an id two levels down, outside any collection.</summary>
public class StrongIdRewriteCarrier {
  public TestOrderId CarrierId { get; set; }
}

/// <summary>A shipment: a nested object holding a nested object.</summary>
public class StrongIdRewriteShipment {
  public StrongIdRewriteCarrier Carrier { get; set; } = new();
}

/// <summary>A value promoted to a jsonb column of its own: read by the serializer, not by Entity Framework.</summary>
public class StrongIdRewritePromoted {
  public TestOrderId CarrierId { get; set; }
  public List<TestOrderId> Ids { get; set; } = [];
}

/// <summary>
/// A model holding a strongly-typed id at the top level, nullable, nested, inside a collection, as a collection of its
/// own, and as a dictionary's key and value.
/// </summary>
/// <remarks>
/// Not a perspective: the generator would configure it on every context in this assembly, and most are built without
/// the Whizbang options extension, which a collection of ids needs. The tests build their own context and register
/// the dictionaries' stored forms the generated registry would.
/// </remarks>
[SuppressIndexAdvisory("rewrite fixture; rows are seeded in the old form and read back raw")]
public class StrongIdRewriteModel {
  [StreamId]
  public Guid Id { get; set; }

  public TestOrderId OrderId { get; set; }
  public TestOrderId? ParentId { get; set; }
  public List<StrongIdRewriteLine> Lines { get; set; } = [];
  public StrongIdRewriteShipment Shipment { get; set; } = new();
  public List<TestOrderId> RelatedIds { get; set; } = [];
  public Dictionary<string, TestOrderId> ByName { get; set; } = [];
  public Dictionary<TestOrderId, int> Counts { get; set; } = [];
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
          d.ComplexProperty(x => x.Shipment, shipment => shipment.ComplexProperty(x => x.Carrier));
        });
        entity.Property<StrongIdRewritePromoted>("promoted").HasColumnName("promoted").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ConverterFor<StrongIdRewritePromoted>());
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
    // What the generated registry does for a perspective's dictionaries; the model is not one (see its remarks).
    DictionaryMemberForms.Register<string, TestOrderId>();
    DictionaryMemberForms.Register<TestOrderId, int>();
    DictionaryMemberForms.RegisterMember(typeof(StrongIdRewriteModel), nameof(StrongIdRewriteModel.ByName), typeof(Dictionary<string, TestOrderId>));
    DictionaryMemberForms.RegisterMember(typeof(StrongIdRewriteModel), nameof(StrongIdRewriteModel.Counts), typeof(Dictionary<TestOrderId, int>));
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = (await PerTestDatabaseFactory.CreateAsync("id_rewrite")).Name;
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    var migrations = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public");
    // 000 first: 557 drops its containment function's overloads through drop_all_overloads.
    await _executeAsync(migrations.GetMigration("000_MigrationTracking")!.Sql);
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
      created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), version int NOT NULL DEFAULT 1,
      promoted jsonb)
    """);

  private async Task<Guid> _seedAsync(string data, string? promoted = null) {
    var id = (Guid)TrackedGuid.New();
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand($"INSERT INTO {TABLE} (id, data, promoted) VALUES ($1, $2::jsonb, $3::jsonb)", db);
    command.Parameters.AddWithValue(id);
    command.Parameters.AddWithValue(data);
    command.Parameters.AddWithValue((object?)promoted ?? DBNull.Value);
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
  private static readonly Guid _carrier = Guid.Parse("01a203cc-54ef-7441-82d6-0a7f5b1c2e3d");

  private static string _oldForm(string parent) =>
    $$$$"""
    {"OrderId":{"Value":"{{{{_order}}}}"},"ParentId":{{{{parent}}}},"Lines":[{"LineId":{"Value":"{{{{_line}}}}"},"Quantity":2}],
     "Shipment":{"Carrier":{"CarrierId":{"Value":"{{{{_carrier}}}}"}}},"RelatedIds":[{"Value":"{{{{_line}}}}"}],
     "ByName":[{"Key":"first","Value":{"Value":"{{{{_order}}}}"}}],"Counts":[{"Key":{"Value":"{{{{_order}}}}"},"Value":3}]}
    """;

  private static string _oldPromoted() =>
    $$$"""{"CarrierId":{"Value":"{{{_carrier}}}"},"Ids":[{"Value":"{{{_order}}}"}]}""";

  [Test]
  public async Task ThePaths_AreEveryIdTheModelHoldsAsync() {
    await using var context = _context();
    var rowType = context.Model.FindEntityType(typeof(PerspectiveRow<StrongIdRewriteModel>))!;

    var paths = StrongIdScalarRewrite.PathsOf(rowType, PerspectiveDocumentSerialization.Options);

    await Assert.That(string.Join(" ", paths.Select(p => p.ToString()).Order(StringComparer.Ordinal)))
      .IsEqualTo(
        "data:ByName.[].Value data:Counts.[].Key data:Lines.[].LineId data:OrderId data:ParentId data:RelatedIds.[] " +
        "data:Shipment.Carrier.CarrierId promoted:CarrierId promoted:Ids.[]");
  }

  [Test]
  public async Task AnObjectFormId_IsRewrittenToTheUuidString_AndTheTableSettlesAsync() {
    await _createTableAsync();
    var old = await _seedAsync(_oldForm($$"""{"Value":"{{_parent}}"}"""), _oldPromoted());
    var nullParent = await _seedAsync(_oldForm("null"));
    var alreadyScalar = await _seedAsync($$"""{"OrderId":"{{_order}}","ParentId":null,"Lines":[]}""");

    await Assert.That(await _runAsync()).IsTrue();

    var data = await _dataAsync(old);
    await Assert.That(data.GetProperty("OrderId").GetString()).IsEqualTo(_order.ToString());
    await Assert.That(data.GetProperty("ParentId").GetString()).IsEqualTo(_parent.ToString());
    await Assert.That(data.GetProperty("Lines")[0].GetProperty("LineId").GetString()).IsEqualTo(_line.ToString());
    await Assert.That(data.GetProperty("Lines")[0].GetProperty("Quantity").GetInt32()).IsEqualTo(2);
    await Assert.That(data.GetProperty("Shipment").GetProperty("Carrier").GetProperty("CarrierId").GetString()).IsEqualTo(_carrier.ToString());
    await Assert.That(data.GetProperty("RelatedIds")[0].GetString()).IsEqualTo(_line.ToString());
    await Assert.That(data.GetProperty("ByName")[0].GetProperty("Value").GetString()).IsEqualTo(_order.ToString());
    await Assert.That(data.GetProperty("Counts")[0].GetProperty("Key").GetString()).IsEqualTo(_order.ToString());
    await Assert.That(data.GetProperty("Counts")[0].GetProperty("Value").GetInt32()).IsEqualTo(3);
    var promoted = JsonElement.Parse(await _scalarAsync($"SELECT promoted::text FROM {TABLE} WHERE id = '{old}'"));
    await Assert.That(promoted.GetProperty("CarrierId").GetString()).IsEqualTo(_carrier.ToString());
    await Assert.That(promoted.GetProperty("Ids")[0].GetString()).IsEqualTo(_order.ToString());
    await Assert.That((await _dataAsync(nullParent)).GetProperty("ParentId").ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(await _journalAsync("rows_converted")).IsEqualTo("2")
      .Because("each row in the old form is updated once for all of its paths, and the row already in the scalar form not at all");
    await Assert.That((await _dataAsync(alreadyScalar)).GetProperty("OrderId").GetString()).IsEqualTo(_order.ToString());
    await Assert.That(await _journalAsync("settled_at")).IsEqualTo("<null>");

    await Assert.That(await _runAsync()).IsTrue();

    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>")
      .Because("the second pass converted nothing, so the table is settled and never scanned again");
  }

  /// <summary>
  /// An id predicate finds a row in the object form before the rewrite reaches it, and one an instance of an earlier
  /// release writes during a rolling deploy after the journal settled, which no later pass rescans (#2518).
  /// </summary>
  [Test]
  public async Task AnIdPredicate_FindsObjectFormRows_BeforeTheRewriteAndFromAnOldWriterAfterItSettlesAsync() {
    await _createTableAsync();
    var unconverted = await _seedAsync(_oldForm("null"));
    await using var context = _context(_connectionString);
    var orderId = new TestOrderId(_order);
    var carrierId = new TestOrderId(_carrier);
    var rows = context.Set<PerspectiveRow<StrongIdRewriteModel>>().AsNoTracking();

    await Assert.That(await rows.Where(r => r.Data.OrderId == orderId).Select(r => r.Id).ToListAsync()).IsEquivalentTo([unconverted]);

    await _runAsync();
    await _runAsync();
    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>");
    var lateOldWrite = await _seedAsync(_oldForm("null"));

    await Assert.That(await rows.Where(r => r.Data.OrderId == orderId).Select(r => r.Id).ToListAsync())
      .IsEquivalentTo([unconverted, lateOldWrite]);
    await Assert.That(await rows.Where(r => r.Data.Shipment.Carrier.CarrierId == carrierId).CountAsync()).IsEqualTo(2);
    await Assert.That(await rows.Where(r => r.Data.RelatedIds.Contains(new TestOrderId(_line))).CountAsync()).IsEqualTo(2);
    await Assert.That((await _dataAsync(lateOldWrite)).GetProperty("OrderId").ValueKind).IsEqualTo(JsonValueKind.Object)
      .Because("a settled table is not scanned again; the row is read in either form until it is next written");
  }

  [Test]
  public async Task ATableThatDoesNotExistYet_IsSettledAtOnceAsync() {
    await Assert.That(await _runAsync()).IsTrue();

    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>");
  }

  /// <summary>
  /// A statement whose budget is spent stops after the batch it is in and leaves the journal unsettled; what it converted
  /// stays converted, and the next run carries on from there (#2518).
  /// </summary>
  [Test]
  public async Task ABudgetSpent_StopsAfterABatch_AndTheNextRunCarriesOnAsync() {
    await _createTableAsync();
    await _executeAsync($$"""
      INSERT INTO {{TABLE}} (id, data)
      SELECT gen_random_uuid(), '{"OrderId":{"Value":"{{_order}}"},"Lines":[]}'::jsonb
      FROM generate_series(1, {{StrongIdScalarRewrite.BATCH_SIZE + 1}})
      """);
    var statement = _rewrites().Single().Sql;

    // A budget already spent: the first batch runs, then the statement stops.
    await _runWithBudgetAsync(statement, "-1");

    await Assert.That(await _journalAsync("rows_converted")).IsEqualTo(StrongIdScalarRewrite.BATCH_SIZE.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Assert.That(await _journalAsync("settled_at")).IsEqualTo("<null>");
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE jsonb_typeof(data -> 'OrderId') = 'object'")).IsEqualTo("1");

    await _runWithBudgetAsync(statement, null);

    await Assert.That(await _journalAsync("rows_converted")).IsEqualTo((StrongIdScalarRewrite.BATCH_SIZE + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Assert.That(await _journalAsync("settled_at")).IsEqualTo("<null>")
      .Because("that pass still converted a row");

    await _runWithBudgetAsync(statement, null);

    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>");
  }

  /// <summary>The phase hands a batched statement a budget short of its command timeout.</summary>
  [Test]
  public async Task ThePhase_SetsABudgetShortOfTheCommandTimeoutAsync() {
    await Assert.That(CanonicalTemporalRewritePhase.BudgetMilliseconds(60)).IsEqualTo(48_000L);
  }

  private async Task _runWithBudgetAsync(string statement, string? budgetMilliseconds) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var transaction = await db.BeginTransactionAsync();
    if (budgetMilliseconds is not null) {
      await using var budget = new NpgsqlCommand($"SELECT set_config('{CanonicalTemporalRewritePhase.BUDGET_SETTING}', $1, true)", db, transaction);
      budget.Parameters.AddWithValue(budgetMilliseconds);
      await budget.ExecuteNonQueryAsync();
    }
    await using (var run = new NpgsqlCommand(statement, db, transaction)) {
      await run.ExecuteNonQueryAsync();
    }
    await transaction.CommitAsync();
  }
}
