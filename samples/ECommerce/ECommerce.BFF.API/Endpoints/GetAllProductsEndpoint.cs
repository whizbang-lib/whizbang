using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Get all non-deleted products
/// </summary>
public class GetAllProductsEndpoint(IProductCatalogLens lens) : EndpointWithoutRequest<List<ProductDto>> {
  private readonly IProductCatalogLens _lens = lens;

  public override void Configure() {
    Get("/products");
    AllowAnonymous();
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var products = await _lens.GetAllAsync(includeDeleted: false, ct);
    Response = [.. products];
  }
}
