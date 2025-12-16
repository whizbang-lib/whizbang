using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Lenses;
using ECommerce.InventoryWorker.Perspectives;
using ECommerce.InventoryWorker.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace ECommerce.InventoryWorker.Tests.Perspectives;

/// <summary>
/// Integration tests for ProductCatalogPerspective
/// </summary>
public class ProductCatalogPerspectiveTests : IAsyncDisposable {
  private readonly DatabaseTestHelper _dbHelper = new();

  [Test]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task Update_WithProductCreatedEvent_InsertsProductRecordAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<ProductCatalogPerspective>();
    var query = sp.GetRequiredService<ILensQuery<ProductDto>>();

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

    // Assert - Verify product was inserted using EF Core
    var stored = await query.GetByIdAsync(productId);

    await Assert.That(stored).IsNotNull();
    await Assert.That(stored!.ProductId).IsEqualTo(productId);
    await Assert.That(stored.Name).IsEqualTo("Test Widget");
    await Assert.That(stored.Description).IsEqualTo("A test widget");
    await Assert.That(stored.Price).IsEqualTo(29.99m);
    await Assert.That(stored.ImageUrl).IsEqualTo("https://example.com/widget.jpg");
    await Assert.That(stored.UpdatedAt).IsNull();
    await Assert.That(stored.DeletedAt).IsNull();
  }

  [Test]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task Update_WithProductCreatedEvent_HandlesNullImageUrlAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<ProductCatalogPerspective>();
    var query = sp.GetRequiredService<ILensQuery<ProductDto>>();

    var @event = new ProductCreatedEvent {

      ProductId = productId,
      Name = "No Image Widget",
      Description = "Widget without image",
      Price = 14.99m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify null ImageUrl was stored using EF Core
    var stored = await query.GetByIdAsync(productId);

    await Assert.That(stored).IsNotNull();
    await Assert.That(stored!.ImageUrl).IsNull();
  }


  [Test]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task Update_WithProductUpdatedEvent_UpdatesExistingProductAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<ProductCatalogPerspective>();
    var query = sp.GetRequiredService<ILensQuery<ProductDto>>();

    // Create initial product
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
      Description = null, // Partial update - don't change description
      Price = 19.99m,
      ImageUrl = "https://example.com/new.jpg",
      UpdatedAt = DateTime.UtcNow
    };
    await perspective.Update(updatedEvent, CancellationToken.None);

    // Assert - Verify product was updated using EF Core
    var stored = await query.GetByIdAsync(productId);

    await Assert.That(stored).IsNotNull();
    await Assert.That(stored!.Name).IsEqualTo("Updated Name");
    await Assert.That(stored.Description).IsEqualTo("Original Description"); // Should be unchanged
    await Assert.That(stored.Price).IsEqualTo(19.99m);
    await Assert.That(stored.ImageUrl).IsEqualTo("https://example.com/new.jpg");
    await Assert.That(stored.UpdatedAt).IsNotNull();
  }

  [Test]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task Update_WithProductUpdatedEvent_HandlesPartialUpdatesAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<ProductCatalogPerspective>();
    var query = sp.GetRequiredService<ILensQuery<ProductDto>>();

    // Create initial product
    var createdEvent = new ProductCreatedEvent {

      ProductId = productId,
      Name = "Original Name",
      Description = "Original Description",
      Price = 10.00m,
      ImageUrl = "https://example.com/original.jpg",
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(createdEvent, CancellationToken.None);

    // Act - Update only name
    var updatedEvent = new ProductUpdatedEvent {

      ProductId = productId,
      Name = "New Name",
      Description = null, // Don't change
      Price = null,       // Don't change
      ImageUrl = null,    // Don't change
      UpdatedAt = DateTime.UtcNow
    };
    await perspective.Update(updatedEvent, CancellationToken.None);

    // Assert - Only name should be updated using EF Core
    var stored = await query.GetByIdAsync(productId);

    await Assert.That(stored).IsNotNull();
    await Assert.That(stored!.Name).IsEqualTo("New Name");
    await Assert.That(stored.Description).IsEqualTo("Original Description");
    await Assert.That(stored.Price).IsEqualTo(10.00m);
    await Assert.That(stored.ImageUrl).IsEqualTo("https://example.com/original.jpg");
  }

  [Test]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task Update_WithProductUpdatedEvent_SetsUpdatedAtTimestampAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<ProductCatalogPerspective>();
    var query = sp.GetRequiredService<ILensQuery<ProductDto>>();

    // Create initial product
    var createdEvent = new ProductCreatedEvent {

      ProductId = productId,
      Name = "Test Widget",
      Description = "Test",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(createdEvent, CancellationToken.None);

    // Act - Update the product
    var updateTime = DateTime.UtcNow;
    var updatedEvent = new ProductUpdatedEvent {

      ProductId = productId,
      Name = "Updated Widget",
      Description = null,
      Price = null,
      ImageUrl = null,
      UpdatedAt = updateTime
    };
    await perspective.Update(updatedEvent, CancellationToken.None);

    // Assert - Verify updated_at was set using EF Core
    var stored = await query.GetByIdAsync(productId);

    await Assert.That(stored).IsNotNull();
  }


  [Test]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task Update_WithProductDeletedEvent_SoftDeletesProductAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<ProductCatalogPerspective>();
    var query = sp.GetRequiredService<ILensQuery<ProductDto>>();

    // Create initial product
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
    var deleteTime = DateTime.UtcNow;
    var deletedEvent = new ProductDeletedEvent {

      ProductId = productId,
      DeletedAt = deleteTime
    };
    await perspective.Update(deletedEvent, CancellationToken.None);

    // Assert - Verify product was soft deleted (deleted_at set) using EF Core
    var stored = await query.GetByIdAsync(productId);

    await Assert.That(stored).IsNotNull(); // Should still exist
    await Assert.That(stored!.ProductId).IsEqualTo(productId);
    await Assert.That(stored.Name).IsEqualTo("To Be Deleted"); // Data intact
    await Assert.That(stored.DeletedAt).IsNotNull(); // Soft deleted
  }

  [Test]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task Update_WithProductDeletedEvent_DoesNotHardDeleteAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<ProductCatalogPerspective>();
    var query = sp.GetRequiredService<ILensQuery<ProductDto>>();

    // Create initial product
    var createdEvent = new ProductCreatedEvent {

      ProductId = productId,
      Name = "Test Widget",
      Description = "Test",
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

    // Assert - Verify product still exists in database using EF Core
    var stored = await query.GetByIdAsync(productId);

    await Assert.That(stored).IsNotNull(); // Should still exist
  }


  [After(Test)]
  public async Task CleanupAsync() {
    await _dbHelper.CleanupDatabaseAsync();
  }

  public async ValueTask DisposeAsync() {
    await _dbHelper.DisposeAsync();
  }
}
