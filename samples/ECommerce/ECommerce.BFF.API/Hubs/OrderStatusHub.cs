using Microsoft.AspNetCore.SignalR;

namespace ECommerce.BFF.API.Hubs;

/// <summary>
/// SignalR hub for real-time order status updates.
/// Clients connect and receive notifications when order status changes.
/// AOT-compatible hub (untyped - strongly-typed hubs not supported with Native AOT).
/// </summary>
public class OrderStatusHub : Hub {
  private readonly ILogger<OrderStatusHub> _logger;

  public OrderStatusHub(ILogger<OrderStatusHub> logger) {
    _logger = logger;
  }

  /// <summary>
  /// Called when a client connects to the hub
  /// </summary>
  public override async Task OnConnectedAsync() {
    var connectionId = Context.ConnectionId;
    var userId = Context.User?.Identity?.Name ?? "Anonymous";

    _logger.LogInformation(
      "Client connected to OrderStatusHub: ConnectionId={ConnectionId}, UserId={UserId}",
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
        "Client disconnected from OrderStatusHub with error: ConnectionId={ConnectionId}",
        connectionId
      );
    } else {
      _logger.LogInformation(
        "Client disconnected from OrderStatusHub: ConnectionId={ConnectionId}",
        connectionId
      );
    }

    await base.OnDisconnectedAsync(exception);
  }

  /// <summary>
  /// Client subscribes to updates for a specific order
  /// </summary>
  public async Task SubscribeToOrder(string orderId) {
    await Groups.AddToGroupAsync(Context.ConnectionId, $"order-{orderId}");

    _logger.LogInformation(
      "Client subscribed to order updates: ConnectionId={ConnectionId}, OrderId={OrderId}",
      Context.ConnectionId,
      orderId
    );
  }

  /// <summary>
  /// Client unsubscribes from updates for a specific order
  /// </summary>
  public async Task UnsubscribeFromOrder(string orderId) {
    await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"order-{orderId}");

    _logger.LogInformation(
      "Client unsubscribed from order updates: ConnectionId={ConnectionId}, OrderId={OrderId}",
      Context.ConnectionId,
      orderId
    );
  }
}

/// <summary>
/// Typed client interface for OrderStatusHub
/// Defines methods that can be called on clients from server
/// </summary>
public interface IOrderStatusClient {
  /// <summary>
  /// Notifies client that order status has changed
  /// </summary>
  Task OrderStatusChanged(OrderStatusUpdate update);

  /// <summary>
  /// Notifies client that order was created
  /// </summary>
  Task OrderCreated(OrderCreatedNotification notification);

  /// <summary>
  /// Notifies client of a general order update
  /// </summary>
  Task OrderUpdated(OrderUpdateNotification notification);
}

/// <summary>
/// Order status update notification
/// </summary>
public record OrderStatusUpdate {
  public required string OrderId { get; init; }
  public required string Status { get; init; }
  public DateTime Timestamp { get; init; } = DateTime.UtcNow;
  public string? Message { get; init; }
  public Dictionary<string, object>? Details { get; init; }
}

/// <summary>
/// Order created notification
/// </summary>
public record OrderCreatedNotification {
  public required string OrderId { get; init; }
  public required string CustomerId { get; init; }
  public decimal TotalAmount { get; init; }
  public DateTime CreatedAt { get; init; }
}

/// <summary>
/// General order update notification
/// </summary>
public record OrderUpdateNotification {
  public required string OrderId { get; init; }
  public required string UpdateType { get; init; }
  public DateTime Timestamp { get; init; } = DateTime.UtcNow;
  public Dictionary<string, object>? Data { get; init; }
}
