using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Sample perspective demonstrating the IPerspectiveFor pattern with pure functions.
/// Listens to OrderCreatedEvent and maintains an Order read model.
/// Generator will discover this and create EF Core configuration for PerspectiveRow&lt;Order&gt;.
/// </summary>
public class OrderPerspective(IPerspectiveStore<Order> store) : IPerspectiveFor<Order, SampleOrderCreatedEvent> {
  private readonly IPerspectiveStore<Order> _store = store;

  public Order Apply(Order currentData, SampleOrderCreatedEvent @event) {
    // Create new read model from the event (pure function - no I/O)
    return new Order {
      OrderId = @event.OrderId,
      Amount = @event.Amount,
      Status = "Created"
    };
  }

  public async Task Update(SampleOrderCreatedEvent @event, CancellationToken cancellationToken = default) {
    // Get current model or create new one
    var streamId = @event.OrderId.Value;
    var current = await _store.GetByStreamIdAsync(streamId, cancellationToken) ?? new Order {
      OrderId = @event.OrderId,
      Amount = 0,
      Status = "New"
    };

    // Apply the event
    var updated = Apply(current, @event);

    // Save the updated model
    await _store.UpsertAsync(streamId, updated, cancellationToken);
  }
}

/// <summary>
/// Sample event for testing.
/// </summary>
public record SampleOrderCreatedEvent : IEvent {
  [StreamKey]
  public required TestOrderId OrderId { get; init; }
  public required decimal Amount { get; init; }
}

/// <summary>
/// Read model maintained by OrderPerspective.
/// Generator infers this from "OrderPerspective" -> "Order".
/// </summary>
public class Order {
  public required TestOrderId OrderId { get; init; }
  public required decimal Amount { get; init; }
  public required string Status { get; init; }
}
