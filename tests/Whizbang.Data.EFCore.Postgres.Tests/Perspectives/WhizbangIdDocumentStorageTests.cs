// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A strongly-typed id inside a perspective document is stored as a UUID string on every context that carries the
/// Whizbang options extension (#2518): top level, nullable and inside a complex collection. Id equality translates to
/// a uuid comparison, and <c>Contains</c> over ids to <c>= ANY</c>.
/// </summary>
/// <remarks>
/// <see cref="StructuralContext"/> is a context without the extension or the convention, Entity Framework's own
/// discovery: the object form <c>{"Value": ...}</c>, which the id mapping refuses to read.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/WhizbangIdTypeMapping.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "#1954: the [After(Test)] hook disposes them.")]
public class WhizbangIdDocumentStorageTests {
  private string? _databaseName;
  private NpgsqlDataSource? _dataSource;

  public sealed class ProbeLine {
    public TestOrderId LineId { get; set; }
    public int Quantity { get; set; }
  }

  public sealed class ProbeDocument {
    public TestOrderId OrderId { get; set; }
    public TestOrderId? ParentId { get; set; }
    public string Name { get; set; } = "";
    public List<ProbeLine> Lines { get; set; } = [];
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
        });
      });
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
  public async Task Convention_WithoutTheExtension_WritesScalarsRoundTripsAndTranslatesAsync() {
    var row = _row(TestOrderId.New(), TestOrderId.New(), TestOrderId.New());
    await using (var write = _context(o => new ConventionContext(o))) {
      write.Rows.Add(row);
      await write.SaveChangesAsync();
    }

    var stored = await _storedTextAsync(row.Id);
    await Assert.That(stored).Contains($"\"OrderId\": \"{row.Data.OrderId.Value}\"");
    await Assert.That(stored).Contains($"\"ParentId\": \"{row.Data.ParentId!.Value.Value}\"");
    await Assert.That(stored).Contains($"\"LineId\": \"{row.Data.Lines[0].LineId.Value}\"");

    await using var db = _context(o => new ConventionContext(o));
    var loaded = await db.Rows.AsNoTracking().SingleAsync(r => r.Id == row.Id);
    await Assert.That(loaded.Data.OrderId).IsEqualTo(row.Data.OrderId);
    await Assert.That(loaded.Data.ParentId).IsEqualTo(row.Data.ParentId);
    await Assert.That(loaded.Data.Lines[0].LineId).IsEqualTo(row.Data.Lines[0].LineId);

    var wanted = new[] { row.Data.OrderId };
    await Assert.That(db.Rows.Where(r => wanted.Contains(r.Data.OrderId)).ToQueryString()).Contains("= ANY (@wanted)");
  }

  /// <summary>
  /// The object form is not a stored form the id mapping reads: a row holding one is refused, naming the id type,
  /// rather than read as something it is not.
  /// </summary>
  [Test]
  public async Task Whizbang_RefusesAnObjectFormIdNamingTheTypeAsync() {
    var row = _row(TestOrderId.New(), null, TestOrderId.New());
    await using (var write = _context(o => new StructuralContext(o))) {
      write.Rows.Add(row);
      await write.SaveChangesAsync();
    }

    await using var db = _context(o => new WhizbangContext(o));

    await Assert.That(async () => await db.Rows.AsNoTracking().SingleAsync(r => r.Id == row.Id))
      .Throws<Exception>()
      .WithMessageContaining("TestOrderId must be a UUID string");
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
    await using var create = new NpgsqlCommand("CREATE TABLE wh_per_probe (id uuid PRIMARY KEY, data jsonb NOT NULL)", connection);
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
    var whizbang = typeof(T) == typeof(WhizbangContext);
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

  private static ProbeRow _row(TestOrderId orderId, TestOrderId? parentId, TestOrderId lineId) => new() {
    Id = Guid.CreateVersion7(),
    Data = new ProbeDocument {
      OrderId = orderId,
      ParentId = parentId,
      Name = "probe",
      Lines = [new ProbeLine { LineId = lineId, Quantity = 2 }],
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

    await Assert.That(sql).Contains("CAST(w.data ->> 'OrderId' AS uuid)");

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

    await Assert.That(query.ToQueryString()).Contains("CAST(w.data ->> 'OrderId' AS uuid) = ANY (@wanted)");

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
}
