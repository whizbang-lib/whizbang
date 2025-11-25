using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dapper;
using ECommerce.BFF.API.Hubs;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Contracts.Generated;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core;
using Whizbang.Core.Data;

namespace ECommerce.BFF.API.Perspectives;

/// <summary>
/// Maintains BFF read model for orders and pushes real-time updates via SignalR.
/// Listens to OrderCreatedEvent and updates the order_perspective table (3-column JSONB pattern).
/// </summary>
public class OrderPerspective : IPerspectiveOf<OrderCreatedEvent> {
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IHubContext<OrderStatusHub> _hubContext;
  private readonly ILogger<OrderPerspective> _logger;

  public OrderPerspective(
    IDbConnectionFactory connectionFactory,
    IHubContext<OrderStatusHub> hubContext,
    ILogger<OrderPerspective> logger
  ) {
    _connectionFactory = connectionFactory;
    _hubContext = hubContext;
    _logger = logger;
  }

  public async Task Update(OrderCreatedEvent @event, CancellationToken cancellationToken = default) {
    try {
      // Create new connection for this operation
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      EnsureConnectionOpen(connection);

      // Build OrderReadModel for model_data JSONB column
      var orderReadModel = new {
        OrderId = @event.OrderId.Value.ToString(),
        CustomerId = @event.CustomerId.Value.ToString(),
        TenantId = (string?)null,  // TODO: Add tenant_id when multi-tenancy implemented
        Status = "Created",
        TotalAmount = @event.TotalAmount,
        CreatedAt = @event.CreatedAt,
        UpdatedAt = @event.CreatedAt,
        ItemCount = @event.LineItems.Count,
        PaymentStatus = (string?)null,
        ShipmentId = (string?)null,
        TrackingNumber = (string?)null,
        LineItems = @event.LineItems.Select(li => new {
          ProductId = li.ProductId.Value.ToString(),
          ProductName = li.ProductName,
          Quantity = li.Quantity,
          Price = li.UnitPrice
        }).ToList()
      };

      // Build metadata JSONB column (correlation, causation, event type)
      var metadata = new {
        EventType = "OrderCreatedEvent",
        EventId = @event.OrderId.Value.ToString(),
        Timestamp = @event.CreatedAt,
        Details = new OrderCreatedDetails {
          TotalAmount = @event.TotalAmount,
          ItemCount = @event.LineItems.Count
        }
      };

      // Build scope JSONB column (tenant, user permissions)
      var scope = new {
        TenantId = (string?)null,
        CustomerId = @event.CustomerId.Value.ToString()
      };

      // 1. Insert/Update order_perspective table (3-column JSONB pattern)
      await connection.ExecuteAsync(@"
        INSERT INTO order_perspective (
          id,
          model_data,
          metadata,
          scope,
          created_at,
          updated_at,
          version
        )
        VALUES (
          @Id::uuid,
          @ModelData::jsonb,
          @Metadata::jsonb,
          @Scope::jsonb,
          @CreatedAt,
          @UpdatedAt,
          1
        )
        ON CONFLICT (id) DO UPDATE SET
          model_data = EXCLUDED.model_data,
          metadata = EXCLUDED.metadata,
          updated_at = EXCLUDED.updated_at,
          version = order_perspective.version + 1",
        new {
          Id = @event.OrderId.Value.ToString(),
          ModelData = JsonSerializer.Serialize(orderReadModel, WhizbangJsonContext.CreateOptions()),
          Metadata = JsonSerializer.Serialize(metadata, WhizbangJsonContext.CreateOptions()),
          Scope = JsonSerializer.Serialize(scope, WhizbangJsonContext.CreateOptions()),
          CreatedAt = @event.CreatedAt,
          UpdatedAt = @event.CreatedAt
        });

      // 2. Push real-time update via SignalR
      await _hubContext.Clients.User(@event.CustomerId.Value.ToString())
        .SendAsync("OrderStatusChanged", new OrderStatusUpdate {
          OrderId = @event.OrderId.Value.ToString(),
          Status = "Created",
          Timestamp = @event.CreatedAt,
          Message = $"Order created with total amount ${@event.TotalAmount:F2}"
        }, cancellationToken);

      _logger.LogInformation(
        "Order {OrderId} perspective updated: Status=Created, Customer={CustomerId}, Total={TotalAmount}",
        @event.OrderId,
        @event.CustomerId,
        @event.TotalAmount
      );
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Error updating OrderPerspective for order {OrderId}",
        @event.OrderId
      );
      throw;
    }
  }

  private static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }
}
