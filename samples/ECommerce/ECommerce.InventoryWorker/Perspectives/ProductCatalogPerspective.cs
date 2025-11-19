using System.Data;
using Dapper;
using ECommerce.Contracts.Events;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Data;

namespace ECommerce.InventoryWorker.Perspectives;

/// <summary>
/// Materializes product catalog events into product_catalog table.
/// Handles ProductCreatedEvent, ProductUpdatedEvent, and ProductDeletedEvent.
/// </summary>
public class ProductCatalogPerspective :
  IPerspectiveOf<ProductCreatedEvent>,
  IPerspectiveOf<ProductUpdatedEvent>,
  IPerspectiveOf<ProductDeletedEvent> {

  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ILogger<ProductCatalogPerspective> _logger;

  public ProductCatalogPerspective(
    IDbConnectionFactory connectionFactory,
    ILogger<ProductCatalogPerspective> logger) {
    _connectionFactory = connectionFactory;
    _logger = logger;
  }

  /// <summary>
  /// Handles ProductCreatedEvent by inserting new product into product_catalog table.
  /// </summary>
  public async Task Update(ProductCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        INSERT INTO inventoryworker.product_catalog (
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
  /// Handles ProductUpdatedEvent by updating existing product in product_catalog table.
  /// Supports partial updates - only non-null properties are updated.
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
        UPDATE inventoryworker.product_catalog
        SET {string.Join(", ", setClauses)}
        WHERE product_id = @ProductId";

      await connection.ExecuteAsync(sql, parameters);

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
  /// Handles ProductDeletedEvent by soft deleting product in product_catalog table.
  /// Sets deleted_at timestamp without removing the record.
  /// </summary>
  public async Task Update(ProductDeletedEvent @event, CancellationToken cancellationToken = default) {
    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      await connection.ExecuteAsync(@"
        UPDATE inventoryworker.product_catalog
        SET deleted_at = @DeletedAt
        WHERE product_id = @ProductId",
        new {
          @event.ProductId,
          @event.DeletedAt
        });

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

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
