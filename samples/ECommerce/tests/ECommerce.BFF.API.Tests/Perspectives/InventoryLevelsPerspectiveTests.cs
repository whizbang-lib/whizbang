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
/// Integration tests for InventoryLevelsPerspective (BFF) using unified Whizbang API
/// </summary>
public class InventoryLevelsPerspectiveTests : IAsyncDisposable {
  private readonly EFCoreTestHelper _helper = new();

  [Test]
  public async Task Update_WithInventoryRestockedEvent_UpdatesQuantityAndAvailableAsync() {
    // Arrange
    var perspective = new InventoryLevelsPerspective(
      _helper.GetPerspectiveStore<InventoryLevelDto>(),
      _helper.GetLensQuery<InventoryLevelDto>(),
      _helper.GetLogger<InventoryLevelsPerspective>(),
      _helper.GetHubContext());

    var lens = _helper.GetLensQuery<InventoryLevelDto>();

    var productId = Guid.CreateVersion7();
    var @event = new InventoryRestockedEvent {

      ProductId = productId,
      QuantityAdded = 100,
      NewTotalQuantity = 100,
      RestockedAt = DateTime.UtcNow
    };

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify inventory was created/updated using lens query
    var inventory = await lens.GetByIdAsync(productId.ToString(), CancellationToken.None);

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.ProductId).IsEqualTo(productId);
    await Assert.That(inventory.Quantity).IsEqualTo(100);
    await Assert.That(inventory.Reserved).IsEqualTo(0);
    await Assert.That(inventory.Available).IsEqualTo(100);
  }

  [Test]
  public async Task Update_WithInventoryReservedEvent_UpdatesReservedAndAvailableAsync() {
    // Arrange
    var perspective = new InventoryLevelsPerspective(
      _helper.GetPerspectiveStore<InventoryLevelDto>(),
      _helper.GetLensQuery<InventoryLevelDto>(),
      _helper.GetLogger<InventoryLevelsPerspective>(),
      _helper.GetHubContext());

    var lens = _helper.GetLensQuery<InventoryLevelDto>();

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

    // Assert - Verify reserved and available were updated using lens query
    var inventory = await lens.GetByIdAsync(productId.ToString(), CancellationToken.None);

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.Quantity).IsEqualTo(100);
    await Assert.That(inventory.Reserved).IsEqualTo(25);
    await Assert.That(inventory.Available).IsEqualTo(75); // 100 - 25
  }

  [Test]
  public async Task Update_WithInventoryReleasedEvent_UpdatesReservedAndAvailableAsync() {
    // Arrange
    var perspective = new InventoryLevelsPerspective(
      _helper.GetPerspectiveStore<InventoryLevelDto>(),
      _helper.GetLensQuery<InventoryLevelDto>(),
      _helper.GetLogger<InventoryLevelsPerspective>(),
      _helper.GetHubContext());

    var lens = _helper.GetLensQuery<InventoryLevelDto>();

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

    // Assert - Verify reserved and available were updated using lens query
    var inventory = await lens.GetByIdAsync(productId.ToString(), CancellationToken.None);

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.Quantity).IsEqualTo(100);
    await Assert.That(inventory.Reserved).IsEqualTo(0); // 30 - 30
    await Assert.That(inventory.Available).IsEqualTo(100); // Back to full availability
  }

  [Test]
  public async Task Update_WithInventoryAdjustedEvent_UpdatesQuantityAndAvailableAsync() {
    // Arrange
    var perspective = new InventoryLevelsPerspective(
      _helper.GetPerspectiveStore<InventoryLevelDto>(),
      _helper.GetLensQuery<InventoryLevelDto>(),
      _helper.GetLogger<InventoryLevelsPerspective>(),
      _helper.GetHubContext());

    var lens = _helper.GetLensQuery<InventoryLevelDto>();

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

    // Assert - Verify quantity and available were updated using lens query
    var inventory = await lens.GetByIdAsync(productId.ToString(), CancellationToken.None);

    await Assert.That(inventory).IsNotNull();
    await Assert.That(inventory!.Quantity).IsEqualTo(90);
    await Assert.That(inventory.Reserved).IsEqualTo(0);
    await Assert.That(inventory.Available).IsEqualTo(90);
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _helper.CleanupDatabaseAsync();
  }

  public async ValueTask DisposeAsync() {
    await _helper.DisposeAsync();
  }
}
