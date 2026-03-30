using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Get a specific order by ID
/// </summary>
public class GetOrderByIdEndpoint(IOrderLens orderLens) : EndpointWithoutRequest<OrderReadModel> {

  public override void Configure() {
    Get("/orders/{orderId}");
    AllowAnonymous(); // TODO: Add authentication
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var orderId = Route<string>("orderId")!;
    var order = await orderLens.GetByIdAsync(orderId, ct);

    if (order == null) {
      HttpContext.Response.StatusCode = 404;
      return;
    }

    // TODO: Check if user has permission to view this order
    Response = order;
  }
}
