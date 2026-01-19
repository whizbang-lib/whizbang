using Dapper;
using ECommerce.BFF.API.Perspectives;
using ECommerce.BFF.API.Tests.TestHelpers;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ECommerce.BFF.API.Tests.Perspectives;

/// <summary>
/// Integration tests for ProductCatalogPerspective (BFF)
/// </summary>
public class ProductCatalogPerspectiveTests : IAsyncDisposable {
  private readonly DatabaseTestHelper _dbHelper = new();

  [Test]
  public async Task Update_WithProductCreatedEvent_InsertsProductAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger, null!);

    var @event = new ProductCreatedEvent {
      ProductId = "prod-123",
      Name = "Test Widget",
      Description = "A test widget",
      Price = 29.99m,
      ImageUrl = "https://example.com/widget.jpg",
      CreatedAt = DateTime.UtcNow
    };

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify product was inserted into bff.product_catalog
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var product = await connection.QuerySingleOrDefaultAsync<ProductRow>(
      "SELECT product_id, name, description, price, image_url, created_at, updated_at, deleted_at FROM bff.product_catalog WHERE product_id = @ProductId",
      new { ProductId = "prod-123" });

    await Assert.That(product).IsNotNull();
    await Assert.That(product!.product_id).IsEqualTo("prod-123");
    await Assert.That(product.name).IsEqualTo("Test Widget");
    await Assert.That(product.description).IsEqualTo("A test widget");
    await Assert.That(product.price).IsEqualTo(29.99m);
    await Assert.That(product.image_url).IsEqualTo("https://example.com/widget.jpg");
    await Assert.That(product.updated_at).IsNull();
    await Assert.That(product.deleted_at).IsNull();
  }

  [Test]
  public async Task Update_WithProductUpdatedEvent_UpdatesProductAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger, null!);

    // Create initial product
    var createdEvent = new ProductCreatedEvent {
      ProductId = "prod-update",
      Name = "Original Name",
      Description = "Original Description",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(createdEvent, CancellationToken.None);

    // Act - Update the product
    var updatedEvent = new ProductUpdatedEvent {
      ProductId = "prod-update",
      Name = "Updated Name",
      Description = null, // Partial update
      Price = 19.99m,
      ImageUrl = "https://example.com/new.jpg",
      UpdatedAt = DateTime.UtcNow
    };
    await perspective.Update(updatedEvent, CancellationToken.None);

    // Assert - Verify product was updated in bff.product_catalog
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var product = await connection.QuerySingleOrDefaultAsync<ProductRow>(
      "SELECT product_id, name, description, price, image_url, updated_at FROM bff.product_catalog WHERE product_id = @ProductId",
      new { ProductId = "prod-update" });

    await Assert.That(product).IsNotNull();
    await Assert.That(product!.name).IsEqualTo("Updated Name");
    await Assert.That(product.description).IsEqualTo("Original Description"); // Unchanged
    await Assert.That(product.price).IsEqualTo(19.99m);
    await Assert.That(product.image_url).IsEqualTo("https://example.com/new.jpg");
    await Assert.That(product.updated_at).IsNotNull();
  }

  [Test]
  public async Task Update_WithProductDeletedEvent_SoftDeletesProductAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger, null!);

    // Create initial product
    var createdEvent = new ProductCreatedEvent {
      ProductId = "prod-delete",
      Name = "To Be Deleted",
      Description = "Will be soft deleted",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(createdEvent, CancellationToken.None);

    // Act - Delete the product
    var deletedEvent = new ProductDeletedEvent {
      ProductId = "prod-delete",
      DeletedAt = DateTime.UtcNow
    };
    await perspective.Update(deletedEvent, CancellationToken.None);

    // Assert - Verify product was soft deleted in bff.product_catalog
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var product = await connection.QuerySingleOrDefaultAsync<ProductRow>(
      "SELECT product_id, name, deleted_at FROM bff.product_catalog WHERE product_id = @ProductId",
      new { ProductId = "prod-delete" });

    await Assert.That(product).IsNotNull(); // Should still exist
    await Assert.That(product!.product_id).IsEqualTo("prod-delete");
    await Assert.That(product.name).IsEqualTo("To Be Deleted"); // Data intact
    await Assert.That(product.deleted_at).IsNotNull(); // Soft deleted
  }

  [Test]
  public async Task Update_WithDuplicateProductCreatedEvent_HandlesGracefullyAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger, null!);

    var @event = new ProductCreatedEvent {
      ProductId = "prod-dup",
      Name = "Test Widget",
      Description = "Test",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };

    // Act - Create same product twice
    await perspective.Update(@event, CancellationToken.None);

    // This should either succeed (upsert) or fail gracefully
    // depending on implementation choice
    var exception = await Assert.ThrowsAsync<Exception>(async () => {
      await perspective.Update(@event, CancellationToken.None);
    });

    // Assert - Should either succeed or throw specific exception
    // Implementation will determine exact behavior
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _dbHelper.CleanupDatabaseAsync();
  }

  public async ValueTask DisposeAsync() {
    await _dbHelper.DisposeAsync();
  }
}

/// <summary>
/// DTO for reading product_catalog rows from database
/// </summary>
internal record ProductRow {
  public string product_id { get; init; } = string.Empty;
  public string name { get; init; } = string.Empty;
  public string? description { get; init; }
  public decimal price { get; init; }
  public string? image_url { get; init; }
  public DateTime created_at { get; init; }
  public DateTime? updated_at { get; init; }
  public DateTime? deleted_at { get; init; }
}
