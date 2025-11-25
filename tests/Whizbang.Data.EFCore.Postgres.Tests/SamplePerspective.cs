using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Sample perspective for testing the EF Core generator.
/// This should trigger the generator to create ConfigureWhizbangPerspectives() method.
/// </summary>
public class OrderPerspective : IPerspectiveOf<SampleOrderCreatedEvent> {
  public Task Update(SampleOrderCreatedEvent @event, CancellationToken cancellationToken = default) {
    // This is just a stub for generator testing
    return Task.CompletedTask;
  }
}

/// <summary>
/// Sample event for testing.
/// </summary>
public record SampleOrderCreatedEvent(string OrderId, decimal Amount) : IEvent;

/// <summary>
/// Model type that should be inferred: "OrderPerspective" -> "Order"
/// </summary>
public class Order {
  public required string OrderId { get; init; }
  public required decimal Amount { get; init; }
  public required string Status { get; init; }
}
