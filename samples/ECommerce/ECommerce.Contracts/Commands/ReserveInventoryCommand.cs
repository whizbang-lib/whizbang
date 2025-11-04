using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to reserve inventory for an order
/// </summary>
public record ReserveInventoryCommand : ICommand {
  public required string OrderId { get; init; }
  public required string ProductId { get; init; }
  public int Quantity { get; init; }
}
