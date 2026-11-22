// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Probe for #2518: can EF Core 10 map a <c>[WhizbangId]</c> inside a <c>ComplexProperty().ToJson()</c> document as a
/// JSON scalar (a UUID string) instead of the structural <c>{"Value": ...}</c> it discovers by default, and do id
/// equality and <c>Contains</c> over such ids translate to SQL?
/// </summary>
/// <remarks>
/// Two contexts over the same table shape: <see cref="StructuralContext"/> is today's mapping (the id discovered as a
/// nested complex type); <see cref="ScalarContext"/> adds a pre-convention value converter per id type, the mapping the
/// issue proposes. Each test prints the SQL it translates so the probe's findings can be read from the run.
/// </remarks>
[Category("Integration")]
[Category("Shard3")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "#1954: the [After(Test)] hook disposes them.")]
public class StrongIdScalarDocumentProbeTests {
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

  private sealed class TestOrderIdConverter() : ValueConverter<TestOrderId, Guid>(id => id.Value, guid => new TestOrderId(guid));

  private abstract class ProbeContext(DbContextOptions options) : DbContext(options) {
    public DbSet<ProbeRow> Rows => Set<ProbeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
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

  private sealed class ScalarContext(DbContextOptions options) : ProbeContext(options) {
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
      configurationBuilder.Properties<TestOrderId>().HaveConversion<TestOrderIdConverter>();
  }

  /// <summary>Reads a UUID string, or the object form <c>{"Value": ...}</c> an older writer left; writes the string.</summary>
  public sealed class TolerantGuidReaderWriter : JsonValueReaderWriter<TestOrderId> {
    public static TolerantGuidReaderWriter Instance { get; } = new();

    public override TestOrderId FromJsonTyped(ref Utf8JsonReaderManager manager, object? existingObject = null) {
      if (manager.CurrentReader.TokenType == JsonTokenType.String) {
        return new TestOrderId(manager.CurrentReader.GetGuid());
      }
      Guid value = Guid.Empty;
      while (true) {
        manager.MoveNext();
        if (manager.CurrentReader.TokenType == JsonTokenType.EndObject) {
          return new TestOrderId(value);
        }
        var isValue = manager.CurrentReader.ValueTextEquals("Value");
        manager.MoveNext();
        if (isValue) {
          value = manager.CurrentReader.GetGuid();
        } else {
          manager.Skip();
        }
      }
    }

    public override void ToJsonTyped(Utf8JsonWriter writer, TestOrderId value) => writer.WriteStringValue(value.Value);

    public override System.Linq.Expressions.Expression ConstructorExpression =>
      System.Linq.Expressions.Expression.Property(null, typeof(TolerantGuidReaderWriter).GetProperty(nameof(Instance))!);
  }

  /// <summary>Gives every strong-id property in the model the tolerant reader/writer, the way the temporal convention does.</summary>
  private sealed class TolerantIdConvention : IModelFinalizingConvention {
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context) {
      foreach (var entity in modelBuilder.Metadata.GetEntityTypes()) {
        foreach (var complex in entity.GetComplexProperties()) {
          _walk(complex.ComplexType);
        }
      }
    }

    private static void _walk(IConventionComplexType type) {
      foreach (var property in type.GetProperties()) {
        if (Nullable.GetUnderlyingType(property.ClrType) == typeof(TestOrderId) || property.ClrType == typeof(TestOrderId)) {
          property.SetJsonValueReaderWriterType(typeof(TolerantGuidReaderWriter), fromDataAnnotation: false);
        }
      }
      foreach (var nested in type.GetComplexProperties()) {
        _walk(nested.ComplexType);
      }
    }
  }

  private sealed class TolerantContext(DbContextOptions options) : ProbeContext(options) {
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) {
      configurationBuilder.Properties<TestOrderId>().HaveConversion<TestOrderIdConverter>();
      configurationBuilder.Conventions.Add(_ => new TolerantIdConvention());
    }
  }

  [Test]
  public async Task Tolerant_ReadsTheObjectFormAndWritesTheScalarAsync() {
    var orderId = TestOrderId.New();
    var lineId = TestOrderId.New();
    var parentId = TestOrderId.New();
    var old = _row(orderId, parentId, lineId);
    await using (var write = _context(o => new StructuralContext(o))) {
      write.Rows.Add(old);
      await write.SaveChangesAsync();
    }

    await using var db = _context(o => new TolerantContext(o));
    var loaded = await db.Rows.SingleAsync(r => r.Id == old.Id);
    await Assert.That(loaded.Data.OrderId).IsEqualTo(orderId);
    await Assert.That(loaded.Data.ParentId).IsEqualTo(parentId);
    await Assert.That(loaded.Data.Lines[0].LineId).IsEqualTo(lineId);

    var fresh = _row(TestOrderId.New(), null, TestOrderId.New());
    db.Rows.Add(fresh);
    await db.SaveChangesAsync();
    var stored = await _storedTextAsync(fresh.Id);
    Console.WriteLine($"[probe] tolerant stored: {stored}");
    await Assert.That(stored).DoesNotContain("\"Value\"");

    // A query that compares the id casts the stored text to uuid, which an object-form row cannot pass: rows must be
    // rewritten to the scalar form before any id predicate runs over them.
    var query = db.Rows.AsNoTracking().Where(r => r.Data.OrderId == orderId);
    await Assert.That(async () => await query.CountAsync())
      .Throws<PostgresException>()
      .WithMessageContaining("invalid input syntax for type uuid");
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = (await PerTestDatabaseFactory.CreateAsync("strong_id_probe")).Name;
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
    var options = new DbContextOptionsBuilder()
      .UseNpgsql(_dataSource!)
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
    Console.WriteLine($"[probe] structural stored: {stored}");

    await Assert.That(stored).Contains($"\"OrderId\": {{\"Value\": \"{orderId.Value}\"}}");
  }

  [Test]
  public async Task Scalar_WritesEveryIdAsAUuidStringAsync() {
    await using var db = _context(o => new ScalarContext(o));
    var orderId = TestOrderId.New();
    var parentId = TestOrderId.New();
    var lineId = TestOrderId.New();
    var row = _row(orderId, parentId, lineId);
    db.Rows.Add(row);
    await db.SaveChangesAsync();

    var stored = await _storedTextAsync(row.Id);
    Console.WriteLine($"[probe] scalar stored: {stored}");

    await Assert.That(stored).Contains($"\"OrderId\": \"{orderId.Value}\"");
    await Assert.That(stored).Contains($"\"ParentId\": \"{parentId.Value}\"");
    await Assert.That(stored).Contains($"\"LineId\": \"{lineId.Value}\"");
    await Assert.That(stored).DoesNotContain("\"Value\"");
  }

  [Test]
  public async Task Scalar_RoundTripsIncludingANullIdAsync() {
    var orderId = TestOrderId.New();
    var lineId = TestOrderId.New();
    var row = _row(orderId, null, lineId);
    await using (var write = _context(o => new ScalarContext(o))) {
      write.Rows.Add(row);
      await write.SaveChangesAsync();
    }

    await using var read = _context(o => new ScalarContext(o));
    var loaded = await read.Rows.AsNoTracking().SingleAsync(r => r.Id == row.Id);

    await Assert.That(loaded.Data.OrderId).IsEqualTo(orderId);
    await Assert.That(loaded.Data.ParentId).IsNull();
    await Assert.That(loaded.Data.Lines[0].LineId).IsEqualTo(lineId);
  }

  [Test]
  public async Task Scalar_IdEqualityTranslatesAndMatchesAsync() {
    var orderId = TestOrderId.New();
    await using var db = _context(o => new ScalarContext(o));
    db.Rows.Add(_row(orderId, null, TestOrderId.New()));
    db.Rows.Add(_row(TestOrderId.New(), null, TestOrderId.New()));
    await db.SaveChangesAsync();

    var query = db.Rows.AsNoTracking().Where(r => r.Data.OrderId == orderId);
    Console.WriteLine($"[probe] scalar equality SQL: {query.ToQueryString().Replace("\n", " ")}");
    var matched = await query.ToListAsync();

    await Assert.That(matched).Count().IsEqualTo(1);
    await Assert.That(matched[0].Data.OrderId).IsEqualTo(orderId);
  }

  [Test]
  public async Task Scalar_NullableIdEqualityTranslatesAndMatchesAsync() {
    var parentId = TestOrderId.New();
    await using var db = _context(o => new ScalarContext(o));
    db.Rows.Add(_row(TestOrderId.New(), parentId, TestOrderId.New()));
    db.Rows.Add(_row(TestOrderId.New(), null, TestOrderId.New()));
    await db.SaveChangesAsync();

    var query = db.Rows.AsNoTracking().Where(r => r.Data.ParentId == parentId);
    Console.WriteLine($"[probe] scalar nullable equality SQL: {query.ToQueryString().Replace("\n", " ")}");

    await Assert.That(await query.CountAsync()).IsEqualTo(1);
  }

  [Test]
  public async Task Scalar_ContainsOverIdsTranslatesAndMatchesAsync() {
    var first = TestOrderId.New();
    var second = TestOrderId.New();
    await using var db = _context(o => new ScalarContext(o));
    db.Rows.Add(_row(first, null, TestOrderId.New()));
    db.Rows.Add(_row(second, null, TestOrderId.New()));
    db.Rows.Add(_row(TestOrderId.New(), null, TestOrderId.New()));
    await db.SaveChangesAsync();

    var wanted = new[] { first, second };
    var query = db.Rows.AsNoTracking().Where(r => wanted.Contains(r.Data.OrderId));
    Console.WriteLine($"[probe] scalar Contains SQL: {query.ToQueryString().Replace("\n", " ")}");

    await Assert.That(await query.CountAsync()).IsEqualTo(2);
  }

  /// <summary>
  /// A converter alone cannot read a row an older writer left in the object form: EF's Guid reader wants a string. The
  /// tolerant reader/writer (<see cref="Tolerant_ReadsTheObjectFormAndWritesTheScalarAsync"/>) is what closes that gap.
  /// </summary>
  [Test]
  public async Task Scalar_WithoutTheTolerantReader_CannotReadAnObjectFormRowAsync() {
    var row = _row(TestOrderId.New(), null, TestOrderId.New());
    await using (var write = _context(o => new StructuralContext(o))) {
      write.Rows.Add(row);
      await write.SaveChangesAsync();
    }

    await using var read = _context(o => new ScalarContext(o));

    await Assert.That(async () => await read.Rows.AsNoTracking().SingleAsync(r => r.Id == row.Id))
      .Throws<InvalidOperationException>()
      .WithMessageContaining("'StartObject' as a string");
  }

  [Test]
  public async Task Structural_ContainsOverIdsTranslatesAsync() {
    await using var db = _context(o => new StructuralContext(o));
    var wanted = new[] { TestOrderId.New() };

    var query = db.Rows.AsNoTracking().Where(r => wanted.Contains(r.Data.OrderId));

    // Baseline from #2084: expected to throw. The probe records what happens today.
    string outcome;
    try {
      outcome = query.ToQueryString();
    } catch (InvalidOperationException ex) {
      outcome = $"THROWS {ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
    }
    Console.WriteLine($"[probe] structural Contains: {outcome}");

    await Assert.That(outcome).IsNotNull();
  }
}
