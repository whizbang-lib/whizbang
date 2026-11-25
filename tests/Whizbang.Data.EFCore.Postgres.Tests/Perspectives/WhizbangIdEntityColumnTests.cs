// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
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
/// A strongly-typed id outside a perspective document is a <c>uuid</c> column named after its property (#2518). A table
/// an earlier release created holds the id in the column Entity Framework gave the complex id's <c>Value</c>,
/// <c>&lt;column&gt;_Value</c>; <c>migrations add</c> scaffolds the move as a rename, so the values stay.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#strong-id-columns</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/WhizbangIdTypeMapping.cs</code-under-test>
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

  /// <summary>
  /// A generated context without the extension: <see cref="WhizbangIdConvention.Apply"/> in its model and
  /// <see cref="WhizbangIdConvention.Configure"/> in its options, as the generated context calls them. Without the type
  /// mapping an entity's own id has to be configured, and stays as configured; the convention replaces ids inside a
  /// complex type.
  /// </summary>
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

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
      WhizbangIdConvention.Configure(optionsBuilder);
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

  /// <summary>What <c>migrations add</c> scaffolds on <paramref name="current"/> from <paramref name="earlier"/>'s model.</summary>
  private static IReadOnlyList<MigrationOperation> _migrationsAdd(DbContext earlier, DbContext current) =>
    current.GetService<IMigrationsModelDiffer>().GetDifferences(
      earlier.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
      current.GetService<IDesignTimeModel>().Model.GetRelationalModel());

  [Test]
  public async Task AnIdOutsideADocument_IsAColumnNamedAfterItsProperty_OnEitherPathAsync() {
    await using var earlier = _context<EarlierContext>(whizbang: false);
    await using var current = _context<CurrentContext>(whizbang: true);
    await using var convention = _context<ConventionContext>(whizbang: false);

    var (before, after, generated) = (_columns(earlier), _columns(current), _columns(convention));

    await Assert.That(before["Depot.DepotId.Value"]).IsEqualTo("Depot_DepotId_Value");
    await Assert.That(before["OrderId.Value"]).IsEqualTo("OrderId_Value");
    await Assert.That(after["Depot.DepotId"]).IsEqualTo("Depot_DepotId");
    await Assert.That(after["OrderId"]).IsEqualTo("OrderId");
    await Assert.That(after["ParentId"]).IsEqualTo("ParentId");
    await Assert.That(after["Converted"]).IsEqualTo("Converted")
      .Because("an id with a converter of the model's own was a column named after the property before too");
    await Assert.That(generated["Depot.DepotId"]).IsEqualTo("Depot_DepotId");
    await Assert.That(generated["OrderId.Value"]).IsEqualTo("OrderId_Value")
      .Because("an entity's own complex id stays as it was configured");
  }

  /// <summary>
  /// <c>migrations add</c> against a table an earlier release created scaffolds each id's move as a rename, never a drop
  /// and an add, and once applied this release reads the values the earlier release wrote.
  /// </summary>
  [Test]
  public async Task MigrationsAdd_FromAnEarlierModel_RenamesTheValueColumn_AndKeepsItsDataAsync() {
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

    IReadOnlyList<MigrationOperation> operations;
    await using (var earlier = _context<EarlierContext>(whizbang: false))
    await using (var current = _context<CurrentContext>(whizbang: true)) {
      operations = _migrationsAdd(earlier, current);
      var commands = current.GetService<IMigrationsSqlGenerator>().Generate(operations, current.Model);
      foreach (var command in commands) {
        await current.Database.ExecuteSqlRawAsync(command.CommandText);
      }
    }
    Shipment read;
    await using (var current = _context<CurrentContext>(whizbang: true)) {
      read = await current.Set<Shipment>().AsNoTracking().SingleAsync(s => s.Id == id);
    }

    var renames = operations.OfType<RenameColumnOperation>().Select(r => $"{r.Name}->{r.NewName}").Order().ToList();
    await Assert.That(renames).IsEquivalentTo(["Depot_DepotId_Value->Depot_DepotId", "OrderId_Value->OrderId", "ParentId_Value->ParentId"]);
    await Assert.That(operations.OfType<DropColumnOperation>()).IsEmpty().Because("no id's values are dropped");
    await Assert.That(operations.OfType<AddColumnOperation>()).IsEmpty();
    await Assert.That(read.OrderId).IsEqualTo(order);
    await Assert.That(read.ParentId).IsEqualTo(parent);
    await Assert.That(read.Depot.DepotId).IsEqualTo(depot);
    await Assert.That(read.Converted).IsEqualTo(converted);
  }

  /// <summary>A generated context without the extension scaffolds its id's move as a rename too.</summary>
  [Test]
  public async Task MigrationsAdd_OnAContextWithoutTheExtension_RenamesTooAsync() {
    await using var earlier = _context<EarlierContext>(whizbang: false);
    await using var convention = _context<ConventionContext>(whizbang: false);

    var operations = _migrationsAdd(earlier, convention);

    await Assert.That(operations.OfType<RenameColumnOperation>().Select(r => $"{r.Name}->{r.NewName}").ToList())
      .IsEquivalentTo(["Depot_DepotId_Value->Depot_DepotId"]);
    await Assert.That(operations.OfType<DropColumnOperation>()).IsEmpty();
  }
}
