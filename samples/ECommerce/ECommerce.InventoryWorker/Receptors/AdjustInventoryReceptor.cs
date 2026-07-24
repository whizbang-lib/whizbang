using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace ECommerce.InventoryWorker.Receptors;

/// <summary>
/// Handles AdjustInventoryCommand and publishes InventoryAdjustedEvent
/// </summary>
public class AdjustInventoryReceptor(IDispatcher dispatcher, ILogger<AdjustInventoryReceptor> logger) : IReceptor<AdjustInventoryCommand, InventoryAdjustedEvent> {

  public async ValueTask<InventoryAdjustedEvent> HandleAsync(
    AdjustInventoryCommand message,
    CancellationToken cancellationToken = default) {

    logger.LogInformation(
      "Adjusting inventory for product {ProductId} by {QuantityChange} (Reason: {Reason})",
      message.ProductId,
      message.QuantityChange,
      message.Reason);

    // Create InventoryAdjustedEvent
    // Note: For now, NewTotalQuantity equals QuantityChange since we don't have state tracking yet
    var inventoryAdjusted = new InventoryAdjustedEvent {
      ProductId = message.ProductId,
      QuantityChange = message.QuantityChange,
      NewTotalQuantity = message.QuantityChange,
      Reason = message.Reason,
      AdjustedAt = DateTime.UtcNow
    };

    // Publish the event
    await dispatcher.PublishAsync(inventoryAdjusted);

    logger.LogInformation(
      "Inventory adjusted for product {ProductId}",
      message.ProductId);

    return inventoryAdjusted;
  }
}
