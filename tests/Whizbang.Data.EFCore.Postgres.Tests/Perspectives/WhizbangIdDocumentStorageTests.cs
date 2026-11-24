// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A strongly-typed id inside a perspective document is stored as a UUID string on every context that carries the
/// Whizbang options extension (#2518): top level, nullable and inside a complex collection. Id equality translates to
/// a uuid comparison, <c>Contains</c> over ids to <c>= ANY</c>, and a row an earlier release stored in the object form
/// <c>{"Value": ...}</c> still loads.
/// </summary>
/// <remarks>
/// <see cref="StructuralContext"/> is a context without the extension, Entity Framework's own discovery: the object
/// form, used here as the earlier release's writer.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/WhizbangIdTypeMapping.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "#1954: the [After(Test)] hook disposes them.")]
public class WhizbangIdDocumentStorageTests {
  /// <summary>How a query reads <c>OrderId</c>: the UUID string, or the <c>Value</c> of the object an earlier release stored.</summary>
  private const string _EITHER_FORM_ORDER_ID =
    "CAST(CASE\n    WHEN w.data ->> 'OrderId' LIKE '{%' THEN jsonb_extract_path_text(CAST(w.data ->> 'OrderId' AS jsonb), 'Value')\n    ELSE w.data ->> 'OrderId'\nEND AS uuid)";
  private string? _databaseName;
  private NpgsqlDataSource? _dataSource;

  public sealed class ProbeLine {
    public TestOrderId LineId { get; set; }
    public int Quantity { get; set; }
  }

  public sealed class ProbeCarrier {
    public TestOrderId CarrierId { get; set; }
  }

  public sealed class ProbeShipment {
    public ProbeCarrier Carrier { get; set; } = new();
  }

  public sealed class ProbeDocument {
    public TestOrderId OrderId { get; set; }
    public TestOrderId? ParentId { get; set; }
    public string Name { get; set; } = "";
    public List<ProbeLine> Lines { get; set; } = [];
    public ProbeShipment Shipment { get; set; } = new();
  }

  public sealed class ListDocument {
    public List<TestOrderId> OrderIds { get; set; } = [];
  }

  public sealed class ListRow {
    public Guid Id { get; set; }
    public ListDocument Data { get; set; } = new();
  }

  public sealed class ProbeRow {
    public Guid Id { get; set; }
    public ProbeDocument Data { get; set; } = new();
  }

  private abstract class ProbeContext(DbContextOptions options) : DbContext(options) {
    public DbSet<ProbeRow> Rows => Set<ProbeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => _configure(modelBuilder);

    protected static void _configure(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<ProbeRow>(entity => {
        entity.ToTable("wh_per_probe");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => {
          d.ToJson("data");
          d.ComplexCollection(x => x.Lines);
          d.ComplexProperty(x => x.Shipment, shipment => shipment.ComplexProperty(x => x.Carrier));
        });
      });
  }

  /// <summary>A document holding a collection of ids, on a model of its own.</summary>
  private abstract class ListContext(DbContextOptions options) : DbContext(options) {
    public DbSet<ListRow> Rows => Set<ListRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => _configure(modelBuilder);

    protected static void _configure(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<ListRow>(entity => {
        entity.ToTable("wh_per_probe_list");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
      });
  }

  /// <summary>The list model on a context carrying the Whizbang options extension.</summary>
  private sealed class WhizbangListContext(DbContextOptions options) : ListContext(options);

  /// <summary>The list model on a context without the extension that calls <see cref="WhizbangIdConvention.Apply"/>.</summary>
  private sealed class ConventionListContext(DbContextOptions options) : ListContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      _configure(modelBuilder);
      WhizbangIdConvention.Apply(modelBuilder);
    }
  }

  private sealed class StructuralContext(DbContextOptions options) : ProbeContext(options);

  /// <summary>The same model on a context carrying the Whizbang options extension, as every perspective context does.</summary>
  private sealed class WhizbangContext(DbContextOptions options) : ProbeContext(options);

  /// <summary>
  /// The same model on a context without the extension that calls <see cref="WhizbangIdConvention.Apply"/>, as every
  /// generated context's <c>OnModelCreating</c> does: a pooled factory registered by hand, for example.
  /// </summary>
  private sealed class ConventionContext(DbContextOptions options) : ProbeContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      _configure(modelBuilder);
      WhizbangIdConvention.Apply(modelBuilder);
    }
  }

  [Test]
  public async Task Convention_WithoutTheExtension_WritesScalarsReadsBothFormsAndTranslatesAsync() {
    var orderId = TestOrderId.New();
    var parentId = TestOrderId.New();
    var lineId = TestOrderId.New();
    var old = _row(orderId, parentId, lineId);
    await using (var write = _context(o => new StructuralContext(o))) {
      write.Rows.Add(old);
      await write.SaveChangesAsync();
    }

    await using var db = _context(o => new ConventionContext(o));
    var loaded = await db.Rows.AsNoTracking().SingleAsync(r => r.Id == old.Id);
    await Assert.That(loaded.Data.OrderId).IsEqualTo(orderId);
    await Assert.That(loaded.Data.ParentId).IsEqualTo(parentId);
    await Assert.That(loaded.Data.Lines[0].LineId).IsEqualTo(lineId);

    var fresh = _row(TestOrderId.New(), null, TestOrderId.New());
    db.Rows.Add(fresh);
    await db.SaveChangesAsync();
    var stored = await _storedTextAsync(fresh.Id);
    await Assert.That(stored).Contains($"\"OrderId\": \"{fresh.Data.OrderId.Value}\"");
    await Assert.That(stored).Contains($"\"LineId\": \"{fresh.Data.Lines[0].LineId.Value}\"");
    await Assert.That(stored).Contains("\"ParentId\": null");

    var wanted = new[] { fresh.Data.OrderId };
    await Assert.That(db.Rows.Where(r => wanted.Contains(r.Data.OrderId)).ToQueryString()).Contains("= ANY (@wanted)");
  }

  [Test]
  public async Task Whizbang_ReadsTheObjectFormAndWritesTheScalarAsync() {
    var orderId = TestOrderId.New();
    var lineId = TestOrderId.New();
    var parentId = TestOrderId.New();
    var old = _row(orderId, parentId, lineId);
    await using (var write = _context(o => new StructuralContext(o))) {
      write.Rows.Add(old);
      await write.SaveChangesAsync();
    }

    await using var db = _context(o => new WhizbangContext(o));
    var loaded = await db.Rows.SingleAsync(r => r.Id == old.Id);
    await Assert.That(loaded.Data.OrderId).IsEqualTo(orderId);
    await Assert.That(loaded.Data.ParentId).IsEqualTo(parentId);
    await Assert.That(loaded.Data.Lines[0].LineId).IsEqualTo(lineId);

    var fresh = _row(TestOrderId.New(), null, TestOrderId.New());
    db.Rows.Add(fresh);
    await db.SaveChangesAsync();
    var stored = await _storedTextAsync(fresh.Id);
    await Assert.That(stored).DoesNotContain("\"Value\"");

    // A query that compares the id reads it in either form, so the object-form row is found rather than failing the
    // statement (Whizbang_EveryIdQueryFindsARowStillInTheObjectFormAsync covers every query shape).
    var query = db.Rows.AsNoTracking().Where(r => r.Data.OrderId == orderId);
    await Assert.That(await query.CountAsync()).IsEqualTo(1);
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = (await PerTestDatabaseFactory.CreateAsync("strong_id_doc")).Name;
    var connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      IncludeErrorDetail = true,
    }.ConnectionString;
    _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();

    await using var connection = await _dataSource.OpenConnectionAsync();
    await using var create = new NpgsqlCommand(
      "CREATE TABLE wh_per_probe (id uuid PRIMARY KEY, data jsonb NOT NULL); CREATE TABLE wh_per_probe_list (id uuid PRIMARY KEY, data jsonb NOT NULL)",
      connection);
    await create.ExecuteNonQueryAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_dataSource is not null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
      _databaseName = null;
    }
  }

  private T _context<T>(Func<DbContextOptions, T> create) where T : DbContext {
    var whizbang = typeof(T) == typeof(WhizbangContext) || typeof(T) == typeof(WhizbangListContext);
    var options = new DbContextOptionsBuilder()
      .UseNpgsql(_dataSource!, npgsql => {
        if (whizbang) {
          npgsql.UseWhizbangFunctions();
        }
      })
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;
    return create(options);
  }

  private static ProbeRow _row(TestOrderId orderId, TestOrderId? parentId, TestOrderId lineId, TestOrderId? carrierId = null) => new() {
    Id = Guid.CreateVersion7(),
    Data = new ProbeDocument {
      OrderId = orderId,
      ParentId = parentId,
      Name = "probe",
      Lines = [new ProbeLine { LineId = lineId, Quantity = 2 }],
      Shipment = new ProbeShipment { Carrier = new ProbeCarrier { CarrierId = carrierId ?? TestOrderId.New() } },
    },
  };

  private async Task<string> _storedTextAsync(Guid rowId) {
    await using var connection = await _dataSource!.OpenConnectionAsync();
    await using var select = new NpgsqlCommand("SELECT data::text FROM wh_per_probe WHERE id = @id", connection);
    select.Parameters.AddWithValue("id", rowId);
    return (string)(await select.ExecuteScalarAsync())!;
  }

  [Test]
  public async Task Structural_WritesTheObjectFormAsync() {
    await using var db = _context(o => new StructuralContext(o));
    var orderId = TestOrderId.New();
    var row = _row(orderId, null, TestOrderId.New());
    db.Rows.Add(row);
    await db.SaveChangesAsync();

    var stored = await _storedTextAsync(row.Id);

    await Assert.That(stored).Contains($"\"OrderId\": {{\"Value\": \"{orderId.Value}\"}}");
  }

  [Test]
  public async Task Whizbang_WritesEveryIdAsAUuidStringAsync() {
    await using var db = _context(o => new WhizbangContext(o));
    var orderId = TestOrderId.New();
    var parentId = TestOrderId.New();
    var lineId = TestOrderId.New();
    var row = _row(orderId, parentId, lineId);
    db.Rows.Add(row);
    await db.SaveChangesAsync();

    var stored = await _storedTextAsync(row.Id);

    await Assert.That(stored).Contains($"\"OrderId\": \"{orderId.Value}\"");
    await Assert.That(stored).Contains($"\"ParentId\": \"{parentId.Value}\"");
    await Assert.That(stored).Contains($"\"LineId\": \"{lineId.Value}\"");
    await Assert.That(stored).DoesNotContain("\"Value\"");
  }

  [Test]
  public async Task Whizbang_RoundTripsIncludingANullIdAsync() {
    var orderId = TestOrderId.New();
    var lineId = TestOrderId.New();
    var row = _row(orderId, null, lineId);
    await using (var write = _context(o => new WhizbangContext(o))) {
      write.Rows.Add(row);
      await write.SaveChangesAsync();
    }

    await using var read = _context(o => new WhizbangContext(o));
    var loaded = await read.Rows.AsNoTracking().SingleAsync(r => r.Id == row.Id);

    await Assert.That(loaded.Data.OrderId).IsEqualTo(orderId);
    await Assert.That(loaded.Data.ParentId).IsNull();
    await Assert.That(loaded.Data.Lines[0].LineId).IsEqualTo(lineId);
  }

  [Test]
  public async Task Whizbang_IdEqualityTranslatesAndMatchesAsync() {
    var orderId = TestOrderId.New();
    await using var db = _context(o => new WhizbangContext(o));
    db.Rows.Add(_row(orderId, null, TestOrderId.New()));
    db.Rows.Add(_row(TestOrderId.New(), null, TestOrderId.New()));
    await db.SaveChangesAsync();

    var query = db.Rows.AsNoTracking().Where(r => r.Data.OrderId == orderId);
    var sql = query.ToQueryString();
    var matched = await query.ToListAsync();

    await Assert.That(sql).Contains(_EITHER_FORM_ORDER_ID);

    await Assert.That(matched).Count().IsEqualTo(1);
    await Assert.That(matched[0].Data.OrderId).IsEqualTo(orderId);
  }

  [Test]
  public async Task Whizbang_NullableIdEqualityTranslatesAndMatchesAsync() {
    var parentId = TestOrderId.New();
    await using var db = _context(o => new WhizbangContext(o));
    db.Rows.Add(_row(TestOrderId.New(), parentId, TestOrderId.New()));
    db.Rows.Add(_row(TestOrderId.New(), null, TestOrderId.New()));
    await db.SaveChangesAsync();

    var query = db.Rows.AsNoTracking().Where(r => r.Data.ParentId == parentId);

    await Assert.That(await query.CountAsync()).IsEqualTo(1);
  }

  [Test]
  public async Task Whizbang_ContainsOverIdsTranslatesAndMatchesAsync() {
    var first = TestOrderId.New();
    var second = TestOrderId.New();
    await using var db = _context(o => new WhizbangContext(o));
    db.Rows.Add(_row(first, null, TestOrderId.New()));
    db.Rows.Add(_row(second, null, TestOrderId.New()));
    db.Rows.Add(_row(TestOrderId.New(), null, TestOrderId.New()));
    await db.SaveChangesAsync();

    var wanted = new[] { first, second };
    var query = db.Rows.AsNoTracking().Where(r => wanted.Contains(r.Data.OrderId));

    await Assert.That(query.ToQueryString()).Contains($"{_EITHER_FORM_ORDER_ID} = ANY (@wanted)");

    await Assert.That(await query.CountAsync()).IsEqualTo(2);
  }

  /// <summary>Entity Framework's own discovery, the object form, cannot translate Contains over ids (#2084).</summary>
  [Test]
  public async Task Structural_ContainsOverIdsDoesNotTranslateAsync() {
    await using var db = _context(o => new StructuralContext(o));
    var wanted = new[] { TestOrderId.New() };

    var query = db.Rows.AsNoTracking().Where(r => wanted.Contains(r.Data.OrderId));

    await Assert.That(() => query.ToQueryString()).Throws<InvalidOperationException>();
  }

  /// <summary>
  /// A query on the id's <c>Value</c> no longer translates: inside a document the id is a scalar, so <c>Value</c> is not
  /// a mapped member. Compare the id itself (#2518, breaking).
  /// </summary>
  [Test]
  public async Task Whizbang_QueryOnTheIdsValueDoesNotTranslateAsync() {
    await using var db = _context(o => new WhizbangContext(o));
    var wanted = TestOrderId.New().Value;

    var query = db.Rows.AsNoTracking().Where(r => r.Data.OrderId.Value == wanted);

    await Assert.That(() => query.ToQueryString()).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Whizbang_NullableIdComparedWithNullTranslatesAndMatchesAsync() {
    var parentId = TestOrderId.New();
    await using var db = _context(o => new WhizbangContext(o));
    var withParent = _row(TestOrderId.New(), parentId, TestOrderId.New());
    var withoutParent = _row(TestOrderId.New(), null, TestOrderId.New());
    db.Rows.AddRange(withParent, withoutParent);
    await db.SaveChangesAsync();
    TestOrderId? none = null;
    TestOrderId? some = parentId;

    var isNull = await db.Rows.AsNoTracking().Where(r => r.Data.ParentId == null).Select(r => r.Id).ToListAsync();
    var isNotNull = await db.Rows.AsNoTracking().Where(r => r.Data.ParentId != null).Select(r => r.Id).ToListAsync();
    var equalsNullVariable = await db.Rows.AsNoTracking().Where(r => r.Data.ParentId == none).Select(r => r.Id).ToListAsync();
    var equalsVariable = await db.Rows.AsNoTracking().Where(r => r.Data.ParentId == some).Select(r => r.Id).ToListAsync();

    await Assert.That(isNull).IsEquivalentTo([withoutParent.Id]);
    await Assert.That(isNotNull).IsEquivalentTo([withParent.Id]);
    await Assert.That(equalsNullVariable).IsEquivalentTo([withoutParent.Id]);
    await Assert.That(equalsVariable).IsEquivalentTo([withParent.Id]);
  }

  /// <summary>
  /// A context without the extension, mapped by the convention alone, reads and queries a row the atomic upsert wrote,
  /// including an id two levels down, and writes the same document itself.
  /// </summary>
  [Test]
  public async Task Convention_ReadsQueriesAndWritesTheAtomicUpsertsBytesAsync() {
    var carrierId = TestOrderId.New();
    var row = _row(TestOrderId.New(), TestOrderId.New(), TestOrderId.New(), carrierId);
    var upsertOptions = PerspectiveDocumentSerialization.Options;
    var upsertJson = System.Text.Json.JsonSerializer.Serialize(row.Data, upsertOptions);
    await using (var connection = await _dataSource!.OpenConnectionAsync())
    await using (var insert = new NpgsqlCommand("INSERT INTO wh_per_probe (id, data) VALUES (@id, CAST(@data AS jsonb))", connection)) {
      insert.Parameters.AddWithValue("id", row.Id);
      insert.Parameters.AddWithValue("data", upsertJson);
      await insert.ExecuteNonQueryAsync();
    }

    await using var db = _context(o => new ConventionContext(o));
    var loaded = await db.Rows.AsNoTracking().SingleAsync(r => r.Id == row.Id);
    var query = db.Rows.AsNoTracking().Where(r => r.Data.Shipment.Carrier.CarrierId == carrierId);

    await Assert.That(upsertJson).Contains($"\"CarrierId\":\"{carrierId.Value}\"");
    await Assert.That(loaded.Data.OrderId).IsEqualTo(row.Data.OrderId);
    await Assert.That(loaded.Data.ParentId).IsEqualTo(row.Data.ParentId);
    await Assert.That(loaded.Data.Lines[0].LineId).IsEqualTo(row.Data.Lines[0].LineId);
    await Assert.That(loaded.Data.Shipment.Carrier.CarrierId).IsEqualTo(carrierId);
    await Assert.That(query.ToQueryString()).Contains("AS uuid)");
    await Assert.That(await query.Select(r => r.Id).ToListAsync()).IsEquivalentTo([row.Id]);

    // Every id set: a null member is the one difference, pinned on its own below.
    var written = _row(TestOrderId.New(), TestOrderId.New(), TestOrderId.New(), TestOrderId.New());
    db.Rows.Add(written);
    await db.SaveChangesAsync();
    await using var compare = await _dataSource!.OpenConnectionAsync();
    await using var same = new NpgsqlCommand("SELECT data = CAST(@upsert AS jsonb) FROM wh_per_probe WHERE id = @id", compare);
    same.Parameters.AddWithValue("id", written.Id);
    same.Parameters.AddWithValue("upsert", System.Text.Json.JsonSerializer.Serialize(written.Data, upsertOptions));
    await Assert.That((bool)(await same.ExecuteScalarAsync())!).IsTrue()
      .Because("the convention's writer and the atomic upsert must store the same document");
  }

  /// <summary>
  /// The one difference between the two writers: the atomic upsert leaves a null id out of the document, where Entity
  /// Framework writes the key with <c>null</c>. Both read back as null and both match a null comparison.
  /// </summary>
  [Test]
  public async Task Convention_ANullIdIsLeftOutByTheUpsertAndWrittenAsNullByEntityFrameworkAsync() {
    var byUpsert = _row(TestOrderId.New(), null, TestOrderId.New());
    var upsertJson = System.Text.Json.JsonSerializer.Serialize(byUpsert.Data, PerspectiveDocumentSerialization.Options);
    await using (var connection = await _dataSource!.OpenConnectionAsync())
    await using (var insert = new NpgsqlCommand("INSERT INTO wh_per_probe (id, data) VALUES (@id, CAST(@data AS jsonb))", connection)) {
      insert.Parameters.AddWithValue("id", byUpsert.Id);
      insert.Parameters.AddWithValue("data", upsertJson);
      await insert.ExecuteNonQueryAsync();
    }
    await using var db = _context(o => new ConventionContext(o));
    var byEntityFramework = _row(TestOrderId.New(), null, TestOrderId.New());
    db.Rows.Add(byEntityFramework);
    await db.SaveChangesAsync();

    var loaded = await db.Rows.AsNoTracking().Select(r => new { r.Id, r.Data.ParentId }).ToListAsync();
    var matched = await db.Rows.AsNoTracking().Where(r => r.Data.ParentId == null).Select(r => r.Id).ToListAsync();

    await Assert.That(upsertJson).DoesNotContain("ParentId");
    await Assert.That(await _storedTextAsync(byEntityFramework.Id)).Contains("\"ParentId\": null");
    await Assert.That(loaded.All(r => r.ParentId is null)).IsTrue();
    await Assert.That(matched).IsEquivalentTo([byUpsert.Id, byEntityFramework.Id]);
  }

  /// <summary>
  /// A collection of ids with the extension is a JSON array of UUID strings, and <c>Contains</c> over it translates.
  /// </summary>
  [Test]
  public async Task Whizbang_ListOfIdsIsAnArrayOfUuidStringsAndContainsTranslatesAsync() {
    var first = TestOrderId.New();
    var second = TestOrderId.New();
    await using var db = _context(o => new WhizbangListContext(o));
    var row = new ListRow { Id = Guid.CreateVersion7(), Data = new ListDocument { OrderIds = [first, second] } };
    db.Rows.Add(row);
    db.Rows.Add(new ListRow { Id = Guid.CreateVersion7(), Data = new ListDocument { OrderIds = [TestOrderId.New()] } });
    await db.SaveChangesAsync();

    await using var connection = await _dataSource!.OpenConnectionAsync();
    await using var select = new NpgsqlCommand("SELECT data::text FROM wh_per_probe_list WHERE id = @id", connection);
    select.Parameters.AddWithValue("id", row.Id);
    var stored = (string)(await select.ExecuteScalarAsync())!;
    var matched = await db.Rows.AsNoTracking().Where(r => r.Data.OrderIds.Contains(second)).Select(r => r.Id).ToListAsync();

    await Assert.That(stored).Contains($"\"OrderIds\": [\"{first.Value}\", \"{second.Value}\"]");
    await Assert.That(matched).IsEquivalentTo([row.Id]);
  }

  /// <summary>
  /// The convention leaves a collection of ids as Entity Framework discovers it, and on a context without the extension
  /// Entity Framework cannot build that model: a collection of ids needs the extension.
  /// </summary>
  [Test]
  public async Task Convention_ListOfIdsCannotBuildTheModelAsync() {
    await using var db = _context(o => new ConventionListContext(o));

    await Assert.That(() => db.Model).Throws<InvalidOperationException>();
  }

  /// <summary>
  /// A row still in the object form an earlier release wrote, which the stored-form rewrite has not reached or which an
  /// instance of that release wrote after the rewrite settled, is found by every query that reads an id in SQL (#2518).
  /// </summary>
  [Test]
  public async Task Whizbang_EveryIdQueryFindsARowStillInTheObjectFormAsync() {
    var orderId = TestOrderId.New();
    var parentId = TestOrderId.New();
    var lineId = TestOrderId.New();
    var carrierId = TestOrderId.New();
    var old = _row(orderId, parentId, lineId, carrierId);
    await using (var structural = _context(o => new StructuralContext(o))) {
      structural.Rows.Add(old);
      structural.Rows.Add(_row(TestOrderId.New(), null, TestOrderId.New()));
      await structural.SaveChangesAsync();
    }
    await Assert.That(await _storedTextAsync(old.Id)).Contains("\"OrderId\": {\"Value\"");
    await using var db = _context(o => new WhizbangContext(o));
    var rows = db.Rows.AsNoTracking();
    var wanted = new[] { orderId };
    TestOrderId? parent = parentId;

    await Assert.That(await rows.Where(r => r.Data.OrderId == orderId).Select(r => r.Id).ToListAsync()).IsEquivalentTo([old.Id]);
    await Assert.That(await rows.Where(r => r.Data.ParentId == parent).Select(r => r.Id).ToListAsync()).IsEquivalentTo([old.Id]);
    await Assert.That(await rows.Where(r => wanted.Contains(r.Data.OrderId)).Select(r => r.Id).ToListAsync()).IsEquivalentTo([old.Id]);
    await Assert.That(await rows.Where(r => r.Data.Shipment.Carrier.CarrierId == carrierId).Select(r => r.Id).ToListAsync()).IsEquivalentTo([old.Id]);
    await Assert.That(await rows.Where(r => r.Data.Lines.Any(l => l.LineId == lineId)).Select(r => r.Id).ToListAsync()).IsEquivalentTo([old.Id]);
    await Assert.That(await rows.Where(r => r.Id == old.Id).Select(r => r.Data.ParentId).SingleAsync()).IsEqualTo(parent);
  }

  /// <summary>
  /// Ordering by an id sorts by the id itself whichever form a row holds it in, ascending and descending, with a null id
  /// where PostgreSQL puts one: last ascending, first descending (#2518). The object-form row holds the lowest ids, so a
  /// sort on the stored text, where <c>{</c> follows every hex digit, would put it last.
  /// </summary>
  [Test]
  public async Task Whizbang_OrderingByAnIdSortsRowsInEitherStoredFormAsync() {
    // Minted in order, so each is lower than the next.
    var (lowOrder, middleOrder, highOrder) = (TestOrderId.New(), TestOrderId.New(), TestOrderId.New());
    var (lowParent, highParent) = (TestOrderId.New(), TestOrderId.New());
    var old = _row(lowOrder, lowParent, TestOrderId.New());
    var orphan = _row(middleOrder, null, TestOrderId.New());
    var current = _row(highOrder, highParent, TestOrderId.New());
    await using (var structural = _context(o => new StructuralContext(o))) {
      structural.Rows.Add(old);
      await structural.SaveChangesAsync();
    }
    await using var db = _context(o => new WhizbangContext(o));
    db.Rows.AddRange(orphan, current);
    await db.SaveChangesAsync();
    await Assert.That(await _storedTextAsync(old.Id)).Contains("\"OrderId\": {\"Value\"");
    await Assert.That(await _storedTextAsync(current.Id)).Contains($"\"OrderId\": \"{highOrder.Value}\"");
    var rows = db.Rows.AsNoTracking();

    var byOrder = await rows.OrderBy(r => r.Data.OrderId).Select(r => r.Id).ToListAsync();
    var byOrderDescending = await rows.OrderByDescending(r => r.Data.OrderId).Select(r => r.Id).ToListAsync();
    var byParent = await rows.OrderBy(r => r.Data.ParentId).Select(r => r.Id).ToListAsync();
    var byParentDescending = await rows.OrderByDescending(r => r.Data.ParentId).Select(r => r.Id).ToListAsync();

    await Assert.That(byOrder).IsEquivalentTo([old.Id, orphan.Id, current.Id], CollectionOrdering.Matching);
    await Assert.That(byOrderDescending).IsEquivalentTo([current.Id, orphan.Id, old.Id], CollectionOrdering.Matching);
    await Assert.That(byParent).IsEquivalentTo([old.Id, current.Id, orphan.Id], CollectionOrdering.Matching);
    await Assert.That(byParentDescending).IsEquivalentTo([orphan.Id, current.Id, old.Id], CollectionOrdering.Matching);
  }

  /// <summary>A collection of ids still in the object form is found by <c>Contains</c> (#2518).</summary>
  [Test]
  public async Task Whizbang_ContainsOverAListOfIdsFindsElementsStillInTheObjectFormAsync() {
    var wanted = TestOrderId.New();
    var rowId = Guid.CreateVersion7();
    await using (var connection = await _dataSource!.OpenConnectionAsync())
    await using (var insert = new NpgsqlCommand("INSERT INTO wh_per_probe_list (id, data) VALUES (@id, CAST(@data AS jsonb))", connection)) {
      insert.Parameters.AddWithValue("id", rowId);
      insert.Parameters.AddWithValue("data", $$"""{"OrderIds": [{"Value": "{{TestOrderId.New().Value}}"}, {"Value": "{{wanted.Value}}"}]}""");
      await insert.ExecuteNonQueryAsync();
    }
    await using var db = _context(o => new WhizbangListContext(o));

    var matched = await db.Rows.AsNoTracking().Where(r => r.Data.OrderIds.Contains(wanted)).Select(r => r.Id).ToListAsync();

    await Assert.That(matched).IsEquivalentTo([rowId]);
  }
}
