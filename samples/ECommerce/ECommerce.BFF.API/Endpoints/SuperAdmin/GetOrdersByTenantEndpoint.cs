using ECommerce.BFF.API.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints.SuperAdmin;

/// <summary>
/// Get all orders for a specific tenant (super-admin view)
/// </summary>
public class GetOrdersByTenantEndpoint(IOrderLens orderLens) : EndpointWithoutRequest<IEnumerable<OrderReadModel>> {
  private readonly IOrderLens _orderLens = orderLens;

  public override void Configure() {
    Get("/superadmin/orders/tenant/{tenantId}");
    AllowAnonymous(); // TODO: Add authentication and super-admin authorization
  }

  public override async Task HandleAsync(CancellationToken ct) {
    // TODO: Verify user is super-admin
    var tenantId = Route<string>("tenantId")!;
    var orders = await _orderLens.GetByTenantIdAsync(tenantId, ct);
    Response = orders;
  }
}
