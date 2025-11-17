using ECommerce.BFF.API.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints.Admin;

/// <summary>
/// Get orders by status for the current tenant (admin view)
/// </summary>
public class GetOrdersByStatusEndpoint : EndpointWithoutRequest<IEnumerable<OrderReadModel>> {
  private readonly IOrderLens _orderLens;

  public GetOrdersByStatusEndpoint(IOrderLens orderLens) {
    _orderLens = orderLens;
  }

  public override void Configure() {
    Get("/admin/orders/status/{status}");
    AllowAnonymous(); // TODO: Add authentication and authorization
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var status = Route<string>("status")!;
    var tenantId = Query<string>("tenantId");

    // TODO: Get tenantId from SecurityContext/JWT claims
    // TODO: Verify user is admin for this tenant
    if (string.IsNullOrEmpty(tenantId)) {
      ThrowError("TenantId is required");
    }

    var orders = await _orderLens.GetByStatusAsync(tenantId!, status);
    Response = orders;
  }
}
