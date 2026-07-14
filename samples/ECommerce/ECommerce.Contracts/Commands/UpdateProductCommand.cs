using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to update product details
/// </summary>
[PinnedId("b015d718-c79f-4a3c-b3ef-91cac04890a8")]
public record UpdateProductCommand : ICommand {
  [StreamId]
  public required Guid ProductId { get; init; }
  public string? Name { get; init; }
  public string? Description { get; init; }
  public decimal? Price { get; init; }
  public string? ImageUrl { get; init; }
}
