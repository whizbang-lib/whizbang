using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Lenses;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.InventoryWorker.Perspectives;

/// <summary>
/// Materializes product catalog events into ProductDto perspective using EF Core.
/// Handles ProductCreatedEvent, ProductUpdatedEvent, and ProductDeletedEvent.
/// Uses ILensQuery for reading existing data and IPerspectiveStore for writing.
/// </summary>
public class ProductCatalogPerspective(
  IPerspectiveStore<ProductDto> store,
  ILensQuery<ProductDto> query,
  ILogger<ProductCatalogPerspective> logger) :
  IPerspectiveOf<ProductCreatedEvent>,
  IPerspectiveOf<ProductUpdatedEvent>,
  IPerspectiveOf<ProductDeletedEvent> {

  private readonly IPerspectiveStore<ProductDto> _store = store;
  private readonly ILensQuery<ProductDto> _query = query;
  private readonly ILogger<ProductCatalogPerspective> _logger = logger;

  /// <summary>
  /// Handles ProductCreatedEvent by inserting new product into ProductDto perspective.
  /// </summary>
  public async Task Update(ProductCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      var product = new ProductDto {
        ProductId = @event.ProductId,
        Name = @event.Name,
        Description = @event.Description,
        Price = @event.Price,
        ImageUrl = @event.ImageUrl,
        CreatedAt = @event.CreatedAt,
        UpdatedAt = null,
        DeletedAt = null
      };

      await _store.UpsertAsync(@event.ProductId.ToString(), product, cancellationToken);

      _logger.LogInformation(
        "Product catalog updated: Product {ProductId} created",
        @event.ProductId);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update product catalog for ProductCreatedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles ProductUpdatedEvent by updating existing product in ProductDto perspective.
  /// Supports partial updates - only non-null properties are updated.
  /// </summary>
  public async Task Update(ProductUpdatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Get existing product to apply partial update
      var existing = await _query.GetByIdAsync(@event.ProductId.ToString(), cancellationToken);
      if (existing == null) {
        _logger.LogWarning(
          "Product {ProductId} not found for update, skipping",
          @event.ProductId);
        return;
      }

      // Apply only non-null fields from event
      var updated = new ProductDto {
        ProductId = existing.ProductId,
        Name = @event.Name ?? existing.Name,
        Description = @event.Description ?? existing.Description,
        Price = @event.Price ?? existing.Price,
        ImageUrl = @event.ImageUrl ?? existing.ImageUrl,
        CreatedAt = existing.CreatedAt,
        UpdatedAt = @event.UpdatedAt,
        DeletedAt = existing.DeletedAt
      };

      await _store.UpsertAsync(@event.ProductId.ToString(), updated, cancellationToken);

      _logger.LogInformation(
        "Product catalog updated: Product {ProductId} updated",
        @event.ProductId);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update product catalog for ProductUpdatedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles ProductDeletedEvent by soft deleting product in ProductDto perspective.
  /// Sets deleted_at timestamp without removing the record.
  /// </summary>
  public async Task Update(ProductDeletedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Get existing product to apply soft delete
      var existing = await _query.GetByIdAsync(@event.ProductId.ToString(), cancellationToken);
      if (existing == null) {
        _logger.LogWarning(
          "Product {ProductId} not found for deletion, skipping",
          @event.ProductId);
        return;
      }

      // Apply soft delete
      var deleted = new ProductDto {
        ProductId = existing.ProductId,
        Name = existing.Name,
        Description = existing.Description,
        Price = existing.Price,
        ImageUrl = existing.ImageUrl,
        CreatedAt = existing.CreatedAt,
        UpdatedAt = existing.UpdatedAt,
        DeletedAt = @event.DeletedAt
      };

      await _store.UpsertAsync(@event.ProductId.ToString(), deleted, cancellationToken);

      _logger.LogInformation(
        "Product catalog updated: Product {ProductId} soft deleted",
        @event.ProductId);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update product catalog for ProductDeletedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }
}
