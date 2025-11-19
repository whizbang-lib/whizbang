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
/// Integration tests for InventoryLens
/// </summary>
public class InventoryLensTests : IAsyncDisposable {
  private readonly DatabaseTestHelper _dbHelper = new();

  [Test]
  public async Task GetByProductIdAsync_WithExistingInventory_ReturnsInventoryDtoAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLens(connectionFactory);

    // Create inventory via perspective
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance);
    var restockEvent = new InventoryRestockedEvent {
      ProductId = "prod-123",
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act
    var result = await lens.GetByProductIdAsync("prod-123");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.ProductId).IsEqualTo("prod-123");
    await Assert.That(result.Quantity).IsEqualTo(100);
    await Assert.That(result.Reserved).IsEqualTo(0);
    await Assert.That(result.Available).IsEqualTo(100);
  }

  [Test]
  public async Task GetByProductIdAsync_WithNonExistent_ReturnsNullAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLens(connectionFactory);

    // Act
    var result = await lens.GetByProductIdAsync("non-existent");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetAllAsync_WithNoInventory_ReturnsEmptyListAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLens(connectionFactory);

    // Act
    var result = await lens.GetAllAsync();

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).HasCount().EqualTo(0);
  }

  [Test]
  public async Task GetAllAsync_WithMultipleEntries_ReturnsAllAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLens(connectionFactory);
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance);

    // Create 3 inventory entries
    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-1",
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-2",
      NewTotalQuantity = 50,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-3",
      NewTotalQuantity = 200,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act
    var result = await lens.GetAllAsync();

    // Assert
    await Assert.That(result).HasCount().EqualTo(3);
    await Assert.That(result.Any(i => i.ProductId == "prod-1")).IsTrue();
    await Assert.That(result.Any(i => i.ProductId == "prod-2")).IsTrue();
    await Assert.That(result.Any(i => i.ProductId == "prod-3")).IsTrue();
  }

  [Test]
  public async Task GetAllAsync_CalculatesAvailableCorrectlyAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLens(connectionFactory);
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance);

    // Create inventory and reserve some
    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-1",
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryReservedEvent {
      ProductId = "prod-1",
      OrderId = "order-1",
      Quantity = 30,
      ReservedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act
    var result = await lens.GetAllAsync();

    // Assert
    await Assert.That(result).HasCount().EqualTo(1);
    var inventory = result.First();
    await Assert.That(inventory.Quantity).IsEqualTo(100);
    await Assert.That(inventory.Reserved).IsEqualTo(30);
    await Assert.That(inventory.Available).IsEqualTo(70); // 100 - 30
  }

  [Test]
  public async Task GetLowStockAsync_WithDefaultThreshold_ReturnsLowStockAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLens(connectionFactory);
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance);

    // Create products with varying stock levels
    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-low",
      NewTotalQuantity = 5,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-medium",
      NewTotalQuantity = 50,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-high",
      NewTotalQuantity = 200,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act - default threshold is 10
    var result = await lens.GetLowStockAsync();

    // Assert
    await Assert.That(result).HasCount().EqualTo(1);
    await Assert.That(result.First().ProductId).IsEqualTo("prod-low");
  }

  [Test]
  public async Task GetLowStockAsync_WithCustomThreshold_UsesThresholdAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLens(connectionFactory);
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance);

    // Create products with varying stock levels
    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-low",
      NewTotalQuantity = 5,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-medium",
      NewTotalQuantity = 50,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-high",
      NewTotalQuantity = 200,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act - custom threshold of 100
    var result = await lens.GetLowStockAsync(threshold: 100);

    // Assert
    await Assert.That(result).HasCount().EqualTo(2);
    await Assert.That(result.Any(i => i.ProductId == "prod-low")).IsTrue();
    await Assert.That(result.Any(i => i.ProductId == "prod-medium")).IsTrue();
  }

  [Test]
  public async Task GetLowStockAsync_WithNoLowStock_ReturnsEmptyListAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLens(connectionFactory);
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance);

    // Create only high-stock products
    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-1",
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryRestockedEvent {
      ProductId = "prod-2",
      NewTotalQuantity = 200,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act - default threshold of 10
    var result = await lens.GetLowStockAsync();

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).HasCount().EqualTo(0);
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _dbHelper.CleanupDatabaseAsync();
  }

  public async ValueTask DisposeAsync() {
    await _dbHelper.DisposeAsync();
  }
}
