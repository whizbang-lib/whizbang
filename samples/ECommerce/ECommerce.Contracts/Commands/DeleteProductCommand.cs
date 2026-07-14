using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to soft-delete a product from catalog
/// </summary>
[PinnedId("81a4b6cc-7a2f-4b26-98e1-26f8ff81745b")]
public record DeleteProductCommand : ICommand {
  [StreamId]
  public required Guid ProductId { get; init; }
}
