using ECommerce.BFF.API.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Get all inventory levels
/// </summary>
public class GetAllInventoryEndpoint : EndpointWithoutRequest<List<InventoryLevelDto>> {
  private readonly IInventoryLevelsLens _lens;

  public GetAllInventoryEndpoint(IInventoryLevelsLens lens) {
    _lens = lens;
  }

  public override void Configure() {
    Get("/inventory");
    AllowAnonymous();
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var inventory = await _lens.GetAllAsync(ct);
    Response = inventory.ToList();
  }
}
