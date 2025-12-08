using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Lenses;
using ECommerce.InventoryWorker.Perspectives;
using ECommerce.InventoryWorker.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
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
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var lens = sp.GetRequiredService<IInventoryLens>();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();

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
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var lens = sp.GetRequiredService<IInventoryLens>();

    // Act
    var result = await lens.GetByProductIdAsync(Guid.CreateVersion7());

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetAllAsync_WithNoInventory_ReturnsEmptyListAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var lens = sp.GetRequiredService<IInventoryLens>();

    // Act
    var result = await lens.GetAllAsync();

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).HasCount().EqualTo(0);
  }

  [Test]
  public async Task GetAllAsync_WithMultipleEntries_ReturnsAllAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var lens = sp.GetRequiredService<IInventoryLens>();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();

    // Create 3 inventory entries
    var productId1 = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {
      ProductId = productId1,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var productId2 = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {
      ProductId = productId2,
      NewTotalQuantity = 50,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var productId3 = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {
      ProductId = productId3,
      NewTotalQuantity = 200,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    // Act
    var result = await lens.GetAllAsync();

    // Assert
    await Assert.That(result).HasCount().EqualTo(3);
    await Assert.That(result.Any(i => i.ProductId == productId1)).IsTrue();
    await Assert.That(result.Any(i => i.ProductId == productId2)).IsTrue();
    await Assert.That(result.Any(i => i.ProductId == productId3)).IsTrue();
  }

  [Test]
  public async Task GetAllAsync_CalculatesAvailableCorrectlyAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var lens = sp.GetRequiredService<IInventoryLens>();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();

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
    var inventory = result[0];
    await Assert.That(inventory.Quantity).IsEqualTo(100);
    await Assert.That(inventory.Reserved).IsEqualTo(30);
    await Assert.That(inventory.Available).IsEqualTo(70); // 100 - 30
  }

  [Test]
  public async Task GetLowStockAsync_WithDefaultThreshold_ReturnsLowStockAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var lens = sp.GetRequiredService<IInventoryLens>();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();

    // Create products with varying stock levels
    var lowStockProductId = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {
      ProductId = lowStockProductId,
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
    await Assert.That(result[0].ProductId).IsEqualTo(lowStockProductId);
  }

  [Test]
  public async Task GetLowStockAsync_WithCustomThreshold_UsesThresholdAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var lens = sp.GetRequiredService<IInventoryLens>();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();

    // Create products with varying stock levels
    var lowStockProductId = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {
      ProductId = lowStockProductId,
      NewTotalQuantity = 5,
      RestockedAt = DateTime.UtcNow
    }, CancellationToken.None);

    var mediumStockProductId = Guid.CreateVersion7();
    await perspective.Update(new InventoryRestockedEvent {
      ProductId = mediumStockProductId,
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
    await Assert.That(result.Any(i => i.ProductId == lowStockProductId)).IsTrue();
    await Assert.That(result.Any(i => i.ProductId == mediumStockProductId)).IsTrue();
  }

  [Test]
  public async Task GetLowStockAsync_WithNoLowStock_ReturnsEmptyListAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var lens = sp.GetRequiredService<IInventoryLens>();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();

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
