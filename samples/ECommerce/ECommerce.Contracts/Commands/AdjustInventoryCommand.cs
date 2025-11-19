using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to manually adjust inventory (corrections, damages)
/// </summary>
public record AdjustInventoryCommand : ICommand {
  public required string ProductId { get; init; }
  public int QuantityChange { get; init; }
  public required string Reason { get; init; }
}
