using Dapper;
using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Lenses;
using ECommerce.InventoryWorker.Perspectives;
using ECommerce.InventoryWorker.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ECommerce.InventoryWorker.Tests.Lenses;

/// <summary>
/// Integration tests for ProductLens
/// </summary>
public class ProductLensTests : IAsyncDisposable {
  private readonly DatabaseTestHelper _dbHelper = new();

  [Test]
  public async Task GetByIdAsync_WithExistingProduct_ReturnsProductDtoAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new ProductLens(connectionFactory);

    // Create a product via perspective
    var perspective = new ProductCatalogPerspective(connectionFactory, NullLogger<ProductCatalogPerspective>.Instance);
    var productEvent = new ProductCreatedEvent {
      ProductId = "prod-123",
      Name = "Test Product",
      Description = "Test Description",
      Price = 29.99m,
      ImageUrl = "https://example.com/image.jpg",
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(productEvent, CancellationToken.None);

    // Act
    var result = await lens.GetByIdAsync("prod-123");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.ProductId).IsEqualTo("prod-123");
    await Assert.That(result.Name).IsEqualTo("Test Product");
    await Assert.That(result.Description).IsEqualTo("Test Description");
    await Assert.That(result.Price).IsEqualTo(29.99m);
    await Assert.That(result.ImageUrl).IsEqualTo("https://example.com/image.jpg");
    await Assert.That(result.DeletedAt).IsNull();
  }

  [Test]
  public async Task GetByIdAsync_WithNonExistentProduct_ReturnsNullAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new ProductLens(connectionFactory);

    // Act
    var result = await lens.GetByIdAsync("non-existent");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetByIdAsync_WithDeletedProduct_ReturnsNullAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new ProductLens(connectionFactory);

    // Create and delete a product
    var perspective = new ProductCatalogPerspective(connectionFactory, NullLogger<ProductCatalogPerspective>.Instance);
    var createdEvent = new ProductCreatedEvent {
      ProductId = "prod-deleted",
      Name = "Deleted Product",
      Description = "Will be deleted",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(createdEvent, CancellationToken.None);

    var deletedEvent = new ProductDeletedEvent {
      ProductId = "prod-deleted",
      DeletedAt = DateTime.UtcNow
    };
    await perspective.Update(deletedEvent, CancellationToken.None);

    // Act
    var result = await lens.GetByIdAsync("prod-deleted");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetAllAsync_WithNoProducts_ReturnsEmptyListAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new ProductLens(connectionFactory);

    // Act
    var result = await lens.GetAllAsync();

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).HasCount().EqualTo(0);
  }

  [Test]
  public async Task GetAllAsync_WithMultipleProducts_ReturnsAllNonDeletedAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new ProductLens(connectionFactory);
    var perspective = new ProductCatalogPerspective(connectionFactory, NullLogger<ProductCatalogPerspective>.Instance);

    // Create 3 products
    await perspective.Update(new ProductCreatedEvent {
      ProductId = "prod-1",
      Name = "Product 1",
      Description = "Desc 1",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new ProductCreatedEvent {
      ProductId = "prod-2",
      Name = "Product 2",
      Description = "Desc 2",
      Price = 20.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new ProductCreatedEvent {
      ProductId = "prod-3",
      Name = "Product 3",
      Description = "Desc 3",
      Price = 30.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Delete one
    await perspective.Update(new ProductDeletedEvent {
      ProductId = "prod-2",
      DeletedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act
    var result = await lens.GetAllAsync();

    // Assert
    await Assert.That(result).HasCount().EqualTo(2);
    await Assert.That(result.Any(p => p.ProductId == "prod-1")).IsTrue();
    await Assert.That(result.Any(p => p.ProductId == "prod-3")).IsTrue();
    await Assert.That(result.Any(p => p.ProductId == "prod-2")).IsFalse();
  }

  [Test]
  public async Task GetAllAsync_WithIncludeDeleted_ReturnsAllProductsAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new ProductLens(connectionFactory);
    var perspective = new ProductCatalogPerspective(connectionFactory, NullLogger<ProductCatalogPerspective>.Instance);

    // Create 2 products
    await perspective.Update(new ProductCreatedEvent {
      ProductId = "prod-1",
      Name = "Product 1",
      Description = "Desc 1",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new ProductCreatedEvent {
      ProductId = "prod-2",
      Name = "Product 2",
      Description = "Desc 2",
      Price = 20.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Delete one
    await perspective.Update(new ProductDeletedEvent {
      ProductId = "prod-2",
      DeletedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act
    var result = await lens.GetAllAsync(includeDeleted: true);

    // Assert
    await Assert.That(result).HasCount().EqualTo(2);
    await Assert.That(result.Any(p => p.ProductId == "prod-1")).IsTrue();
    await Assert.That(result.Any(p => p.ProductId == "prod-2")).IsTrue();
  }

  [Test]
  public async Task GetByIdsAsync_WithExistingIds_ReturnsMatchingProductsAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new ProductLens(connectionFactory);
    var perspective = new ProductCatalogPerspective(connectionFactory, NullLogger<ProductCatalogPerspective>.Instance);

    // Create 3 products
    await perspective.Update(new ProductCreatedEvent {
      ProductId = "prod-1",
      Name = "Product 1",
      Description = "Desc 1",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new ProductCreatedEvent {
      ProductId = "prod-2",
      Name = "Product 2",
      Description = "Desc 2",
      Price = 20.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new ProductCreatedEvent {
      ProductId = "prod-3",
      Name = "Product 3",
      Description = "Desc 3",
      Price = 30.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act
    var result = await lens.GetByIdsAsync(new[] { "prod-1", "prod-3" });

    // Assert
    await Assert.That(result).HasCount().EqualTo(2);
    await Assert.That(result.Any(p => p.ProductId == "prod-1")).IsTrue();
    await Assert.That(result.Any(p => p.ProductId == "prod-3")).IsTrue();
  }

  [Test]
  public async Task GetByIdsAsync_WithEmptyList_ReturnsEmptyListAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new ProductLens(connectionFactory);

    // Act
    var result = await lens.GetByIdsAsync(Array.Empty<string>());

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).HasCount().EqualTo(0);
  }

  [Test]
  public async Task GetByIdsAsync_WithMixedIds_ReturnsOnlyExistingAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new ProductLens(connectionFactory);
    var perspective = new ProductCatalogPerspective(connectionFactory, NullLogger<ProductCatalogPerspective>.Instance);

    // Create 2 products
    await perspective.Update(new ProductCreatedEvent {
      ProductId = "prod-1",
      Name = "Product 1",
      Description = "Desc 1",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new ProductCreatedEvent {
      ProductId = "prod-2",
      Name = "Product 2",
      Description = "Desc 2",
      Price = 20.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act - request existing and non-existing
    var result = await lens.GetByIdsAsync(new[] { "prod-1", "non-existent", "prod-2" });

    // Assert
    await Assert.That(result).HasCount().EqualTo(2);
    await Assert.That(result.Any(p => p.ProductId == "prod-1")).IsTrue();
    await Assert.That(result.Any(p => p.ProductId == "prod-2")).IsTrue();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _dbHelper.CleanupDatabaseAsync();
  }

  public async ValueTask DisposeAsync() {
    await _dbHelper.DisposeAsync();
  }
}
