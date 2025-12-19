using ECommerce.BFF.API.Hubs;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Materializes inventory events into BFF read model.
/// Handles ProductCreatedEvent (initializes at 0), InventoryRestockedEvent, InventoryReservedEvent, InventoryReleasedEvent, and InventoryAdjustedEvent.
/// Sends real-time SignalR notifications after successful database updates.
/// Uses EF Core with 3-column JSONB pattern - zero reflection, AOT compatible.
/// </summary>
public class InventoryLevelsPerspective(
  IPerspectiveStore<InventoryLevelDto> store,
  ILensQuery<InventoryLevelDto> query,
  ILogger<InventoryLevelsPerspective> logger,
  IHubContext<ProductInventoryHub> hubContext) :
  IPerspectiveOf<ProductCreatedEvent>,
  IPerspectiveOf<InventoryRestockedEvent>,
  IPerspectiveOf<InventoryReservedEvent>,
  IPerspectiveOf<InventoryReleasedEvent>,
  IPerspectiveOf<InventoryAdjustedEvent> {

  private readonly IPerspectiveStore<InventoryLevelDto> _store = store;
  private readonly ILensQuery<InventoryLevelDto> _query = query;
  private readonly ILogger<InventoryLevelsPerspective> _logger = logger;
  private readonly IHubContext<ProductInventoryHub> _hubContext = hubContext;

  /// <summary>
  /// Handles ProductCreatedEvent by initializing inventory at 0 quantity.
  /// Creates initial inventory record for new products.
  /// Sends real-time SignalR notification after successful database insert.
  /// </summary>
  public async Task Update(ProductCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      var model = new InventoryLevelDto {
        ProductId = @event.ProductId,
        Quantity = 0,
        Reserved = 0,
        Available = 0,
        LastUpdated = @event.CreatedAt
      };

      // Store handles JSON serialization, metadata, scope, timestamps
      await _store.UpsertAsync(@event.ProductId.ToString(), model, cancellationToken);

      _logger.LogInformation(
        "BFF inventory levels initialized: Product {ProductId} created with 0 quantity",
        @event.ProductId);

      // Send real-time notification
      await SendInventoryNotificationAsync(
        @event.ProductId.ToString(),
        "Created",
        model,
        null,
        cancellationToken);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to initialize BFF inventory levels for ProductCreatedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles InventoryRestockedEvent by upserting inventory levels.
  /// Creates new record if product doesn't exist, updates if it does.
  /// Preserves existing Reserved count - only updates Quantity.
  /// Sends real-time SignalR notification after successful database update.
  /// </summary>
  public async Task Update(InventoryRestockedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Get existing inventory to preserve reserved count
      var existing = await _query.GetByIdAsync(@event.ProductId, cancellationToken);

      if (existing is null) {
        _logger.LogInformation(
          "Product {ProductId} not found for restock - creating new entry",
          @event.ProductId);

        // Create new entry with no reservations
        existing = new InventoryLevelDto {
          ProductId = @event.ProductId,
          Quantity = 0,
          Reserved = 0,
          Available = 0,
          LastUpdated = @event.RestockedAt
        };
      }

      var model = new InventoryLevelDto {
        ProductId = @event.ProductId,
        Quantity = @event.NewTotalQuantity,
        Reserved = existing.Reserved,  // Preserve existing reserved count
        Available = @event.NewTotalQuantity - existing.Reserved,
        LastUpdated = @event.RestockedAt
      };

      // Store handles JSON serialization, metadata, scope, timestamps
      await _store.UpsertAsync(@event.ProductId.ToString(), model, cancellationToken);

      _logger.LogInformation(
        "BFF inventory levels updated: Product {ProductId} restocked to {Quantity} (Reserved: {Reserved}, Available: {Available})",
        @event.ProductId,
        @event.NewTotalQuantity,
        model.Reserved,
        model.Available);

      // Send real-time notification
      await SendInventoryNotificationAsync(
        @event.ProductId.ToString(),
        "Restocked",
        model,
        null,
        cancellationToken);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update BFF inventory levels for InventoryRestockedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles InventoryReservedEvent by incrementing reserved count and updating available.
  /// Sends real-time SignalR notification after successful database update.
  /// </summary>
  public async Task Update(InventoryReservedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Get existing inventory to increment reserved count
      var existing = await _query.GetByIdAsync(@event.ProductId, cancellationToken);

      if (existing is null) {
        _logger.LogWarning(
          "Product {ProductId} not found for reservation - creating new entry with reserved quantity",
          @event.ProductId);

        // Defensive: create new entry with reserved quantity
        existing = new InventoryLevelDto {
          ProductId = @event.ProductId,
          Quantity = 0,
          Reserved = 0,
          Available = 0,
          LastUpdated = @event.ReservedAt
        };
      }

      var updated = new InventoryLevelDto {
        ProductId = @event.ProductId,
        Quantity = existing.Quantity,
        Reserved = existing.Reserved + @event.Quantity,
        Available = existing.Quantity - (existing.Reserved + @event.Quantity),
        LastUpdated = @event.ReservedAt
      };

      await _store.UpsertAsync(@event.ProductId.ToString(), updated, cancellationToken);

      _logger.LogInformation(
        "BFF inventory levels updated: Product {ProductId} reserved {Quantity} units",
        @event.ProductId,
        @event.Quantity);

      // Send real-time notification
      await SendInventoryNotificationAsync(
        @event.ProductId.ToString(),
        "Reserved",
        updated,
        null,
        cancellationToken);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update BFF inventory levels for InventoryReservedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles InventoryReleasedEvent by decrementing reserved count and updating available.
  /// No SignalR notification sent - released events are internal operations.
  /// </summary>
  public async Task Update(InventoryReleasedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Get existing inventory to decrement reserved count
      var existing = await _query.GetByIdAsync(@event.ProductId, cancellationToken);

      if (existing is null) {
        _logger.LogWarning(
          "Product {ProductId} not found for release - ignoring event",
          @event.ProductId);
        return;
      }

      var updated = new InventoryLevelDto {
        ProductId = @event.ProductId,
        Quantity = existing.Quantity,
        Reserved = existing.Reserved - @event.Quantity,
        Available = existing.Quantity - (existing.Reserved - @event.Quantity),
        LastUpdated = @event.ReleasedAt
      };

      await _store.UpsertAsync(@event.ProductId.ToString(), updated, cancellationToken);

      _logger.LogInformation(
        "BFF inventory levels updated: Product {ProductId} released {Quantity} units",
        @event.ProductId,
        @event.Quantity);

      // NOT sending notification for Released events - internal operations, not customer-facing
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update BFF inventory levels for InventoryReleasedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles InventoryAdjustedEvent by setting new quantity total and recalculating available.
  /// Used for manual adjustments (e.g., damaged goods, audits).
  /// Sends real-time SignalR notification after successful database update.
  /// </summary>
  public async Task Update(InventoryAdjustedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Get existing inventory to preserve reserved count
      var existing = await _query.GetByIdAsync(@event.ProductId, cancellationToken);

      if (existing is null) {
        _logger.LogWarning(
          "Product {ProductId} not found for adjustment - creating new entry",
          @event.ProductId);

        // Defensive: create new entry with adjusted quantity
        existing = new InventoryLevelDto {
          ProductId = @event.ProductId,
          Quantity = 0,
          Reserved = 0,
          Available = 0,
          LastUpdated = @event.AdjustedAt
        };
      }

      var updated = new InventoryLevelDto {
        ProductId = @event.ProductId,
        Quantity = @event.NewTotalQuantity,
        Reserved = existing.Reserved,
        Available = @event.NewTotalQuantity - existing.Reserved,
        LastUpdated = @event.AdjustedAt
      };

      await _store.UpsertAsync(@event.ProductId.ToString(), updated, cancellationToken);

      _logger.LogInformation(
        "BFF inventory levels updated: Product {ProductId} adjusted to {Quantity} (Reason: {Reason})",
        @event.ProductId,
        @event.NewTotalQuantity,
        @event.Reason);

      // Send real-time notification
      await SendInventoryNotificationAsync(
        @event.ProductId.ToString(),
        "Adjusted",
        updated,
        @event.Reason,
        cancellationToken);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update BFF inventory levels for InventoryAdjustedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Sends SignalR notification to all-products group and product-specific group
  /// </summary>
  private async Task SendInventoryNotificationAsync(
    string productId,
    string notificationType,
    InventoryLevelDto inventory,
    string? reason,
    CancellationToken cancellationToken) {
    try {
      var notification = new InventoryNotification {
        ProductId = productId,
        NotificationType = notificationType,
        Quantity = inventory.Quantity,
        Reserved = inventory.Reserved,
        Available = inventory.Available,
        Reason = reason
      };

      var methodName = $"Inventory{notificationType}";  // e.g., "InventoryRestocked", "InventoryAdjusted"

      // Send to all-products group
      await _hubContext.Clients.Group("all-products")
        .SendAsync(methodName, notification, cancellationToken);

      // Send to product-specific group
      await _hubContext.Clients.Group($"product-{productId}")
        .SendAsync(methodName, notification, cancellationToken);

      _logger.LogInformation(
        "Sent SignalR inventory notification for product {ProductId}: {NotificationType}",
        productId,
        notificationType);
    } catch (Exception ex) {
      // Log error but don't throw - SignalR failure shouldn't break perspective update
      _logger.LogError(ex,
        "Failed to send SignalR inventory notification for product {ProductId}",
        productId);
    }
  }
}
