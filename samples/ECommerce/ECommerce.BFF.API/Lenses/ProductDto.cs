namespace ECommerce.BFF.API.Lenses;

/// <summary>
/// Data transfer object for product catalog information.
/// Maps to the bff.product_catalog table materialized by ProductCatalogPerspective.
/// </summary>
public record ProductDto {
  /// <summary>
  /// Unique product identifier
  /// </summary>
  public Guid ProductId { get; init; }

  /// <summary>
  /// Product name
  /// </summary>
  public string Name { get; init; } = string.Empty;

  /// <summary>
  /// Product description (optional)
  /// </summary>
  public string? Description { get; init; }

  /// <summary>
  /// Product price
  /// </summary>
  public decimal Price { get; init; }

  /// <summary>
  /// Product image URL (optional)
  /// </summary>
  public string? ImageUrl { get; init; }

  /// <summary>
  /// When the product was created
  /// </summary>
  public DateTime CreatedAt { get; init; }

  /// <summary>
  /// When the product was last updated (null if never updated)
  /// </summary>
  public DateTime? UpdatedAt { get; init; }

  /// <summary>
  /// When the product was deleted (null if not deleted - soft delete)
  /// </summary>
  public DateTime? DeletedAt { get; init; }
}
