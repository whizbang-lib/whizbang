using System.Data;
using Dapper;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Data;

namespace ECommerce.InventoryWorker.Perspectives;

/// <summary>
/// Materializes inventory events into inventory_levels table.
/// Handles ProductCreatedEvent (initializes at 0), InventoryRestockedEvent, InventoryReservedEvent, and InventoryAdjustedEvent.
/// </summary>
public class InventoryLevelsPerspective(
  IDbConnectionFactory connectionFactory,
  ILogger<InventoryLevelsPerspective> logger) :
  IPerspectiveOf<ProductCreatedEvent>,
  IPerspectiveOf<InventoryRestockedEvent>,
  IPerspectiveOf<InventoryReservedEvent>,
  IPerspectiveOf<InventoryAdjustedEvent> {

  private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
  private readonly ILogger<InventoryLevelsPerspective> _logger = logger;

  /// <summary>
  /// Handles ProductCreatedEvent by initializing inventory at 0 quantity.
  /// Creates initial inventory record for new products.
  /// </summary>
  public async Task Update(ProductCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        INSERT INTO inventoryworker.inventory_levels (
          product_id, quantity, reserved, last_updated
        ) VALUES (
          @ProductId, 0, 0, @CreatedAt
        )
        ON CONFLICT (product_id) DO NOTHING",
        new {
          @event.ProductId,
          @event.CreatedAt
        });

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
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        INSERT INTO inventoryworker.inventory_levels (
          product_id, quantity, reserved, last_updated
        ) VALUES (
          @ProductId, @NewTotalQuantity, 0, @RestockedAt
        )
        ON CONFLICT (product_id) DO UPDATE
        SET
          quantity = @NewTotalQuantity,
          last_updated = @RestockedAt",
        new {
          @event.ProductId,
          @event.NewTotalQuantity,
          @event.RestockedAt
        });

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
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        UPDATE inventoryworker.inventory_levels
        SET
          reserved = reserved + @Quantity,
          last_updated = @ReservedAt
        WHERE product_id = @ProductId",
        new {
          @event.ProductId,
          @event.Quantity,
          @event.ReservedAt
        });

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
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        UPDATE inventoryworker.inventory_levels
        SET
          quantity = @NewTotalQuantity,
          last_updated = @AdjustedAt
        WHERE product_id = @ProductId",
        new {
          @event.ProductId,
          @event.NewTotalQuantity,
          @event.AdjustedAt
        });

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

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
