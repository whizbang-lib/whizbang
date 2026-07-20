using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to add inventory (restocking)
/// </summary>
[PinnedId("7282a2a3-a017-439e-b946-a28f6b18d9dc")]
public record RestockInventoryCommand : ICommand {
  [StreamId]
  public required Guid ProductId { get; init; }
  public int QuantityToAdd { get; init; }
}
