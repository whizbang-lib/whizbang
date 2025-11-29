using ECommerce.BFF.API.Lenses;
using ECommerce.BFF.API.Perspectives;
using ECommerce.BFF.API.Tests.TestHelpers;
using ECommerce.Contracts.Events;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.BFF.API.Tests.Lenses;

/// <summary>
/// Integration tests for ProductCatalogLens (BFF) using unified Whizbang API
/// </summary>
public class ProductCatalogLensTests : IAsyncDisposable {
  private readonly EFCoreTestHelper _helper = new();

  [Test]
  public async Task GetByIdAsync_WithExistingProduct_ReturnsProductDtoAsync() {
    // Arrange
    var lens = new ProductCatalogLens(_helper.GetLensQuery<ProductDto>());

    // Create a product via perspective
    var perspective = new ProductCatalogPerspective(
      _helper.GetPerspectiveStore<ProductDto>(),
      _helper.GetLensQuery<ProductDto>(),
      _helper.GetLogger<ProductCatalogPerspective>(),
      _helper.GetHubContext());

    var productId = Guid.CreateVersion7();
    var productEvent = new ProductCreatedEvent {
      ProductId = productId,
      Name = "Test Product",
      Description = "Test Description",
      Price = 29.99m,
      ImageUrl = "https://example.com/image.jpg",
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(productEvent, CancellationToken.None);

    // Act
    var result = await lens.GetByIdAsync(productId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.ProductId).IsEqualTo(productId);
    await Assert.That(result.Name).IsEqualTo("Test Product");
    await Assert.That(result.Description).IsEqualTo("Test Description");
    await Assert.That(result.Price).IsEqualTo(29.99m);
    await Assert.That(result.ImageUrl).IsEqualTo("https://example.com/image.jpg");
    await Assert.That(result.DeletedAt).IsNull();
  }

  [Test]
  public async Task GetByIdAsync_WithNonExistentProduct_ReturnsNullAsync() {
    // Arrange
    var lens = new ProductCatalogLens(_helper.GetLensQuery<ProductDto>());

    // Act
    var result = await lens.GetByIdAsync(Guid.CreateVersion7());

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetByIdAsync_WithDeletedProduct_ReturnsNullAsync() {
    // Arrange
    var lens = new ProductCatalogLens(_helper.GetLensQuery<ProductDto>());

    // Create and delete a product
    var perspective = new ProductCatalogPerspective(
      _helper.GetPerspectiveStore<ProductDto>(),
      _helper.GetLensQuery<ProductDto>(),
      _helper.GetLogger<ProductCatalogPerspective>(),
      _helper.GetHubContext());

    var productId = Guid.CreateVersion7();
    var createdEvent = new ProductCreatedEvent {
      ProductId = productId,
      Name = "Deleted Product",
      Description = "Will be deleted",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(createdEvent, CancellationToken.None);

    var deletedEvent = new ProductDeletedEvent {
      ProductId = productId,
      DeletedAt = DateTime.UtcNow
    };
    await perspective.Update(deletedEvent, CancellationToken.None);

    // Act
    var result = await lens.GetByIdAsync(productId);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetAllAsync_WithNoProducts_ReturnsEmptyListAsync() {
    // Arrange
    var lens = new ProductCatalogLens(_helper.GetLensQuery<ProductDto>());

    // Act
    var result = await lens.GetAllAsync();

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).HasCount().EqualTo(0);
  }

  [Test]
  public async Task GetAllAsync_WithMultipleProducts_ReturnsAllNonDeletedAsync() {
    // Arrange
    var lens = new ProductCatalogLens(_helper.GetLensQuery<ProductDto>());
    var perspective = new ProductCatalogPerspective(
      _helper.GetPerspectiveStore<ProductDto>(),
      _helper.GetLensQuery<ProductDto>(),
      _helper.GetLogger<ProductCatalogPerspective>(),
      _helper.GetHubContext());

    // Create 3 products
    var prod1 = Guid.CreateVersion7();
    await perspective.Update(new ProductCreatedEvent {
      ProductId = prod1,
      Name = "Product 1",
      Description = "Desc 1",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var prod2 = Guid.CreateVersion7();
    await perspective.Update(new ProductCreatedEvent {
      ProductId = prod2,
      Name = "Product 2",
      Description = "Desc 2",
      Price = 20.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var prod3 = Guid.CreateVersion7();
    await perspective.Update(new ProductCreatedEvent {
      ProductId = prod3,
      Name = "Product 3",
      Description = "Desc 3",
      Price = 30.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Delete one
    await perspective.Update(new ProductDeletedEvent {
      ProductId = prod2,
      DeletedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act
    var result = await lens.GetAllAsync();

    // Assert
    await Assert.That(result).HasCount().EqualTo(2);
    await Assert.That(result.Any(p => p.ProductId == prod1)).IsTrue();
    await Assert.That(result.Any(p => p.ProductId == prod3)).IsTrue();
    await Assert.That(result.Any(p => p.ProductId == prod2)).IsFalse();
  }

  [Test]
  public async Task GetAllAsync_WithIncludeDeleted_ReturnsAllProductsAsync() {
    // Arrange
    var lens = new ProductCatalogLens(_helper.GetLensQuery<ProductDto>());
    var perspective = new ProductCatalogPerspective(
      _helper.GetPerspectiveStore<ProductDto>(),
      _helper.GetLensQuery<ProductDto>(),
      _helper.GetLogger<ProductCatalogPerspective>(),
      _helper.GetHubContext());

    // Create 2 products
    var prod1 = Guid.CreateVersion7();
    await perspective.Update(new ProductCreatedEvent {
      ProductId = prod1,
      Name = "Product 1",
      Description = "Desc 1",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var prod2 = Guid.CreateVersion7();
    await perspective.Update(new ProductCreatedEvent {
      ProductId = prod2,
      Name = "Product 2",
      Description = "Desc 2",
      Price = 20.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Delete one
    await perspective.Update(new ProductDeletedEvent {
      ProductId = prod2,
      DeletedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act
    var result = await lens.GetAllAsync(includeDeleted: true);

    // Assert
    await Assert.That(result).HasCount().EqualTo(2);
    await Assert.That(result.Any(p => p.ProductId == prod1)).IsTrue();
    await Assert.That(result.Any(p => p.ProductId == prod2)).IsTrue();
  }

  [Test]
  public async Task GetByIdsAsync_WithExistingIds_ReturnsMatchingProductsAsync() {
    // Arrange
    var lens = new ProductCatalogLens(_helper.GetLensQuery<ProductDto>());
    var perspective = new ProductCatalogPerspective(
      _helper.GetPerspectiveStore<ProductDto>(),
      _helper.GetLensQuery<ProductDto>(),
      _helper.GetLogger<ProductCatalogPerspective>(),
      _helper.GetHubContext());

    // Create 3 products
    var prod1 = Guid.CreateVersion7();
    await perspective.Update(new ProductCreatedEvent {
      ProductId = prod1,
      Name = "Product 1",
      Description = "Desc 1",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var prod2 = Guid.CreateVersion7();
    await perspective.Update(new ProductCreatedEvent {
      ProductId = prod2,
      Name = "Product 2",
      Description = "Desc 2",
      Price = 20.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var prod3 = Guid.CreateVersion7();
    await perspective.Update(new ProductCreatedEvent {
      ProductId = prod3,
      Name = "Product 3",
      Description = "Desc 3",
      Price = 30.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act
    var result = await lens.GetByIdsAsync(new[] { prod1, prod3 });

    // Assert
    await Assert.That(result).HasCount().EqualTo(2);
    await Assert.That(result.Any(p => p.ProductId == prod1)).IsTrue();
    await Assert.That(result.Any(p => p.ProductId == prod3)).IsTrue();
  }

  [Test]
  public async Task GetByIdsAsync_WithEmptyList_ReturnsEmptyListAsync() {
    // Arrange
    var lens = new ProductCatalogLens(_helper.GetLensQuery<ProductDto>());

    // Act
    var result = await lens.GetByIdsAsync(Array.Empty<Guid>());

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).HasCount().EqualTo(0);
  }

  [Test]
  public async Task GetByIdsAsync_WithMixedIds_ReturnsOnlyExistingAsync() {
    // Arrange
    var lens = new ProductCatalogLens(_helper.GetLensQuery<ProductDto>());
    var perspective = new ProductCatalogPerspective(
      _helper.GetPerspectiveStore<ProductDto>(),
      _helper.GetLensQuery<ProductDto>(),
      _helper.GetLogger<ProductCatalogPerspective>(),
      _helper.GetHubContext());

    // Create 2 products
    var prod1 = Guid.CreateVersion7();
    await perspective.Update(new ProductCreatedEvent {
      ProductId = prod1,
      Name = "Product 1",
      Description = "Desc 1",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var prod2 = Guid.CreateVersion7();
    await perspective.Update(new ProductCreatedEvent {
      ProductId = prod2,
      Name = "Product 2",
      Description = "Desc 2",
      Price = 20.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act - request existing and non-existing
    var nonExistentId = Guid.CreateVersion7();
    var result = await lens.GetByIdsAsync(new[] { prod1, nonExistentId, prod2 });

    // Assert
    await Assert.That(result).HasCount().EqualTo(2);
    await Assert.That(result.Any(p => p.ProductId == prod1)).IsTrue();
    await Assert.That(result.Any(p => p.ProductId == prod2)).IsTrue();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _helper.CleanupDatabaseAsync();
  }

  public async ValueTask DisposeAsync() {
    await _helper.DisposeAsync();
  }
}
