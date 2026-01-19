using ECommerce.Contracts.Events;
using ECommerce.Contracts.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.InventoryWorker.Perspectives;

/// <summary>
/// Materializes product catalog events into ProductDto perspective.
/// Handles ProductCreatedEvent, ProductUpdatedEvent, and ProductDeletedEvent.
/// Pure functions - no I/O, no side effects, deterministic.
/// </summary>
public class ProductCatalogPerspective :
  IPerspectiveFor<ProductDto, ProductCreatedEvent, ProductUpdatedEvent, ProductDeletedEvent> {

  /// <summary>
  /// Handles ProductCreatedEvent by creating new product.
  /// </summary>
  public ProductDto Apply(ProductDto currentData, ProductCreatedEvent @event) {
    return new ProductDto {
      ProductId = @event.ProductId,
      Name = @event.Name,
      Description = @event.Description,
      Price = @event.Price,
      ImageUrl = @event.ImageUrl,
      CreatedAt = @event.CreatedAt,
      UpdatedAt = null,
      DeletedAt = null
    };
  }

  /// <summary>
  /// Handles ProductUpdatedEvent by updating existing product.
  /// Supports partial updates - only non-null properties are updated.
  /// </summary>
  public ProductDto Apply(ProductDto currentData, ProductUpdatedEvent @event) {
    // If no existing data, cannot update - skip
    if (currentData == null) {
      return null!; // PerspectiveRunner will handle null return
    }

    // Apply only non-null fields from event
    return new ProductDto {
      ProductId = currentData.ProductId,
      Name = @event.Name ?? currentData.Name,
      Description = @event.Description ?? currentData.Description,
      Price = @event.Price ?? currentData.Price,
      ImageUrl = @event.ImageUrl ?? currentData.ImageUrl,
      CreatedAt = currentData.CreatedAt,
      UpdatedAt = @event.UpdatedAt,
      DeletedAt = currentData.DeletedAt
    };
  }

  /// <summary>
  /// Handles ProductDeletedEvent by soft deleting product.
  /// Sets deleted_at timestamp without removing the record.
  /// </summary>
  public ProductDto Apply(ProductDto currentData, ProductDeletedEvent @event) {
    // If no existing data, cannot delete - skip
    if (currentData == null) {
      return null!; // PerspectiveRunner will handle null return
    }

    // Apply soft delete
    return new ProductDto {
      ProductId = currentData.ProductId,
      Name = currentData.Name,
      Description = currentData.Description,
      Price = currentData.Price,
      ImageUrl = currentData.ImageUrl,
      CreatedAt = currentData.CreatedAt,
      UpdatedAt = currentData.UpdatedAt,
      DeletedAt = @event.DeletedAt
    };
  }
}
