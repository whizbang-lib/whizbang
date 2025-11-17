using ECommerce.BFF.API.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints.SuperAdmin;

/// <summary>
/// Get a specific order by ID (super-admin view, cross-tenant)
/// </summary>
public class GetOrderByIdEndpoint : EndpointWithoutRequest<OrderReadModel> {
  private readonly IOrderLens _orderLens;

  public GetOrderByIdEndpoint(IOrderLens orderLens) {
    _orderLens = orderLens;
  }

  public override void Configure() {
    Get("/superadmin/orders/{orderId}");
    AllowAnonymous(); // TODO: Add authentication and super-admin authorization
  }

  public override async Task HandleAsync(CancellationToken ct) {
    // TODO: Verify user is super-admin
    var orderId = Route<string>("orderId")!;
    var order = await _orderLens.GetByIdAsync(orderId);

    if (order == null) {
      HttpContext.Response.StatusCode = 404;
      return;
    }

    Response = order;
  }
}
