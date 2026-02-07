using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for full LINQ support on JSONB columns.
/// Verifies that EF Core 10's ComplexProperty().ToJson() works correctly with:
/// - Property access (Where, OrderBy, Select)
/// - Nested property access
/// - Collection operations (Any, Contains, Count)
/// - String functions (Contains, StartsWith)
/// - Scope queries (TenantId, Extensions)
/// </summary>
/// <remarks>
/// These tests validate Phase 6 of the "Full LINQ Support for JSONB Columns" plan.
/// They ensure pure LINQ queries work without raw SQL or EF.Functions calls.
/// </remarks>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class FullLinqSupportTests : IAsyncDisposable {
  private static readonly Uuid7IdProvider _idProvider = new();

  static FullLinqSupportTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;
  private LinqTestDbContext? _context;
  private string _connectionString = null!;

  /// <summary>
  /// Address model for testing nested property access.
  /// </summary>
  public class Address {
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? ZipCode { get; set; }
  }

  /// <summary>
  /// Order item for testing collection queries.
  /// </summary>
  public class OrderItem {
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public decimal Total => Price * Quantity;
  }

  /// <summary>
  /// Test model with various property types for comprehensive LINQ testing.
  /// </summary>
  public class CustomerOrder {
    public string CustomerName { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    public Address? ShippingAddress { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public bool IsUrgent { get; set; }
    public DateTimeOffset OrderDate { get; set; }
  }

  /// <summary>
  /// DbContext using EF Core 10 ComplexProperty().ToJson() pattern for full LINQ support.
  /// </summary>
  /// <remarks>
  /// ComplexProperty().ToJson() provides:
  /// - Full LINQ query support (Where, OrderBy, Select on nested properties)
  /// - Collection queries (Any, Contains, Count) translated to server-side SQL
  /// - String methods (Contains, StartsWith)
  /// </remarks>
  private sealed class LinqTestDbContext : DbContext {
    public LinqTestDbContext(DbContextOptions<LinqTestDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      modelBuilder.Entity<PerspectiveRow<CustomerOrder>>(entity => {
        entity.ToTable("wh_per_linq_test");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        // JSONB columns - EF Core 10 ComplexProperty().ToJson() for full LINQ support
        // This enables server-side translation of collection queries like Any(), Contains()
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => s.ToJson("scope"));

        // Physical fields as shadow properties for optimized queries
        entity.Property<string?>("customer_name").HasColumnName("customer_name").HasMaxLength(200);
        entity.Property<decimal>("total_amount").HasColumnName("total_amount");
        entity.Property<string>("status").HasColumnName("status").HasMaxLength(50);

        entity.HasIndex("customer_name");
        entity.HasIndex("total_amount");
        entity.HasIndex("status");
      });
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"linq_test_{Guid.NewGuid():N}";

    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    };
    _connectionString = builder.ConnectionString;

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
    dataSourceBuilder.EnableDynamicJson();
    _dataSource = dataSourceBuilder.Build();

    // Register physical fields
    PhysicalFieldRegistry.Clear();
    PhysicalFieldRegistry.Register<CustomerOrder>("CustomerName", "customer_name");
    PhysicalFieldRegistry.Register<CustomerOrder>("TotalAmount", "total_amount");
    PhysicalFieldRegistry.Register<CustomerOrder>("Status", "status");

    var optionsBuilder = new DbContextOptionsBuilder<LinqTestDbContext>();
    optionsBuilder
        .UseNpgsql(_dataSource)
        .UseWhizbangPhysicalFields()
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    _context = new LinqTestDbContext(optionsBuilder.Options);

    await _initializeSchemaAsync();
    await _seedTestDataAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_context != null) {
      await _context.DisposeAsync();
      _context = null;
    }

    if (_dataSource != null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }

    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();

        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}'
          AND pid <> pg_backend_pid()");

        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch {
        // Ignore cleanup errors
      }

      _testDatabaseName = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private async Task _initializeSchemaAsync() {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    await connection.ExecuteAsync("""
      CREATE TABLE IF NOT EXISTS wh_per_linq_test (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        version INTEGER NOT NULL,
        customer_name VARCHAR(200),
        total_amount DECIMAL NOT NULL DEFAULT 0,
        status VARCHAR(50)
      );

      CREATE INDEX IF NOT EXISTS idx_linq_test_customer_name ON wh_per_linq_test(customer_name);
      CREATE INDEX IF NOT EXISTS idx_linq_test_total_amount ON wh_per_linq_test(total_amount);
      CREATE INDEX IF NOT EXISTS idx_linq_test_status ON wh_per_linq_test(status);

      -- GIN indexes for JSONB queries
      CREATE INDEX IF NOT EXISTS idx_linq_test_data_gin ON wh_per_linq_test USING gin (data);
      CREATE INDEX IF NOT EXISTS idx_linq_test_scope_gin ON wh_per_linq_test USING gin (scope);
      """);
  }

  private async Task _seedTestDataAsync() {
    var strategy = new PostgresUpsertStrategy();

    var testOrders = new[] {
      new CustomerOrder {
        CustomerName = "Acme Corp",
        TotalAmount = 150.00m,
        Status = "completed",
        ShippingAddress = new Address { City = "New York", State = "NY", ZipCode = "10001" },
        Items = [
          new OrderItem { ProductName = "Widget A", Price = 50.00m, Quantity = 2 },
          new OrderItem { ProductName = "Widget B", Price = 50.00m, Quantity = 1 }
        ],
        Tags = ["wholesale", "priority"],
        IsUrgent = true,
        OrderDate = DateTimeOffset.UtcNow.AddDays(-1)
      },
      new CustomerOrder {
        CustomerName = "TechStart Inc",
        TotalAmount = 75.00m,
        Status = "pending",
        ShippingAddress = new Address { City = "San Francisco", State = "CA", ZipCode = "94102" },
        Items = [
          new OrderItem { ProductName = "Gadget X", Price = 75.00m, Quantity = 1 }
        ],
        Tags = ["startup", "tech"],
        IsUrgent = false,
        OrderDate = DateTimeOffset.UtcNow
      },
      new CustomerOrder {
        CustomerName = "Global Industries",
        TotalAmount = 500.00m,
        Status = "completed",
        ShippingAddress = new Address { City = "Chicago", State = "IL", ZipCode = "60601" },
        Items = [
          new OrderItem { ProductName = "Premium Package", Price = 250.00m, Quantity = 2 }
        ],
        Tags = ["enterprise", "wholesale", "vip"],
        IsUrgent = false,
        OrderDate = DateTimeOffset.UtcNow.AddDays(-7)
      }
    };

    var tenants = new[] { "tenant-001", "tenant-002", "tenant-001" };
    var regions = new[] { "us-east", "us-west", "us-midwest" };

    for (int i = 0; i < testOrders.Length; i++) {
      var order = testOrders[i];
      var metadata = new PerspectiveMetadata {
        EventType = "OrderCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      };
      var scope = new PerspectiveScope {
        TenantId = tenants[i],
        Extensions = [new ScopeExtension("region", regions[i])]
      };

      var id = _idProvider.NewGuid();
      var physicalFields = new Dictionary<string, object?> {
        { "customer_name", order.CustomerName },
        { "total_amount", order.TotalAmount },
        { "status", order.Status }
      };

      await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
          _context!,
          "wh_per_linq_test",
          id,
          order,
          metadata,
          scope,
          physicalFields);
    }
  }

  // ==================== PROPERTY ACCESS TESTS ====================

  /// <summary>
  /// Test simple property access in Where clause.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Where_SimpleProperty_FiltersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Where(r => r.Data.CustomerName == "Acme Corp")
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.CustomerName).IsEqualTo("Acme Corp");
  }

  /// <summary>
  /// Test numeric comparison in Where clause.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Where_NumericComparison_FiltersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act - query orders > $100
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Where(r => r.Data.TotalAmount > 100.00m)
        .ToListAsync(cancellationToken);

    // Assert - Acme Corp ($150) and Global Industries ($500)
    await Assert.That(results).Count().IsEqualTo(2);
  }

  /// <summary>
  /// Test boolean property in Where clause.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Where_BooleanProperty_FiltersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act - query urgent orders
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Where(r => r.Data.IsUrgent)
        .ToListAsync(cancellationToken);

    // Assert - only Acme Corp
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.CustomerName).IsEqualTo("Acme Corp");
  }

  // ==================== NESTED PROPERTY ACCESS TESTS ====================

  /// <summary>
  /// Test nested property access in Where clause.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Where_NestedProperty_FiltersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act - query by city
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Where(r => r.Data.ShippingAddress!.City == "New York")
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.CustomerName).IsEqualTo("Acme Corp");
  }

  /// <summary>
  /// Test multiple nested property conditions.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Where_MultipleNestedProperties_FiltersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act - query by state
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Where(r => r.Data.ShippingAddress!.State == "CA" || r.Data.ShippingAddress!.State == "NY")
        .ToListAsync(cancellationToken);

    // Assert - NY (Acme) and CA (TechStart)
    await Assert.That(results).Count().IsEqualTo(2);
  }

  // ==================== STRING FUNCTION TESTS ====================

  /// <summary>
  /// Test string Contains in Where clause.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Where_StringContains_FiltersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Where(r => r.Data.CustomerName.Contains("Corp"))
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.CustomerName).IsEqualTo("Acme Corp");
  }

  /// <summary>
  /// Test string StartsWith in Where clause.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Where_StringStartsWith_FiltersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Where(r => r.Data.CustomerName.StartsWith("Tech"))
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.CustomerName).IsEqualTo("TechStart Inc");
  }

  // ==================== SCOPE QUERY TESTS ====================

  /// <summary>
  /// Test Scope.TenantId in Where clause.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Where_ScopeTenantId_FiltersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Where(r => r.Scope.TenantId == "tenant-001")
        .ToListAsync(cancellationToken);

    // Assert - Acme Corp and Global Industries have tenant-001
    await Assert.That(results).Count().IsEqualTo(2);
  }

  /// <summary>
  /// Test Scope.Extensions query using server-side LINQ with ComplexProperty().ToJson().
  /// </summary>
  /// <remarks>
  /// With ComplexProperty().ToJson(), collection queries like Extensions.Any() are
  /// translated to server-side SQL, enabling efficient indexed queries.
  /// </remarks>
  [Test]
  [Timeout(60000)]
  public async Task Where_ScopeExtensionsAny_FiltersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act - find orders in us-west region
    // Server-side evaluation via ComplexProperty().ToJson()
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Where(r => r.Scope.Extensions.Any(e => e.Key == "region" && e.Value == "us-west"))
        .ToListAsync(cancellationToken);

    // Assert - only TechStart in us-west
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.CustomerName).IsEqualTo("TechStart Inc");
  }

  // ==================== ORDERBY TESTS ====================

  /// <summary>
  /// Test OrderBy on Data property.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task OrderBy_DataProperty_SortsCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .OrderBy(r => r.Data.TotalAmount)
        .ToListAsync(cancellationToken);

    // Assert - ascending order
    await Assert.That(results).Count().IsEqualTo(3);
    await Assert.That(results[0].Data.TotalAmount).IsEqualTo(75.00m);
    await Assert.That(results[1].Data.TotalAmount).IsEqualTo(150.00m);
    await Assert.That(results[2].Data.TotalAmount).IsEqualTo(500.00m);
  }

  /// <summary>
  /// Test OrderByDescending on Data property.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task OrderByDescending_DataProperty_SortsCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .OrderByDescending(r => r.Data.TotalAmount)
        .ToListAsync(cancellationToken);

    // Assert - descending order
    await Assert.That(results).Count().IsEqualTo(3);
    await Assert.That(results[0].Data.TotalAmount).IsEqualTo(500.00m);
    await Assert.That(results[1].Data.TotalAmount).IsEqualTo(150.00m);
    await Assert.That(results[2].Data.TotalAmount).IsEqualTo(75.00m);
  }

  // ==================== SELECT PROJECTION TESTS ====================

  /// <summary>
  /// Test Select projection with anonymous type.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Select_AnonymousProjection_WorksCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Select(r => new { r.Data.CustomerName, r.Data.TotalAmount })
        .OrderBy(r => r.TotalAmount)
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(results).Count().IsEqualTo(3);
    await Assert.That(results[0].CustomerName).IsEqualTo("TechStart Inc");
    await Assert.That(results[0].TotalAmount).IsEqualTo(75.00m);
  }

  // ==================== COMBINED QUERY TESTS ====================

  /// <summary>
  /// Test combined Where, OrderBy, and Select.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task CombinedQuery_WhereOrderBySelect_WorksCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act - get completed orders sorted by amount
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Where(r => r.Data.Status == "completed")
        .OrderByDescending(r => r.Data.TotalAmount)
        .Select(r => new { r.Data.CustomerName, r.Data.TotalAmount })
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].CustomerName).IsEqualTo("Global Industries");
    await Assert.That(results[1].CustomerName).IsEqualTo("Acme Corp");
  }

  /// <summary>
  /// Test query combining Data and Scope conditions.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task CombinedQuery_DataAndScope_WorksCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act - completed orders in tenant-001
    var results = await _context!.Set<PerspectiveRow<CustomerOrder>>()
        .Where(r => r.Data.Status == "completed")
        .Where(r => r.Scope.TenantId == "tenant-001")
        .ToListAsync(cancellationToken);

    // Assert - only Global Industries (Acme is completed but tenant-001, Global is completed and tenant-001)
    await Assert.That(results).Count().IsEqualTo(2); // Both Acme and Global are tenant-001 and completed
  }
}
