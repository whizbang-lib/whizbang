using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints.Admin;

/// <summary>
/// Get a specific order by ID (admin view)
/// </summary>
public class GetOrderByIdEndpoint(IOrderLens orderLens) : EndpointWithoutRequest<OrderReadModel> {
  private readonly IOrderLens _orderLens = orderLens;

  public override void Configure() {
    Get("/admin/orders/{orderId}");
    AllowAnonymous(); // TODO: Add authentication and authorization
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var orderId = Route<string>("orderId")!;
    var tenantId = Query<string>("tenantId");

    // TODO: Get tenantId from SecurityContext/JWT claims
    // TODO: Verify user is admin for this tenant
    if (string.IsNullOrEmpty(tenantId)) {
      ThrowError("TenantId is required");
    }

    var order = await _orderLens.GetByIdAsync(orderId, ct);

    if (order == null) {
      HttpContext.Response.StatusCode = 404;
      return;
    }

    // Verify order belongs to this tenant
    if (order.TenantId != tenantId) {
      HttpContext.Response.StatusCode = 403;
      return;
    }

    Response = order;
  }
}
