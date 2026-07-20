using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to manually adjust inventory (corrections, damages)
/// </summary>
[PinnedId("47fd4fc8-f8cd-45eb-bc3b-73f017354915")]
public record AdjustInventoryCommand : ICommand {
  [StreamId]
  public required Guid ProductId { get; init; }
  public int QuantityChange { get; init; }
  public required string Reason { get; init; }
}
