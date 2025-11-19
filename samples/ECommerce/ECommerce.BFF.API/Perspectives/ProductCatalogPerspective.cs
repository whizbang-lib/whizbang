using System.Data;
using Dapper;
using ECommerce.BFF.API.Hubs;
using ECommerce.Contracts.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Data;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Materializes product catalog events into bff.product_catalog table.
/// Handles ProductCreatedEvent, ProductUpdatedEvent, and ProductDeletedEvent.
/// Sends real-time SignalR notifications after successful database updates.
/// </summary>
public class ProductCatalogPerspective :
  IPerspectiveOf<ProductCreatedEvent>,
  IPerspectiveOf<ProductUpdatedEvent>,
  IPerspectiveOf<ProductDeletedEvent> {

  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ILogger<ProductCatalogPerspective> _logger;
  private readonly IHubContext<ProductInventoryHub> _hubContext;

  public ProductCatalogPerspective(
    IDbConnectionFactory connectionFactory,
    ILogger<ProductCatalogPerspective> logger,
    IHubContext<ProductInventoryHub> hubContext) {
    _connectionFactory = connectionFactory;
    _logger = logger;
    _hubContext = hubContext;
  }

  /// <summary>
  /// Handles ProductCreatedEvent by inserting new product into bff.product_catalog table.
  /// Sends real-time SignalR notification after successful database update.
  /// </summary>
  public async Task Update(ProductCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        INSERT INTO bff.product_catalog (
          product_id, name, description, price, image_url, created_at
        ) VALUES (
          @ProductId, @Name, @Description, @Price, @ImageUrl, @CreatedAt
        )",
        new {
          @event.ProductId,
          @event.Name,
          @event.Description,
          @event.Price,
          @event.ImageUrl,
          @event.CreatedAt
        });

      _logger.LogInformation(
        "BFF product catalog updated: Product {ProductId} created",
        @event.ProductId);

      // Send SignalR notification after successful database update
      await SendProductNotificationAsync(
        @event.ProductId,
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
  /// Handles ProductUpdatedEvent by updating existing product in bff.product_catalog table.
  /// Supports partial updates - only non-null properties are updated.
  /// Sends real-time SignalR notification after successful database update.
  /// </summary>
  public async Task Update(ProductUpdatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      // Build dynamic UPDATE statement with only non-null fields
      var setClauses = new List<string> { "updated_at = @UpdatedAt" };
      var parameters = new DynamicParameters();
      parameters.Add("ProductId", @event.ProductId);
      parameters.Add("UpdatedAt", @event.UpdatedAt);

      if (@event.Name is not null) {
        setClauses.Add("name = @Name");
        parameters.Add("Name", @event.Name);
      }

      if (@event.Description is not null) {
        setClauses.Add("description = @Description");
        parameters.Add("Description", @event.Description);
      }

      if (@event.Price.HasValue) {
        setClauses.Add("price = @Price");
        parameters.Add("Price", @event.Price.Value);
      }

      if (@event.ImageUrl is not null) {
        setClauses.Add("image_url = @ImageUrl");
        parameters.Add("ImageUrl", @event.ImageUrl);
      }

      var sql = $@"
        UPDATE bff.product_catalog
        SET {string.Join(", ", setClauses)}
        WHERE product_id = @ProductId";

      await connection.ExecuteAsync(sql, parameters);

      _logger.LogInformation(
        "BFF product catalog updated: Product {ProductId} updated",
        @event.ProductId);

      // Query updated product for notification (needed because update is partial)
      var product = await connection.QuerySingleOrDefaultAsync<ProductRecord>(@"
        SELECT product_id, name, description, price, image_url
        FROM bff.product_catalog
        WHERE product_id = @ProductId",
        new { @event.ProductId });

      // Send SignalR notification after successful database update
      if (product is not null) {
        await SendProductNotificationAsync(
          @event.ProductId,
          "Updated",
          product.name,
          product.description,
          product.price,
          product.image_url,
          cancellationToken);
      }
    } catch (Exception ex) {
      _logger.LogError(ex,
        "Failed to update BFF product catalog for ProductUpdatedEvent: {ProductId}",
        @event.ProductId);
      throw;
    }
  }

  /// <summary>
  /// Handles ProductDeletedEvent by soft deleting product in bff.product_catalog table.
  /// Sets deleted_at timestamp without removing the record.
  /// Sends real-time SignalR notification after successful database update.
  /// </summary>
  public async Task Update(ProductDeletedEvent @event, CancellationToken cancellationToken = default) {
    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      // Query product details before soft delete for notification
      var product = await connection.QuerySingleOrDefaultAsync<ProductRecord>(@"
        SELECT product_id, name, description, price, image_url
        FROM bff.product_catalog
        WHERE product_id = @ProductId",
        new { @event.ProductId });

      await connection.ExecuteAsync(@"
        UPDATE bff.product_catalog
        SET deleted_at = @DeletedAt
        WHERE product_id = @ProductId",
        new {
          @event.ProductId,
          @event.DeletedAt
        });

      _logger.LogInformation(
        "BFF product catalog updated: Product {ProductId} soft deleted",
        @event.ProductId);

      // Send SignalR notification after successful database update
      if (product is not null) {
        await SendProductNotificationAsync(
          @event.ProductId,
          "Deleted",
          product.name,
          product.description,
          product.price,
          product.image_url,
          cancellationToken);
      }
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

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }

  /// <summary>
  /// Record for querying product details from database
  /// </summary>
  private record ProductRecord(
    string product_id,
    string name,
    string? description,
    decimal? price,
    string? image_url
  );
}
