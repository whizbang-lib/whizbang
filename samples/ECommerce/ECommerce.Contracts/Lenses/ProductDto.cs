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
  /// <remarks>
  /// Indexed because the GraphQL surface offers sorting by it. A request-time sort is composed after
  /// this assembly is built, so nothing in source shows the ORDER BY: the declaration here is what
  /// says the sort can be served without reading the whole catalog.
  /// </remarks>
  [Indexed]
  public string Name { get; init; } = string.Empty;

  /// <summary>
  /// Product description (optional)
  /// </summary>
  [SuppressIndexAdvisory("long free text; a catalog sorts and filters by name and price, not by "
    + "description, and a btree over a paragraph costs more than the scan it saves")]
  public string? Description { get; init; }

  /// <summary>
  /// Product price
  /// </summary>
  /// <remarks>
  /// Indexed because the surface offers range filtering on it, and a range predicate is exactly what
  /// the document's containment index cannot answer.
  /// </remarks>
  [Indexed]
  public decimal Price { get; init; }

  /// <summary>
  /// Product image URL (optional)
  /// </summary>
  [SuppressIndexAdvisory("an opaque URL nobody orders a catalog by")]
  public string? ImageUrl { get; init; }

  /// <summary>
  /// When the product was created
  /// </summary>
  /// <remarks>
  /// Indexed because "newest first" is the default order of almost every catalog, so this is the
  /// sort most likely to arrive even though no source here writes it.
  /// </remarks>
  [Indexed]
  public DateTime CreatedAt { get; init; }

  /// <summary>
  /// When the product was last updated (null if never updated)
  /// </summary>
  [SuppressIndexAdvisory("mostly null and not offered as an order; CreatedAt is the date this "
    + "catalog is sorted by")]
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
  [Indexed]
  public DateTime? DeletedAt { get; init; }
}
