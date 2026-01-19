using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints.SuperAdmin;

/// <summary>
/// Get recent orders across all tenants (super-admin view)
/// </summary>
public class GetRecentOrdersEndpoint(IOrderLens orderLens) : EndpointWithoutRequest<IEnumerable<OrderReadModel>> {
  private readonly IOrderLens _orderLens = orderLens;

  public override void Configure() {
    Get("/superadmin/orders/recent");
    AllowAnonymous(); // TODO: Add authentication and super-admin authorization
  }

  public override async Task HandleAsync(CancellationToken ct) {
    // TODO: Verify user is super-admin
    var limit = Query<int?>("limit", isRequired: false) ?? 100;

    if (limit < 1 || limit > 1000) {
      ThrowError("Limit must be between 1 and 1000");
    }

    var orders = await _orderLens.GetRecentOrdersAsync(limit, ct);
    Response = orders;
  }
}
