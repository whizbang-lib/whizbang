using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to create a new order
/// </summary>
public record CreateOrderCommand : ICommand {
  [AggregateId]
  public required OrderId OrderId { get; init; }
  public required CustomerId CustomerId { get; init; }
  public required List<OrderLineItem> LineItems { get; init; }
  public decimal TotalAmount { get; init; }
}

public record OrderLineItem {
  public required ProductId ProductId { get; init; }
  public required string ProductName { get; init; }
  public int Quantity { get; init; }
  public decimal UnitPrice { get; init; }
}
