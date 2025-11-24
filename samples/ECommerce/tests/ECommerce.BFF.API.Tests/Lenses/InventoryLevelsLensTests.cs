using Dapper;
using ECommerce.BFF.API.Lenses;
using ECommerce.BFF.API.Perspectives;
using ECommerce.BFF.API.Tests.TestHelpers;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ECommerce.BFF.API.Tests.Lenses;

/// <summary>
/// Integration tests for InventoryLevelsLens (BFF)
/// </summary>
public class InventoryLevelsLensTests : IAsyncDisposable {
  private readonly DatabaseTestHelper _dbHelper = new();

  [Test]
  public async Task GetByProductIdAsync_WithExistingInventory_ReturnsInventoryDtoAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLevelsLens(connectionFactory);

    // Create inventory via perspective
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance, null!);
    var productId = Guid.CreateVersion7();
    var restockEvent = new InventoryRestockedEvent {

      ProductId = productId,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act
    var result = await lens.GetByProductIdAsync(productId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.ProductId).IsEqualTo(productId);
    await Assert.That(result.Quantity).IsEqualTo(100);
    await Assert.That(result.Reserved).IsEqualTo(0);
    await Assert.That(result.Available).IsEqualTo(100);
  }

  [Test]
  public async Task GetByProductIdAsync_WithNonExistent_ReturnsNullAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLevelsLens(connectionFactory);

    // Act
    var result = await lens.GetByProductIdAsync(Guid.CreateVersion7());

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetAllAsync_WithNoInventory_ReturnsEmptyListAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLevelsLens(connectionFactory);

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
    var lens = new InventoryLevelsLens(connectionFactory);
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance, null!);

    // Create 3 inventory entries
    var prod1 = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {

      ProductId = prod1,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var prod2 = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {

      ProductId = prod2,
      NewTotalQuantity = 50,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var prod3 = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {

      ProductId = prod3,
      NewTotalQuantity = 200,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act
    var result = await lens.GetAllAsync();

    // Assert
    await Assert.That(result).HasCount().EqualTo(3);
    await Assert.That(result.Any(i => i.ProductId == prod1)).IsTrue();
    await Assert.That(result.Any(i => i.ProductId == prod2)).IsTrue();
    await Assert.That(result.Any(i => i.ProductId == prod3)).IsTrue();
  }

  [Test]
  public async Task GetAllAsync_CalculatesAvailableCorrectlyAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLevelsLens(connectionFactory);
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance, null!);

    // Create inventory and reserve some
    var productId = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {

      ProductId = productId,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryReservedEvent {

      ProductId = productId,
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
    var lens = new InventoryLevelsLens(connectionFactory);
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance, null!);

    // Create products with varying stock levels
    var prodLow = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {

      ProductId = prodLow,
      NewTotalQuantity = 5,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryRestockedEvent {

      ProductId = Guid.CreateVersion7(),
      NewTotalQuantity = 50,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryRestockedEvent {

      ProductId = Guid.CreateVersion7(),
      NewTotalQuantity = 200,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act - default threshold is 10
    var result = await lens.GetLowStockAsync();

    // Assert
    await Assert.That(result).HasCount().EqualTo(1);
    await Assert.That(result.First().ProductId).IsEqualTo(prodLow);
  }

  [Test]
  public async Task GetLowStockAsync_WithCustomThreshold_UsesThresholdAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLevelsLens(connectionFactory);
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance, null!);

    // Create products with varying stock levels
    var prodLow = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {

      ProductId = prodLow,
      NewTotalQuantity = 5,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var prodMedium = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {

      ProductId = prodMedium,
      NewTotalQuantity = 50,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryRestockedEvent {

      ProductId = Guid.CreateVersion7(),
      NewTotalQuantity = 200,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act - custom threshold of 100
    var result = await lens.GetLowStockAsync(threshold: 100);

    // Assert
    await Assert.That(result).HasCount().EqualTo(2);
    await Assert.That(result.Any(i => i.ProductId == prodLow)).IsTrue();
    await Assert.That(result.Any(i => i.ProductId == prodMedium)).IsTrue();
  }

  [Test]
  public async Task GetLowStockAsync_WithNoLowStock_ReturnsEmptyListAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var lens = new InventoryLevelsLens(connectionFactory);
    var perspective = new InventoryLevelsPerspective(connectionFactory, NullLogger<InventoryLevelsPerspective>.Instance, null!);

    // Create only high-stock products
    await perspective.Update(new InventoryRestockedEvent {

      ProductId = Guid.CreateVersion7(),
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    await perspective.Update(new InventoryRestockedEvent {

      ProductId = Guid.CreateVersion7(),
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
