using Microsoft.AspNetCore.SignalR;

namespace ECommerce.BFF.API.Hubs;

/// <summary>
/// SignalR hub for real-time product catalog and inventory updates.
/// Clients connect and receive notifications when products or inventory levels change.
/// AOT-compatible hub (untyped - strongly-typed hubs not supported with Native AOT).
/// </summary>
public class ProductInventoryHub(ILogger<ProductInventoryHub> logger) : Hub {
  private readonly ILogger<ProductInventoryHub> _logger = logger;

  /// <summary>
  /// Called when a client connects to the hub
  /// </summary>
  public override async Task OnConnectedAsync() {
    var connectionId = Context.ConnectionId;
    var userId = Context.User?.Identity?.Name ?? "Anonymous";

    _logger.LogInformation(
      "Client connected to ProductInventoryHub: ConnectionId={ConnectionId}, UserId={UserId}",
      connectionId,
      userId
    );

    await base.OnConnectedAsync();
  }

  /// <summary>
  /// Called when a client disconnects from the hub
  /// </summary>
  public override async Task OnDisconnectedAsync(Exception? exception) {
    var connectionId = Context.ConnectionId;

    if (exception != null) {
      _logger.LogError(
        exception,
        "Client disconnected from ProductInventoryHub with error: ConnectionId={ConnectionId}",
        connectionId
      );
    } else {
      _logger.LogInformation(
        "Client disconnected from ProductInventoryHub: ConnectionId={ConnectionId}",
        connectionId
      );
    }

    await base.OnDisconnectedAsync(exception);
  }

  /// <summary>
  /// Client subscribes to updates for a specific product
  /// </summary>
  public async Task SubscribeToProductAsync(string productId) {
    await Groups.AddToGroupAsync(Context.ConnectionId, $"product-{productId}");

    _logger.LogInformation(
      "Client subscribed to product updates: ConnectionId={ConnectionId}, ProductId={ProductId}",
      Context.ConnectionId,
      productId
    );
  }

  /// <summary>
  /// Client unsubscribes from updates for a specific product
  /// </summary>
  public async Task UnsubscribeFromProductAsync(string productId) {
    await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"product-{productId}");

    _logger.LogInformation(
      "Client unsubscribed from product updates: ConnectionId={ConnectionId}, ProductId={ProductId}",
      Context.ConnectionId,
      productId
    );
  }

  /// <summary>
  /// Client subscribes to updates for all products
  /// </summary>
  public async Task SubscribeToAllProductsAsync() {
    await Groups.AddToGroupAsync(Context.ConnectionId, "all-products");

    _logger.LogInformation(
      "Client subscribed to all product updates: ConnectionId={ConnectionId}",
      Context.ConnectionId
    );
  }

  /// <summary>
  /// Client unsubscribes from updates for all products
  /// </summary>
  public async Task UnsubscribeFromAllProductsAsync() {
    await Groups.RemoveFromGroupAsync(Context.ConnectionId, "all-products");

    _logger.LogInformation(
      "Client unsubscribed from all product updates: ConnectionId={ConnectionId}",
      Context.ConnectionId
    );
  }
}
