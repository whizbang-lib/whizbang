using Dapper;
using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Perspectives;
using ECommerce.InventoryWorker.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ECommerce.InventoryWorker.Tests.Perspectives;

/// <summary>
/// Integration tests for ProductCatalogPerspective
/// </summary>
public class ProductCatalogPerspectiveTests : IAsyncDisposable {
  private readonly DatabaseTestHelper _dbHelper = new();

  [Test]
  public async Task Update_WithProductCreatedEvent_InsertsProductRecordAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger);

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

    // Assert - Verify product was inserted
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var product = await connection.QuerySingleOrDefaultAsync<ProductRow>(
      "SELECT product_id, name, description, price, image_url, created_at, updated_at, deleted_at FROM inventoryworker.product_catalog WHERE product_id = @ProductId",
      new { ProductId = productId });

    await Assert.That(product).IsNotNull();
    await Assert.That(product!.product_id).IsEqualTo(productId);
    await Assert.That(product.name).IsEqualTo("Test Widget");
    await Assert.That(product.description).IsEqualTo("A test widget");
    await Assert.That(product.price).IsEqualTo(29.99m);
    await Assert.That(product.image_url).IsEqualTo("https://example.com/widget.jpg");
    await Assert.That(product.updated_at).IsNull();
    await Assert.That(product.deleted_at).IsNull();
  }

  [Test]
  public async Task Update_WithProductCreatedEvent_HandlesNullImageUrlAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger);

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

    // Assert - Verify null ImageUrl was stored
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var product = await connection.QuerySingleOrDefaultAsync<ProductRow>(
      "SELECT product_id, image_url FROM inventoryworker.product_catalog WHERE product_id = @ProductId",
      new { ProductId = productId });

    await Assert.That(product).IsNotNull();
    await Assert.That(product!.image_url).IsNull();
  }

  [Test]
  public async Task Update_WithProductCreatedEvent_LogsSuccessAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger);

    var @event = new ProductCreatedEvent {

      ProductId = Guid.CreateVersion7(),
      Name = "Log Widget",
      Description = "Test logging",
      Price = 5.99m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Should have logged something
    await Assert.That(logger.LoggedMessages).HasCount().GreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task Update_WithProductUpdatedEvent_UpdatesExistingProductAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger);

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

    // Assert - Verify product was updated
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var product = await connection.QuerySingleOrDefaultAsync<ProductRow>(
      "SELECT product_id, name, description, price, image_url, updated_at FROM inventoryworker.product_catalog WHERE product_id = @ProductId",
      new { ProductId = productId });

    await Assert.That(product).IsNotNull();
    await Assert.That(product!.name).IsEqualTo("Updated Name");
    await Assert.That(product.description).IsEqualTo("Original Description"); // Should be unchanged
    await Assert.That(product.price).IsEqualTo(19.99m);
    await Assert.That(product.image_url).IsEqualTo("https://example.com/new.jpg");
    await Assert.That(product.updated_at).IsNotNull();
  }

  [Test]
  public async Task Update_WithProductUpdatedEvent_HandlesPartialUpdatesAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger);

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

    // Assert - Only name should be updated
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var product = await connection.QuerySingleOrDefaultAsync<ProductRow>(
      "SELECT product_id, name, description, price, image_url FROM inventoryworker.product_catalog WHERE product_id = @ProductId",
      new { ProductId = productId });

    await Assert.That(product).IsNotNull();
    await Assert.That(product!.name).IsEqualTo("New Name");
    await Assert.That(product.description).IsEqualTo("Original Description");
    await Assert.That(product.price).IsEqualTo(10.00m);
    await Assert.That(product.image_url).IsEqualTo("https://example.com/original.jpg");
  }

  [Test]
  public async Task Update_WithProductUpdatedEvent_SetsUpdatedAtTimestampAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger);

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

    // Assert - Verify updated_at was set
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var product = await connection.QuerySingleOrDefaultAsync<ProductRow>(
      "SELECT updated_at FROM inventoryworker.product_catalog WHERE product_id = @ProductId",
      new { ProductId = productId });

    await Assert.That(product).IsNotNull();
    await Assert.That(product!.updated_at).IsNotNull();
  }

  [Test]
  public async Task Update_WithProductUpdatedEvent_LogsSuccessAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger);

    // Create initial product
    var createdEvent = new ProductCreatedEvent {

      ProductId = Guid.CreateVersion7(),
      Name = "Test",
      Description = "Test",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(createdEvent, CancellationToken.None);

    // Act
    var updatedEvent = new ProductUpdatedEvent {

      ProductId = Guid.CreateVersion7(),
      Name = "Updated",
      Description = null,
      Price = null,
      ImageUrl = null,
      UpdatedAt = DateTime.UtcNow
    };
    await perspective.Update(updatedEvent, CancellationToken.None);

    // Assert - Should have logged something
    await Assert.That(logger.LoggedMessages).HasCount().GreaterThanOrEqualTo(2); // Create + Update
  }

  [Test]
  public async Task Update_WithProductDeletedEvent_SoftDeletesProductAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger);

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

    // Assert - Verify product was soft deleted (deleted_at set)
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var product = await connection.QuerySingleOrDefaultAsync<ProductRow>(
      "SELECT product_id, name, deleted_at FROM inventoryworker.product_catalog WHERE product_id = @ProductId",
      new { ProductId = productId });

    await Assert.That(product).IsNotNull(); // Should still exist
    await Assert.That(product!.product_id).IsEqualTo(productId);
    await Assert.That(product.name).IsEqualTo("To Be Deleted"); // Data intact
    await Assert.That(product.deleted_at).IsNotNull(); // Soft deleted
  }

  [Test]
  public async Task Update_WithProductDeletedEvent_DoesNotHardDeleteAsync() {
    // Arrange
    var productId = Guid.CreateVersion7();
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger);

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

    // Assert - Verify product still exists in database
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var count = await connection.ExecuteScalarAsync<int>(
      "SELECT COUNT(*) FROM inventoryworker.product_catalog WHERE product_id = @ProductId",
      new { ProductId = productId });

    await Assert.That(count).IsEqualTo(1); // Should still exist
  }

  [Test]
  public async Task Update_WithProductDeletedEvent_LogsSuccessAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<ProductCatalogPerspective>();
    var perspective = new ProductCatalogPerspective(connectionFactory, logger);

    // Create initial product
    var createdEvent = new ProductCreatedEvent {

      ProductId = Guid.CreateVersion7(),
      Name = "Test",
      Description = "Test",
      Price = 10.00m,
      ImageUrl = null,
      CreatedAt = DateTime.UtcNow
    };
    await perspective.Update(createdEvent, CancellationToken.None);

    // Act
    var deletedEvent = new ProductDeletedEvent {

      ProductId = Guid.CreateVersion7(),
      DeletedAt = DateTime.UtcNow
    };
    await perspective.Update(deletedEvent, CancellationToken.None);

    // Assert - Should have logged something
    await Assert.That(logger.LoggedMessages).HasCount().GreaterThanOrEqualTo(2); // Create + Delete
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
  public Guid product_id { get; init; }
  public string name { get; init; } = string.Empty;
  public string? description { get; init; }
  public decimal price { get; init; }
  public string? image_url { get; init; }
  public DateTime created_at { get; init; }
  public DateTime? updated_at { get; init; }
  public DateTime? deleted_at { get; init; }
}

/// <summary>
/// Test implementation of ILogger for testing
/// </summary>
internal class TestLogger<T> : ILogger<T> {
  public List<string> LoggedMessages { get; } = [];

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
    var message = formatter(state, exception);
    LoggedMessages.Add(message);
  }
}
