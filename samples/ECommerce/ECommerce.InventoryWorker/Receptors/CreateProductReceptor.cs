using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace ECommerce.InventoryWorker.Receptors;

/// <summary>
/// Handles CreateProductCommand and publishes ProductCreatedEvent (and optionally InventoryRestockedEvent)
/// </summary>
public class CreateProductReceptor(IDispatcher dispatcher, ILogger<CreateProductReceptor> logger) : IReceptor<CreateProductCommand, ProductCreatedEvent> {
  private readonly IDispatcher _dispatcher = dispatcher;
  private readonly ILogger<CreateProductReceptor> _logger = logger;

  public async ValueTask<ProductCreatedEvent> HandleAsync(
    CreateProductCommand message,
    CancellationToken cancellationToken = default) {

    _logger.LogInformation(
      "Creating product {ProductId} with name {Name} and price {Price}",
      message.ProductId,
      message.Name,
      message.Price);

    // Create ProductCreatedEvent
    var productCreated = new ProductCreatedEvent {
      ProductId = message.ProductId,
      Name = message.Name,
      Description = message.Description,
      Price = message.Price,
      ImageUrl = message.ImageUrl,
      CreatedAt = DateTime.UtcNow
    };

    // Publish ProductCreatedEvent
    await _dispatcher.PublishAsync(productCreated);

    // If there's initial stock, also publish InventoryRestockedEvent
    if (message.InitialStock > 0) {
      var inventoryRestocked = new InventoryRestockedEvent {
        ProductId = message.ProductId,
        QuantityAdded = message.InitialStock,
        NewTotalQuantity = message.InitialStock,
        RestockedAt = DateTime.UtcNow
      };

      await _dispatcher.PublishAsync(inventoryRestocked);

      _logger.LogInformation(
        "Product {ProductId} created with initial stock of {InitialStock}",
        message.ProductId,
        message.InitialStock);
    } else {
      _logger.LogInformation(
        "Product {ProductId} created without initial stock",
        message.ProductId);
    }

    return productCreated;
  }
}
