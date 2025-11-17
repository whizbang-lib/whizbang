using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Create a new order (fire-and-forget)
/// </summary>
public class CreateOrderEndpoint : Endpoint<CreateOrderRequest, CreateOrderResponse> {
  private readonly ILogger<CreateOrderEndpoint> _logger;
  // TODO: Uncomment after implementing dispatcher
  // private readonly IDispatcher _dispatcher;

  public CreateOrderEndpoint(ILogger<CreateOrderEndpoint> logger) {
    _logger = logger;
    // _dispatcher = dispatcher;
  }

  public override void Configure() {
    Post("/orders");
    AllowAnonymous(); // TODO: Add authentication
  }

  public override async Task HandleAsync(CreateOrderRequest req, CancellationToken ct) {
    // TODO: Uncomment after implementing dispatcher
    // var command = new CreateOrderCommand {
    //   CustomerId = req.CustomerId, // TODO: Get from SecurityContext
    //   LineItems = req.LineItems
    // };

    // // Fire-and-forget: Returns correlation ID immediately
    // var receipt = await _dispatcher.SendAsync(command);

    // _logger.LogInformation(
    //   "Order creation command dispatched with CorrelationId={CorrelationId}",
    //   receipt.CorrelationId
    // );

    // Response = new CreateOrderResponse {
    //   CorrelationId = receipt.CorrelationId.Value,
    //   Message = "Order is being processed. You will receive real-time updates via SignalR."
    // };
    // HttpContext.Response.StatusCode = 202;

    _logger.LogWarning("CreateOrder endpoint not yet implemented - dispatcher integration pending");
    Response = new CreateOrderResponse {
      CorrelationId = "",
      Message = "Order creation not yet implemented - dispatcher integration pending"
    };
    HttpContext.Response.StatusCode = 501;

    await Task.CompletedTask;
  }
}

/// <summary>
/// Request to create a new order
/// </summary>
public record CreateOrderRequest {
  public required string CustomerId { get; init; }
  public required List<CreateOrderLineItem> LineItems { get; init; }
}

/// <summary>
/// Line item for order creation
/// </summary>
public record CreateOrderLineItem {
  public required string ProductId { get; init; }
  public required string ProductName { get; init; }
  public int Quantity { get; init; }
  public decimal Price { get; init; }
}

/// <summary>
/// Response from fire-and-forget order creation
/// </summary>
public record CreateOrderResponse {
  public required string CorrelationId { get; init; }
  public required string Message { get; init; }
}
