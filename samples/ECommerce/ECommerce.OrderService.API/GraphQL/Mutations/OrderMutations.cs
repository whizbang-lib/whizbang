using ECommerce.Contracts.Commands;
using Whizbang.Core;

namespace ECommerce.OrderService.API.GraphQL.Mutations;

/// <summary>
/// GraphQL mutations for order operations
/// </summary>
public class OrderMutations {
  /// <summary>
  /// Creates a new order
  /// </summary>
  /// <param name="customerId">The customer ID</param>
  /// <param name="lineItems">The order line items</param>
  /// <param name="dispatcher">The Whizbang dispatcher</param>
  /// <returns>The created order ID</returns>
  public async Task<string> CreateOrderAsync(
    string customerId,
    List<OrderLineItemInput> lineItems,
    [Service] IDispatcher dispatcher) {

    var orderId = Guid.NewGuid().ToString();
    var items = lineItems.Select(li => new OrderLineItem {
      ProductId = li.ProductId,
      ProductName = li.ProductName,
      Quantity = li.Quantity,
      UnitPrice = li.UnitPrice
    }).ToList();

    var totalAmount = items.Sum(i => i.Quantity * i.UnitPrice);

    var command = new CreateOrderCommand {
      OrderId = orderId,
      CustomerId = customerId,
      LineItems = items,
      TotalAmount = totalAmount
    };

    // Dispatch the command - for now we'll just dispatch and return the ID
    // In a real system, you'd want to wait for confirmation or handle errors
    await dispatcher.PublishAsync(command);

    return orderId;
  }
}

/// <summary>
/// Input type for order line items in GraphQL
/// </summary>
public record OrderLineItemInput {
  public required string ProductId { get; init; }
  public required string ProductName { get; init; }
  public int Quantity { get; init; }
  public decimal UnitPrice { get; init; }
}
