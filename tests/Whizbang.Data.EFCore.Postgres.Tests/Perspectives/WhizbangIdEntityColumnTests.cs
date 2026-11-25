// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A strongly-typed id outside a perspective document keeps the column it had before #2518, <c>&lt;column&gt;_Value</c>,
/// so an instance of an earlier release and one of this release read and write the same column during a rolling deploy
/// and no migration is needed. A configured column name is the opt-in to a column named after the property; a converter
/// of the model's own is left alone.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#strong-id-columns</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/WhizbangIdColumnConvention.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/WhizbangIdConvention.cs</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class WhizbangIdEntityColumnTests {
  private const string TABLE = "shipments";
  private static readonly StoreObjectIdentifier _table = StoreObjectIdentifier.Table(TABLE, null);

  private string? _databaseName;
  private string _connectionString = string.Empty;

  public sealed class Depot {
    public string City { get; set; } = string.Empty;
    public TestOrderId DepotId { get; set; }
  }

  public sealed class Shipment {
    public Guid Id { get; set; }
    public Depot Depot { get; set; } = new();
    public TestOrderId OrderId { get; set; }
    public TestOrderId? ParentId { get; set; }
    public TestOrderId Converted { get; set; }
  }

  /// <summary>The model as an earlier release mapped it: each id a complex type, or a converter of the model's own.</summary>
  private sealed class EarlierContext(DbContextOptions<EarlierContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<Shipment>(e => {
        e.ToTable(TABLE);
        e.ComplexProperty(x => x.Depot);
        e.ComplexProperty(x => x.OrderId);
        e.ComplexProperty(x => x.ParentId);
        e.Property(x => x.Converted).HasConversion(v => v.Value, g => new TestOrderId(g));
      });
  }

  /// <summary>The model on a context with the Whizbang options extension, where the type mapping maps every id.</summary>
  private sealed class CurrentContext(DbContextOptions<CurrentContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<Shipment>(e => {
        e.ToTable(TABLE);
        e.ComplexProperty(x => x.Depot);
        e.Property(x => x.Converted).HasConversion(v => v.Value, g => new TestOrderId(g));
      });
  }

  /// <summary>A context with the extension that opts one id into a column named after its property.</summary>
  private sealed class OptedInContext(DbContextOptions<OptedInContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<Shipment>(e => {
        e.ToTable(TABLE);
        e.ComplexProperty(x => x.Depot);
        e.Property(x => x.OrderId).HasColumnName("OrderId");
        e.Property(x => x.Converted).HasConversion(v => v.Value, g => new TestOrderId(g));
      });
  }

  /// <summary>A generated context without the extension, which calls <see cref="WhizbangIdConvention.Apply"/>.</summary>
  private sealed class ConventionContext(DbContextOptions<ConventionContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<Shipment>(e => {
        e.ToTable(TABLE);
        e.ComplexProperty(x => x.Depot);
        e.ComplexProperty(x => x.OrderId);
        e.ComplexProperty(x => x.ParentId);
        e.Property(x => x.Converted).HasConversion(v => v.Value, g => new TestOrderId(g));
      });
      WhizbangIdConvention.Apply(modelBuilder);
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = (await PerTestDatabaseFactory.CreateAsync("id_columns")).Name;
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
    }.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private T _context<T>(bool whizbang) where T : DbContext {
    var options = new DbContextOptionsBuilder<T>()
      .UseNpgsql(_connectionString, npgsql => {
        if (whizbang) {
          npgsql.UseWhizbangFunctions();
        }
      })
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;
    return (T)Activator.CreateInstance(typeof(T), options)!;
  }

  private static Dictionary<string, string?> _columns(DbContext context) {
    var columns = new Dictionary<string, string?>(StringComparer.Ordinal);
    void collect(ITypeBase type, string prefix) {
      foreach (var property in type.GetDeclaredProperties()) {
        columns[prefix + property.Name] = property.GetColumnName(_table);
      }
      foreach (var complex in type.GetDeclaredComplexProperties()) {
        collect(complex.ComplexType, prefix + complex.Name + ".");
      }
    }
    collect(context.Model.FindEntityType(typeof(Shipment))!, string.Empty);
    return columns;
  }

  [Test]
  public async Task WithTheExtension_AnIdOutsideADocument_KeepsTheColumnItHadAsAComplexTypeAsync() {
    await using var earlier = _context<EarlierContext>(whizbang: false);
    await using var current = _context<CurrentContext>(whizbang: true);

    var before = _columns(earlier);
    var after = _columns(current);

    await Assert.That(before["Depot.DepotId.Value"]).IsEqualTo("Depot_DepotId_Value");
    await Assert.That(before["OrderId.Value"]).IsEqualTo("OrderId_Value");
    await Assert.That(after["Depot.DepotId"]).IsEqualTo("Depot_DepotId_Value");
    await Assert.That(after["OrderId"]).IsEqualTo("OrderId_Value");
    await Assert.That(after["ParentId"]).IsEqualTo("ParentId_Value");
    await Assert.That(after["Converted"]).IsEqualTo("Converted")
      .Because("an id with a converter of the model's own was a column named after the property before too");
  }

  [Test]
  public async Task WithoutTheExtension_TheGeneratedConvention_KeepsTheSameColumnsAsync() {
    await using var context = _context<ConventionContext>(whizbang: false);

    var columns = _columns(context);

    await Assert.That(columns["Depot.DepotId"]).IsEqualTo("Depot_DepotId_Value");
    await Assert.That(columns["Converted"]).IsEqualTo("Converted");
    await Assert.That(context.Model.FindEntityType(typeof(Shipment))!.FindProperty(nameof(Shipment.OrderId))).IsNull()
      .Because("an entity's own complex id stays as it was configured; only ids inside a complex type are replaced");
  }

  [Test]
  public async Task AConfiguredColumnName_IsTheOptInToAColumnNamedAfterThePropertyAsync() {
    await using var context = _context<OptedInContext>(whizbang: true);

    var columns = _columns(context);

    await Assert.That(columns["OrderId"]).IsEqualTo("OrderId");
    await Assert.That(columns["ParentId"]).IsEqualTo("ParentId_Value");
  }

  /// <summary>
  /// The rolling deploy: a table an earlier release created, written by it, is read and written by this release through
  /// the same columns, and what this release writes the earlier release reads.
  /// </summary>
  [Test]
  public async Task ATableAnEarlierReleaseCreated_IsReadAndWrittenByThisRelease_AndTheOtherWayAsync() {
    var (order, parent, depot, converted) = (TestOrderId.New(), TestOrderId.New(), TestOrderId.New(), TestOrderId.New());
    var id = Guid.CreateVersion7();
    await using (var earlier = _context<EarlierContext>(whizbang: false)) {
      await earlier.Database.EnsureCreatedAsync();
      earlier.Add(new Shipment {
        Id = id,
        OrderId = order,
        ParentId = parent,
        Converted = converted,
        Depot = new Depot { City = "Oslo", DepotId = depot },
      });
      await earlier.SaveChangesAsync();
    }

    Shipment read;
    var newOrder = TestOrderId.New();
    await using (var current = _context<CurrentContext>(whizbang: true)) {
      read = await current.Set<Shipment>().AsNoTracking().SingleAsync(s => s.Id == id);
      var tracked = await current.Set<Shipment>().SingleAsync(s => s.Id == id);
      tracked.OrderId = newOrder;
      tracked.ParentId = null;
      await current.SaveChangesAsync();
    }
    Shipment readBack;
    await using (var earlier = _context<EarlierContext>(whizbang: false)) {
      readBack = await earlier.Set<Shipment>().AsNoTracking().SingleAsync(s => s.Id == id);
    }

    await Assert.That(read.OrderId).IsEqualTo(order);
    await Assert.That(read.ParentId).IsEqualTo(parent);
    await Assert.That(read.Depot.DepotId).IsEqualTo(depot);
    await Assert.That(read.Converted).IsEqualTo(converted);
    await Assert.That(readBack.OrderId).IsEqualTo(newOrder);
    await Assert.That(readBack.ParentId).IsNull();
    await Assert.That(readBack.Depot.DepotId).IsEqualTo(depot);
  }
}
