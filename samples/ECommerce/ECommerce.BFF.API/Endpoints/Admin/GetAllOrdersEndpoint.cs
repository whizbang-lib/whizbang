using ECommerce.BFF.API.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints.Admin;

/// <summary>
/// Get all orders for the current tenant (admin view)
/// </summary>
public class GetAllOrdersEndpoint : EndpointWithoutRequest<IEnumerable<OrderReadModel>> {
  private readonly IOrderLens _orderLens;

  public GetAllOrdersEndpoint(IOrderLens orderLens) {
    _orderLens = orderLens;
  }

  public override void Configure() {
    Get("/admin/orders");
    AllowAnonymous(); // TODO: Add authentication and authorization
  }

  public override async Task HandleAsync(CancellationToken ct) {
    // TODO: Get tenantId from SecurityContext/JWT claims instead of query param
    // TODO: Verify user is admin for this tenant
    var tenantId = Query<string>("tenantId");

    if (string.IsNullOrEmpty(tenantId)) {
      ThrowError("TenantId is required");
    }

    var orders = await _orderLens.GetByTenantIdAsync(tenantId!);
    Response = orders;
  }
}
