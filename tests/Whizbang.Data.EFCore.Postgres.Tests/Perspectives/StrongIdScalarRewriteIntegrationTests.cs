// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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

  private async Task _createTableAsync(string table = TABLE) => await _executeAsync($$"""
    CREATE TABLE {{table}} (
      id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb NOT NULL DEFAULT '{}', scope jsonb NOT NULL DEFAULT '{}',
      created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), version int NOT NULL DEFAULT 1,
      promoted jsonb)
    """);

  private async Task<Guid> _seedAsync(string data, string? promoted = null, Guid? key = null, string table = TABLE) {
    var id = key ?? (Guid)TrackedGuid.New();
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand($"INSERT INTO {table} (id, data, promoted) VALUES ($1, $2::jsonb, $3::jsonb)", db);
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

  private static ImmutableArray<StrongIdScalarTable> _rewrites(int batchSize = StrongIdScalarRewrite.BATCH_SIZE) {
    using var context = _context();
    return StrongIdScalarRewrite.ForModel(context.Model, PerspectiveDocumentSerialization.Options, "public", batchSize);
  }

  /// <summary>One run of the batched rewrite, its budget counted in batches so a test is deterministic.</summary>
  private async Task<BatchedRewriteRun> _runAsync(
      int batchSize = StrongIdScalarRewrite.BATCH_SIZE, int maxBatches = int.MaxValue,
      Func<NpgsqlConnection, CancellationToken, Task<IReadOnlyList<string>>>? olderReleases = null,
      IReadOnlyList<(string Name, string Sql)>? rewrites = null) =>
    await BatchedRewriteRunner.RunAsync(
      new BatchedRewriteRequest(
        () => new NpgsqlConnection(_connectionString), "public", LOCK_ID,
        rewrites ?? [.. _rewrites(batchSize).Select(t => (t.Name, t.Sql))], TIMEOUT_SECONDS),
      new BatchedRewriteBudget(maxBatches, TimeSpan.FromDays(1)),
      olderReleases,
      applier: null,
      TimeProvider.System);

  private async Task<string> _journalAsync(string column, string? name = null) =>
    await _scalarAsync($"SELECT {column} FROM wh_stored_form_migrations WHERE name = '{name ?? _rewrites().Single().Name}'");

  /// <summary>An ordered key: the rows a batch reads, and where a run stops, are then known.</summary>
  private static Guid _key(int n) => Guid.Parse($"00000000-0000-7000-8000-{n:D12}");

  private const string OLD_ROW = """{"OrderId":{"Value":"01a203cc-54ef-7441-8295-5a6cdae706e6"},"Lines":[]}""";

  private async Task<string> _objectFormKeysAsync(string table = TABLE) =>
    await _scalarAsync($"SELECT coalesce(string_agg(right(id::text, 2), ',' ORDER BY id), '') FROM {table} WHERE jsonb_typeof(data -> 'OrderId') = 'object'");

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

    var run = await _runAsync();

    await Assert.That(run.Outcome).IsEqualTo(BatchedRewriteOutcome.Settled)
      .Because("one pass converts, and a second finds nothing left and settles the table");
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
    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>");
    await Assert.That((await _runAsync()).Batches).IsEqualTo(0).Because("a settled table is never scanned again");
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

    await Assert.That((await _runAsync()).Outcome).IsEqualTo(BatchedRewriteOutcome.Settled);
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
    await Assert.That((await _runAsync()).Outcome).IsEqualTo(BatchedRewriteOutcome.Settled);

    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>");
  }


  /// <summary>
  /// A table larger than one run's budget settles across runs, each starting from the key the last one recorded, so a
  /// restart redoes nothing: one pass converts, a second finds nothing left and settles (#2518).
  /// </summary>
  [Test]
  public async Task ATableLargerThanOneRun_SettlesAcrossRuns_EachResumingWhereTheLastStoppedAsync() {
    await _createTableAsync();
    for (var n = 1; n <= 5; n++) {
      await _seedAsync(OLD_ROW, key: _key(n));
    }

    var first = await _runAsync(batchSize: 2, maxBatches: 1);

    await Assert.That(first.Outcome).IsEqualTo(BatchedRewriteOutcome.Unfinished);
    await Assert.That(await _objectFormKeysAsync()).IsEqualTo("03,04,05");
    await Assert.That(await _journalAsync("resume_key")).IsEqualTo(_key(2).ToString());
    var firstBatchVersion = await _scalarAsync($"SELECT xmin::text FROM {TABLE} WHERE id = '{_key(1)}'");

    // A restart: a new run, with nothing carried over but the journal.
    await _runAsync(batchSize: 2, maxBatches: 1);

    await Assert.That(await _objectFormKeysAsync()).IsEqualTo("05");
    await Assert.That(await _scalarAsync($"SELECT xmin::text FROM {TABLE} WHERE id = '{_key(1)}'")).IsEqualTo(firstBatchVersion)
      .Because("the second run started from the recorded key, so the first batch's rows were not touched again");

    var runs = 2;
    while ((await _runAsync(batchSize: 2, maxBatches: 1)).Outcome != BatchedRewriteOutcome.Settled) {
      runs++;
    }
    runs++;

    await Assert.That(await _objectFormKeysAsync()).IsEqualTo("");
    await Assert.That(await _journalAsync("rows_converted")).IsEqualTo("5").Because("each row was converted once");
    await Assert.That(runs).IsEqualTo(8)
      .Because("two passes of three batches each, and a batch per pass that finds the end: the first converts, the second settles");
  }

  /// <summary>
  /// A row an earlier release writes in the object form behind the key while the rewrite runs is converted by the next
  /// pass: a pass that converted anything is followed by another (#2518).
  /// </summary>
  [Test]
  public async Task ARowWrittenBehindTheKeyDuringTheRewrite_IsConvertedByTheNextPassAsync() {
    await _createTableAsync();
    for (var n = 2; n <= 5; n++) {
      await _seedAsync(OLD_ROW, key: _key(n));
    }
    await _runAsync(batchSize: 2, maxBatches: 1);
    await Assert.That(await _journalAsync("resume_key")).IsEqualTo(_key(3).ToString());

    await _seedAsync(OLD_ROW, key: _key(1));
    var run = await _runAsync(batchSize: 2);

    await Assert.That(run.Outcome).IsEqualTo(BatchedRewriteOutcome.Settled);
    await Assert.That(await _objectFormKeysAsync()).IsEqualTo("");
    await Assert.That(await _journalAsync("rows_converted")).IsEqualTo("5");
  }

  /// <summary>
  /// Two instances never convert the same rows: a batch takes the schema lock, so a run whose lock is held elsewhere
  /// converts nothing, and two runs at once convert each row once between them (#2518).
  /// </summary>
  [Test]
  public async Task TwoInstances_NeverConvertTheSameRowsAsync() {
    await _createTableAsync();
    for (var n = 1; n <= 6; n++) {
      await _seedAsync(OLD_ROW, key: _key(n));
    }

    await using (var holder = new NpgsqlConnection(_connectionString)) {
      await holder.OpenAsync();
      await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({LOCK_ID})", holder)) {
        await take.ExecuteNonQueryAsync();
      }

      var blocked = await _runAsync(batchSize: 2);

      await Assert.That(blocked.Outcome).IsEqualTo(BatchedRewriteOutcome.Unfinished);
      await Assert.That(blocked.Batches).IsEqualTo(0);
      await Assert.That(await _objectFormKeysAsync()).IsEqualTo("01,02,03,04,05,06");
    }

    var runs = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => _runAsync(batchSize: 2)));
    while ((await _runAsync(batchSize: 2)).Outcome != BatchedRewriteOutcome.Settled) {
      // A run that lost the lock to the other stops; this finishes whatever both left.
    }

    await Assert.That(runs.Sum(r => r.Batches)).IsGreaterThan(0);
    await Assert.That(await _objectFormKeysAsync()).IsEqualTo("");
    await Assert.That(await _journalAsync("rows_converted")).IsEqualTo("6").Because("no row was converted twice");
  }

  /// <summary>
  /// Each table's progress is its own: the rewrites take a batch in turn, each records its own key, and one that fails
  /// does not stop the others (#2518).
  /// </summary>
  [Test]
  public async Task EachTablesProgress_IsItsOwnAsync() {
    const string OTHER = "wh_per_strong_id_rewrite_other";
    await _createTableAsync();
    await _createTableAsync(OTHER);
    await _seedAsync(OLD_ROW, key: _key(1));
    for (var n = 1; n <= 5; n++) {
      await _seedAsync(OLD_ROW, key: _key(n), table: OTHER);
    }
    var table = _rewrites(batchSize: 2).Single();
    await using var context = _context();
    var paths = StrongIdScalarRewrite.PathsOf(context.Model.FindEntityType(typeof(PerspectiveRow<StrongIdRewriteModel>))!, PerspectiveDocumentSerialization.Options);
    var other = (Name: StrongIdScalarRewrite.JournalName(OTHER, paths), Sql: StrongIdScalarRewrite.StatementFor("public", OTHER, paths, "id", "uuid", 2));
    var broken = (Name: "strong-id-scalar:broken:0", Sql: "SELECT 1 / 0");
    IReadOnlyList<(string Name, string Sql)> rewrites = [broken, (table.Name, table.Sql), other];

    // Four batches in turn: this table's one and its end of pass, the other's first two.
    var first = await _runAsync(maxBatches: 4, rewrites: rewrites);

    await Assert.That(first.Outcome).IsEqualTo(BatchedRewriteOutcome.Unfinished);
    await Assert.That(await _objectFormKeysAsync()).IsEqualTo("");
    await Assert.That(await _journalAsync("resume_key")).IsEqualTo("<null>").Because("its first pass is done and a second has begun");
    await Assert.That(await _journalAsync("resume_key", other.Name)).IsEqualTo(_key(4).ToString());
    await Assert.That(await _objectFormKeysAsync(OTHER)).IsEqualTo("05");

    var rest = await _runAsync(rewrites: rewrites);

    await Assert.That(rest.Unsettled).IsEquivalentTo([broken.Name]).Because("the failing rewrite is reported and left for the next run");
    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>");
    await Assert.That(await _journalAsync("settled_at", other.Name)).IsNotEqualTo("<null>");
    await Assert.That(await _journalAsync("rows_converted", other.Name)).IsEqualTo("5");
  }

  /// <summary>
  /// The rewrite waits while an instance of an earlier library release is live, since that release reads only the object
  /// form, and runs once none is; a newer release, a build of the same one, or an instance no longer live does not hold
  /// it (#2518).
  /// </summary>
  [Test]
  public async Task TheRewrite_WaitsWhileAnOlderReleaseIsLive_AndRunsOnceNoneIsAsync() {
    await _createTableAsync();
    await _seedAsync(OLD_ROW, key: _key(1));
    // The registry and its liveness rule, reduced to what the question reads.
    await _executeAsync("""
      CREATE TABLE wh_service_instances (instance_id uuid PRIMARY KEY, library_version text);
      CREATE TABLE live (instance_id uuid PRIMARY KEY);
      CREATE FUNCTION wh_live_instance_ids() RETURNS TABLE (instance_id uuid) LANGUAGE sql AS 'SELECT instance_id FROM live';
      INSERT INTO wh_service_instances VALUES
        ('00000000-0000-0000-0000-000000000001', '1.2.0'),
        ('00000000-0000-0000-0000-000000000002', '1.2.0-local.3+abc'),
        ('00000000-0000-0000-0000-000000000003', '1.3.0'),
        ('00000000-0000-0000-0000-000000000004', '1.1.9'),
        ('00000000-0000-0000-0000-000000000005', NULL),
        ('00000000-0000-0000-0000-000000000006', '1.0.0');
      INSERT INTO live SELECT instance_id FROM wh_service_instances WHERE instance_id <> '00000000-0000-0000-0000-000000000006';
      """);
    Func<NpgsqlConnection, CancellationToken, Task<IReadOnlyList<string>>> older =
      (connection, ct) => FleetVersions.OlderLiveLibraryVersionsAsync(connection, "public", "1.2.0", ct);

    var held = await _runAsync(olderReleases: older);

    await Assert.That(held.Outcome).IsEqualTo(BatchedRewriteOutcome.HeldForOlderRelease);
    await Assert.That(await _objectFormKeysAsync()).IsEqualTo("01");
    await using (var connection = new NpgsqlConnection(_connectionString)) {
      await connection.OpenAsync();
      await Assert.That(await older(connection, CancellationToken.None)).IsEquivalentTo(["1.1.9", FleetVersions.UNKNOWN]);
    }

    await _executeAsync("""
      DELETE FROM live WHERE instance_id IN ('00000000-0000-0000-0000-000000000004', '00000000-0000-0000-0000-000000000005')
      """);
    var released = await _runAsync(olderReleases: older);

    await Assert.That(released.Outcome).IsEqualTo(BatchedRewriteOutcome.Settled);
    await Assert.That(await _objectFormKeysAsync()).IsEqualTo("");
  }

  /// <summary>
  /// The maintenance step finishes what startup's slice left, from the recorded key, and once every table is settled does
  /// nothing more in the process (#2518).
  /// </summary>
  [Test]
  public async Task TheMaintenanceStep_FinishesWhatStartupLeft_ThenDoesNothingAsync() {
    await _createTableAsync();
    for (var n = 1; n <= 3; n++) {
      await _seedAsync(OLD_ROW, key: _key(n));
    }
    // Startup's slice, cut short. The step's batches are the full size, which finishes the table from the recorded key.
    await _runAsync(batchSize: 1, maxBatches: 1);
    await Assert.That(await _objectFormKeysAsync()).IsEqualTo("02,03");
    var services = new ServiceCollection();
    var connectionString = _connectionString;
    services.AddScoped(_ => _context(connectionString));
    await using var provider = services.BuildServiceProvider();
    var step = new StrongIdScalarRewriteStep(typeof(RewriteDbContext));

    await using (var scope = provider.CreateAsyncScope()) {
      await step.RunAsync(scope.ServiceProvider, CancellationToken.None);
    }

    await Assert.That(await _objectFormKeysAsync()).IsEqualTo("");
    await Assert.That(await _journalAsync("settled_at")).IsNotEqualTo("<null>");
    await Assert.That(await _journalAsync("rows_converted")).IsEqualTo("3");

    await _seedAsync(OLD_ROW, key: _key(9));
    await using (var scope = provider.CreateAsyncScope()) {
      await step.RunAsync(scope.ServiceProvider, CancellationToken.None);
    }

    await Assert.That(await _objectFormKeysAsync()).IsEqualTo("09")
      .Because("a settled context is remembered for the process; queries read the row in either form");
  }
}
