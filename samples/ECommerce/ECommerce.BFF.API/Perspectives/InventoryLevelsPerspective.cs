using System.Data;
using Dapper;
using ECommerce.BFF.API.Hubs;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Data;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Materializes inventory events into bff.inventory_levels table.
/// Handles InventoryRestockedEvent, InventoryReservedEvent, InventoryReleasedEvent, and InventoryAdjustedEvent.
/// Sends real-time SignalR notifications after successful database updates.
/// </summary>
public class InventoryLevelsPerspective :
  IPerspectiveOf<InventoryRestockedEvent>,
  IPerspectiveOf<InventoryReservedEvent>,
  IPerspectiveOf<InventoryReleasedEvent>,
  IPerspectiveOf<InventoryAdjustedEvent> {

  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ILogger<InventoryLevelsPerspective> _logger;
  private readonly IHubContext<ProductInventoryHub> _hubContext;

  public InventoryLevelsPerspective(
    IDbConnectionFactory connectionFactory,
    ILogger<InventoryLevelsPerspective> logger,
    IHubContext<ProductInventoryHub> hubContext) {
    _connectionFactory = connectionFactory;
    _logger = logger;
    _hubContext = hubContext;
  }

  /// <summary>
  /// Handles InventoryRestockedEvent by upserting inventory levels.
  /// Creates new record if product doesn't exist, updates if it does.
  /// Sends real-time SignalR notification after successful database update.
  /// </summary>
  public async Task Update(InventoryRestockedEvent @event, CancellationToken cancellationToken = default) {
    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        INSERT INTO bff.inventory_levels (
          product_id, quantity, reserved, available, last_updated
        ) VALUES (
          @ProductId, @NewTotalQuantity, 0, @NewTotalQuantity, @RestockedAt
        )
        ON CONFLICT (product_id) DO UPDATE
        SET
          quantity = @NewTotalQuantity,
          available = @NewTotalQuantity - bff.inventory_levels.reserved,
          last_updated = @RestockedAt",
        new {
          @event.ProductId,
          @event.NewTotalQuantity,
          @event.RestockedAt
        });

      _logger.LogInformation(
        "BFF inventory levels updated: Product {ProductId} restocked to {Quantity}",
        @event.ProductId,
        @event.NewTotalQuantity);

      // Query current inventory state and send notification
      await SendInventoryNotificationAfterUpdateAsync(
        connection,
        @event.ProductId.ToString(),
        "Restocked",
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
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        UPDATE bff.inventory_levels
        SET
          reserved = reserved + @Quantity,
          available = quantity - (reserved + @Quantity),
          last_updated = @ReservedAt
        WHERE product_id = @ProductId",
        new {
          @event.ProductId,
          @event.Quantity,
          @event.ReservedAt
        });

      _logger.LogInformation(
        "BFF inventory levels updated: Product {ProductId} reserved {Quantity} units",
        @event.ProductId,
        @event.Quantity);

      // Query current inventory state and send notification
      await SendInventoryNotificationAfterUpdateAsync(
        connection,
        @event.ProductId.ToString(),
        "Reserved",
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
  /// Sends real-time SignalR notification after successful database update.
  /// </summary>
  public async Task Update(InventoryReleasedEvent @event, CancellationToken cancellationToken = default) {
    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        UPDATE bff.inventory_levels
        SET
          reserved = reserved - @Quantity,
          available = quantity - (reserved - @Quantity),
          last_updated = @ReleasedAt
        WHERE product_id = @ProductId",
        new {
          @event.ProductId,
          @event.Quantity,
          @event.ReleasedAt
        });

      _logger.LogInformation(
        "BFF inventory levels updated: Product {ProductId} released {Quantity} units",
        @event.ProductId,
        @event.Quantity);

      // Query current inventory state and send notification (NOT sending notification for Released events)
      // Released events are internal operations, not customer-facing
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
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        UPDATE bff.inventory_levels
        SET
          quantity = @NewTotalQuantity,
          available = @NewTotalQuantity - reserved,
          last_updated = @AdjustedAt
        WHERE product_id = @ProductId",
        new {
          @event.ProductId,
          @event.NewTotalQuantity,
          @event.AdjustedAt
        });

      _logger.LogInformation(
        "BFF inventory levels updated: Product {ProductId} adjusted to {Quantity} (Reason: {Reason})",
        @event.ProductId,
        @event.NewTotalQuantity,
        @event.Reason);

      // Query current inventory state and send notification
      await SendInventoryNotificationAfterUpdateAsync(
        connection,
        @event.ProductId.ToString(),
        "Adjusted",
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
  /// Queries current inventory state and sends SignalR notification
  /// </summary>
  private async Task SendInventoryNotificationAfterUpdateAsync(
    IDbConnection connection,
    string productId,
    string notificationType,
    string? reason,
    CancellationToken cancellationToken) {
    try {
      // Query current inventory state
      var inventory = await connection.QuerySingleOrDefaultAsync<InventoryRecord>(@"
        SELECT product_id, quantity, reserved, available
        FROM bff.inventory_levels
        WHERE product_id = @ProductId",
        new { ProductId = productId });

      if (inventory is null) {
        _logger.LogWarning(
          "Cannot send inventory notification - product {ProductId} not found",
          productId);
        return;
      }

      var notification = new InventoryNotification {
        ProductId = productId,
        NotificationType = notificationType,
        Quantity = inventory.quantity,
        Reserved = inventory.reserved,
        Available = inventory.available,
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

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }

  /// <summary>
  /// Record for querying inventory details from database
  /// </summary>
  private record InventoryRecord(
    Guid product_id,
    int quantity,
    int reserved,
    int available
  );
}
