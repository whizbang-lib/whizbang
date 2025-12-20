using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.InventoryWorker.Perspectives;

/// <summary>
/// Materializes inventory events into InventoryLevelDto perspective.
/// Handles ProductCreatedEvent (initializes at 0), InventoryRestockedEvent, InventoryReservedEvent, and InventoryAdjustedEvent.
/// Pure functions - no I/O, no side effects, deterministic.
/// </summary>
public class InventoryLevelsPerspective :
  IPerspectiveFor<InventoryLevelDto, ProductCreatedEvent, InventoryRestockedEvent, InventoryReservedEvent, InventoryAdjustedEvent> {

  /// <summary>
  /// Handles ProductCreatedEvent by initializing inventory at 0 quantity.
  /// Creates initial inventory record for new products.
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
  /// Returns currentData unchanged if no existing data.
  /// </summary>
  public InventoryLevelDto Apply(InventoryLevelDto currentData, InventoryReservedEvent @event) {
    // If no existing data, cannot reserve - return null or throw?
    // Based on old logic, we skip if not found
    if (currentData == null) {
      return null!; // PerspectiveRunner will handle null return
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
  /// Handles InventoryAdjustedEvent by setting new quantity total.
  /// Used for manual adjustments (e.g., damaged goods, audits).
  /// Preserves reserved count from existing data.
  /// </summary>
  public InventoryLevelDto Apply(InventoryLevelDto currentData, InventoryAdjustedEvent @event) {
    // If no existing data, cannot adjust - skip
    if (currentData == null) {
      return null!; // PerspectiveRunner will handle null return
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
