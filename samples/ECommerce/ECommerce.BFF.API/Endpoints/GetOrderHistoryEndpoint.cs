using ECommerce.BFF.API.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Get order status history (tracking timeline)
/// </summary>
public class GetOrderHistoryEndpoint(IOrderLens orderLens) : EndpointWithoutRequest<IEnumerable<OrderStatusHistory>> {
  private readonly IOrderLens _orderLens = orderLens;

  public override void Configure() {
    Get("/orders/{orderId}/history");
    AllowAnonymous(); // TODO: Add authentication
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var orderId = Route<string>("orderId")!;
    var history = await _orderLens.GetStatusHistoryAsync(orderId, ct);

    // TODO: Check if user has permission to view this order
    Response = history;
  }
}
