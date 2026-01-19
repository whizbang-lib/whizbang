namespace ECommerce.OrderService.API.Endpoints.Models;

/// <summary>
/// Response model for creating an order
/// </summary>
public record CreateOrderResponse {
  public required string OrderId { get; init; }
  public required string Status { get; init; }
  public decimal TotalAmount { get; init; }
}
