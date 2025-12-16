using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Sample perspective demonstrating the IPerspectiveStore pattern.
/// Listens to OrderCreatedEvent and maintains an Order read model.
/// Generator will discover this and create EF Core configuration for PerspectiveRow&lt;Order&gt;.
/// </summary>
public class OrderPerspective : IPerspectiveOf<SampleOrderCreatedEvent> {
  private readonly IPerspectiveStore<Order> _store;

  public OrderPerspective(IPerspectiveStore<Order> store) {
    _store = store;
  }

  public async Task Update(SampleOrderCreatedEvent @event, CancellationToken cancellationToken = default) {
    // Create the read model from the event
    var order = new Order {
      OrderId = @event.OrderId,
      Amount = @event.Amount,
      Status = "Created"
    };

    // Use the store to persist the model
    // The store handles:
    // - JSON serialization to model_data column
    // - Default metadata creation
    // - Default scope creation
    // - Timestamp management (CreatedAt/UpdatedAt)
    // - Version incrementing for optimistic concurrency
    await _store.UpsertAsync(
      id: @event.OrderId,
      model: order,
      cancellationToken: cancellationToken
    );
  }
}

/// <summary>
/// Sample event for testing.
/// </summary>
public record SampleOrderCreatedEvent(Guid OrderId, decimal Amount) : IEvent;

/// <summary>
/// Read model maintained by OrderPerspective.
/// Generator infers this from "OrderPerspective" -> "Order".
/// </summary>
public class Order {
  public required Guid OrderId { get; init; }
  public required decimal Amount { get; init; }
  public required string Status { get; init; }
}
