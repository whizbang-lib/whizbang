using ECommerce.Contracts.Events;
using ECommerce.Contracts.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Materializes inventory events into BFF read model.
/// Handles ProductCreatedEvent (initializes at 0), InventoryRestockedEvent, InventoryReservedEvent, InventoryReleasedEvent, and InventoryAdjustedEvent.
/// Pure functions - no I/O, no side effects, deterministic.
/// NOTE: SignalR notifications removed - perspectives must be pure functions.
/// </summary>
public class InventoryLevelsPerspective :
  IPerspectiveFor<InventoryLevelDto, ProductCreatedEvent, InventoryRestockedEvent, InventoryReservedEvent, InventoryReleasedEvent, InventoryAdjustedEvent> {

  /// <summary>
  /// Handles ProductCreatedEvent by initializing inventory at 0 quantity.
  /// </summary>
  public InventoryLevelDto Apply(InventoryLevelDto currentData, ProductCreatedEvent @event) {
    return new InventoryLevelDto {
      ProductId = @event.ProductId,
      Quantity = 0,
      Reserved = 0,
      Available = 0,
      LastUpdated = @event.CreatedAt
    };
  }

  /// <summary>
  /// Handles InventoryRestockedEvent by setting new quantity.
  /// Preserves reserved count from existing data (defaults to 0 if no existing data).
  /// </summary>
  public InventoryLevelDto Apply(InventoryLevelDto currentData, InventoryRestockedEvent @event) {
    var reserved = currentData?.Reserved ?? 0;
    return new InventoryLevelDto {
      ProductId = @event.ProductId,
      Quantity = @event.NewTotalQuantity,
      Reserved = reserved,
      Available = @event.NewTotalQuantity - reserved,
      LastUpdated = @event.RestockedAt
    };
  }

  /// <summary>
  /// Handles InventoryReservedEvent by incrementing reserved count.
  /// </summary>
  public InventoryLevelDto Apply(InventoryLevelDto currentData, InventoryReservedEvent @event) {
    // Defensive: if no existing data, create with reserved quantity
    if (currentData == null) {
      return new InventoryLevelDto {
        ProductId = @event.ProductId,
        Quantity = 0,
        Reserved = @event.Quantity,
        Available = -@event.Quantity, // Negative available indicates we're over-reserved
        LastUpdated = @event.ReservedAt
      };
    }

    var newReserved = currentData.Reserved + @event.Quantity;
    return new InventoryLevelDto {
      ProductId = currentData.ProductId,
      Quantity = currentData.Quantity,
      Reserved = newReserved,
      Available = currentData.Quantity - newReserved,
      LastUpdated = @event.ReservedAt
    };
  }

  /// <summary>
  /// Handles InventoryReleasedEvent by decrementing reserved count.
  /// </summary>
  public InventoryLevelDto Apply(InventoryLevelDto currentData, InventoryReleasedEvent @event) {
    // If no existing data, cannot release - skip
    if (currentData == null) {
      return null!;
    }

    var newReserved = currentData.Reserved - @event.Quantity;
    return new InventoryLevelDto {
      ProductId = currentData.ProductId,
      Quantity = currentData.Quantity,
      Reserved = newReserved,
      Available = currentData.Quantity - newReserved,
      LastUpdated = @event.ReleasedAt
    };
  }

  /// <summary>
  /// Handles InventoryAdjustedEvent by setting new quantity total.
  /// </summary>
  public InventoryLevelDto Apply(InventoryLevelDto currentData, InventoryAdjustedEvent @event) {
    // Defensive: if no existing data, create with adjusted quantity
    if (currentData == null) {
      return new InventoryLevelDto {
        ProductId = @event.ProductId,
        Quantity = @event.NewTotalQuantity,
        Reserved = 0,
        Available = @event.NewTotalQuantity,
        LastUpdated = @event.AdjustedAt
      };
    }

    return new InventoryLevelDto {
      ProductId = currentData.ProductId,
      Quantity = @event.NewTotalQuantity,
      Reserved = currentData.Reserved,
      Available = @event.NewTotalQuantity - currentData.Reserved,
      LastUpdated = @event.AdjustedAt
    };
  }
}
