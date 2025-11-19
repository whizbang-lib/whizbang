using ECommerce.BFF.API.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Get inventory levels for a specific product
/// </summary>
public class GetInventoryByProductIdEndpoint : EndpointWithoutRequest<InventoryLevelDto> {
  private readonly IInventoryLevelsLens _lens;

  public GetInventoryByProductIdEndpoint(IInventoryLevelsLens lens) {
    _lens = lens;
  }

  public override void Configure() {
    Get("/inventory/{productId}");
    AllowAnonymous();
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var productId = Route<string>("productId")!;
    var inventory = await _lens.GetByProductIdAsync(productId, ct);

    if (inventory == null) {
      HttpContext.Response.StatusCode = 404;
      return;
    }

    Response = inventory;
  }
}
