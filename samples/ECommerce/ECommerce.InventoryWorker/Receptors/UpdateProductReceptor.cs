using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace ECommerce.InventoryWorker.Receptors;

/// <summary>
/// Handles UpdateProductCommand and publishes ProductUpdatedEvent
/// </summary>
public class UpdateProductReceptor(IDispatcher dispatcher, ILogger<UpdateProductReceptor> logger) : IReceptor<UpdateProductCommand, ProductUpdatedEvent> {
  public async ValueTask<ProductUpdatedEvent> HandleAsync(
    UpdateProductCommand message,
    CancellationToken cancellationToken = default) {

    logger.LogInformation(
      "Updating product {ProductId}",
      message.ProductId);

    // Create ProductUpdatedEvent with nullable properties from command
    var productUpdated = new ProductUpdatedEvent {
      ProductId = message.ProductId,
      Name = message.Name,
      Description = message.Description,
      Price = message.Price,
      ImageUrl = message.ImageUrl,
      UpdatedAt = DateTime.UtcNow
    };

    // Publish the event
    await dispatcher.PublishAsync(productUpdated);

    logger.LogInformation(
      "Product {ProductId} updated successfully",
      message.ProductId);

    return productUpdated;
  }
}
