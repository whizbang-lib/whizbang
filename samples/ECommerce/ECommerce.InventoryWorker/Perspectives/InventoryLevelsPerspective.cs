using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Lenses;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.InventoryWorker.Perspectives;

/// <summary>
/// Materializes inventory events into InventoryLevelDto perspective using EF Core.
/// Handles ProductCreatedEvent (initializes at 0), InventoryRestockedEvent, InventoryReservedEvent, and InventoryAdjustedEvent.
/// </summary>
public class InventoryLevelsPerspective(
  IPerspectiveStore<InventoryLevelDto> store,
  ILensQuery<InventoryLevelDto> query,
  ILogger<InventoryLevelsPerspective> logger) :
  IPerspectiveOf<ProductCreatedEvent>,
  IPerspectiveOf<InventoryRestockedEvent>,
  IPerspectiveOf<InventoryReservedEvent>,
  IPerspectiveOf<InventoryAdjustedEvent> {

  private readonly IPerspectiveStore<InventoryLevelDto> _store = store;
  private readonly ILensQuery<InventoryLevelDto> _query = query;
  private readonly ILogger<InventoryLevelsPerspective> _logger = logger;

  /// <summary>
  /// Handles ProductCreatedEvent by initializing inventory at 0 quantity.
  /// Creates initial inventory record for new products.
  /// </summary>
  public async Task Update(ProductCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      var inventory = new InventoryLevelDto {
        ProductId = @event.ProductId,
        Quantity = 0,
        Reserved = 0,
        Available = 0,
        LastUpdated = @event.CreatedAt
      };

      await _store.UpsertAsync(@event.ProductId.ToString(), inventory, cancellationToken);

      _logger.LogInformation(
        "Inventory levels initialized: Product {ProductId} created with 0 quantity",
        @event.ProductId);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to initialize inventory levels for ProductCreatedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles InventoryRestockedEvent by upserting inventory levels.
  /// Creates new record if product doesn't exist, updates if it does.
  /// </summary>
  public async Task Update(InventoryRestockedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Get existing inventory or create new with defaults
      var existing = await _query.GetByIdAsync(@event.ProductId.ToString(), cancellationToken);

      var reserved = existing?.Reserved ?? 0;
      var inventory = new InventoryLevelDto {
        ProductId = @event.ProductId,
        Quantity = @event.NewTotalQuantity,
        Reserved = reserved,
        Available = @event.NewTotalQuantity - reserved,
        LastUpdated = @event.RestockedAt
      };

      await _store.UpsertAsync(@event.ProductId.ToString(), inventory, cancellationToken);

      _logger.LogInformation(
        "Inventory levels updated: Product {ProductId} restocked to {Quantity}",
        @event.ProductId,
        @event.NewTotalQuantity);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update inventory levels for InventoryRestockedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles InventoryReservedEvent by incrementing reserved count.
  /// </summary>
  public async Task Update(InventoryReservedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Get existing inventory to increment reserved
      var existing = await _query.GetByIdAsync(@event.ProductId.ToString(), cancellationToken);
      if (existing == null) {
        _logger.LogWarning(
          "Inventory {ProductId} not found for reservation, skipping",
          @event.ProductId);
        return;
      }

      var newReserved = existing.Reserved + @event.Quantity;
      var updated = new InventoryLevelDto {
        ProductId = existing.ProductId,
        Quantity = existing.Quantity,
        Reserved = newReserved,
        Available = existing.Quantity - newReserved,
        LastUpdated = @event.ReservedAt
      };

      await _store.UpsertAsync(@event.ProductId.ToString(), updated, cancellationToken);

      _logger.LogInformation(
        "Inventory levels updated: Product {ProductId} reserved {Quantity} units",
        @event.ProductId,
        @event.Quantity);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update inventory levels for InventoryReservedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles InventoryAdjustedEvent by setting new quantity total.
  /// Used for manual adjustments (e.g., damaged goods, audits).
  /// </summary>
  public async Task Update(InventoryAdjustedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Get existing inventory to preserve reserved count
      var existing = await _query.GetByIdAsync(@event.ProductId.ToString(), cancellationToken);
      if (existing == null) {
        _logger.LogWarning(
          "Inventory {ProductId} not found for adjustment, skipping",
          @event.ProductId);
        return;
      }

      var updated = new InventoryLevelDto {
        ProductId = existing.ProductId,
        Quantity = @event.NewTotalQuantity,
        Reserved = existing.Reserved,
        Available = @event.NewTotalQuantity - existing.Reserved,
        LastUpdated = @event.AdjustedAt
      };

      await _store.UpsertAsync(@event.ProductId.ToString(), updated, cancellationToken);

      _logger.LogInformation(
        "Inventory levels updated: Product {ProductId} adjusted to {Quantity} (Reason: {Reason})",
        @event.ProductId,
        @event.NewTotalQuantity,
        @event.Reason);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update inventory levels for InventoryAdjustedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }
}
