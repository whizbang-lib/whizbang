using ECommerce.BFF.API.Lenses;
using FastEndpoints;

namespace ECommerce.BFF.API.Endpoints;

/// <summary>
/// Get a specific product by ID
/// </summary>
public class GetProductByIdEndpoint : EndpointWithoutRequest<ProductDto> {
  private readonly IProductCatalogLens _lens;

  public GetProductByIdEndpoint(IProductCatalogLens lens) {
    _lens = lens;
  }

  public override void Configure() {
    Get("/products/{productId}");
    AllowAnonymous();
  }

  public override async Task HandleAsync(CancellationToken ct) {
    var productId = Route<string>("productId")!;
    var product = await _lens.GetByIdAsync(productId, ct);

    if (product == null) {
      HttpContext.Response.StatusCode = 404;
      return;
    }

    Response = product;
  }
}
