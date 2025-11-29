using ECommerce.BFF.API.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Get all orders for the current customer
/// </summary>
public class GetMyOrdersEndpoint(IOrderLens orderLens) : EndpointWithoutRequest<IEnumerable<OrderReadModel>> {
  private readonly IOrderLens _orderLens = orderLens;

  public override void Configure() {
    Get("/orders/my");
    AllowAnonymous(); // TODO: Add authentication
  }

  public override async Task HandleAsync(CancellationToken ct) {
    // TODO: Get customerId from SecurityContext/JWT claims instead of query param
    var customerId = Query<string>("customerId");

    if (string.IsNullOrEmpty(customerId)) {
      ThrowError("CustomerId is required");
    }

    var orders = await _orderLens.GetByCustomerIdAsync(customerId!, cancellationToken: ct);
    Response = orders;
  }
}
