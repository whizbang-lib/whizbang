using ECommerce.BFF.API.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Get all non-deleted products
/// </summary>
public class GetAllProductsEndpoint : EndpointWithoutRequest<List<ProductDto>> {
  private readonly IProductCatalogLens _lens;

  public GetAllProductsEndpoint(IProductCatalogLens lens) {
    _lens = lens;
  }

  public override void Configure() {
    Get("/products");
    AllowAnonymous();
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var products = await _lens.GetAllAsync(includeDeleted: false, ct);
    Response = products.ToList();
  }
}
