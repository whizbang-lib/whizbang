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
  public Order Apply(Order currentData, SampleOrderCreatedEvent @event) {
    // Create new read model from the event (pure function - no I/O)
    return new Order {
      OrderId = @event.OrderId,
      Amount = @event.Amount,
      Status = "Created"
    };
  }
}

/// <summary>
/// Sample event for testing.
/// </summary>
public record SampleOrderCreatedEvent(TestOrderId OrderId, decimal Amount) : IEvent;

/// <summary>
/// Read model maintained by OrderPerspective.
/// Generator infers this from "OrderPerspective" -> "Order".
/// </summary>
public class Order {
  public required TestOrderId OrderId { get; init; }
  public required decimal Amount { get; init; }
  public required string Status { get; init; }
}
