using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Get inventory levels for a specific product
/// </summary>
public class GetInventoryByProductIdEndpoint(IInventoryLevelsLens lens) : EndpointWithoutRequest<InventoryLevelDto> {
  private readonly IInventoryLevelsLens _lens = lens;

  public override void Configure() {
    Get("/inventory/{productId}");
    AllowAnonymous();
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var productIdString = Route<string>("productId")!;
    if (!Guid.TryParse(productIdString, out var productId)) {
      HttpContext.Response.StatusCode = 400; // Bad Request
      return;
    }

    var inventory = await _lens.GetByProductIdAsync(productId, ct);

    if (inventory == null) {
      HttpContext.Response.StatusCode = 404;
      return;
    }

    Response = inventory;
  }
}
