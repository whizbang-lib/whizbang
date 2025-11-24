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
/// Integration tests for InventoryLevelsPerspective (BFF)
/// </summary>
public class InventoryLevelsPerspectiveTests : IAsyncDisposable {
  private readonly DatabaseTestHelper _dbHelper = new();

  [Test]
  public async Task Update_WithInventoryRestockedEvent_UpdatesQuantityAndAvailableAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger, null!);

    var productId = Guid.CreateVersion7();
    var @event = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify inventory was created/updated in bff.inventory_levels
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT product_id, quantity, reserved, available, last_updated FROM bff.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = productId });

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.product_id).IsEqualTo(productId);
    await Assert.That(inventory.quantity).IsEqualTo(100);
    await Assert.That(inventory.reserved).IsEqualTo(0);
    await Assert.That(inventory.available).IsEqualTo(100);
  }

  [Test]
  public async Task Update_WithInventoryReservedEvent_UpdatesReservedAndAvailableAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger, null!);

    // Create initial inventory
    var productId = Guid.CreateVersion7();
    var restockEvent = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Reserve some inventory
    var reservedEvent = new InventoryReservedEvent {
      OrderId = "order-123",

      ProductId = productId,
      Quantity = 25,
      ReservedAt = DateTime.UtcNow
    };
    await perspective.Update(reservedEvent, CancellationToken.None);

    // Assert - Verify reserved and available were updated in bff.inventory_levels
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT quantity, reserved, available FROM bff.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = productId });

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.quantity).IsEqualTo(100);
    await Assert.That(inventory.reserved).IsEqualTo(25);
    await Assert.That(inventory.available).IsEqualTo(75); // 100 - 25
  }

  [Test]
  public async Task Update_WithInventoryReleasedEvent_UpdatesReservedAndAvailableAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger, null!);

    // Create initial inventory and reserve some
    var productId = Guid.CreateVersion7();
    var restockEvent = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    var reservedEvent = new InventoryReservedEvent {
      OrderId = "order-release",

      ProductId = productId,
      Quantity = 30,
      ReservedAt = DateTime.UtcNow
    };
    await perspective.Update(reservedEvent, CancellationToken.None);

    // Act - Release some reserved inventory
    var releasedEvent = new InventoryReleasedEvent {
      OrderId = "order-release",

      ProductId = productId,
      Quantity = 30,
      ReleasedAt = DateTime.UtcNow
    };
    await perspective.Update(releasedEvent, CancellationToken.None);

    // Assert - Verify reserved and available were updated in bff.inventory_levels
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT quantity, reserved, available FROM bff.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = productId });

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.quantity).IsEqualTo(100);
    await Assert.That(inventory.reserved).IsEqualTo(0); // 30 - 30
    await Assert.That(inventory.available).IsEqualTo(100); // Back to full availability
  }

  [Test]
  public async Task Update_WithInventoryAdjustedEvent_UpdatesQuantityAndAvailableAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger, null!);

    // Create initial inventory
    var productId = Guid.CreateVersion7();
    var restockEvent = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Adjust inventory (e.g., due to damage/loss)
    var adjustedEvent = new InventoryAdjustedEvent {

      ProductId = productId,
      QuantityChange = -10, // Lost 10 items
      NewTotalQuantity = 90,
      Reason = "Damaged items removed",
      AdjustedAt = DateTime.UtcNow
    };
    await perspective.Update(adjustedEvent, CancellationToken.None);

    // Assert - Verify quantity and available were updated in bff.inventory_levels
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT quantity, reserved, available FROM bff.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = productId });

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.quantity).IsEqualTo(90);
    await Assert.That(inventory.reserved).IsEqualTo(0);
    await Assert.That(inventory.available).IsEqualTo(90);
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
/// DTO for reading inventory_levels rows from database
/// </summary>
internal record InventoryRow {
  public Guid product_id { get; init; }
  public int quantity { get; init; }
  public int reserved { get; init; }
  public int available { get; init; }
  public DateTime last_updated { get; init; }
}
