using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to create a new product in the catalog
/// </summary>
[PinnedId("9fee35c4-d3fc-4cd6-9edf-4e9a2abecb0f")]
public record CreateProductCommand : ICommand {
  [StreamId]
  public required ProductId ProductId { get; init; }
  public required string Name { get; init; }
  public required string Description { get; init; }
  public required decimal Price { get; init; }
  public string? ImageUrl { get; init; }
  public int InitialStock { get; init; }
}
