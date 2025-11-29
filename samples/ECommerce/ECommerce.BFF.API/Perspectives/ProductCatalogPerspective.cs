using ECommerce.BFF.API.Hubs;
using ECommerce.BFF.API.Lenses;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Materializes product catalog events into product_perspective table using EF Core.
/// Handles ProductCreatedEvent, ProductUpdatedEvent, and ProductDeletedEvent.
/// Sends real-time SignalR notifications after successful database updates.
/// Uses EF Core with 3-column JSONB pattern - zero reflection, AOT compatible.
/// </summary>
public class ProductCatalogPerspective(
  IPerspectiveStore<ProductDto> store,
  ILensQuery<ProductDto> query,
  ILogger<ProductCatalogPerspective> logger,
  IHubContext<ProductInventoryHub> hubContext) :
  IPerspectiveOf<ProductCreatedEvent>,
  IPerspectiveOf<ProductUpdatedEvent>,
  IPerspectiveOf<ProductDeletedEvent> {

  private readonly IPerspectiveStore<ProductDto> _store = store;
  private readonly ILensQuery<ProductDto> _query = query;
  private readonly ILogger<ProductCatalogPerspective> _logger = logger;
  private readonly IHubContext<ProductInventoryHub> _hubContext = hubContext;

  /// <summary>
  /// Handles ProductCreatedEvent by creating new product in perspective store.
  /// Sends real-time SignalR notification after successful database update.
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
        "BFF product catalog updated: Product {ProductId} created",
        @event.ProductId);

      // Send SignalR notification after successful database update
      await SendProductNotificationAsync(
        @event.ProductId.ToString(),
        "Created",
        @event.Name,
        @event.Description,
        @event.Price,
        @event.ImageUrl,
        cancellationToken);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update BFF product catalog for ProductCreatedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles ProductUpdatedEvent by updating existing product in perspective store.
  /// Supports partial updates - only non-null properties are updated.
  /// Sends real-time SignalR notification after successful database update.
  /// </summary>
  public async Task Update(ProductUpdatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Get existing product to merge partial updates
      var existing = await _query.GetByIdAsync(@event.ProductId.ToString(), cancellationToken);

      if (existing is null) {
        _logger.LogWarning(
          "Product {ProductId} not found for update - creating new entry",
          @event.ProductId);

        // If product doesn't exist, treat as create (defensive)
        existing = new ProductDto {
          ProductId = @event.ProductId,
          Name = string.Empty,
          Description = null,
          Price = 0,
          ImageUrl = null,
          CreatedAt = @event.UpdatedAt,
          UpdatedAt = null,
          DeletedAt = null
        };
      }

      // Merge partial updates
      var updated = new ProductDto {
        ProductId = @event.ProductId,
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
        "BFF product catalog updated: Product {ProductId} updated",
        @event.ProductId);

      // Send SignalR notification after successful database update
      await SendProductNotificationAsync(
        @event.ProductId.ToString(),
        "Updated",
        updated.Name,
        updated.Description,
        updated.Price,
        updated.ImageUrl,
        cancellationToken);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update BFF product catalog for ProductUpdatedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles ProductDeletedEvent by soft deleting product in perspective store.
  /// Sets deleted_at timestamp without removing the record.
  /// Sends real-time SignalR notification after successful database update.
  /// </summary>
  public async Task Update(ProductDeletedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Get existing product for notification
      var existing = await _query.GetByIdAsync(@event.ProductId.ToString(), cancellationToken);

      if (existing is null) {
        _logger.LogWarning(
          "Product {ProductId} not found for deletion",
          @event.ProductId);
        return;
      }

      // Update with soft delete timestamp
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
        "BFF product catalog updated: Product {ProductId} soft deleted",
        @event.ProductId);

      // Send SignalR notification after successful database update
      await SendProductNotificationAsync(
        @event.ProductId.ToString(),
        "Deleted",
        existing.Name,
        existing.Description,
        existing.Price,
        existing.ImageUrl,
        cancellationToken);
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update BFF product catalog for ProductDeletedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Sends SignalR notification to all-products group and product-specific group
  /// </summary>
  private async Task SendProductNotificationAsync(
    string productId,
    string notificationType,
    string name,
    string? description,
    decimal? price,
    string? imageUrl,
    CancellationToken cancellationToken) {
    try {
      var notification = new ProductNotification {
        ProductId = productId,
        NotificationType = notificationType,
        Name = name,
        Description = description,
        Price = price,
        ImageUrl = imageUrl
      };

      var methodName = $"Product{notificationType}";  // e.g., "ProductCreated", "ProductUpdated", "ProductDeleted"

      // Send to all-products group
      await _hubContext.Clients.Group("all-products")
        .SendAsync(methodName, notification, cancellationToken);

      // Send to product-specific group
      await _hubContext.Clients.Group($"product-{productId}")
        .SendAsync(methodName, notification, cancellationToken);

      _logger.LogInformation(
        "Sent SignalR notification for product {ProductId}: {NotificationType}",
        productId,
        notificationType);
    } catch (Exception ex) {
      // Log error but don't throw - SignalR failure shouldn't break perspective update
      _logger.LogError(ex,
        "Failed to send SignalR notification for product {ProductId}",
        productId);
    }
  }

}
