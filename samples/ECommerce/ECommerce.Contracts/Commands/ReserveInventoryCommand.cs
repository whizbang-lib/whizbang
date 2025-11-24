using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to reserve inventory for an order
/// </summary>
public record ReserveInventoryCommand : ICommand {
  public required OrderId OrderId { get; init; }
  public required ProductId ProductId { get; init; }
  public int Quantity { get; init; }
}
