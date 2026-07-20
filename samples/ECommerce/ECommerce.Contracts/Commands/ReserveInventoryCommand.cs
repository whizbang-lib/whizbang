using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to reserve inventory for an order
/// </summary>
[PinnedId("6bfe1dee-7a31-4d73-9481-170817327096")]
public record ReserveInventoryCommand : ICommand {
  public required OrderId OrderId { get; init; }
  [StreamId]
  public required ProductId ProductId { get; init; }
  public int Quantity { get; init; }
}
