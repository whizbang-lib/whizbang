using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.OrderService.API.Endpoints.Models;
using FastEndpoints;
using Whizbang.Core;

namespace ECommerce.OrderService.API.Endpoints.Orders;

/// <summary>
/// FastEndpoints endpoint for creating orders
/// </summary>
public class CreateOrderEndpoint(IDispatcher dispatcher) : Endpoint<CreateOrderRequest, CreateOrderResponse> {
  private readonly IDispatcher _dispatcher = dispatcher;

  public override void Configure() {
    Post("/orders");
    AllowAnonymous();
    Summary(s => {
      s.Summary = "Create a new order";
      s.Description = "Creates a new order and dispatches it via Whizbang";
      s.ExampleRequest = new CreateOrderRequest {
        CustomerId = "customer-123",
        LineItems = [
          new OrderLineItemDto {
            ProductId = "product-1",
            ProductName = "Sample Product",
            Quantity = 2,
            UnitPrice = 49.99m
          }
        ]
      };
    });
  }

  public override async Task HandleAsync(CreateOrderRequest req, CancellationToken ct) {
    var orderId = OrderId.New();
    var items = req.LineItems.Select(li => new OrderLineItem {
      ProductId = ProductId.From(Guid.Parse(li.ProductId)),
      ProductName = li.ProductName,
      Quantity = li.Quantity,
      UnitPrice = li.UnitPrice
    }).ToList();

    var totalAmount = items.Sum(i => i.Quantity * i.UnitPrice);

    var command = new CreateOrderCommand {
      OrderId = orderId,
      CustomerId = CustomerId.From(Guid.Parse(req.CustomerId)),
      LineItems = items,
      TotalAmount = totalAmount
    };

    // Dispatch the command locally and wait for the result
    var orderCreated = await _dispatcher.LocalInvokeAsync<OrderCreatedEvent>(command);

    Response = new CreateOrderResponse {
      OrderId = orderCreated.OrderId.Value.ToString(),
      Status = "Created",
      TotalAmount = orderCreated.TotalAmount
    };
  }
}
