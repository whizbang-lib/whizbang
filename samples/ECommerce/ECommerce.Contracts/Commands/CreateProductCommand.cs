using Whizbang.Core;

namespace ECommerce.Contracts.Commands;

/// <summary>
/// Command to create a new product in the catalog
/// </summary>
public record CreateProductCommand : ICommand {
  [AggregateId]
  public required ProductId ProductId { get; init; }
  public required string Name { get; init; }
  public required string Description { get; init; }
  public required decimal Price { get; init; }
  public string? ImageUrl { get; init; }
  public int InitialStock { get; init; }
}
