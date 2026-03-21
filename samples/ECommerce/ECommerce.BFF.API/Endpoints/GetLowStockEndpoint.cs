using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Get products with low stock levels
/// </summary>
public class GetLowStockEndpoint(IInventoryLevelsLens lens) : EndpointWithoutRequest<List<InventoryLevelDto>> {

  public override void Configure() {
    Get("/inventory/low-stock");
    AllowAnonymous();
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var threshold = Query<int?>("threshold") ?? 10;
    var inventory = await lens.GetLowStockAsync(threshold, ct);
    Response = [.. inventory];
  }
}
