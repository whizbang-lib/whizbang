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
/// Integration tests for InventoryLevelsPerspective
/// </summary>
public class InventoryLevelsPerspectiveTests : IAsyncDisposable {
  private readonly DatabaseTestHelper _dbHelper = new();

  [Test]
  public async Task Update_WithInventoryRestockedEvent_CreatesNewInventoryRecordAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    var @event = new InventoryRestockedEvent {
      ProductId = "prod-123",
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify inventory record was created
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT product_id, quantity, reserved, available, last_updated FROM inventoryworker.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = "prod-123" });

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.product_id).IsEqualTo("prod-123");
    await Assert.That(inventory.quantity).IsEqualTo(100);
    await Assert.That(inventory.reserved).IsEqualTo(0);
    await Assert.That(inventory.available).IsEqualTo(100); // Computed column: quantity - reserved
  }

  [Test]
  public async Task Update_WithInventoryRestockedEvent_UpdatesExistingInventoryAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    // Create initial inventory
    var initialEvent = new InventoryRestockedEvent {
      ProductId = "prod-update",
      QuantityAdded = 50,
      NewTotalQuantity = 50,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(initialEvent, CancellationToken.None);

    // Act - Restock again
    var restockEvent = new InventoryRestockedEvent {
      ProductId = "prod-update",
      QuantityAdded = 75,
      NewTotalQuantity = 125,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Assert - Verify quantity was updated
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT quantity, reserved, available FROM inventoryworker.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = "prod-update" });

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.quantity).IsEqualTo(125);
    await Assert.That(inventory.reserved).IsEqualTo(0); // Should remain unchanged
    await Assert.That(inventory.available).IsEqualTo(125);
  }

  [Test]
  public async Task Update_WithInventoryRestockedEvent_UpdatesLastUpdatedTimestampAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    var restockTime = DateTime.UtcNow;
    var @event = new InventoryRestockedEvent {
      ProductId = "prod-timestamp",
      QuantityAdded = 50,
      NewTotalQuantity = 50,
      RestockedAt = restockTime
    };

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify last_updated was set
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT last_updated FROM inventoryworker.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = "prod-timestamp" });

    await Assert.That(inventory).IsNotNull();
  }

  [Test]
  public async Task Update_WithInventoryRestockedEvent_LogsSuccessAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    var @event = new InventoryRestockedEvent {
      ProductId = "prod-log",
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Should have logged something
    await Assert.That(logger.LoggedMessages).HasCount().GreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task Update_WithInventoryReservedEvent_UpdatesReservedQuantityAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {
      ProductId = "prod-reserve",
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Reserve some inventory
    var reservedEvent = new InventoryReservedEvent {
      OrderId = "order-123",
      ProductId = "prod-reserve",
      Quantity = 25,
      ReservedAt = DateTime.UtcNow
    };
    await perspective.Update(reservedEvent, CancellationToken.None);

    // Assert - Verify reserved was updated
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT quantity, reserved, available FROM inventoryworker.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = "prod-reserve" });

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.quantity).IsEqualTo(100);
    await Assert.That(inventory.reserved).IsEqualTo(25);
    await Assert.That(inventory.available).IsEqualTo(75); // Computed: 100 - 25
  }

  [Test]
  public async Task Update_WithInventoryReservedEvent_AccumulatesReservationsAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {
      ProductId = "prod-multi-reserve",
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Reserve multiple times
    var reservation1 = new InventoryReservedEvent {
      OrderId = "order-1",
      ProductId = "prod-multi-reserve",
      Quantity = 10,
      ReservedAt = DateTime.UtcNow
    };
    await perspective.Update(reservation1, CancellationToken.None);

    var reservation2 = new InventoryReservedEvent {
      OrderId = "order-2",
      ProductId = "prod-multi-reserve",
      Quantity = 15,
      ReservedAt = DateTime.UtcNow
    };
    await perspective.Update(reservation2, CancellationToken.None);

    // Assert - Verify reserved accumulated
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT quantity, reserved, available FROM inventoryworker.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = "prod-multi-reserve" });

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.quantity).IsEqualTo(100);
    await Assert.That(inventory.reserved).IsEqualTo(25); // 10 + 15
    await Assert.That(inventory.available).IsEqualTo(75); // 100 - 25
  }

  [Test]
  public async Task Update_WithInventoryReservedEvent_UpdatesLastUpdatedTimestampAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {
      ProductId = "prod-reserve-time",
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Reserve inventory
    var reserveTime = DateTime.UtcNow;
    var reservedEvent = new InventoryReservedEvent {
      OrderId = "order-time",
      ProductId = "prod-reserve-time",
      Quantity = 25,
      ReservedAt = reserveTime
    };
    await perspective.Update(reservedEvent, CancellationToken.None);

    // Assert - Verify last_updated was updated
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT last_updated FROM inventoryworker.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = "prod-reserve-time" });

    await Assert.That(inventory).IsNotNull();
  }

  [Test]
  public async Task Update_WithInventoryReservedEvent_LogsSuccessAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {
      ProductId = "prod-log-reserve",
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act
    var reservedEvent = new InventoryReservedEvent {
      OrderId = "order-log",
      ProductId = "prod-log-reserve",
      Quantity = 25,
      ReservedAt = DateTime.UtcNow
    };
    await perspective.Update(reservedEvent, CancellationToken.None);

    // Assert - Should have logged something
    await Assert.That(logger.LoggedMessages).HasCount().GreaterThanOrEqualTo(2); // Restock + Reserve
  }

  [Test]
  public async Task Update_WithInventoryAdjustedEvent_UpdatesQuantityAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {
      ProductId = "prod-adjust",
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Adjust inventory (e.g., correcting count)
    var adjustedEvent = new InventoryAdjustedEvent {
      ProductId = "prod-adjust",
      QuantityChange = -10, // Found 10 damaged items
      NewTotalQuantity = 90,
      Reason = "Damaged items removed",
      AdjustedAt = DateTime.UtcNow
    };
    await perspective.Update(adjustedEvent, CancellationToken.None);

    // Assert - Verify quantity was adjusted
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT quantity, available FROM inventoryworker.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = "prod-adjust" });

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.quantity).IsEqualTo(90);
    await Assert.That(inventory.available).IsEqualTo(90);
  }

  [Test]
  public async Task Update_WithInventoryAdjustedEvent_HandlesPositiveAdjustmentAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {
      ProductId = "prod-adjust-pos",
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Positive adjustment (found extra items)
    var adjustedEvent = new InventoryAdjustedEvent {
      ProductId = "prod-adjust-pos",
      QuantityChange = 15,
      NewTotalQuantity = 115,
      Reason = "Found extra items during audit",
      AdjustedAt = DateTime.UtcNow
    };
    await perspective.Update(adjustedEvent, CancellationToken.None);

    // Assert - Verify quantity was increased
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT quantity FROM inventoryworker.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = "prod-adjust-pos" });

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.quantity).IsEqualTo(115);
  }

  [Test]
  public async Task Update_WithInventoryAdjustedEvent_UpdatesLastUpdatedTimestampAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {
      ProductId = "prod-adjust-time",
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Adjust inventory
    var adjustTime = DateTime.UtcNow;
    var adjustedEvent = new InventoryAdjustedEvent {
      ProductId = "prod-adjust-time",
      QuantityChange = -5,
      NewTotalQuantity = 95,
      Reason = "Adjustment",
      AdjustedAt = adjustTime
    };
    await perspective.Update(adjustedEvent, CancellationToken.None);

    // Assert - Verify last_updated was updated
    var connectionString = await _dbHelper.GetConnectionStringAsync();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRow>(
      "SELECT last_updated FROM inventoryworker.inventory_levels WHERE product_id = @ProductId",
      new { ProductId = "prod-adjust-time" });

    await Assert.That(inventory).IsNotNull();
  }

  [Test]
  public async Task Update_WithInventoryAdjustedEvent_LogsSuccessAsync() {
    // Arrange
    var connectionFactory = await _dbHelper.CreateConnectionFactoryAsync();
    var logger = new TestLogger<InventoryLevelsPerspective>();
    var perspective = new InventoryLevelsPerspective(connectionFactory, logger);

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {
      ProductId = "prod-log-adjust",
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act
    var adjustedEvent = new InventoryAdjustedEvent {
      ProductId = "prod-log-adjust",
      QuantityChange = -5,
      NewTotalQuantity = 95,
      Reason = "Test",
      AdjustedAt = DateTime.UtcNow
    };
    await perspective.Update(adjustedEvent, CancellationToken.None);

    // Assert - Should have logged something
    await Assert.That(logger.LoggedMessages).HasCount().GreaterThanOrEqualTo(2); // Restock + Adjust
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
  public string product_id { get; init; } = string.Empty;
  public int quantity { get; init; }
  public int reserved { get; init; }
  public int available { get; init; }
  public DateTime last_updated { get; init; }
}
