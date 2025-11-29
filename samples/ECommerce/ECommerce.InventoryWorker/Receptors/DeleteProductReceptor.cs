using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace ECommerce.InventoryWorker.Receptors;

/// <summary>
/// Handles DeleteProductCommand and publishes ProductDeletedEvent
/// </summary>
public class DeleteProductReceptor(IDispatcher dispatcher, ILogger<DeleteProductReceptor> logger) : IReceptor<DeleteProductCommand, ProductDeletedEvent> {
  private readonly IDispatcher _dispatcher = dispatcher;
  private readonly ILogger<DeleteProductReceptor> _logger = logger;

  public async ValueTask<ProductDeletedEvent> HandleAsync(
    DeleteProductCommand message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation(
      "Deleting product {ProductId}",
      message.ProductId);

    // Create ProductDeletedEvent
    var productDeleted = new ProductDeletedEvent {
      ProductId = message.ProductId,
      DeletedAt = DateTime.UtcNow
    };

    // Publish the event
    await _dispatcher.PublishAsync(productDeleted);

    _logger.LogInformation(
      "Product {ProductId} deleted successfully",
      message.ProductId);

    return productDeleted;
  }
}
