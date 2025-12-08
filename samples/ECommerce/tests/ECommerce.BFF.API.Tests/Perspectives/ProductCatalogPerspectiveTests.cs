using System.Diagnostics.CodeAnalysis;
using ECommerce.BFF.API.Lenses;
using ECommerce.BFF.API.Perspectives;
using ECommerce.BFF.API.Tests.TestHelpers;
using ECommerce.Contracts.Events;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.BFF.API.Tests.Perspectives;

/// <summary>
/// Integration tests for ProductCatalogPerspective (BFF) using unified Whizbang API
/// </summary>
public class ProductCatalogPerspectiveTests : IAsyncDisposable {
  private readonly EFCoreTestHelper _helper = new();

  [Test]
  public async Task Update_WithProductCreatedEvent_InsertsProductAsync() {
    // Arrange
    var perspective = new ProductCatalogPerspective(
      _helper.GetPerspectiveStore<ProductDto>(),
      _helper.GetLensQuery<ProductDto>(),
      _helper.GetLogger<ProductCatalogPerspective>(),
      _helper.GetHubContext());

    var lens = _helper.GetLensQuery<ProductDto>();

    var productId = Guid.CreateVersion7();
    var @event = new ProductCreatedEvent {

      ProductId = productId,
      Name = "Test Widget",
      Description = "A test widget",
      Price = 29.99m,
      ImageUrl = "https://example.com/widget.jpg",
      CreatedAt = DateTime.UtcNow
    };

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify product was inserted using lens query
    var product = await lens.GetByIdAsync(productId.ToString(), CancellationToken.None);

    await Assert.That(product).IsNotNull();
    await Assert.That(product!.ProductId).IsEqualTo(productId);
    await Assert.That(product.Name).IsEqualTo("Test Widget");
    await Assert.That(product.Description).IsEqualTo("A test widget");
    await Assert.That(product.Price).IsEqualTo(29.99m);
    await Assert.That(product.ImageUrl).IsEqualTo("https://example.com/widget.jpg");
    await Assert.That(product.UpdatedAt).IsNull();
    await Assert.That(product.DeletedAt).IsNull();
  }

  [Test]
  public async Task Update_WithProductUpdatedEvent_UpdatesProductAsync() {
    // Arrange
    var perspective = new ProductCatalogPerspective(
      _helper.GetPerspectiveStore<ProductDto>(),
      _helper.GetLensQuery<ProductDto>(),
      _helper.GetLogger<ProductCatalogPerspective>(),
      _helper.GetHubContext());

    var lens = _helper.GetLensQuery<ProductDto>();

    // Create initial product
    var productId = Guid.CreateVersion7();
    var createdEvent = new ProductCreatedEvent {

      ProductId = productId,
      Name = "Original Name",
      Description = "Original Description",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(createdEvent, CancellationToken.None);

    // Act - Update the product
    var updatedEvent = new ProductUpdatedEvent {

      ProductId = productId,
      Name = "Updated Name",
      Description = null, // Partial update
      Price = 19.99m,
      ImageUrl = "https://example.com/new.jpg",
      UpdatedAt = DateTime.UtcNow
    };
    await perspective.Update(updatedEvent, CancellationToken.None);

    // Assert - Verify product was updated using lens query
    var product = await lens.GetByIdAsync(productId.ToString(), CancellationToken.None);

    await Assert.That(product).IsNotNull();
    await Assert.That(product!.Name).IsEqualTo("Updated Name");
    await Assert.That(product.Description).IsEqualTo("Original Description"); // Unchanged
    await Assert.That(product.Price).IsEqualTo(19.99m);
    await Assert.That(product.ImageUrl).IsEqualTo("https://example.com/new.jpg");
    await Assert.That(product.UpdatedAt).IsNotNull();
  }

  [Test]
  public async Task Update_WithProductDeletedEvent_SoftDeletesProductAsync() {
    // Arrange
    var perspective = new ProductCatalogPerspective(
      _helper.GetPerspectiveStore<ProductDto>(),
      _helper.GetLensQuery<ProductDto>(),
      _helper.GetLogger<ProductCatalogPerspective>(),
      _helper.GetHubContext());

    var lens = _helper.GetLensQuery<ProductDto>();

    // Create initial product
    var productId = Guid.CreateVersion7();
    var createdEvent = new ProductCreatedEvent {

      ProductId = productId,
      Name = "To Be Deleted",
      Description = "Will be soft deleted",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(createdEvent, CancellationToken.None);

    // Act - Delete the product
    var deletedEvent = new ProductDeletedEvent {

      ProductId = productId,
      DeletedAt = DateTime.UtcNow
    };
    await perspective.Update(deletedEvent, CancellationToken.None);

    // Assert - Verify product was soft deleted using lens query
    var product = await lens.GetByIdAsync(productId.ToString(), CancellationToken.None);

    await Assert.That(product).IsNotNull(); // Should still exist
    await Assert.That(product!.ProductId).IsEqualTo(productId);
    await Assert.That(product.Name).IsEqualTo("To Be Deleted"); // Data intact
    await Assert.That(product.DeletedAt).IsNotNull(); // Soft deleted
  }

  [Test]
  public async Task Update_WithDuplicateProductCreatedEvent_HandlesGracefullyAsync() {
    // Arrange
    var perspective = new ProductCatalogPerspective(
      _helper.GetPerspectiveStore<ProductDto>(),
      _helper.GetLensQuery<ProductDto>(),
      _helper.GetLogger<ProductCatalogPerspective>(),
      _helper.GetHubContext());

    var productId = Guid.CreateVersion7();
    var @event = new ProductCreatedEvent {

      ProductId = productId,
      Name = "Test Widget",
      Description = "Test",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };

    // Act - Create same product twice (upsert should handle gracefully)
    await perspective.Update(@event, CancellationToken.None);
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Should succeed (upsert behavior)
    var lens = _helper.GetLensQuery<ProductDto>();
    var product = await lens.GetByIdAsync(productId.ToString(), CancellationToken.None);
    await Assert.That(product).IsNotNull();
    await Assert.That(product!.ProductId).IsEqualTo(productId);
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _helper.CleanupDatabaseAsync();
  }

  public async ValueTask DisposeAsync() {
    await _helper.DisposeAsync();
  }
}
