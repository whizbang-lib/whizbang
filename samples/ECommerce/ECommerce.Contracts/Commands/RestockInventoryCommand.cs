using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to add inventory (restocking)
/// </summary>
public record RestockInventoryCommand : ICommand {
  public required string ProductId { get; init; }
  public int QuantityToAdd { get; init; }
}
