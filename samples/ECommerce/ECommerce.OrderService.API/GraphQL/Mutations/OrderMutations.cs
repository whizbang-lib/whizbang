using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
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

    var orderId = OrderId.New();
    var items = lineItems.Select(li => new OrderLineItem {
      ProductId = ProductId.From(Guid.Parse(li.ProductId)),
      ProductName = li.ProductName,
      Quantity = li.Quantity,
      UnitPrice = li.UnitPrice
    }).ToList();

    var totalAmount = items.Sum(i => i.Quantity * i.UnitPrice);

    var command = new CreateOrderCommand {
      OrderId = orderId,
      CustomerId = CustomerId.From(Guid.Parse(customerId)),
      LineItems = items,
      TotalAmount = totalAmount
    };

    // Dispatch the command locally and wait for the result
    var orderCreated = await dispatcher.LocalInvokeAsync<OrderCreatedEvent>(command);

    return orderCreated.OrderId.Value.ToString();
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
