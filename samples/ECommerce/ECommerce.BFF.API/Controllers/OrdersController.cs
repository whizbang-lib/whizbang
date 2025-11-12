using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Commands;
using Microsoft.AspNetCore.Mvc;
using Whizbang.Core;

namespace ECommerce.BFF.API.Controllers;

/// <summary>
/// Customer-facing order API endpoints.
/// Fire-and-forget pattern: Commands return correlation IDs immediately,
/// real-time updates come via SignalR.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase {
  private readonly IOrderLens _orderLens;
  // TODO: Uncomment after implementing dispatcher
  // private readonly IDispatcher _dispatcher;
  private readonly ILogger<OrdersController> _logger;

  public OrdersController(
    IOrderLens orderLens,
    // IDispatcher dispatcher,
    ILogger<OrdersController> logger
  ) {
    _orderLens = orderLens;
    // _dispatcher = dispatcher;
    _logger = logger;
  }

  /// <summary>
  /// Get all orders for the current customer
  /// </summary>
  [HttpGet("my")]
  public async Task<ActionResult<IEnumerable<OrderReadModel>>> GetMyOrders([FromQuery] string customerId) {
    // TODO: Get customerId from SecurityContext/JWT claims instead of query param
    if (string.IsNullOrEmpty(customerId)) {
      return BadRequest("CustomerId is required");
    }

    var orders = await _orderLens.GetByCustomerIdAsync(customerId);
    return Ok(orders);
  }

  /// <summary>
  /// Get a specific order by ID
  /// </summary>
  [HttpGet("{orderId}")]
  public async Task<ActionResult<OrderReadModel>> GetById(string orderId) {
    var order = await _orderLens.GetByIdAsync(orderId);

    if (order == null) {
      return NotFound();
    }

    // TODO: Check if user has permission to view this order
    return Ok(order);
  }

  /// <summary>
  /// Get order status history (tracking timeline)
  /// </summary>
  [HttpGet("{orderId}/history")]
  public async Task<ActionResult<IEnumerable<OrderStatusHistory>>> GetStatusHistory(string orderId) {
    var history = await _orderLens.GetStatusHistoryAsync(orderId);

    // TODO: Check if user has permission to view this order
    return Ok(history);
  }

  /// <summary>
  /// Create a new order (fire-and-forget)
  /// </summary>
  [HttpPost]
  public async Task<ActionResult<CreateOrderResponse>> CreateOrder([FromBody] CreateOrderRequest request) {
    // TODO: Uncomment after implementing dispatcher
    // var command = new CreateOrderCommand {
    //   CustomerId = request.CustomerId, // TODO: Get from SecurityContext
    //   LineItems = request.LineItems
    // };

    // // Fire-and-forget: Returns correlation ID immediately
    // var receipt = await _dispatcher.SendAsync(command);

    // _logger.LogInformation(
    //   "Order creation command dispatched with CorrelationId={CorrelationId}",
    //   receipt.CorrelationId
    // );

    // return Accepted(new CreateOrderResponse {
    //   CorrelationId = receipt.CorrelationId.Value,
    //   Message = "Order is being processed. You will receive real-time updates via SignalR."
    // });

    _logger.LogWarning("CreateOrder endpoint not yet implemented - dispatcher integration pending");
    return StatusCode(501, "Order creation not yet implemented - dispatcher integration pending");
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
