using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.OrderService.API.Endpoints.Models;
using FastEndpoints;
using Whizbang.Core;

namespace ECommerce.OrderService.API.Endpoints.Orders;

/// <summary>
/// FastEndpoints endpoint for creating orders
/// </summary>
public class CreateOrderEndpoint : Endpoint<CreateOrderRequest, CreateOrderResponse> {
  private readonly IDispatcher _dispatcher;

  public CreateOrderEndpoint(IDispatcher dispatcher) {
    _dispatcher = dispatcher;
  }

  public override void Configure() {
    Post("/orders");
    AllowAnonymous();
    Summary(s => {
      s.Summary = "Create a new order";
      s.Description = "Creates a new order and dispatches it via Whizbang";
      s.ExampleRequest = new CreateOrderRequest {
        CustomerId = "customer-123",
        LineItems = new List<OrderLineItemDto> {
          new OrderLineItemDto {
            ProductId = "product-1",
            ProductName = "Sample Product",
            Quantity = 2,
            UnitPrice = 49.99m
          }
        }
      };
    });
  }

  public override async Task HandleAsync(CreateOrderRequest req, CancellationToken ct) {
    var orderId = Guid.NewGuid().ToString();
    var items = req.LineItems.Select(li => new OrderLineItem {
      ProductId = li.ProductId,
      ProductName = li.ProductName,
      Quantity = li.Quantity,
      UnitPrice = li.UnitPrice
    }).ToList();

    var totalAmount = items.Sum(i => i.Quantity * i.UnitPrice);

    var command = new CreateOrderCommand {
      OrderId = orderId,
      CustomerId = req.CustomerId,
      LineItems = items,
      TotalAmount = totalAmount
    };

    // Dispatch the command locally and wait for the result
    var orderCreated = await _dispatcher.LocalInvokeAsync<OrderCreatedEvent>(command);

    Response = new CreateOrderResponse {
      OrderId = orderCreated.OrderId,
      Status = "Created",
      TotalAmount = orderCreated.TotalAmount
    };
  }
}
