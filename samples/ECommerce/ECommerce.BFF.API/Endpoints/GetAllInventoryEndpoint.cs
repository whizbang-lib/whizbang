using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Get all inventory levels
/// </summary>
public class GetAllInventoryEndpoint(IInventoryLevelsLens lens) : EndpointWithoutRequest<List<InventoryLevelDto>> {

  public override void Configure() {
    Get("/inventory");
    AllowAnonymous();
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var inventory = await lens.GetAllAsync(ct);
    Response = [.. inventory];
  }
}
