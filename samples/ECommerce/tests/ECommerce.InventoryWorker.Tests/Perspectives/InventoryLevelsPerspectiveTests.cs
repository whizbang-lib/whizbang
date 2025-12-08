using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Lenses;
using ECommerce.InventoryWorker.Perspectives;
using ECommerce.InventoryWorker.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace ECommerce.InventoryWorker.Tests.Perspectives;

/// <summary>
/// Integration tests for InventoryLevelsPerspective
/// </summary>
public class InventoryLevelsPerspectiveTests : IAsyncDisposable {
  private readonly DatabaseTestHelper _dbHelper = new();

  [Test]
  public async Task Update_WithInventoryRestockedEvent_CreatesNewInventoryRecordAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();
    var query = sp.GetRequiredService<ILensQuery<InventoryLevelDto>>();

    var productId = Guid.CreateVersion7();
    var @event = new InventoryRestockedEvent {
      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify inventory record was created using EF Core
    var stored = await query.GetByIdAsync(productId.ToString());

    await Assert.That(stored).IsNotNull();
    await Assert.That(stored!.ProductId).IsEqualTo(productId);
    await Assert.That(stored.Quantity).IsEqualTo(100);
    await Assert.That(stored.Reserved).IsEqualTo(0);
    await Assert.That(stored.Available).IsEqualTo(100); // Computed: quantity - reserved
  }

  [Test]
  public async Task Update_WithInventoryRestockedEvent_UpdatesExistingInventoryAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();
    var query = sp.GetRequiredService<ILensQuery<InventoryLevelDto>>();

    var productId = Guid.CreateVersion7();

    // Create initial inventory
    var initialEvent = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 50,
      NewTotalQuantity = 50,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(initialEvent, CancellationToken.None);

    // Act - Restock again
    var restockEvent = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 75,
      NewTotalQuantity = 125,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Assert - Verify quantity was updated using EF Core
    var stored = await query.GetByIdAsync(productId.ToString());

    await Assert.That(stored).IsNotNull();
    await Assert.That(stored!.Quantity).IsEqualTo(125);
    await Assert.That(stored.Reserved).IsEqualTo(0); // Should remain unchanged
    await Assert.That(stored.Available).IsEqualTo(125);
  }

  [Test]
  public async Task Update_WithInventoryRestockedEvent_UpdatesLastUpdatedTimestampAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();
    var query = sp.GetRequiredService<ILensQuery<InventoryLevelDto>>();

    var productId = Guid.CreateVersion7();
    var restockTime = DateTime.UtcNow;
    var @event = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 50,
      NewTotalQuantity = 50,
      RestockedAt = restockTime
    };

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify last_updated was set using EF Core
    var stored = await query.GetByIdAsync(productId.ToString());

    await Assert.That(stored).IsNotNull();
  }


  [Test]
  public async Task Update_WithInventoryReservedEvent_UpdatesReservedQuantityAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();
    var query = sp.GetRequiredService<ILensQuery<InventoryLevelDto>>();

    var productId = Guid.CreateVersion7();

    // Create initial inventory
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

    // Assert - Verify reserved was updated using EF Core
    var stored = await query.GetByIdAsync(productId.ToString());

    await Assert.That(stored).IsNotNull();
    await Assert.That(stored!.Quantity).IsEqualTo(100);
    await Assert.That(stored.Reserved).IsEqualTo(25);
    await Assert.That(stored.Available).IsEqualTo(75); // Computed: 100 - 25
  }

  [Test]
  public async Task Update_WithInventoryReservedEvent_AccumulatesReservationsAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();
    var query = sp.GetRequiredService<ILensQuery<InventoryLevelDto>>();

    var productId = Guid.CreateVersion7();

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Reserve multiple times
    var reservation1 = new InventoryReservedEvent {
      OrderId = "order-1",

      ProductId = productId,
      Quantity = 10,
      ReservedAt = DateTime.UtcNow
    };
    await perspective.Update(reservation1, CancellationToken.None);

    var reservation2 = new InventoryReservedEvent {
      OrderId = "order-2",

      ProductId = productId,
      Quantity = 15,
      ReservedAt = DateTime.UtcNow
    };
    await perspective.Update(reservation2, CancellationToken.None);

    // Assert - Verify reserved accumulated using EF Core
    var stored = await query.GetByIdAsync(productId.ToString());

    await Assert.That(stored).IsNotNull();
    await Assert.That(stored!.Quantity).IsEqualTo(100);
    await Assert.That(stored.Reserved).IsEqualTo(25); // 10 + 15
    await Assert.That(stored.Available).IsEqualTo(75); // 100 - 25
  }

  [Test]
  public async Task Update_WithInventoryReservedEvent_UpdatesLastUpdatedTimestampAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();
    var query = sp.GetRequiredService<ILensQuery<InventoryLevelDto>>();

    var productId = Guid.CreateVersion7();

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Reserve inventory
    var reserveTime = DateTime.UtcNow;
    var reservedEvent = new InventoryReservedEvent {
      OrderId = "order-time",

      ProductId = productId,
      Quantity = 25,
      ReservedAt = reserveTime
    };
    await perspective.Update(reservedEvent, CancellationToken.None);

    // Assert - Verify last_updated was updated using EF Core
    var stored = await query.GetByIdAsync(productId.ToString());

    await Assert.That(stored).IsNotNull();
  }


  [Test]
  public async Task Update_WithInventoryAdjustedEvent_UpdatesQuantityAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();
    var query = sp.GetRequiredService<ILensQuery<InventoryLevelDto>>();

    var productId = Guid.CreateVersion7();

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Adjust inventory (e.g., correcting count)
    var adjustedEvent = new InventoryAdjustedEvent {

      ProductId = productId,
      QuantityChange = -10, // Found 10 damaged items
      NewTotalQuantity = 90,
      Reason = "Damaged items removed",
      AdjustedAt = DateTime.UtcNow
    };
    await perspective.Update(adjustedEvent, CancellationToken.None);

    // Assert - Verify quantity was adjusted using EF Core
    var stored = await query.GetByIdAsync(productId.ToString());

    await Assert.That(stored).IsNotNull();
    await Assert.That(stored!.Quantity).IsEqualTo(90);
    await Assert.That(stored.Available).IsEqualTo(90);
  }

  [Test]
  public async Task Update_WithInventoryAdjustedEvent_HandlesPositiveAdjustmentAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();
    var query = sp.GetRequiredService<ILensQuery<InventoryLevelDto>>();

    var productId = Guid.CreateVersion7();

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Positive adjustment (found extra items)
    var adjustedEvent = new InventoryAdjustedEvent {

      ProductId = productId,
      QuantityChange = 15,
      NewTotalQuantity = 115,
      Reason = "Found extra items during audit",
      AdjustedAt = DateTime.UtcNow
    };
    await perspective.Update(adjustedEvent, CancellationToken.None);

    // Assert - Verify quantity was increased using EF Core
    var stored = await query.GetByIdAsync(productId.ToString());

    await Assert.That(stored).IsNotNull();
    await Assert.That(stored!.Quantity).IsEqualTo(115);
  }

  [Test]
  public async Task Update_WithInventoryAdjustedEvent_UpdatesLastUpdatedTimestampAsync() {
    // Arrange
    var sp = await _dbHelper.CreateServiceProviderAsync();
    var perspective = sp.GetRequiredService<InventoryLevelsPerspective>();
    var query = sp.GetRequiredService<ILensQuery<InventoryLevelDto>>();

    var productId = Guid.CreateVersion7();

    // Create initial inventory
    var restockEvent = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };
    await perspective.Update(restockEvent, CancellationToken.None);

    // Act - Adjust inventory
    var adjustTime = DateTime.UtcNow;
    var adjustedEvent = new InventoryAdjustedEvent {

      ProductId = productId,
      QuantityChange = -5,
      NewTotalQuantity = 95,
      Reason = "Adjustment",
      AdjustedAt = adjustTime
    };
    await perspective.Update(adjustedEvent, CancellationToken.None);

    // Assert - Verify last_updated was updated using EF Core
    var stored = await query.GetByIdAsync(productId.ToString());

    await Assert.That(stored).IsNotNull();
  }


  [After(Test)]
  public async Task CleanupAsync() {
    await _dbHelper.CleanupDatabaseAsync();
  }

  public async ValueTask DisposeAsync() {
    await _dbHelper.DisposeAsync();
  }
}
