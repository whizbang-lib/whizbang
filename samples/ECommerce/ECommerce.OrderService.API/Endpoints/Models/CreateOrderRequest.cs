namespace ECommerce.OrderService.API.Endpoints.Models;

/// <summary>
/// Request model for creating an order
/// </summary>
public record CreateOrderRequest {
  public required string CustomerId { get; init; }
  public required List<OrderLineItemDto> LineItems { get; init; }
}

/// <summary>
/// DTO for order line items
/// </summary>
public record OrderLineItemDto {
  public required string ProductId { get; init; }
  public required string ProductName { get; init; }
  public int Quantity { get; init; }
  public decimal UnitPrice { get; init; }
}
