using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for EFCorePostgresLensQuery multi-generic implementations.
/// Verifies Query&lt;T&gt;() method behavior, DbContext sharing, and disposal.
/// </summary>
[Category("EFCore")]
[Category("Lenses")]
[Category("Unit")]
public class EFCorePostgresLensQueryMultiGenericTests {
  private readonly Uuid7IdProvider _idProvider = new();

  #region Test Models

  private sealed record OrderModel {
    public required string OrderNumber { get; init; }
    public required decimal Total { get; init; }
    public Guid? CustomerId { get; init; }
  }

  private sealed record CustomerModel {
    public required string Name { get; init; }
    public required string Email { get; init; }
  }

  private sealed record ProductModel {
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public required decimal Price { get; init; }
  }

  #endregion

  #region Test DbContext

  private sealed class MultiModelDbContext : DbContext {
    public MultiModelDbContext(DbContextOptions<MultiModelDbContext> options) : base(options) { }

    public DbSet<PerspectiveRow<OrderModel>> Orders => Set<PerspectiveRow<OrderModel>>();
    public DbSet<PerspectiveRow<CustomerModel>> Customers => Set<PerspectiveRow<CustomerModel>>();
    public DbSet<PerspectiveRow<ProductModel>> Products => Set<PerspectiveRow<ProductModel>>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      // Configure PerspectiveRow for each model type
      ConfigurePerspectiveRow<OrderModel>(modelBuilder);
      ConfigurePerspectiveRow<CustomerModel>(modelBuilder);
      ConfigurePerspectiveRow<ProductModel>(modelBuilder);
    }

    private static void ConfigurePerspectiveRow<TModel>(ModelBuilder modelBuilder) where TModel : class {
      modelBuilder.Entity<PerspectiveRow<TModel>>(entity => {
        entity.HasKey(e => e.Id);

        entity.OwnsOne(e => e.Data, data => {
          data.WithOwner();
        });

        entity.OwnsOne(e => e.Metadata, metadata => {
          metadata.WithOwner();
          metadata.Property(m => m.EventType).IsRequired();
          metadata.Property(m => m.EventId).IsRequired();
          metadata.Property(m => m.Timestamp).IsRequired();
        });

        // Use JSON conversion for Scope to support complex types like Extensions
        entity.Property(e => e.Scope)
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);
      });
    }
  }

  #endregion

  #region Helper Methods

  private MultiModelDbContext CreateInMemoryDbContext() {
    var options = new DbContextOptionsBuilder<MultiModelDbContext>()
        .UseInMemoryDatabase(databaseName: _idProvider.NewGuid().ToString())
        .Options;

    return new MultiModelDbContext(options);
  }

  private async Task SeedOrderAsync(MultiModelDbContext context, Guid id, OrderModel order) {
    var row = new PerspectiveRow<OrderModel> {
      Id = id,
      Data = order,
      Metadata = new PerspectiveMetadata {
        EventType = "OrderCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope { TenantId = "test-tenant" },
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };
    context.Orders.Add(row);
    await context.SaveChangesAsync();
  }

  private async Task SeedCustomerAsync(MultiModelDbContext context, Guid id, CustomerModel customer) {
    var row = new PerspectiveRow<CustomerModel> {
      Id = id,
      Data = customer,
      Metadata = new PerspectiveMetadata {
        EventType = "CustomerCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope { TenantId = "test-tenant" },
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };
    context.Customers.Add(row);
    await context.SaveChangesAsync();
  }

  #endregion

  #region Constructor Tests

  [Test]
  public async Task Constructor_WithValidParameters_CreatesInstanceAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };

    // Act
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    // Assert
    await Assert.That(lensQuery).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithNullDbContext_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };

    // Act & Assert
    await Assert.That(() =>
        new EFCorePostgresLensQuery<OrderModel, CustomerModel>(null!, tableNames))
        .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullTableNames_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();

    // Act & Assert
    await Assert.That(() =>
        new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, null!))
        .Throws<ArgumentNullException>();
  }

  #endregion

  #region Query<T> Tests - Valid Types

  [Test]
  public async Task Query_WithT1_ReturnsQueryableAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    // Act
    var query = lensQuery.Query<OrderModel>();

    // Assert
    await Assert.That(query).IsNotNull();
    await Assert.That(query).IsAssignableTo<IQueryable<PerspectiveRow<OrderModel>>>();
  }

  [Test]
  public async Task Query_WithT2_ReturnsQueryableAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    // Act
    var query = lensQuery.Query<CustomerModel>();

    // Assert
    await Assert.That(query).IsNotNull();
    await Assert.That(query).IsAssignableTo<IQueryable<PerspectiveRow<CustomerModel>>>();
  }

  [Test]
  public async Task Query_WithT1AndT2_BothUseSameDbContextAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    // Act
    var ordersQuery = lensQuery.Query<OrderModel>();
    var customersQuery = lensQuery.Query<CustomerModel>();

    // Assert - Both queries should work and share the same DbContext
    await Assert.That(ordersQuery).IsNotNull();
    await Assert.That(customersQuery).IsNotNull();
  }

  #endregion

  #region Query<T> Tests - Invalid Types

  [Test]
  public async Task Query_WithInvalidType_ThrowsArgumentExceptionAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    // Act & Assert
    await Assert.That(() => lensQuery.Query<ProductModel>())
        .Throws<ArgumentException>();
  }

  [Test]
  public async Task Query_WithInvalidType_ExceptionMessageContainsValidTypesAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    // Act
    ArgumentException? caught = null;
    try {
      lensQuery.Query<ProductModel>();
    } catch (ArgumentException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("ProductModel");
    await Assert.That(caught.Message).Contains("OrderModel");
    await Assert.That(caught.Message).Contains("CustomerModel");
  }

  #endregion

  #region GetByIdAsync<T> Tests

  [Test]
  public async Task GetByIdAsync_WithT1_WhenExists_ReturnsModelAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    var orderId = _idProvider.NewGuid();
    var order = new OrderModel { OrderNumber = "ORD-001", Total = 99.99m };
    await SeedOrderAsync(context, orderId, order);

    // Act
    var result = await lensQuery.GetByIdAsync<OrderModel>(orderId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.OrderNumber).IsEqualTo("ORD-001");
    await Assert.That(result.Total).IsEqualTo(99.99m);
  }

  [Test]
  public async Task GetByIdAsync_WithT2_WhenExists_ReturnsModelAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    var customerId = _idProvider.NewGuid();
    var customer = new CustomerModel { Name = "John Doe", Email = "john@example.com" };
    await SeedCustomerAsync(context, customerId, customer);

    // Act
    var result = await lensQuery.GetByIdAsync<CustomerModel>(customerId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("John Doe");
    await Assert.That(result.Email).IsEqualTo("john@example.com");
  }

  [Test]
  public async Task GetByIdAsync_WhenNotExists_ReturnsNullAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    // Act
    var result = await lensQuery.GetByIdAsync<OrderModel>(Guid.NewGuid());

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetByIdAsync_WithInvalidType_ThrowsArgumentExceptionAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    // Act & Assert
    await Assert.That(async () => await lensQuery.GetByIdAsync<ProductModel>(Guid.NewGuid()))
        .Throws<ArgumentException>();
  }

  #endregion

  #region Disposal Tests

  [Test]
  public async Task DisposeAsync_DisposesDbContextAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    // Act
    await lensQuery.DisposeAsync();

    // Assert - Context should be disposed (accessing Set should throw)
    await Assert.That(() => context.Orders.ToList())
        .Throws<ObjectDisposedException>();
  }

  [Test]
  public async Task DisposeAsync_WhenCalledTwice_OnlyDisposesOnceAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel>(context, tableNames);

    // Act
    await lensQuery.DisposeAsync();

    // Assert - Second dispose should not throw
    await Assert.That(async () => await lensQuery.DisposeAsync())
        .ThrowsNothing();
  }

  #endregion

  #region Three Generic Parameter Tests

  [Test]
  public async Task ThreeGeneric_Query_WithT1_ReturnsQueryableAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" },
      { typeof(ProductModel), "products" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel, ProductModel>(context, tableNames);

    // Act
    var query = lensQuery.Query<OrderModel>();

    // Assert
    await Assert.That(query).IsNotNull();
  }

  [Test]
  public async Task ThreeGeneric_Query_WithT2_ReturnsQueryableAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" },
      { typeof(ProductModel), "products" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel, ProductModel>(context, tableNames);

    // Act
    var query = lensQuery.Query<CustomerModel>();

    // Assert
    await Assert.That(query).IsNotNull();
  }

  [Test]
  public async Task ThreeGeneric_Query_WithT3_ReturnsQueryableAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var tableNames = new Dictionary<Type, string> {
      { typeof(OrderModel), "orders" },
      { typeof(CustomerModel), "customers" },
      { typeof(ProductModel), "products" }
    };
    var lensQuery = new EFCorePostgresLensQuery<OrderModel, CustomerModel, ProductModel>(context, tableNames);

    // Act
    var query = lensQuery.Query<ProductModel>();

    // Assert
    await Assert.That(query).IsNotNull();
  }

  #endregion
}
