using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to create a new order
/// </summary>
[PinnedId("ec60c90b-f68f-48d1-8720-d879529894b7")]
public record CreateOrderCommand : ICommand {
  [StreamId]
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
