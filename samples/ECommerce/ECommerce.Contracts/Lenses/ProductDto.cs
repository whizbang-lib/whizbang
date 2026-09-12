using Whizbang;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace ECommerce.Contracts.Lenses;

/// <summary>
/// Data transfer object for product catalog information.
/// Shared lens model used by both BFF.API and InventoryWorker perspectives.
/// Maps to perspective-specific tables (e.g., bff.product_catalog, inventory.product_catalog).
/// </summary>
[WhizbangSerializable]
public record ProductDto {
  /// <summary>
  /// Unique product identifier
  /// </summary>
  [StreamId]
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
  /// <remarks>
  /// Indexed because every catalog query filters on it: a soft-delete marker is read on the way to
  /// every other answer, so the one predicate that is always present is the one most worth an index.
  /// A date is indexable because its stored form is a number, which casts through an immutable
  /// expression; stored as a rendering it could not carry an index at all.
  /// </remarks>
  [JsonIndexed]
  public DateTime? DeletedAt { get; init; }
}
